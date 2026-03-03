using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Server.Data;
using Server.Models;

namespace Server.Services.Ai
{
    public class PipelineExecutor : IPipelineExecutor
    {
        private readonly IAiProviderRegistry _registry;

        public PipelineExecutor(IAiProviderRegistry registry)
        {
            _registry = registry;
        }

        public async Task<ProjectExecution> ExecuteAsync(
            Guid projectId, string? userInput, UserDbContext db, string tenantDbName)
        {
            var project = await db.Projects
                .Include(p => p.ProjectModules.Where(pm => pm.IsActive).OrderBy(pm => pm.StepOrder))
                    .ThenInclude(pm => pm.AiModule)
                        .ThenInclude(m => m.ApiKey)
                .FirstOrDefaultAsync(p => p.Id == projectId);

            if (project is null)
                throw new InvalidOperationException("Proyecto no encontrado");

            if (!project.ProjectModules.Any())
                throw new InvalidOperationException("El proyecto no tiene modulos asignados");

            var executionId = Guid.NewGuid();
            var workspacePath = Path.Combine("storage", tenantDbName, projectId.ToString(), executionId.ToString());

            var execution = new ProjectExecution
            {
                Id = executionId,
                ProjectId = projectId,
                Status = "Running",
                WorkspacePath = workspacePath,
                CreatedAt = DateTime.UtcNow,
            };

            db.ProjectExecutions.Add(execution);
            await db.SaveChangesAsync();

            Directory.CreateDirectory(workspacePath);

            var stepResults = new Dictionary<int, AiResult>();

            foreach (var pm in project.ProjectModules)
            {
                var stepExecution = new StepExecution
                {
                    Id = Guid.NewGuid(),
                    ExecutionId = executionId,
                    ProjectModuleId = pm.Id,
                    StepOrder = pm.StepOrder,
                    Status = "Running",
                    CreatedAt = DateTime.UtcNow,
                };

                db.StepExecutions.Add(stepExecution);
                await db.SaveChangesAsync();

                try
                {
                    var input = ResolveInput(pm, userInput, stepResults);
                    var apiKey = pm.AiModule.ApiKey?.EncryptedKey
                        ?? throw new InvalidOperationException($"Paso {pm.StepOrder}: ApiKey no configurada para modulo '{pm.AiModule.Name}'");

                    var provider = _registry.GetProvider(pm.AiModule.ProviderType)
                        ?? throw new InvalidOperationException($"Paso {pm.StepOrder}: Proveedor '{pm.AiModule.ProviderType}' no disponible");

                    var config = MergeConfiguration(pm.AiModule.Configuration, pm.Configuration);

                    var context = new AiExecutionContext
                    {
                        ModuleType = pm.AiModule.ModuleType,
                        ModelName = pm.AiModule.ModelName,
                        ApiKey = apiKey,
                        Input = input,
                        Configuration = config,
                    };

                    stepExecution.InputData = JsonSerializer.Serialize(new { prompt = input });

                    var result = await provider.ExecuteAsync(context);
                    stepResults[pm.StepOrder] = result;

                    if (!result.Success)
                    {
                        stepExecution.Status = "Failed";
                        stepExecution.ErrorMessage = result.Error;
                        stepExecution.CompletedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync();

                        execution.Status = "Failed";
                        execution.CompletedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync();
                        return execution;
                    }

                    stepExecution.OutputData = JsonSerializer.Serialize(new
                    {
                        text = result.TextOutput,
                        contentType = result.ContentType,
                        metadata = result.Metadata
                    });

                    if (result.FileOutput is not null)
                    {
                        var stepDir = Path.Combine(workspacePath, $"step_{pm.StepOrder}");
                        Directory.CreateDirectory(stepDir);

                        var ext = GetExtension(result.ContentType ?? "application/octet-stream");
                        var fileName = $"output{ext}";
                        var filePath = Path.Combine(stepDir, fileName);

                        await File.WriteAllBytesAsync(filePath, result.FileOutput);

                        var execFile = new ExecutionFile
                        {
                            Id = Guid.NewGuid(),
                            StepExecutionId = stepExecution.Id,
                            FileName = fileName,
                            ContentType = result.ContentType ?? "application/octet-stream",
                            FilePath = Path.Combine($"step_{pm.StepOrder}", fileName),
                            Direction = "Output",
                            FileSize = result.FileOutput.Length,
                            CreatedAt = DateTime.UtcNow,
                        };

                        db.ExecutionFiles.Add(execFile);
                    }

                    if (result.TextOutput is not null)
                    {
                        var stepDir = Path.Combine(workspacePath, $"step_{pm.StepOrder}");
                        Directory.CreateDirectory(stepDir);

                        var textFile = Path.Combine(stepDir, "output.txt");
                        await File.WriteAllTextAsync(textFile, result.TextOutput);

                        var execFile = new ExecutionFile
                        {
                            Id = Guid.NewGuid(),
                            StepExecutionId = stepExecution.Id,
                            FileName = "output.txt",
                            ContentType = "text/plain",
                            FilePath = Path.Combine($"step_{pm.StepOrder}", "output.txt"),
                            Direction = "Output",
                            FileSize = System.Text.Encoding.UTF8.GetByteCount(result.TextOutput),
                            CreatedAt = DateTime.UtcNow,
                        };

                        db.ExecutionFiles.Add(execFile);
                    }

                    stepExecution.Status = "Completed";
                    stepExecution.CompletedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    stepExecution.Status = "Failed";
                    stepExecution.ErrorMessage = ex.Message;
                    stepExecution.CompletedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();

                    execution.Status = "Failed";
                    execution.CompletedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                    return execution;
                }
            }

            execution.Status = "Completed";
            execution.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            return execution;
        }

        private static string ResolveInput(ProjectModule pm, string? userInput, Dictionary<int, AiResult> stepResults)
        {
            if (pm.InputMapping is null)
                return userInput ?? throw new InvalidOperationException($"Paso {pm.StepOrder}: No hay input de usuario y no se definio InputMapping");

            var mapping = JsonSerializer.Deserialize<JsonElement>(pm.InputMapping);

            var source = mapping.GetProperty("source").GetString();
            var field = mapping.TryGetProperty("field", out var f) ? f.GetString() : null;

            switch (source)
            {
                case "user":
                    return userInput ?? throw new InvalidOperationException($"Paso {pm.StepOrder}: InputMapping requiere input de usuario pero no se proporciono");

                case "previous":
                    var prevOrder = stepResults.Keys.Where(k => k < pm.StepOrder).OrderByDescending(k => k).FirstOrDefault();
                    if (!stepResults.ContainsKey(prevOrder))
                        throw new InvalidOperationException($"Paso {pm.StepOrder}: No hay paso anterior con resultado");
                    return ExtractField(stepResults[prevOrder], field);

                case "step":
                    var targetStep = mapping.GetProperty("stepOrder").GetInt32();
                    if (!stepResults.TryGetValue(targetStep, out var targetResult))
                        throw new InvalidOperationException($"Paso {pm.StepOrder}: Paso {targetStep} no tiene resultado disponible");
                    return ExtractField(targetResult, field);

                default:
                    return userInput ?? "";
            }
        }

        private static string ExtractField(AiResult result, string? field)
        {
            return field switch
            {
                "text" or null => result.TextOutput ?? "",
                _ => result.TextOutput ?? ""
            };
        }

        private static Dictionary<string, object> MergeConfiguration(string? moduleConfig, string? stepConfig)
        {
            var config = new Dictionary<string, object>();

            if (moduleConfig is not null)
            {
                var moduleDict = JsonSerializer.Deserialize<Dictionary<string, object>>(moduleConfig);
                if (moduleDict is not null)
                    foreach (var kv in moduleDict) config[kv.Key] = kv.Value;
            }

            if (stepConfig is not null)
            {
                var stepDict = JsonSerializer.Deserialize<Dictionary<string, object>>(stepConfig);
                if (stepDict is not null)
                    foreach (var kv in stepDict) config[kv.Key] = kv.Value;
            }

            return config;
        }

        private static string GetExtension(string contentType) => contentType switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            "audio/mpeg" or "audio/mp3" => ".mp3",
            "audio/wav" => ".wav",
            "text/plain" => ".txt",
            _ => ".bin"
        };
    }
}
