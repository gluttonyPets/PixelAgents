using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Server.Data;
using Server.Hubs;
using Server.Models;

namespace Server.Services.Ai
{
    public class PipelineExecutor : IPipelineExecutor
    {
        private readonly IAiProviderRegistry _registry;
        private readonly IExecutionLogger _logger;

        public PipelineExecutor(IAiProviderRegistry registry, IExecutionLogger logger)
        {
            _registry = registry;
            _logger = logger;
        }

        public async Task<ProjectExecution> ExecuteAsync(
            Guid projectId, string? userInput, UserDbContext db, string tenantDbName, CancellationToken ct = default)
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

            // ── Pre-flight: validate all API keys before starting ──
            var validatedKeys = new HashSet<string>();
            var errors = new List<string>();

            foreach (var pm in project.ProjectModules)
            {
                var stepName = pm.StepName ?? pm.AiModule.Name;

                if (pm.AiModule.ApiKey is null || string.IsNullOrEmpty(pm.AiModule.ApiKey.EncryptedKey))
                {
                    errors.Add($"Paso {pm.StepOrder} ({stepName}): No tiene API Key configurada");
                    continue;
                }

                var provider = _registry.GetProvider(pm.AiModule.ProviderType);
                if (provider is null)
                {
                    errors.Add($"Paso {pm.StepOrder} ({stepName}): Proveedor '{pm.AiModule.ProviderType}' no disponible");
                    continue;
                }

                // Only validate each unique key once per provider
                var keySignature = $"{pm.AiModule.ProviderType}::{pm.AiModule.ApiKey.Id}";
                if (!validatedKeys.Add(keySignature))
                    continue;

                var (valid, error) = await provider.ValidateKeyAsync(pm.AiModule.ApiKey.EncryptedKey);
                if (!valid)
                    errors.Add($"Paso {pm.StepOrder} ({stepName}): {error}");
            }

            if (errors.Count > 0)
                throw new InvalidOperationException(
                    "Validacion pre-ejecucion fallida:\n" + string.Join("\n", errors));

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

            await _logger.LogAsync(projectId, executionId, "info",
                $"Iniciando pipeline con {project.ProjectModules.Count} paso(s)");

            var stepResults = new Dictionary<int, AiResult>();
            var stepOutputs = new Dictionary<int, StepOutput>();
            var stepModuleTypes = new Dictionary<int, string>();

            var allModules = project.ProjectModules.ToList();

            for (var mi = 0; mi < allModules.Count; mi++)
            {
                var pm = allModules[mi];
                var nextModule = mi + 1 < allModules.Count ? allModules[mi + 1] : null;

                if (ct.IsCancellationRequested)
                {
                    await _logger.LogAsync(projectId, executionId, "warning", "Pipeline cancelado por el usuario");
                    execution.Status = "Cancelled";
                    execution.CompletedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                    return execution;
                }

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
                    var stepName = pm.StepName ?? pm.AiModule.Name;
                    await _logger.LogAsync(projectId, executionId, "info",
                        $"Ejecutando paso {pm.StepOrder}: {stepName} ({pm.AiModule.ProviderType}/{pm.AiModule.ModelName})",
                        pm.StepOrder, stepName);

                    var apiKey = pm.AiModule.ApiKey?.EncryptedKey
                        ?? throw new InvalidOperationException($"Paso {pm.StepOrder}: ApiKey no configurada para modulo '{pm.AiModule.Name}'");

                    var provider = _registry.GetProvider(pm.AiModule.ProviderType)
                        ?? throw new InvalidOperationException($"Paso {pm.StepOrder}: Proveedor '{pm.AiModule.ProviderType}' no disponible");

                    var config = MergeConfiguration(pm.AiModule.Configuration, pm.Configuration);

                    // If this is a Text step and the next step consumes its output, inject prompt length rule
                    if (pm.AiModule.ModuleType == "Text" && nextModule is not null)
                    {
                        var nextMaxLen = InputAdapter.GetMaxPromptLength(nextModule.AiModule.ModelName);
                        var rule = $"\n\nREGLA DE LONGITUD: El siguiente paso usa el modelo {nextModule.AiModule.ModelName} ({nextModule.AiModule.ModuleType}) que acepta maximo {nextMaxLen} caracteres por prompt. Cada elemento en \"items\" DEBE tener un \"content\" de maximo {nextMaxLen} caracteres.";
                        if (config.TryGetValue("systemPrompt", out var existing) && existing is string s)
                            config["systemPrompt"] = s + rule;
                        else
                            config["systemPrompt"] = rule;
                    }

                    // Resolve inputs: check if previous step has multiple items
                    var inputs = ResolveInputs(pm, userInput, stepResults, stepOutputs, pm.AiModule.ModuleType, pm.AiModule.ModelName);

                    stepExecution.InputData = inputs.Count == 1
                        ? JsonSerializer.Serialize(new { prompt = inputs[0] })
                        : JsonSerializer.Serialize(new { prompts = inputs, count = inputs.Count });

                    if (inputs.Count > 1)
                    {
                        await _logger.LogAsync(projectId, executionId, "info",
                            $"Recibidos {inputs.Count} inputs del paso anterior — se ejecutara {inputs.Count} veces",
                            pm.StepOrder, stepName);
                    }

                    if (pm.AiModule.ModuleType == "Text")
                    {
                        // Text modules: single call, structured JSON output
                        await _logger.LogAsync(projectId, executionId, "info",
                            $"Enviando prompt al modelo de texto ({pm.AiModule.ModelName})...",
                            pm.StepOrder, stepName);
                        await _logger.LogAsync(projectId, executionId, "info",
                            $"Prompt: {inputs[0]}",
                            pm.StepOrder, stepName);

                        var context = new AiExecutionContext
                        {
                            ModuleType = pm.AiModule.ModuleType,
                            ModelName = pm.AiModule.ModelName,
                            ApiKey = apiKey,
                            Input = inputs[0], // Text modules always get single input
                            ProjectContext = project.Context,
                            Configuration = config,
                        };

                        var result = await provider.ExecuteAsync(context);
                        stepResults[pm.StepOrder] = result;
                        stepModuleTypes[pm.StepOrder] = pm.AiModule.ModuleType;

                        if (!result.Success)
                        {
                            await _logger.LogAsync(projectId, executionId, "error",
                                $"Error en texto: {result.Error}", pm.StepOrder, stepName);
                            await FailStep(stepExecution, execution, result.Error!, db);
                            return execution;
                        }

                        // Parse structured output
                        var stepOutput = OutputSchemaHelper.ParseTextOutput(
                            result.TextOutput ?? "", result.Metadata);
                        stepOutputs[pm.StepOrder] = stepOutput;

                        // Save text file
                        var stepDir = Path.Combine(workspacePath, $"step_{pm.StepOrder}");
                        Directory.CreateDirectory(stepDir);
                        await File.WriteAllTextAsync(Path.Combine(stepDir, "output.json"), result.TextOutput ?? "");

                        var execFile = new ExecutionFile
                        {
                            Id = Guid.NewGuid(),
                            StepExecutionId = stepExecution.Id,
                            FileName = "output.json",
                            ContentType = "application/json",
                            FilePath = Path.Combine($"step_{pm.StepOrder}", "output.json"),
                            Direction = "Output",
                            FileSize = System.Text.Encoding.UTF8.GetByteCount(result.TextOutput ?? ""),
                            CreatedAt = DateTime.UtcNow,
                        };
                        db.ExecutionFiles.Add(execFile);

                        stepExecution.OutputData = JsonSerializer.Serialize(stepOutput);

                        var itemsMsg = stepOutput.Items.Count > 0
                            ? $" — {stepOutput.Items.Count} items generados"
                            : "";
                        await _logger.LogAsync(projectId, executionId, "success",
                            $"Texto generado correctamente{itemsMsg}",
                            pm.StepOrder, stepName);
                    }
                    else if (pm.AiModule.ModuleType == "Image")
                    {
                        // Image modules: may execute multiple times if previous step had items
                        var outputFiles = new List<OutputFile>();
                        string? imageError = null;

                        for (var i = 0; i < inputs.Count; i++)
                        {
                            await _logger.LogAsync(projectId, executionId, "info",
                                inputs.Count > 1
                                    ? $"Generando imagen {i + 1}/{inputs.Count}..."
                                    : $"Generando imagen...",
                                pm.StepOrder, stepName);
                            await _logger.LogAsync(projectId, executionId, "info",
                                $"Prompt: {inputs[i]}",
                                pm.StepOrder, stepName);

                            var singleInput = inputs[i];

                            // Apply truncation safety
                            var maxLen = InputAdapter.GetMaxPromptLength(pm.AiModule.ModelName);
                            if (singleInput.Length > maxLen)
                                singleInput = InputAdapter.TruncateAtWord(singleInput, maxLen);

                            var context = new AiExecutionContext
                            {
                                ModuleType = pm.AiModule.ModuleType,
                                ModelName = pm.AiModule.ModelName,
                                ApiKey = apiKey,
                                Input = singleInput,
                                ProjectContext = project.Context,
                                Configuration = config,
                            };

                            var result = await provider.ExecuteAsync(context);

                            if (!result.Success)
                            {
                                imageError = $"Error en imagen {i + 1}/{inputs.Count}: {result.Error}";
                                await _logger.LogAsync(projectId, executionId, "error",
                                    imageError, pm.StepOrder, stepName);
                                break;
                            }

                            if (result.FileOutput is not null)
                            {
                                var stepDir = Path.Combine(workspacePath, $"step_{pm.StepOrder}");
                                Directory.CreateDirectory(stepDir);

                                var ext = GetExtension(result.ContentType ?? "application/octet-stream");
                                var fileName = inputs.Count > 1 ? $"output_{i + 1}{ext}" : $"output{ext}";
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

                                outputFiles.Add(new OutputFile
                                {
                                    FileId = execFile.Id,
                                    FileName = fileName,
                                    ContentType = execFile.ContentType,
                                    FileSize = execFile.FileSize,
                                    RevisedPrompt = result.Metadata.TryGetValue("revisedPrompt", out var rp) ? rp?.ToString() : null
                                });
                            }

                            // Store last result for downstream steps
                            stepResults[pm.StepOrder] = result;
                        }

                        stepModuleTypes[pm.StepOrder] = pm.AiModule.ModuleType;

                        // Always save partial results so successfully generated images are visible
                        var imageOutput = OutputSchemaHelper.BuildImageOutput(outputFiles, pm.AiModule.ModelName);
                        stepOutputs[pm.StepOrder] = imageOutput;
                        stepExecution.OutputData = JsonSerializer.Serialize(imageOutput);
                        await db.SaveChangesAsync();

                        if (imageError is not null)
                        {
                            await _logger.LogAsync(projectId, executionId, "warning",
                                $"{outputFiles.Count} imagen(es) generada(s) antes del error",
                                pm.StepOrder, stepName);
                            await FailStep(stepExecution, execution, imageError, db);
                            return execution;
                        }

                        await _logger.LogAsync(projectId, executionId, "success",
                            $"{outputFiles.Count} imagen(es) generada(s) correctamente",
                            pm.StepOrder, stepName);
                    }
                    else
                    {
                        // Generic fallback for other module types (Audio, Transcription, etc.)
                        var context = new AiExecutionContext
                        {
                            ModuleType = pm.AiModule.ModuleType,
                            ModelName = pm.AiModule.ModelName,
                            ApiKey = apiKey,
                            Input = inputs[0],
                            ProjectContext = project.Context,
                            Configuration = config,
                        };

                        var result = await provider.ExecuteAsync(context);
                        stepResults[pm.StepOrder] = result;
                        stepModuleTypes[pm.StepOrder] = pm.AiModule.ModuleType;

                        if (!result.Success)
                        {
                            await FailStep(stepExecution, execution, result.Error!, db);
                            return execution;
                        }

                        // Legacy serialization for unsupported types
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
                            await File.WriteAllBytesAsync(Path.Combine(stepDir, fileName), result.FileOutput);

                            db.ExecutionFiles.Add(new ExecutionFile
                            {
                                Id = Guid.NewGuid(),
                                StepExecutionId = stepExecution.Id,
                                FileName = fileName,
                                ContentType = result.ContentType ?? "application/octet-stream",
                                FilePath = Path.Combine($"step_{pm.StepOrder}", fileName),
                                Direction = "Output",
                                FileSize = result.FileOutput.Length,
                                CreatedAt = DateTime.UtcNow,
                            });
                        }
                    }

                    stepExecution.Status = "Completed";
                    stepExecution.CompletedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                }
                catch (OperationCanceledException)
                {
                    await _logger.LogAsync(projectId, executionId, "warning",
                        "Pipeline cancelado por el usuario", pm.StepOrder, pm.StepName ?? pm.AiModule.Name);

                    stepExecution.Status = "Cancelled";
                    stepExecution.CompletedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();

                    execution.Status = "Cancelled";
                    execution.CompletedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                    return execution;
                }
                catch (Exception ex)
                {
                    await _logger.LogAsync(projectId, executionId, "error",
                        $"Error inesperado: {ex.Message}", pm.StepOrder, pm.StepName ?? pm.AiModule.Name);

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

            await _logger.LogAsync(projectId, executionId, "success", "Pipeline completado correctamente");

            return execution;
        }

        /// <summary>
        /// Resolve inputs for a step. If the previous step produced multiple items
        /// and this step consumes from it, return all items as separate inputs.
        /// </summary>
        private static List<string> ResolveInputs(
            ProjectModule pm, string? userInput,
            Dictionary<int, AiResult> stepResults,
            Dictionary<int, StepOutput> stepOutputs,
            string targetModuleType, string targetModelName)
        {
            if (pm.InputMapping is null)
            {
                var raw = userInput ?? throw new InvalidOperationException(
                    $"Paso {pm.StepOrder}: No hay input de usuario y no se definio InputMapping");
                return [raw];
            }

            var mapping = JsonSerializer.Deserialize<JsonElement>(pm.InputMapping);
            var source = mapping.GetProperty("source").GetString();

            switch (source)
            {
                case "user":
                    return [userInput ?? throw new InvalidOperationException(
                        $"Paso {pm.StepOrder}: InputMapping requiere input de usuario pero no se proporciono")];

                case "previous":
                {
                    var prevOrder = stepOutputs.Keys.Where(k => k < pm.StepOrder)
                        .OrderByDescending(k => k).FirstOrDefault();

                    // Check if previous step has structured items
                    if (stepOutputs.TryGetValue(prevOrder, out var prevOutput) && prevOutput.Items.Count > 0)
                    {
                        return prevOutput.Items.Select(item => InputAdapter.SanitizePlainText(item.Content)).ToList();
                    }

                    // Fallback to raw text
                    if (stepResults.TryGetValue(prevOrder, out var prevResult))
                        return [InputAdapter.SanitizePlainText(prevResult.TextOutput ?? "")];

                    throw new InvalidOperationException($"Paso {pm.StepOrder}: No hay paso anterior con resultado");
                }

                case "step":
                {
                    var targetStep = mapping.GetProperty("stepOrder").GetInt32();

                    if (stepOutputs.TryGetValue(targetStep, out var targetOutput) && targetOutput.Items.Count > 0)
                    {
                        return targetOutput.Items.Select(item => InputAdapter.SanitizePlainText(item.Content)).ToList();
                    }

                    if (stepResults.TryGetValue(targetStep, out var targetResult))
                        return [InputAdapter.SanitizePlainText(targetResult.TextOutput ?? "")];

                    throw new InvalidOperationException($"Paso {pm.StepOrder}: Paso {targetStep} no tiene resultado disponible");
                }

                default:
                    return [userInput ?? ""];
            }
        }

        private static async Task FailStep(StepExecution step, ProjectExecution execution, string error, UserDbContext db)
        {
            step.Status = "Failed";
            step.ErrorMessage = error;
            step.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            execution.Status = "Failed";
            execution.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
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

        public async Task<ProjectExecution> RetryFromStepAsync(
            Guid executionId, int fromStepOrder, string? comment, UserDbContext db, CancellationToken ct = default)
        {
            var execution = await db.ProjectExecutions
                .Include(e => e.StepExecutions.OrderBy(s => s.StepOrder))
                    .ThenInclude(s => s.Files)
                .Include(e => e.StepExecutions)
                    .ThenInclude(s => s.ProjectModule)
                        .ThenInclude(pm => pm.AiModule)
                            .ThenInclude(m => m.ApiKey)
                .FirstOrDefaultAsync(e => e.Id == executionId)
                ?? throw new InvalidOperationException("Ejecucion no encontrada");

            var project = await db.Projects
                .Include(p => p.ProjectModules.Where(pm => pm.IsActive).OrderBy(pm => pm.StepOrder))
                    .ThenInclude(pm => pm.AiModule)
                        .ThenInclude(m => m.ApiKey)
                .FirstOrDefaultAsync(p => p.Id == execution.ProjectId)
                ?? throw new InvalidOperationException("Proyecto no encontrado");

            var workspacePath = execution.WorkspacePath;

            // ── Pre-flight: validate API keys for steps that will re-execute ──
            var retryModules = project.ProjectModules.Where(pm => pm.StepOrder >= fromStepOrder).ToList();
            var validatedKeys = new HashSet<string>();
            var errors = new List<string>();

            foreach (var pm in retryModules)
            {
                var stepName = pm.StepName ?? pm.AiModule.Name;

                if (pm.AiModule.ApiKey is null || string.IsNullOrEmpty(pm.AiModule.ApiKey.EncryptedKey))
                {
                    errors.Add($"Paso {pm.StepOrder} ({stepName}): No tiene API Key configurada");
                    continue;
                }

                var provider = _registry.GetProvider(pm.AiModule.ProviderType);
                if (provider is null)
                {
                    errors.Add($"Paso {pm.StepOrder} ({stepName}): Proveedor '{pm.AiModule.ProviderType}' no disponible");
                    continue;
                }

                var keySignature = $"{pm.AiModule.ProviderType}::{pm.AiModule.ApiKey.Id}";
                if (!validatedKeys.Add(keySignature))
                    continue;

                var (valid, error) = await provider.ValidateKeyAsync(pm.AiModule.ApiKey.EncryptedKey);
                if (!valid)
                    errors.Add($"Paso {pm.StepOrder} ({stepName}): {error}");
            }

            if (errors.Count > 0)
                throw new InvalidOperationException(
                    "Validacion pre-ejecucion fallida:\n" + string.Join("\n", errors));

            // Collect previous outputs and inputs for context
            var stepResults = new Dictionary<int, AiResult>();
            var stepOutputs = new Dictionary<int, StepOutput>();
            var stepModuleTypes = new Dictionary<int, string>();
            var previousOutputsByStep = new Dictionary<int, string?>();
            var previousInputsByStep = new Dictionary<int, string?>();

            foreach (var oldStep in execution.StepExecutions.OrderBy(s => s.StepOrder))
            {
                previousOutputsByStep[oldStep.StepOrder] = oldStep.OutputData;
                previousInputsByStep[oldStep.StepOrder] = oldStep.InputData;

                if (oldStep.StepOrder < fromStepOrder)
                {
                    // Rebuild StepOutput for downstream steps
                    stepModuleTypes[oldStep.StepOrder] = oldStep.ProjectModule.AiModule.ModuleType;
                    if (oldStep.OutputData is not null)
                    {
                        try
                        {
                            var parsed = JsonSerializer.Deserialize<StepOutput>(oldStep.OutputData,
                                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (parsed is not null)
                                stepOutputs[oldStep.StepOrder] = parsed;
                        }
                        catch { }
                    }
                    if (oldStep.ProjectModule.AiModule.ModuleType == "Text" && oldStep.OutputData is not null)
                    {
                        stepResults[oldStep.StepOrder] = AiResult.Ok(oldStep.OutputData);
                    }
                }
            }

            // Delete old step executions from retry point onwards
            var stepsToRemove = execution.StepExecutions
                .Where(s => s.StepOrder >= fromStepOrder).ToList();

            foreach (var oldStep in stepsToRemove)
            {
                var stepDir = Path.Combine(workspacePath, $"step_{oldStep.StepOrder}");
                if (Directory.Exists(stepDir))
                    Directory.Delete(stepDir, recursive: true);

                db.ExecutionFiles.RemoveRange(oldStep.Files);
                db.StepExecutions.Remove(oldStep);
            }

            execution.Status = "Running";
            execution.CompletedAt = null;
            await db.SaveChangesAsync();

            var projectId = execution.ProjectId;
            await _logger.LogAsync(projectId, executionId, "info",
                $"Reintentando desde paso {fromStepOrder}" +
                (comment is not null ? $" con feedback: \"{comment}\"" : ""));

            // Recover the original user input
            string? originalUserInput = null;
            if (previousInputsByStep.TryGetValue(1, out var step1Input) && step1Input is not null)
            {
                try
                {
                    var inputDoc = JsonSerializer.Deserialize<JsonElement>(step1Input);
                    if (inputDoc.TryGetProperty("prompt", out var promptEl))
                        originalUserInput = promptEl.GetString();
                }
                catch { }
            }

            // Re-execute from retry step onwards
            var retryModulesList = project.ProjectModules
                .Where(pm => pm.StepOrder >= fromStepOrder).ToList();

            for (var mi = 0; mi < retryModulesList.Count; mi++)
            {
                var pm = retryModulesList[mi];
                var nextModule = mi + 1 < retryModulesList.Count ? retryModulesList[mi + 1] : null;

                if (ct.IsCancellationRequested)
                {
                    await _logger.LogAsync(projectId, executionId, "warning", "Pipeline cancelado por el usuario");
                    execution.Status = "Cancelled";
                    execution.CompletedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                    return execution;
                }

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
                    var stepName = pm.StepName ?? pm.AiModule.Name;
                    await _logger.LogAsync(projectId, executionId, "info",
                        $"Ejecutando paso {pm.StepOrder}: {stepName} ({pm.AiModule.ProviderType}/{pm.AiModule.ModelName})",
                        pm.StepOrder, stepName);

                    var apiKey = pm.AiModule.ApiKey?.EncryptedKey
                        ?? throw new InvalidOperationException($"Paso {pm.StepOrder}: ApiKey no configurada");

                    var provider = _registry.GetProvider(pm.AiModule.ProviderType)
                        ?? throw new InvalidOperationException($"Paso {pm.StepOrder}: Proveedor '{pm.AiModule.ProviderType}' no disponible");

                    var config = MergeConfiguration(pm.AiModule.Configuration, pm.Configuration);

                    // If this is a Text step and the next step consumes its output, inject prompt length rule
                    if (pm.AiModule.ModuleType == "Text" && nextModule is not null)
                    {
                        var nextMaxLen = InputAdapter.GetMaxPromptLength(nextModule.AiModule.ModelName);
                        var rule = $"\n\nREGLA DE LONGITUD: El siguiente paso usa el modelo {nextModule.AiModule.ModelName} ({nextModule.AiModule.ModuleType}) que acepta maximo {nextMaxLen} caracteres por prompt. Cada elemento en \"items\" DEBE tener un \"content\" de maximo {nextMaxLen} caracteres.";
                        if (config.TryGetValue("systemPrompt", out var existing) && existing is string s)
                            config["systemPrompt"] = s + rule;
                        else
                            config["systemPrompt"] = rule;
                    }

                    var inputs = ResolveInputs(pm, originalUserInput, stepResults, stepOutputs,
                        pm.AiModule.ModuleType, pm.AiModule.ModelName);

                    // For the retry step: enrich input with feedback + previous output
                    if (pm.StepOrder == fromStepOrder && comment is not null)
                    {
                        var prevOutput = previousOutputsByStep.GetValueOrDefault(pm.StepOrder);
                        inputs = EnrichInputsWithFeedback(inputs, comment, prevOutput, pm.AiModule.ModuleType);
                    }

                    stepExecution.InputData = inputs.Count == 1
                        ? JsonSerializer.Serialize(new { prompt = inputs[0] })
                        : JsonSerializer.Serialize(new { prompts = inputs, count = inputs.Count });

                    if (pm.AiModule.ModuleType == "Text")
                    {
                        await _logger.LogAsync(projectId, executionId, "info",
                            $"Enviando prompt al modelo de texto ({pm.AiModule.ModelName})...",
                            pm.StepOrder, stepName);
                        await _logger.LogAsync(projectId, executionId, "info",
                            $"Prompt: {inputs[0]}",
                            pm.StepOrder, stepName);

                        var context = new AiExecutionContext
                        {
                            ModuleType = pm.AiModule.ModuleType,
                            ModelName = pm.AiModule.ModelName,
                            ApiKey = apiKey,
                            Input = inputs[0],
                            ProjectContext = project.Context,
                            Configuration = config,
                        };

                        var result = await provider.ExecuteAsync(context);
                        stepResults[pm.StepOrder] = result;
                        stepModuleTypes[pm.StepOrder] = pm.AiModule.ModuleType;

                        if (!result.Success)
                        {
                            await _logger.LogAsync(projectId, executionId, "error",
                                $"Error en texto: {result.Error}", pm.StepOrder, stepName);
                            await FailStep(stepExecution, execution, result.Error!, db);
                            return execution;
                        }

                        var stepOutput = OutputSchemaHelper.ParseTextOutput(result.TextOutput ?? "", result.Metadata);
                        stepOutputs[pm.StepOrder] = stepOutput;

                        var stepDir = Path.Combine(workspacePath, $"step_{pm.StepOrder}");
                        Directory.CreateDirectory(stepDir);
                        await File.WriteAllTextAsync(Path.Combine(stepDir, "output.json"), result.TextOutput ?? "");

                        var execFile = new ExecutionFile
                        {
                            Id = Guid.NewGuid(),
                            StepExecutionId = stepExecution.Id,
                            FileName = "output.json",
                            ContentType = "application/json",
                            FilePath = Path.Combine($"step_{pm.StepOrder}", "output.json"),
                            Direction = "Output",
                            FileSize = System.Text.Encoding.UTF8.GetByteCount(result.TextOutput ?? ""),
                            CreatedAt = DateTime.UtcNow,
                        };
                        db.ExecutionFiles.Add(execFile);

                        stepExecution.OutputData = JsonSerializer.Serialize(stepOutput);

                        await _logger.LogAsync(projectId, executionId, "success",
                            $"Texto regenerado correctamente", pm.StepOrder, stepName);
                    }
                    else if (pm.AiModule.ModuleType == "Image")
                    {
                        var outputFiles = new List<OutputFile>();
                        string? imageError = null;

                        for (var i = 0; i < inputs.Count; i++)
                        {
                            await _logger.LogAsync(projectId, executionId, "info",
                                inputs.Count > 1
                                    ? $"Generando imagen {i + 1}/{inputs.Count}..."
                                    : $"Generando imagen...",
                                pm.StepOrder, stepName);
                            await _logger.LogAsync(projectId, executionId, "info",
                                $"Prompt: {inputs[i]}",
                                pm.StepOrder, stepName);

                            var singleInput = inputs[i];
                            var maxLen = InputAdapter.GetMaxPromptLength(pm.AiModule.ModelName);
                            if (singleInput.Length > maxLen)
                                singleInput = InputAdapter.TruncateAtWord(singleInput, maxLen);

                            var context = new AiExecutionContext
                            {
                                ModuleType = pm.AiModule.ModuleType,
                                ModelName = pm.AiModule.ModelName,
                                ApiKey = apiKey,
                                Input = singleInput,
                                ProjectContext = project.Context,
                                Configuration = config,
                            };

                            var result = await provider.ExecuteAsync(context);

                            if (!result.Success)
                            {
                                imageError = $"Error en imagen {i + 1}/{inputs.Count}: {result.Error}";
                                await _logger.LogAsync(projectId, executionId, "error",
                                    imageError, pm.StepOrder, stepName);
                                break;
                            }

                            if (result.FileOutput is not null)
                            {
                                var stepDir = Path.Combine(workspacePath, $"step_{pm.StepOrder}");
                                Directory.CreateDirectory(stepDir);

                                var ext = GetExtension(result.ContentType ?? "application/octet-stream");
                                var fileName = inputs.Count > 1 ? $"output_{i + 1}{ext}" : $"output{ext}";
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

                                outputFiles.Add(new OutputFile
                                {
                                    FileId = execFile.Id,
                                    FileName = fileName,
                                    ContentType = execFile.ContentType,
                                    FileSize = execFile.FileSize,
                                    RevisedPrompt = result.Metadata.TryGetValue("revisedPrompt", out var rp) ? rp?.ToString() : null
                                });
                            }

                            stepResults[pm.StepOrder] = result;
                        }

                        stepModuleTypes[pm.StepOrder] = pm.AiModule.ModuleType;

                        // Always save partial results so successfully generated images are visible
                        var imageOutput = OutputSchemaHelper.BuildImageOutput(outputFiles, pm.AiModule.ModelName);
                        stepOutputs[pm.StepOrder] = imageOutput;
                        stepExecution.OutputData = JsonSerializer.Serialize(imageOutput);
                        await db.SaveChangesAsync();

                        if (imageError is not null)
                        {
                            await _logger.LogAsync(projectId, executionId, "warning",
                                $"{outputFiles.Count} imagen(es) generada(s) antes del error",
                                pm.StepOrder, stepName);
                            await FailStep(stepExecution, execution, imageError, db);
                            return execution;
                        }

                        await _logger.LogAsync(projectId, executionId, "success",
                            $"{outputFiles.Count} imagen(es) regenerada(s) correctamente",
                            pm.StepOrder, stepName);
                    }
                    else
                    {
                        var context = new AiExecutionContext
                        {
                            ModuleType = pm.AiModule.ModuleType,
                            ModelName = pm.AiModule.ModelName,
                            ApiKey = apiKey,
                            Input = inputs[0],
                            ProjectContext = project.Context,
                            Configuration = config,
                        };

                        var result = await provider.ExecuteAsync(context);
                        stepResults[pm.StepOrder] = result;
                        stepModuleTypes[pm.StepOrder] = pm.AiModule.ModuleType;

                        if (!result.Success)
                        {
                            await FailStep(stepExecution, execution, result.Error!, db);
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
                            await File.WriteAllBytesAsync(Path.Combine(stepDir, fileName), result.FileOutput);

                            db.ExecutionFiles.Add(new ExecutionFile
                            {
                                Id = Guid.NewGuid(),
                                StepExecutionId = stepExecution.Id,
                                FileName = fileName,
                                ContentType = result.ContentType ?? "application/octet-stream",
                                FilePath = Path.Combine($"step_{pm.StepOrder}", fileName),
                                Direction = "Output",
                                FileSize = result.FileOutput.Length,
                                CreatedAt = DateTime.UtcNow,
                            });
                        }
                    }

                    stepExecution.Status = "Completed";
                    stepExecution.CompletedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                }
                catch (OperationCanceledException)
                {
                    await _logger.LogAsync(projectId, executionId, "warning",
                        "Pipeline cancelado por el usuario", pm.StepOrder, pm.StepName ?? pm.AiModule.Name);

                    stepExecution.Status = "Cancelled";
                    stepExecution.CompletedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();

                    execution.Status = "Cancelled";
                    execution.CompletedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                    return execution;
                }
                catch (Exception ex)
                {
                    await _logger.LogAsync(projectId, executionId, "error",
                        $"Error inesperado: {ex.Message}", pm.StepOrder, pm.StepName ?? pm.AiModule.Name);

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

            await _logger.LogAsync(projectId, executionId, "success", "Pipeline reintentado correctamente");

            return execution;
        }

        /// <summary>
        /// Enrich inputs with user feedback and previous output context.
        /// Text: includes previous output + feedback instruction.
        /// Image: appends feedback to the prompt.
        /// </summary>
        private static List<string> EnrichInputsWithFeedback(
            List<string> originalInputs, string comment, string? previousOutputData, string moduleType)
        {
            if (moduleType == "Text")
            {
                string? previousContent = null;
                if (previousOutputData is not null)
                {
                    try
                    {
                        var prevOutput = JsonSerializer.Deserialize<StepOutput>(previousOutputData,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        previousContent = prevOutput?.Content;
                    }
                    catch { previousContent = previousOutputData; }
                }

                string enriched;
                if (previousContent is not null)
                {
                    enriched = $"[Tu respuesta anterior fue]:\n{previousContent}\n\n" +
                               $"[Feedback del usuario]: {comment}\n\n" +
                               $"Genera una nueva respuesta incorporando el feedback. Manten el mismo formato de salida JSON.";
                }
                else
                {
                    enriched = $"{originalInputs[0]}\n\n[Feedback adicional del usuario]: {comment}";
                }

                return [enriched];
            }
            else
            {
                // Image / other: append feedback to each prompt
                return originalInputs
                    .Select(input => $"{input}\n\nAjustes solicitados: {comment}")
                    .ToList();
            }
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
