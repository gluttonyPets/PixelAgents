using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Server.Data;
using Server.Hubs;
using Server.Models;
using Server.Services.WhatsApp;
using Server.Services.Telegram;
using Server.Services.Instagram;

namespace Server.Services.Ai
{
    public class PipelineExecutor : IPipelineExecutor
    {
        private readonly IAiProviderRegistry _registry;
        private IExecutionLogger _logger;
        private readonly IExecutionLogger _baseLogger;
        private readonly WhatsAppService _whatsApp;
        private readonly TelegramService _telegram;
        private readonly BufferService _buffer;
        private readonly CoreDbContext _coreDb;
        private readonly IConfiguration _configuration;
        private readonly string _mediaRoot;

        public PipelineExecutor(IAiProviderRegistry registry, IExecutionLogger logger,
            WhatsAppService whatsApp, TelegramService telegram, BufferService buffer,
            CoreDbContext coreDb, IConfiguration configuration, IWebHostEnvironment env)
        {
            _registry = registry;
            _baseLogger = logger;
            _logger = logger;
            _mediaRoot = Path.Combine(env.ContentRootPath, "GeneratedMedia");
            _whatsApp = whatsApp;
            _telegram = telegram;
            _buffer = buffer;
            _coreDb = coreDb;
            _configuration = configuration;
        }

        private static bool IsInteractionStep(AiModule module) =>
            module.ModuleType == "Interaction" ||
            (module.ProviderType == "System" && (module.ModelName == "whatsapp" || module.ModelName == "telegram"));

        private static bool IsPublishStep(AiModule module) =>
            module.ModuleType == "Publish" ||
            (module.ProviderType == "System" && module.ModelName == "instagram");

        public async Task<ProjectExecution> ExecuteAsync(
            Guid projectId, string? userInput, UserDbContext db, string tenantDbName, CancellationToken ct = default)
        {
            _logger = _baseLogger.WithDb(db);

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

                // Interaction and Publish steps don't need API key validation
                // Also skip modules without ApiKeyId (e.g. system modules) — they have their own validation
                if (IsInteractionStep(pm.AiModule) || IsPublishStep(pm.AiModule) || pm.AiModule.ApiKeyId is null)
                    continue;

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
            var workspacePath = Path.Combine(_mediaRoot, tenantDbName, projectId.ToString(), executionId.ToString());

            var execution = new ProjectExecution
            {
                Id = executionId,
                ProjectId = projectId,
                Status = "Running",
                WorkspacePath = workspacePath,
                CreatedAt = DateTime.UtcNow,
                UserInput = userInput,
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

                    // ── Interaction step: send message, optionally pause pipeline ──
                    if (IsInteractionStep(pm.AiModule))
                    {
                        var shouldPause = await HandleInteractionStepAsync(
                            project, execution, stepExecution, pm, userInput,
                            stepResults, stepOutputs, stepModuleTypes, db, tenantDbName);
                        if (shouldPause) return execution;
                        continue; // fire-and-forget: move to next step
                    }

                    // ── Publish step: publish content via Buffer ──
                    if (IsPublishStep(pm.AiModule))
                    {
                        await HandlePublishStepAsync(
                            project, execution, stepExecution, pm,
                            stepResults, stepOutputs, stepModuleTypes, db, tenantDbName);
                        continue;
                    }

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

                    // Inject image count rule if configured
                    if (pm.AiModule.ModuleType == "Text")
                        InjectImageCountRule(config);

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
                        stepExecution.EstimatedCost += result.EstimatedCost;

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
                            stepExecution.EstimatedCost += result.EstimatedCost;

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
                    else if (pm.AiModule.ModuleType == "Video")
                    {
                        // Video modules: prompt always from step config (set by user), never from previous step
                        // Debug: log raw config from DB
                        await _logger.LogAsync(projectId, executionId, "info",
                            $"[Video Debug] moduleConfig={pm.AiModule.Configuration ?? "null"}, stepConfig={pm.Configuration ?? "null"}",
                            pm.StepOrder, stepName);
                        await _logger.LogAsync(projectId, executionId, "info",
                            $"[Video Debug] merged config keys=[{string.Join(", ", config.Keys)}], values=[{string.Join(", ", config.Select(kv => $"{kv.Key}={kv.Value}"))}]",
                            pm.StepOrder, stepName);

                        // Read prompt from step configuration (mandatory, set in UI)
                        // Values come as JsonElement from MergeConfiguration
                        var videoPrompt = "";
                        if (config.TryGetValue("videoPrompt", out var vp))
                            videoPrompt = vp is JsonElement vpEl ? vpEl.GetString() ?? "" : vp?.ToString() ?? "";

                        await _logger.LogAsync(projectId, executionId, "info",
                            $"[Video Debug] videoPrompt extracted='{videoPrompt}', vp type={vp?.GetType().Name ?? "null"}, vp value={vp}",
                            pm.StepOrder, stepName);

                        if (string.IsNullOrWhiteSpace(videoPrompt))
                        {
                            await _logger.LogAsync(projectId, executionId, "error",
                                $"El paso de video no tiene prompt configurado. Config keys: [{string.Join(", ", config.Keys)}]",
                                pm.StepOrder, stepName);
                            await FailStep(stepExecution, execution, "Prompt de video no configurado", db);
                            return execution;
                        }

                        var videoSource = "prompt";
                        if (config.TryGetValue("videoSource", out var vs))
                            videoSource = vs is JsonElement vsEl ? vsEl.GetString() ?? "prompt" : vs?.ToString() ?? "prompt";

                        await _logger.LogAsync(projectId, executionId, "info",
                            $"Video config: videoSource={videoSource}, prompt={videoPrompt[..Math.Min(videoPrompt.Length, 100)]}..., model={pm.AiModule.ModelName}",
                            pm.StepOrder, stepName);

                        var videoContext = new AiExecutionContext
                        {
                            ModuleType = pm.AiModule.ModuleType,
                            ModelName = pm.AiModule.ModelName,
                            ApiKey = apiKey,
                            Input = videoPrompt,
                            ProjectContext = project.Context,
                            Configuration = config,
                        };

                        // Load image from previous step if videoSource is "both"
                        if (videoSource == "both")
                        {
                            var prevOrder = stepOutputs.Keys.Where(k => k < pm.StepOrder)
                                .OrderByDescending(k => k).FirstOrDefault();

                            await _logger.LogAsync(projectId, executionId, "info",
                                $"Buscando imagen en paso anterior (orden {prevOrder}). StepOutputs keys: [{string.Join(", ", stepOutputs.Keys)}]",
                                pm.StepOrder, stepName);

                            if (stepOutputs.TryGetValue(prevOrder, out var prevOutput))
                            {
                                await _logger.LogAsync(projectId, executionId, "info",
                                    $"Paso {prevOrder}: tipo={prevOutput.Type}, files={prevOutput.Files.Count}" +
                                    (prevOutput.Files.Count > 0 ? $", file0={prevOutput.Files[0].FileName} ({prevOutput.Files[0].ContentType}, {prevOutput.Files[0].FileSize} bytes)" : ""),
                                    pm.StepOrder, stepName);

                                if (prevOutput.Files.Count > 0
                                    && prevOutput.Files[0].ContentType.StartsWith("image/"))
                                {
                                    var prevFile = prevOutput.Files[0];
                                    var prevFilePath = Path.Combine(workspacePath, $"step_{prevOrder}", prevFile.FileName);

                                    await _logger.LogAsync(projectId, executionId, "info",
                                        $"Cargando imagen: {prevFilePath} (exists={File.Exists(prevFilePath)})",
                                        pm.StepOrder, stepName);

                                    if (File.Exists(prevFilePath))
                                    {
                                        var imgBytes = await File.ReadAllBytesAsync(prevFilePath);
                                        videoContext.InputFiles = new List<byte[]> { imgBytes };
                                        await _logger.LogAsync(projectId, executionId, "info",
                                            $"Imagen cargada: {imgBytes.Length} bytes, se pasara a Veo como entrada",
                                            pm.StepOrder, stepName);
                                    }
                                }
                            }
                            else
                            {
                                await _logger.LogAsync(projectId, executionId, "warning",
                                    $"No se encontro output del paso {prevOrder}. Se generara video solo con prompt.",
                                    pm.StepOrder, stepName);
                            }
                        }

                        await _logger.LogAsync(projectId, executionId, "info",
                            $"[Video Debug] Llamando a Veo: Input='{videoContext.Input[..Math.Min(videoContext.Input.Length, 100)]}', InputFiles={videoContext.InputFiles?.Count ?? 0}, Model={videoContext.ModelName}",
                            pm.StepOrder, stepName);

                        var result = await provider.ExecuteAsync(videoContext);
                        stepResults[pm.StepOrder] = result;
                        stepModuleTypes[pm.StepOrder] = pm.AiModule.ModuleType;
                        stepExecution.EstimatedCost += result.EstimatedCost;

                        if (!result.Success)
                        {
                            await _logger.LogAsync(projectId, executionId, "error",
                                $"Error en video: {result.Error}", pm.StepOrder, stepName);
                            await FailStep(stepExecution, execution, result.Error!, db);
                            return execution;
                        }

                        var outputFiles = new List<OutputFile>();

                        if (result.FileOutput is not null)
                        {
                            var stepDir = Path.Combine(workspacePath, $"step_{pm.StepOrder}");
                            Directory.CreateDirectory(stepDir);

                            var ext = GetExtension(result.ContentType ?? "video/mp4");
                            var fileName = $"output{ext}";
                            var filePath = Path.Combine(stepDir, fileName);
                            await File.WriteAllBytesAsync(filePath, result.FileOutput);

                            var execFile = new ExecutionFile
                            {
                                Id = Guid.NewGuid(),
                                StepExecutionId = stepExecution.Id,
                                FileName = fileName,
                                ContentType = result.ContentType ?? "video/mp4",
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
                                FileSize = execFile.FileSize
                            });
                        }

                        var videoOutput = OutputSchemaHelper.BuildVideoOutput(outputFiles, pm.AiModule.ModelName, result.Metadata);
                        stepOutputs[pm.StepOrder] = videoOutput;
                        stepExecution.OutputData = JsonSerializer.Serialize(videoOutput);
                        await db.SaveChangesAsync();

                        await _logger.LogAsync(projectId, executionId, "success",
                            $"Video generado correctamente ({result.Metadata.GetValueOrDefault("duration", "?")}s)",
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
                        stepExecution.EstimatedCost += result.EstimatedCost;

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
            execution.TotalEstimatedCost = await db.StepExecutions
                .Where(s => s.ExecutionId == execution.Id)
                .SumAsync(s => s.EstimatedCost);
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

        // ── Publish step handler (via Buffer) ──
        private async Task HandlePublishStepAsync(
            Project project, ProjectExecution execution, StepExecution stepExecution,
            ProjectModule pm,
            Dictionary<int, AiResult> stepResults,
            Dictionary<int, StepOutput> stepOutputs,
            Dictionary<int, string> stepModuleTypes,
            UserDbContext db,
            string? tenantDbName = null)
        {
            var stepName = pm.StepName ?? pm.AiModule.Name;
            var projectId = project.Id;
            var executionId = execution.Id;

            if (string.IsNullOrWhiteSpace(project.InstagramConfig))
                throw new InvalidOperationException($"Paso {pm.StepOrder}: El proyecto no tiene configuracion de Buffer");

            var bufferConfig = JsonSerializer.Deserialize<BufferConfig>(project.InstagramConfig)
                ?? throw new InvalidOperationException($"Paso {pm.StepOrder}: Configuracion de Buffer invalida");

            if (string.IsNullOrWhiteSpace(bufferConfig.ApiKey))
                throw new InvalidOperationException($"Paso {pm.StepOrder}: API Key de Buffer no configurada");

            // Read publish config from step configuration
            var stepConfig = MergeConfiguration(pm.AiModule.Configuration, pm.Configuration);

            // Build caption from template + previous output
            var captionTemplate = "{previous_output}";
            if (stepConfig.TryGetValue("caption", out var capVal))
            {
                var cap = capVal is JsonElement capEl ? capEl.GetString() : capVal?.ToString();
                if (!string.IsNullOrWhiteSpace(cap))
                    captionTemplate = cap;
            }

            // For caption, skip Interaction steps (their output is just the user's response, e.g. "continue")
            var previousText = "";
            string? previousTitle = null;
            var candidateOrders = stepOutputs.Keys
                .Where(k => k < pm.StepOrder)
                .OrderByDescending(k => k)
                .ToList();

            foreach (var co in candidateOrders)
            {
                if (stepModuleTypes.TryGetValue(co, out var prevModType) &&
                    prevModType == "Interaction")
                    continue;

                if (stepOutputs.TryGetValue(co, out var prevOutput))
                {
                    previousText = prevOutput.Content ?? string.Join("\n", prevOutput.Items.Select(i => i.Content));
                    if (!string.IsNullOrWhiteSpace(prevOutput.Title))
                        previousTitle ??= prevOutput.Title;
                    break;
                }
                if (stepResults.TryGetValue(co, out var prevResult))
                {
                    previousText = prevResult.TextOutput ?? "";
                    break;
                }
            }

            // If no title found yet, search all previous non-Interaction steps for a title
            if (previousTitle is null)
            {
                foreach (var co in candidateOrders)
                {
                    if (stepModuleTypes.TryGetValue(co, out var mt) && mt == "Interaction")
                        continue;
                    if (stepOutputs.TryGetValue(co, out var so) && !string.IsNullOrWhiteSpace(so.Title))
                    {
                        previousTitle = so.Title;
                        break;
                    }
                }
            }

            // Fallback: if all previous steps were Interaction, use the most recent one
            if (string.IsNullOrEmpty(previousText) && candidateOrders.Count > 0)
            {
                var fallbackOrder = candidateOrders[0];
                if (stepOutputs.TryGetValue(fallbackOrder, out var fbOutput))
                    previousText = fbOutput.Content ?? string.Join("\n", fbOutput.Items.Select(i => i.Content));
                else if (stepResults.TryGetValue(fallbackOrder, out var fbResult))
                    previousText = fbResult.TextOutput ?? "";
            }

            // Use the AI-generated title as caption if available, otherwise fall back to template
            var caption = !string.IsNullOrWhiteSpace(previousTitle) && captionTemplate == "{previous_output}"
                ? previousTitle
                : captionTemplate
                    .Replace("{previous_output}", previousText)
                    .Replace("{step_number}", pm.StepOrder.ToString());

            await _logger.LogAsync(projectId, executionId, "info",
                $"Publicando via Buffer...", pm.StepOrder, stepName);

            // Collect and classify media from the nearest non-Interaction previous step
            var classifiedMedia = new List<ClassifiedMedia>();
            var mediaStepOrder = candidateOrders.FirstOrDefault(co =>
                !stepModuleTypes.TryGetValue(co, out var mt) || mt != "Interaction");
            if (mediaStepOrder == 0 && candidateOrders.Count > 0)
                mediaStepOrder = candidateOrders[0]; // fallback

            var prevStepExec = await db.StepExecutions
                .Include(s => s.Files)
                .FirstOrDefaultAsync(s => s.ExecutionId == executionId && s.StepOrder == mediaStepOrder);

            if (prevStepExec?.Files is not null)
            {
                var serverBaseUrl = (_configuration["BaseUrl"]
                    ?? _configuration["AllowedOrigin"]
                    ?? "").TrimEnd('/');

                // Warn if the URL is not publicly accessible
                var isLocal = string.IsNullOrEmpty(serverBaseUrl)
                    || serverBaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase)
                    || serverBaseUrl.Contains("127.0.0.1")
                    || serverBaseUrl.Contains("0.0.0.0");

                if (isLocal)
                {
                    await _logger.LogAsync(projectId, executionId, "warning",
                        $"La URL base del servidor ({(string.IsNullOrEmpty(serverBaseUrl) ? "(vacia)" : serverBaseUrl)}) " +
                        "no es accesible desde internet. Buffer no podra descargar las imagenes. " +
                        "Configura BaseUrl o AllowedOrigin con tu IP/dominio publico.",
                        pm.StepOrder, stepName);
                }

                if (tenantDbName is null)
                {
                    await _logger.LogAsync(projectId, executionId, "error",
                        "No se puede publicar con imagenes sin tenant. Buffer necesita una URL publica.",
                        pm.StepOrder, stepName);
                }

                foreach (var file in prevStepExec.Files.Where(f =>
                    f.ContentType.StartsWith("image/") || f.ContentType.StartsWith("video/")))
                {
                    var publicUrl = $"{serverBaseUrl}/api/public/files/{tenantDbName}/{executionId}/{file.Id}/{file.FileName}";
                    var kind = file.ContentType.StartsWith("video/") ? MediaKind.Video : MediaKind.Image;
                    classifiedMedia.Add(new ClassifiedMedia { Url = publicUrl, Kind = kind });
                }
            }

            var hasImages = classifiedMedia.Any(m => m.Kind == MediaKind.Image);
            var hasVideos = classifiedMedia.Any(m => m.Kind == MediaKind.Video);

            // Read publish type from step configuration (post/reel/story)
            var publishType = "post";
            if (stepConfig.TryGetValue("publishType", out var ptVal))
            {
                var pt = ptVal is JsonElement ptEl ? ptEl.GetString() : ptVal?.ToString();
                if (!string.IsNullOrWhiteSpace(pt))
                    publishType = pt;
            }

            // Auto-adjust publishType based on actual media type
            var originalPublishType = publishType;
            if (hasImages && !hasVideos && publishType == "reel")
            {
                publishType = "post";
                await _logger.LogAsync(projectId, executionId, "warning",
                    $"publishType cambiado de 'reel' a 'post' porque solo hay imagenes (reel requiere video)",
                    pm.StepOrder, stepName);
            }

            // Publish via Buffer
            BufferPublishResult bufferResult;
            try
            {
                bufferResult = await _buffer.PublishAsync(
                    bufferConfig, caption, classifiedMedia.Count > 0 ? classifiedMedia : null, publishType);
            }
            catch (Exception ex)
            {
                await _logger.LogAsync(projectId, executionId, "error",
                    $"Error publicando via Buffer: {ex.Message}", pm.StepOrder, stepName);
                await FailStep(stepExecution, execution, $"Error Buffer: {ex.Message}", db);
                throw;
            }

            // Build plain-text output with status and schedule
            string outputText;
            string scheduleLine = "";
            if (bufferResult.IsSuccess)
            {
                if (!string.IsNullOrEmpty(bufferResult.DueAt) &&
                    DateTime.TryParse(bufferResult.DueAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dueDate))
                {
                    scheduleLine = $"Programado para: {dueDate:dd/MM/yyyy HH:mm} UTC";
                }
                else
                {
                    scheduleLine = "Programado (horario pendiente de confirmacion)";
                }
                outputText = $"Publicacion exitosa\nPost ID: {bufferResult.PostId}\n{scheduleLine}";
            }
            else
            {
                outputText = $"Error en publicacion: {bufferResult.Error}";
            }

            var publishOutput = new StepOutput
            {
                Type = "text",
                Content = outputText,
                Summary = bufferResult.IsSuccess
                    ? $"Publicado via Buffer - {scheduleLine}"
                    : $"Error Buffer: {bufferResult.Error}",
                Items = [new OutputItem { Content = outputText, Label = "buffer publish" }],
                Metadata = new Dictionary<string, object>
                {
                    ["publishType"] = publishType,
                    ["caption"] = caption,
                    ["bufferPostId"] = bufferResult.PostId,
                    ["bufferDueAt"] = bufferResult.DueAt ?? "",
                    ["bufferRequest"] = bufferResult.RequestBody,
                    ["bufferResponse"] = bufferResult.ResponseBody,
                    ["bufferStatusCode"] = bufferResult.StatusCode
                }
            };

            stepExecution.OutputData = JsonSerializer.Serialize(publishOutput);
            stepExecution.InputData = JsonSerializer.Serialize(new
            {
                caption,
                mediaCount = classifiedMedia.Count,
                imageCount = classifiedMedia.Count(m => m.Kind == MediaKind.Image),
                videoCount = classifiedMedia.Count(m => m.Kind == MediaKind.Video),
                originalPublishType,
                effectivePublishType = publishType,
                bufferPostId = bufferResult.PostId
            });

            if (bufferResult.IsSuccess)
            {
                stepExecution.Status = "Completed";
                stepExecution.CompletedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();

                stepOutputs[pm.StepOrder] = publishOutput;
                stepModuleTypes[pm.StepOrder] = "Publish";
                stepResults[pm.StepOrder] = AiResult.Ok(outputText, new Dictionary<string, object>
                {
                    ["bufferPostId"] = bufferResult.PostId,
                    ["bufferDueAt"] = bufferResult.DueAt ?? "",
                    ["mediaCount"] = classifiedMedia.Count
                });

                await _logger.LogAsync(projectId, executionId, "success",
                    $"Publicado via Buffer (post {bufferResult.PostId}) - {scheduleLine}",
                    pm.StepOrder, stepName);
            }
            else
            {
                await _logger.LogAsync(projectId, executionId, "error",
                    $"Error publicando via Buffer: {bufferResult.Error}", pm.StepOrder, stepName);
                await FailStep(stepExecution, execution, $"Error Buffer: {bufferResult.Error}", db);
                throw new InvalidOperationException(bufferResult.Error);
            }
        }

        // ── Interaction step handler (supports Telegram and WhatsApp) ──
        // Returns true if the pipeline should pause (waiting for response), false if it should continue.
        private async Task<bool> HandleInteractionStepAsync(
            Project project, ProjectExecution execution, StepExecution stepExecution,
            ProjectModule pm, string? userInput,
            Dictionary<int, AiResult> stepResults,
            Dictionary<int, StepOutput> stepOutputs,
            Dictionary<int, string> stepModuleTypes,
            UserDbContext db, string tenantDbName)
        {
            var stepName = pm.StepName ?? pm.AiModule.Name;

            // 1. Determine messaging channel: prefer Telegram, fall back to WhatsApp
            // Check that config is not just an empty DTO (e.g. {"BotToken":"","ChatId":""})
            var useTelegram = false;
            if (!string.IsNullOrWhiteSpace(project.TelegramConfig))
            {
                var tgCheck = JsonSerializer.Deserialize<TelegramConfig>(project.TelegramConfig);
                useTelegram = tgCheck is not null && !string.IsNullOrWhiteSpace(tgCheck.BotToken);
            }
            var useWhatsApp = false;
            if (!useTelegram && !string.IsNullOrWhiteSpace(project.WhatsAppConfig))
            {
                var waCheck = JsonSerializer.Deserialize<WhatsAppConfig>(project.WhatsAppConfig);
                useWhatsApp = waCheck is not null && !string.IsNullOrWhiteSpace(waCheck.AccessToken);
            }

            if (!useTelegram && !useWhatsApp)
                throw new InvalidOperationException($"Paso {pm.StepOrder}: El proyecto no tiene configuracion de mensajeria (Telegram o WhatsApp)");

            TelegramConfig? tgConfig = null;
            WhatsAppConfig? waConfig = null;

            if (useTelegram)
                tgConfig = JsonSerializer.Deserialize<TelegramConfig>(project.TelegramConfig!)
                    ?? throw new InvalidOperationException($"Paso {pm.StepOrder}: Configuracion Telegram invalida");
            else
                waConfig = JsonSerializer.Deserialize<WhatsAppConfig>(project.WhatsAppConfig!)
                    ?? throw new InvalidOperationException($"Paso {pm.StepOrder}: Configuracion WhatsApp invalida");

            // 2. Build message from template + previous output
            var stepConfig = MergeConfiguration(pm.AiModule.Configuration, pm.Configuration);
            var messageTemplate = "{previous_output}";
            if (stepConfig.TryGetValue("messageTemplate", out var tmpl) && tmpl is JsonElement tmplEl)
                messageTemplate = tmplEl.GetString() ?? messageTemplate;
            else if (tmpl is string tmplStr)
                messageTemplate = tmplStr;

            var previousText = "";
            var prevOrder = stepOutputs.Keys.Where(k => k < pm.StepOrder).OrderByDescending(k => k).FirstOrDefault();
            if (stepOutputs.TryGetValue(prevOrder, out var prevOutput))
                previousText = prevOutput.Content ?? string.Join("\n", prevOutput.Items.Select(i => i.Content));
            else if (stepResults.TryGetValue(prevOrder, out var prevResult))
                previousText = prevResult.TextOutput ?? "";

            var message = messageTemplate
                .Replace("{previous_output}", previousText)
                .Replace("{step_number}", pm.StepOrder.ToString());

            var channelName = useTelegram ? "Telegram" : "WhatsApp";

            // 2b. Read interaction config: messageType, waitForResponse
            var messageType = "combined"; // default: text + images
            var waitForResponse = true;   // default: wait for user response

            if (stepConfig.TryGetValue("messageType", out var mtVal))
            {
                var mt = mtVal is JsonElement mtEl ? mtEl.GetString() : mtVal?.ToString();
                if (!string.IsNullOrWhiteSpace(mt))
                    messageType = mt;
            }
            if (stepConfig.TryGetValue("waitForResponse", out var wfrVal))
            {
                if (wfrVal is JsonElement wfrEl)
                    waitForResponse = wfrEl.ValueKind == JsonValueKind.True;
                else if (wfrVal is bool wfrBool)
                    waitForResponse = wfrBool;
            }

            // 3. Send messages based on messageType
            var sendText = messageType is "text" or "combined";
            var sendImages = messageType is "image" or "combined";

            if (sendText)
            {
                await _logger.LogAsync(project.Id, execution.Id, "info",
                    $"Enviando mensaje a {channelName}...", pm.StepOrder, stepName);

                if (useTelegram && waitForResponse)
                {
                    // Build pipeline control buttons
                    var allModules = project.ProjectModules.OrderBy(p => p.StepOrder).ToList();
                    var currentIdx = allModules.FindIndex(p => p.Id == pm.Id);
                    var nextPm = currentIdx + 1 < allModules.Count ? allModules[currentIdx + 1] : null;
                    var isLastStep = nextPm is null;

                    var nextStepName = nextPm?.StepName ?? nextPm?.AiModule.Name ?? "";
                    if (nextStepName.Length > 40)
                        nextStepName = nextStepName[..37] + "...";

                    var continueLabel = isLastStep
                        ? "✅ Finalizar"
                        : $"▶️ Continuar con: {nextStepName}";

                    var controlOptions = new List<(string Label, string CallbackData)>
                    {
                        (continueLabel, "continue"),
                        ("❌ Abortar", "abort"),
                        ("🔄 Reiniciar", "restart")
                    };

                    await _telegram.SendTextMessageWithOptionsAsync(tgConfig!, message, controlOptions);
                }
                else if (useTelegram)
                    await _telegram.SendTextMessageAsync(tgConfig!, message);
                else
                    await _whatsApp.SendTextMessageAsync(waConfig!, message);
            }

            // 4. Send images from previous step if configured
            if (sendImages)
            {
                var prevStepExec = await db.StepExecutions
                    .Include(s => s.Files)
                    .FirstOrDefaultAsync(s => s.ExecutionId == execution.Id && s.StepOrder == prevOrder);

                if (prevStepExec?.Files is not null)
                {
                    foreach (var file in prevStepExec.Files.Where(f => f.ContentType.StartsWith("image/")))
                    {
                        var filePath = Path.Combine(execution.WorkspacePath, file.FilePath);
                        if (!System.IO.File.Exists(filePath)) continue;

                        var fileBytes = await System.IO.File.ReadAllBytesAsync(filePath);

                        if (useTelegram)
                            await _telegram.SendPhotoAsync(tgConfig!, fileBytes, file.FileName);
                        else
                        {
                            var mediaId = await _whatsApp.UploadMediaAsync(waConfig!, fileBytes, file.ContentType, file.FileName);
                            await _whatsApp.SendImageMessageAsync(waConfig!, mediaId);
                        }
                    }

                    await _logger.LogAsync(project.Id, execution.Id, "info",
                        $"Imagenes del paso anterior enviadas a {channelName}", pm.StepOrder, stepName);
                }
            }

            // 5. If not waiting for response, complete step immediately and continue pipeline
            if (!waitForResponse)
            {
                var fireAndForgetOutput = new StepOutput
                {
                    Type = "text",
                    Content = message,
                    Summary = $"Mensaje enviado a {channelName} (sin esperar respuesta)",
                    Items = [new OutputItem { Content = message, Label = "mensaje enviado" }]
                };

                stepExecution.Status = "Completed";
                stepExecution.OutputData = JsonSerializer.Serialize(fireAndForgetOutput);
                stepExecution.CompletedAt = DateTime.UtcNow;
                stepExecution.InputData = JsonSerializer.Serialize(new { message });
                await db.SaveChangesAsync();

                stepOutputs[pm.StepOrder] = fireAndForgetOutput;
                stepModuleTypes[pm.StepOrder] = "Interaction";
                stepResults[pm.StepOrder] = AiResult.Ok(message, new Dictionary<string, object>());

                await _logger.LogAsync(project.Id, execution.Id, "success",
                    $"Mensaje enviado a {channelName} (sin esperar respuesta)", pm.StepOrder, stepName);
                return false; // continue with next steps in the pipeline
            }

            // 6. Serialize pause state (waiting for response)
            var pauseState = new PausedPipelineState
            {
                UserInput = userInput,
                StepOutputs = stepOutputs.ToDictionary(kv => kv.Key.ToString(), kv => JsonSerializer.Serialize(kv.Value)),
                StepModuleTypes = stepModuleTypes.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            };

            execution.PausedAtStepOrder = pm.StepOrder;
            execution.PausedStepData = JsonSerializer.Serialize(pauseState);
            execution.Status = "WaitingForInput";

            stepExecution.Status = "WaitingForInput";
            stepExecution.InputData = JsonSerializer.Serialize(new { message });
            await db.SaveChangesAsync();

            // 7. Create correlation for webhook resolution
            if (useTelegram)
            {
                _coreDb.TelegramCorrelations.Add(new TelegramCorrelation
                {
                    Id = Guid.NewGuid(),
                    ExecutionId = execution.Id,
                    TenantDbName = tenantDbName,
                    ChatId = tgConfig!.ChatId,
                    StepOrder = pm.StepOrder,
                    CreatedAt = DateTime.UtcNow,
                    IsResolved = false,
                });
            }
            else
            {
                _coreDb.WhatsAppCorrelations.Add(new WhatsAppCorrelation
                {
                    Id = Guid.NewGuid(),
                    ExecutionId = execution.Id,
                    TenantDbName = tenantDbName,
                    RecipientNumber = waConfig!.RecipientNumber,
                    StepOrder = pm.StepOrder,
                    CreatedAt = DateTime.UtcNow,
                    IsResolved = false,
                });
            }
            await _coreDb.SaveChangesAsync();

            await _logger.LogAsync(project.Id, execution.Id, "info",
                $"Esperando respuesta de {channelName}...", pm.StepOrder, stepName);
            return true; // pipeline paused, waiting for user response
        }

        public async Task<ProjectExecution> ResumeFromInteractionAsync(
            Guid executionId, string responseText, UserDbContext db, string tenantDbName, CancellationToken ct = default)
        {
            _logger = _baseLogger.WithDb(db);

            var execution = await db.ProjectExecutions
                .Include(e => e.StepExecutions.OrderBy(s => s.StepOrder))
                .FirstOrDefaultAsync(e => e.Id == executionId && e.Status == "WaitingForInput")
                ?? throw new InvalidOperationException("Ejecucion no encontrada o no esta esperando input");

            var project = await db.Projects
                .Include(p => p.ProjectModules.Where(pm => pm.IsActive).OrderBy(pm => pm.StepOrder))
                    .ThenInclude(pm => pm.AiModule)
                        .ThenInclude(m => m.ApiKey)
                .FirstOrDefaultAsync(p => p.Id == execution.ProjectId)
                ?? throw new InvalidOperationException("Proyecto no encontrado");

            var pausedStep = execution.PausedAtStepOrder
                ?? throw new InvalidOperationException("No hay paso pausado registrado");

            // Deserialize pause state
            var pauseState = JsonSerializer.Deserialize<PausedPipelineState>(execution.PausedStepData ?? "{}")
                ?? throw new InvalidOperationException("Estado de pausa invalido");

            var stepOutputs = new Dictionary<int, StepOutput>();
            foreach (var kv in pauseState.StepOutputs)
            {
                if (int.TryParse(kv.Key, out var stepOrder))
                    stepOutputs[stepOrder] = JsonSerializer.Deserialize<StepOutput>(kv.Value) ?? new StepOutput();
            }

            var stepModuleTypes = new Dictionary<int, string>();
            foreach (var kv in pauseState.StepModuleTypes)
            {
                if (int.TryParse(kv.Key, out var stepOrder))
                    stepModuleTypes[stepOrder] = kv.Value;
            }

            var stepResults = new Dictionary<int, AiResult>();

            // Determine the channel name from the paused step's module
            var pausedModule = project.ProjectModules.FirstOrDefault(pm => pm.StepOrder == pausedStep);
            var channelLabel = pausedModule?.AiModule.ModelName == "telegram" ? "Telegram"
                : pausedModule?.AiModule.ModelName == "whatsapp" ? "WhatsApp" : "Mensajeria";

            // Complete the interaction step with the response
            var interactionStepExec = execution.StepExecutions.FirstOrDefault(s => s.StepOrder == pausedStep);
            if (interactionStepExec is not null)
            {
                var interactionOutput = new StepOutput
                {
                    Type = "text",
                    Content = responseText,
                    Summary = $"Respuesta de {channelLabel}",
                    Items = [new OutputItem { Content = responseText, Label = "respuesta" }]
                };

                interactionStepExec.Status = "Completed";
                interactionStepExec.OutputData = JsonSerializer.Serialize(interactionOutput);
                interactionStepExec.CompletedAt = DateTime.UtcNow;

                stepOutputs[pausedStep] = interactionOutput;
                stepModuleTypes[pausedStep] = "Interaction";
                stepResults[pausedStep] = AiResult.Ok(responseText, new Dictionary<string, object>());
            }

            // Clear pause state
            execution.PausedAtStepOrder = null;
            execution.PausedStepData = null;
            execution.Status = "Running";
            await db.SaveChangesAsync();

            await _logger.LogAsync(project.Id, execution.Id, "info",
                $"Respuesta recibida de {channelLabel}: \"{responseText}\". Reanudando pipeline...");

            // Continue execution from the step after the paused one
            var allModules = project.ProjectModules.ToList();
            var workspacePath = execution.WorkspacePath;

            for (var mi = 0; mi < allModules.Count; mi++)
            {
                var pm = allModules[mi];
                if (pm.StepOrder <= pausedStep) continue;

                var nextModule = mi + 1 < allModules.Count ? allModules[mi + 1] : null;

                if (ct.IsCancellationRequested)
                {
                    execution.Status = "Cancelled";
                    execution.CompletedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                    return execution;
                }

                var stepExecution = new StepExecution
                {
                    Id = Guid.NewGuid(),
                    ExecutionId = execution.Id,
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
                    await _logger.LogAsync(project.Id, execution.Id, "info",
                        $"Ejecutando paso {pm.StepOrder}: {stepName} ({pm.AiModule.ProviderType}/{pm.AiModule.ModelName})",
                        pm.StepOrder, stepName);

                    // Handle another interaction step
                    if (IsInteractionStep(pm.AiModule))
                    {
                        var shouldPause = await HandleInteractionStepAsync(
                            project, execution, stepExecution, pm, pauseState.UserInput,
                            stepResults, stepOutputs, stepModuleTypes, db, tenantDbName);
                        if (shouldPause) return execution;
                        continue;
                    }

                    // Handle publish step
                    if (IsPublishStep(pm.AiModule))
                    {
                        await HandlePublishStepAsync(
                            project, execution, stepExecution, pm,
                            stepResults, stepOutputs, stepModuleTypes, db, tenantDbName);
                        continue;
                    }

                    var apiKey = pm.AiModule.ApiKey?.EncryptedKey
                        ?? throw new InvalidOperationException($"Paso {pm.StepOrder}: ApiKey no configurada");

                    var provider = _registry.GetProvider(pm.AiModule.ProviderType)
                        ?? throw new InvalidOperationException($"Paso {pm.StepOrder}: Proveedor no disponible");

                    var config = MergeConfiguration(pm.AiModule.Configuration, pm.Configuration);

                    if (pm.AiModule.ModuleType == "Text" && nextModule is not null)
                    {
                        var nextMaxLen = InputAdapter.GetMaxPromptLength(nextModule.AiModule.ModelName);
                        var rule = $"\n\nREGLA DE LONGITUD: El siguiente paso usa el modelo {nextModule.AiModule.ModelName} ({nextModule.AiModule.ModuleType}) que acepta maximo {nextMaxLen} caracteres por prompt. Cada elemento en \"items\" DEBE tener un \"content\" de maximo {nextMaxLen} caracteres.";
                        if (config.TryGetValue("systemPrompt", out var existing) && existing is string s)
                            config["systemPrompt"] = s + rule;
                        else
                            config["systemPrompt"] = rule;
                    }

                    // Inject image count rule if configured
                    if (pm.AiModule.ModuleType == "Text")
                        InjectImageCountRule(config);

                    var inputs = ResolveInputs(pm, pauseState.UserInput, stepResults, stepOutputs, pm.AiModule.ModuleType, pm.AiModule.ModelName);

                    if (pm.AiModule.ModuleType == "Text")
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
                        stepExecution.EstimatedCost += result.EstimatedCost;

                        if (!result.Success)
                        {
                            await FailStep(stepExecution, execution, result.Error!, db);
                            return execution;
                        }

                        var stepOutput = OutputSchemaHelper.ParseTextOutput(result.TextOutput ?? "", result.Metadata);
                        stepOutputs[pm.StepOrder] = stepOutput;

                        var stepDir = Path.Combine(workspacePath, $"step_{pm.StepOrder}");
                        Directory.CreateDirectory(stepDir);
                        await File.WriteAllTextAsync(Path.Combine(stepDir, "output.json"), result.TextOutput ?? "");

                        db.ExecutionFiles.Add(new ExecutionFile
                        {
                            Id = Guid.NewGuid(),
                            StepExecutionId = stepExecution.Id,
                            FileName = "output.json",
                            ContentType = "application/json",
                            FilePath = Path.Combine($"step_{pm.StepOrder}", "output.json"),
                            Direction = "Output",
                            FileSize = System.Text.Encoding.UTF8.GetByteCount(result.TextOutput ?? ""),
                            CreatedAt = DateTime.UtcNow,
                        });

                        stepExecution.OutputData = JsonSerializer.Serialize(stepOutput);

                        await _logger.LogAsync(project.Id, execution.Id, "success",
                            $"Texto generado correctamente", pm.StepOrder, stepName);
                    }
                    else if (pm.AiModule.ModuleType == "Image")
                    {
                        var outputFiles = new List<OutputFile>();
                        string? imageError = null;

                        for (var i = 0; i < inputs.Count; i++)
                        {
                            await _logger.LogAsync(project.Id, execution.Id, "info",
                                inputs.Count > 1 ? $"Generando imagen {i + 1}/{inputs.Count}..." : $"Generando imagen...",
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
                            stepExecution.EstimatedCost += result.EstimatedCost;

                            if (!result.Success)
                            {
                                imageError = $"Error en imagen {i + 1}/{inputs.Count}: {result.Error}";
                                break;
                            }

                            if (result.FileOutput is not null)
                            {
                                var stepDir = Path.Combine(workspacePath, $"step_{pm.StepOrder}");
                                Directory.CreateDirectory(stepDir);
                                var ext = GetExtension(result.ContentType ?? "application/octet-stream");
                                var fileName = inputs.Count > 1 ? $"output_{i + 1}{ext}" : $"output{ext}";
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

                                outputFiles.Add(new OutputFile
                                {
                                    FileId = Guid.NewGuid(),
                                    FileName = fileName,
                                    ContentType = result.ContentType ?? "application/octet-stream",
                                    FileSize = result.FileOutput.Length,
                                });
                            }

                            stepResults[pm.StepOrder] = result;
                        }

                        stepModuleTypes[pm.StepOrder] = pm.AiModule.ModuleType;
                        var imageOutput = OutputSchemaHelper.BuildImageOutput(outputFiles, pm.AiModule.ModelName);
                        stepOutputs[pm.StepOrder] = imageOutput;
                        stepExecution.OutputData = JsonSerializer.Serialize(imageOutput);
                        await db.SaveChangesAsync();

                        if (imageError is not null)
                        {
                            await FailStep(stepExecution, execution, imageError, db);
                            return execution;
                        }

                        await _logger.LogAsync(project.Id, execution.Id, "success",
                            $"{outputFiles.Count} imagen(es) generada(s) correctamente", pm.StepOrder, stepName);
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
                    execution.Status = "Failed";
                    execution.CompletedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                    return execution;
                }
            }

            execution.Status = "Completed";
            execution.CompletedAt = DateTime.UtcNow;
            execution.TotalEstimatedCost = await db.StepExecutions
                .Where(s => s.ExecutionId == execution.Id)
                .SumAsync(s => s.EstimatedCost);
            await db.SaveChangesAsync();
            await _logger.LogAsync(project.Id, execution.Id, "success", "Pipeline completado correctamente");
            return execution;
        }

        // Serializable pause state
        private class PausedPipelineState
        {
            public string? UserInput { get; set; }
            public Dictionary<string, string> StepOutputs { get; set; } = new();
            public Dictionary<string, string> StepModuleTypes { get; set; } = new();
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

        /// <summary>
        /// If the Text step is configured as an image prompt generator, injects a rule
        /// forcing the model to return exactly N items in the output.
        /// </summary>
        private static void InjectImageCountRule(Dictionary<string, object> config)
        {
            if (!config.TryGetValue("isImagePrompt", out var ipVal))
                return;

            var isImgPrompt = ipVal is JsonElement jpIp ? jpIp.GetBoolean() : ipVal is bool b && b;
            if (!isImgPrompt)
                return;

            var imgCount = 1;
            if (config.TryGetValue("imageCount", out var icVal))
                imgCount = icVal is JsonElement jpIc ? jpIc.GetInt32() : Convert.ToInt32(icVal);

            var imgRule = $"\n\nREGLA DE IMAGENES: Este prompt genera descripciones para imagenes. " +
                $"DEBES devolver EXACTAMENTE {imgCount} elemento(s) en el array \"items\". " +
                $"Cada item debe ser un prompt descriptivo independiente para generar una imagen. " +
                $"No generes mas ni menos de {imgCount}. Esto es obligatorio.";

            if (config.TryGetValue("systemPrompt", out var existingSp) && existingSp is string sp)
                config["systemPrompt"] = sp + imgRule;
            else
                config["systemPrompt"] = imgRule;
        }

        public async Task<ProjectExecution> AbortFromInteractionAsync(
            Guid executionId, UserDbContext db, string tenantDbName)
        {
            var execution = await db.ProjectExecutions
                .Include(e => e.StepExecutions.OrderBy(s => s.StepOrder))
                .FirstOrDefaultAsync(e => e.Id == executionId && e.Status == "WaitingForInput")
                ?? throw new InvalidOperationException("Ejecucion no encontrada o no esta esperando input");

            var pausedStep = execution.PausedAtStepOrder;

            // Mark the waiting step as cancelled
            var waitingStepExec = execution.StepExecutions.FirstOrDefault(s => s.StepOrder == pausedStep);
            if (waitingStepExec is not null)
            {
                waitingStepExec.Status = "Cancelled";
                waitingStepExec.CompletedAt = DateTime.UtcNow;
            }

            execution.Status = "Cancelled";
            execution.CompletedAt = DateTime.UtcNow;
            execution.PausedAtStepOrder = null;
            execution.PausedStepData = null;
            await db.SaveChangesAsync();

            await _logger.LogAsync(execution.ProjectId, execution.Id, "warning",
                "Pipeline abortado por el usuario desde Telegram");

            return execution;
        }

        public async Task<ProjectExecution> RetryFromStepAsync(
            Guid executionId, int fromStepOrder, string? comment, UserDbContext db, string tenantDbName, CancellationToken ct = default)
        {
            _logger = _baseLogger.WithDb(db);

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

                if (IsInteractionStep(pm.AiModule) || IsPublishStep(pm.AiModule) || pm.AiModule.ApiKeyId is null)
                    continue;

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

                    // Handle interaction step during retry
                    if (IsInteractionStep(pm.AiModule))
                    {
                        var shouldPause = await HandleInteractionStepAsync(
                            project, execution, stepExecution, pm, originalUserInput,
                            stepResults, stepOutputs, stepModuleTypes, db, tenantDbName);
                        if (shouldPause) return execution;
                        continue;
                    }

                    // Handle publish step during retry
                    if (IsPublishStep(pm.AiModule))
                    {
                        await HandlePublishStepAsync(
                            project, execution, stepExecution, pm,
                            stepResults, stepOutputs, stepModuleTypes, db, tenantDbName);
                        continue;
                    }

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

                    // Inject image count rule if configured
                    if (pm.AiModule.ModuleType == "Text")
                        InjectImageCountRule(config);

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
                        stepExecution.EstimatedCost += result.EstimatedCost;

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
                            stepExecution.EstimatedCost += result.EstimatedCost;

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
                        stepExecution.EstimatedCost += result.EstimatedCost;

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
            execution.TotalEstimatedCost = await db.StepExecutions
                .Where(s => s.ExecutionId == execution.Id)
                .SumAsync(s => s.EstimatedCost);
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
            "video/mp4" => ".mp4",
            "video/webm" => ".webm",
            "text/plain" => ".txt",
            _ => ".bin"
        };
    }
}
