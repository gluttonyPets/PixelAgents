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
using Server.Services.Canva;

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
        private readonly CanvaService _canva;
        private readonly CoreDbContext _coreDb;
        private readonly IConfiguration _configuration;
        private readonly string _mediaRoot;

        public PipelineExecutor(IAiProviderRegistry registry, IExecutionLogger logger,
            WhatsAppService whatsApp, TelegramService telegram, BufferService buffer,
            CanvaService canva,
            CoreDbContext coreDb, IConfiguration configuration, IWebHostEnvironment env)
        {
            _registry = registry;
            _baseLogger = logger;
            _logger = logger;
            _mediaRoot = Path.Combine(env.ContentRootPath, "GeneratedMedia");
            _whatsApp = whatsApp;
            _telegram = telegram;
            _buffer = buffer;
            _canva = canva;
            _coreDb = coreDb;
            _configuration = configuration;
        }

        /// <summary>
        /// Resolves a WorkspacePath (relative or legacy absolute) to an absolute disk path.
        /// </summary>
        private string ResolveWorkspacePath(string storedPath) =>
            Path.IsPathRooted(storedPath) ? storedPath : Path.Combine(_mediaRoot, storedPath);

        private static bool IsInteractionStep(AiModule module) =>
            module.ModuleType == "Interaction" ||
            (module.ProviderType == "System" && (module.ModelName == "whatsapp" || module.ModelName == "telegram"));

        private static bool IsPublishStep(AiModule module) =>
            module.ModuleType == "Publish" ||
            (module.ProviderType == "System" && module.ModelName == "instagram");

        private static bool IsDesignStep(AiModule module) =>
            module.ModuleType == "Design" ||
            module.ProviderType == "Canva";

        private static bool IsOrchestratorStep(AiModule module) =>
            module.ModuleType == "Orchestrator";

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
                if (IsInteractionStep(pm.AiModule) || IsPublishStep(pm.AiModule) || IsDesignStep(pm.AiModule) || IsOrchestratorStep(pm.AiModule) || pm.AiModule.ApiKeyId is null)
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
            // Store relative path so it survives app restarts / redeployments
            var relativeWorkspace = Path.Combine(tenantDbName, projectId.ToString(), executionId.ToString());
            var workspacePath = Path.Combine(_mediaRoot, relativeWorkspace);

            var execution = new ProjectExecution
            {
                Id = executionId,
                ProjectId = projectId,
                Status = "Running",
                WorkspacePath = relativeWorkspace,
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

            // ── Load previous execution summaries for context ──
            var previousSummaryContext = await BuildPreviousSummaryContextAsync(db, projectId, executionId);

            var allModules = project.ProjectModules.ToList();
            var stepBranches = BuildStepBranches(allModules);

            // ── Branch-aware execution: group modules by branch ──
            var mainModules = allModules.Where(m => m.BranchId == "main").OrderBy(m => m.StepOrder).ToList();
            var branchModules = allModules.Where(m => m.BranchId != "main")
                .GroupBy(m => m.BranchId)
                .ToDictionary(g => g.Key, g => g.OrderBy(m => m.StepOrder).ToList());

            // Execute main branch, launching sub-branches after each step
            for (var mi = 0; mi < mainModules.Count; mi++)
            {
                var pm = mainModules[mi];
                var nextModule = mi + 1 < mainModules.Count ? mainModules[mi + 1] : null;

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
                await _logger.LogStepProgressAsync(projectId, pm.Id, "Running");

                try
                {
                    var stepName = pm.StepName ?? pm.AiModule.Name;
                    var stepLabel = GetStepLabel(pm, project.ProjectModules);
                    await _logger.LogAsync(projectId, executionId, "info",
                        $"Ejecutando paso {stepLabel}: {stepName} ({pm.AiModule.ProviderType}/{pm.AiModule.ModelName})",
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

                    // ── Design step: create design via Canva ──
                    if (IsDesignStep(pm.AiModule))
                    {
                        await HandleCanvaPublishStepAsync(
                            project, execution, stepExecution, pm,
                            stepResults, stepOutputs, stepModuleTypes, db, tenantDbName);
                        continue;
                    }

                    // ── Orchestrator step: generate plan, pause for review ──
                    if (IsOrchestratorStep(pm.AiModule))
                    {
                        var shouldPause = await HandleOrchestratorStepAsync(
                            project, execution, stepExecution, pm, userInput,
                            stepResults, stepOutputs, stepModuleTypes,
                            workspacePath, previousSummaryContext, db, tenantDbName, ct);
                        if (shouldPause) return execution;
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
                    var inputs = ResolveInputs(pm, userInput, stepResults, stepOutputs, pm.AiModule.ModuleType, pm.AiModule.ModelName, stepBranches);

                    // Store raw input data including systemPrompt so history shows exactly what was sent
                    var systemPrompt = config.TryGetValue("systemPrompt", out var spVal) && spVal is string spStr ? spStr : null;
                    if (pm.AiModule.ModuleType == "Image")
                    {
                        // For Image steps, also record whether input files are being passed
                        var hasFileInput = pm.InputMapping is not null
                            && pm.InputMapping.Contains("\"file\"");
                        stepExecution.InputData = inputs.Count == 1
                            ? JsonSerializer.Serialize(new { systemPrompt, projectContext = project.Context, prompt = inputs[0], inputMode = hasFileInput ? "image-to-image" : "text-to-image" })
                            : JsonSerializer.Serialize(new { systemPrompt, projectContext = project.Context, prompts = inputs, count = inputs.Count, inputMode = hasFileInput ? "image-to-image" : "text-to-image" });
                    }
                    else
                    {
                        stepExecution.InputData = inputs.Count == 1
                            ? JsonSerializer.Serialize(new { systemPrompt, projectContext = project.Context, prompt = inputs[0] })
                            : JsonSerializer.Serialize(new { systemPrompt, projectContext = project.Context, prompts = inputs, count = inputs.Count });
                    }

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
                            PreviousExecutionsSummary = previousSummaryContext,
                            Configuration = config,
                            InputFiles = await LoadModuleFilesAsync(pm, db),
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
                        // If a fixed imagePrompt is configured in step config, use it instead of resolved inputs
                        var imagePrompt = "";
                        if (config.TryGetValue("imagePrompt", out var ip))
                            imagePrompt = ip is JsonElement ipEl ? ipEl.GetString() ?? "" : ip?.ToString() ?? "";

                        if (!string.IsNullOrWhiteSpace(imagePrompt))
                            inputs = new List<string> { imagePrompt };

                        // Detect if InputMapping requests file from previous step (image-to-image editing)
                        List<byte[]>? previousStepFiles = null;
                        var isFileInput = false;
                        if (pm.InputMapping is not null)
                        {
                            var mappingJson = JsonSerializer.Deserialize<JsonElement>(pm.InputMapping);
                            if (mappingJson.TryGetProperty("field", out var fieldProp) && fieldProp.GetString() == "file")
                            {
                                isFileInput = true;
                                var prevOrder = FindPreviousStepInBranch(
                                    pm.StepOrder, pm.BranchId, pm.BranchFromStep,
                                    stepOutputs, stepResults, stepBranches);

                                if (stepOutputs.TryGetValue(prevOrder, out var prevOutput) && prevOutput.Files.Count > 0)
                                {
                                    previousStepFiles = new List<byte[]>();
                                    foreach (var prevFile in prevOutput.Files.Where(f => f.ContentType.StartsWith("image/")))
                                    {
                                        var prevFilePath = Path.Combine(workspacePath, $"step_{prevOrder}", prevFile.FileName);
                                        if (File.Exists(prevFilePath))
                                        {
                                            var imgBytes = await File.ReadAllBytesAsync(prevFilePath);
                                            previousStepFiles.Add(imgBytes);
                                            await _logger.LogAsync(projectId, executionId, "info",
                                                $"Imagen del paso {prevOrder} cargada: {prevFile.FileName} ({imgBytes.Length} bytes)",
                                                pm.StepOrder, stepName);
                                        }
                                    }

                                    if (previousStepFiles.Count == 0)
                                    {
                                        await _logger.LogAsync(projectId, executionId, "warning",
                                            $"InputMapping solicita archivo del paso anterior pero no se encontraron imagenes en paso {prevOrder}",
                                            pm.StepOrder, stepName);
                                    }
                                }
                                else
                                {
                                    await _logger.LogAsync(projectId, executionId, "warning",
                                        $"InputMapping solicita archivo del paso anterior pero no hay archivos en paso {prevOrder}",
                                        pm.StepOrder, stepName);
                                }
                            }
                        }

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

                            // Build InputFiles: prefer previous step files for image-to-image, fallback to module files
                            var inputFiles = previousStepFiles is { Count: > 0 }
                                ? previousStepFiles
                                : await LoadModuleFilesAsync(pm, db);

                            if (isFileInput)
                            {
                                await _logger.LogAsync(projectId, executionId, "info",
                                    $"[Image Debug] Mode=image-to-image, InputFiles={inputFiles?.Count ?? 0}, Prompt='{singleInput[..Math.Min(singleInput.Length, 100)]}', Model={pm.AiModule.ModelName}",
                                    pm.StepOrder, stepName);
                            }

                            var context = new AiExecutionContext
                            {
                                ModuleType = pm.AiModule.ModuleType,
                                ModelName = pm.AiModule.ModelName,
                                ApiKey = apiKey,
                                Input = singleInput,
                                ProjectContext = project.Context,
                                Configuration = config,
                                InputFiles = inputFiles,
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
                            var prevOrder = FindPreviousStepInBranch(
                                pm.StepOrder, pm.BranchId, pm.BranchFromStep,
                                stepOutputs, stepResults, stepBranches);

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
                    else if (pm.AiModule.ModuleType == "VideoSearch")
                    {
                        // VideoSearch modules: use input from previous step or user input as search query
                        var searchQuery = inputs[0];
                        if (string.IsNullOrWhiteSpace(searchQuery))
                        {
                            await FailStep(stepExecution, execution, "VideoSearch: no se proporciono texto de busqueda", db);
                            return execution;
                        }

                        await _logger.LogAsync(projectId, executionId, "info",
                            $"Buscando video en Pexels: '{searchQuery[..Math.Min(searchQuery.Length, 100)]}'",
                            pm.StepOrder, stepName);

                        var searchContext = new AiExecutionContext
                        {
                            ModuleType = pm.AiModule.ModuleType,
                            ModelName = pm.AiModule.ModelName,
                            ApiKey = apiKey,
                            Input = searchQuery,
                            ProjectContext = project.Context,
                            Configuration = config,
                        };

                        var result = await provider.ExecuteAsync(searchContext);
                        stepResults[pm.StepOrder] = result;
                        stepModuleTypes[pm.StepOrder] = pm.AiModule.ModuleType;
                        stepExecution.EstimatedCost += result.EstimatedCost;

                        if (!result.Success)
                        {
                            await _logger.LogAsync(projectId, executionId, "error",
                                $"Error en busqueda de video: {result.Error}", pm.StepOrder, stepName);
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

                        var pexelsQuery = result.Metadata.GetValueOrDefault("query", "?");
                        var pexelsTotal = result.Metadata.GetValueOrDefault("totalResults", 0);
                        var pexelsPhotographer = result.Metadata.GetValueOrDefault("photographer", "");
                        await _logger.LogAsync(projectId, executionId, "success",
                            $"Video encontrado en Pexels (query='{pexelsQuery}', {pexelsTotal} resultados, por {pexelsPhotographer})",
                            pm.StepOrder, stepName);
                    }
                    else if (pm.AiModule.ModuleType == "VideoEdit")
                    {
                        // VideoEdit modules: prefer videoPrompt from step config (set in UI),
                        // fallback to input from previous step
                        var editInput = "";
                        if (config.TryGetValue("videoPrompt", out var vp2))
                            editInput = vp2 is JsonElement vp2El ? vp2El.GetString() ?? "" : vp2?.ToString() ?? "";
                        if (string.IsNullOrWhiteSpace(editInput))
                            editInput = inputs[0];
                        if (string.IsNullOrWhiteSpace(editInput))
                        {
                            await FailStep(stepExecution, execution, "VideoEdit: no se proporciono guion de video (videoPrompt)", db);
                            return execution;
                        }

                        // Merge subtitleLanguage from step config into module config
                        if (config.TryGetValue("subtitleLanguage", out var sl))
                        {
                            var slStr = sl is JsonElement slEl ? slEl.GetString() ?? "es" : sl?.ToString() ?? "es";
                            config["subtitleLanguage"] = slStr;
                        }

                        // Collect video URLs from previous VideoSearch steps to pass in config
                        var videoUrls = new List<string>();
                        foreach (var prevOrder in stepOutputs.Keys.Where(k => k < pm.StepOrder).OrderBy(k => k))
                        {
                            if (stepModuleTypes.TryGetValue(prevOrder, out var prevType) && prevType == "VideoSearch"
                                && stepResults.TryGetValue(prevOrder, out var prevResult) && prevResult.Metadata.TryGetValue("pexelsUrl", out var pUrl))
                            {
                                // Use the downloaded file URL from workspace
                                var prevOutput = stepOutputs.GetValueOrDefault(prevOrder);
                                if (prevOutput?.Files.Count > 0)
                                {
                                    var filePath = Path.Combine(workspacePath, $"step_{prevOrder}", prevOutput.Files[0].FileName);
                                    if (File.Exists(filePath))
                                    {
                                        // Json2Video needs public URLs — use the download URL from Pexels metadata
                                        if (prevResult.Metadata.TryGetValue("downloadUrl", out var dlUrl) && dlUrl is string dlUrlStr)
                                            videoUrls.Add(dlUrlStr);
                                    }
                                }
                            }
                        }

                        // Inject collected video URLs into config if not already set
                        if (videoUrls.Count > 0 && !config.ContainsKey("videoUrls"))
                            config["videoUrls"] = JsonSerializer.Serialize(videoUrls);

                        await _logger.LogAsync(projectId, executionId, "info",
                            $"Editando video con Json2Video: input={editInput[..Math.Min(editInput.Length, 100)]}..., videoUrls={videoUrls.Count}",
                            pm.StepOrder, stepName);

                        var editContext = new AiExecutionContext
                        {
                            ModuleType = pm.AiModule.ModuleType,
                            ModelName = pm.AiModule.ModelName,
                            ApiKey = apiKey,
                            Input = editInput,
                            ProjectContext = project.Context,
                            Configuration = config,
                        };

                        var result = await provider.ExecuteAsync(editContext);
                        stepResults[pm.StepOrder] = result;
                        stepModuleTypes[pm.StepOrder] = pm.AiModule.ModuleType;
                        stepExecution.EstimatedCost += result.EstimatedCost;

                        if (!result.Success)
                        {
                            await _logger.LogAsync(projectId, executionId, "error",
                                $"Error en edicion de video: {result.Error}", pm.StepOrder, stepName);
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

                        var duration = result.Metadata.GetValueOrDefault("duration", "?");
                        var renderTime = result.Metadata.GetValueOrDefault("renderingTime", "?");
                        await _logger.LogAsync(projectId, executionId, "success",
                            $"Video editado correctamente con Json2Video (duracion={duration}s, renderizado={renderTime}s)",
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
                    await _logger.LogStepProgressAsync(projectId, pm.Id, "Completed");
                }
                catch (OperationCanceledException)
                {
                    await _logger.LogAsync(projectId, executionId, "warning",
                        "Pipeline cancelado por el usuario", pm.StepOrder, pm.StepName ?? pm.AiModule.Name);

                    stepExecution.Status = "Cancelled";
                    stepExecution.CompletedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                    await _logger.LogStepProgressAsync(projectId, pm.Id, "Cancelled");

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
                    await _logger.LogStepProgressAsync(projectId, pm.Id, "Failed");

                    execution.Status = "Failed";
                    execution.CompletedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                    return execution;
                }

                // ── Execute sub-branches that fork from this main step ──
                var forksFromHere = branchModules
                    .Where(kv => kv.Value.FirstOrDefault()?.BranchFromStep == pm.StepOrder)
                    .ToList();

                foreach (var (branchId, branchSteps) in forksFromHere)
                {
                    await _logger.LogAsync(projectId, executionId, "info",
                        $"Iniciando rama '{branchId}' (bifurcacion desde paso {pm.StepOrder}, {branchSteps.Count} pasos)");

                    var branchResult = await ExecuteBranchStepsAsync(
                        project, execution, branchId, branchSteps, userInput,
                        stepResults, stepOutputs, stepModuleTypes,
                        workspacePath, previousSummaryContext, db, tenantDbName, ct);

                    if (branchResult == BranchResult.Cancelled)
                    {
                        execution.Status = "Cancelled";
                        execution.CompletedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync();
                        return execution;
                    }

                    var branchLevel = branchResult == BranchResult.Failed ? "warning"
                        : branchResult == BranchResult.Paused ? "info" : "success";
                    var branchMsg = branchResult == BranchResult.Failed
                        ? $"Rama '{branchId}' fallo — continuando con otras ramas"
                        : branchResult == BranchResult.Paused
                            ? $"Rama '{branchId}' pausada esperando respuesta del usuario"
                            : $"Rama '{branchId}' completada correctamente";
                    await _logger.LogAsync(projectId, executionId, branchLevel, branchMsg);
                }
            }

            execution.Status = "Completed";
            execution.CompletedAt = DateTime.UtcNow;
            execution.TotalEstimatedCost = await db.StepExecutions
                .Where(s => s.ExecutionId == execution.Id)
                .SumAsync(s => s.EstimatedCost);
            await db.SaveChangesAsync();

            await _logger.LogAsync(projectId, executionId, "success", "Pipeline completado correctamente");

            // ── Generate execution summary for future context ──
            try
            {
                await GenerateExecutionSummaryAsync(execution, stepOutputs, userInput, db);
            }
            catch (Exception ex)
            {
                await _logger.LogAsync(projectId, executionId, "warning",
                    $"No se pudo generar resumen: {ex.Message}");
            }

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
            string targetModuleType, string targetModelName,
            Dictionary<int, string>? stepBranches = null)
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
                    int prevOrder;
                    if (stepBranches is not null)
                    {
                        prevOrder = FindPreviousStepInBranch(
                            pm.StepOrder, pm.BranchId, pm.BranchFromStep,
                            stepOutputs, stepResults, stepBranches);
                    }
                    else
                    {
                        prevOrder = stepOutputs.Keys.Where(k => k < pm.StepOrder)
                            .OrderByDescending(k => k).FirstOrDefault();
                        if (!stepOutputs.ContainsKey(prevOrder) && !stepResults.ContainsKey(prevOrder)
                            && pm.BranchFromStep.HasValue)
                            prevOrder = pm.BranchFromStep.Value;
                    }

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

        /// <summary>
        /// Find the nearest previous step that belongs to the same branch (or trunk).
        /// </summary>
        private static int FindPreviousStepInBranch(
            int currentStepOrder, string branchId, int? branchFromStep,
            Dictionary<int, StepOutput> stepOutputs,
            Dictionary<int, AiResult> stepResults,
            Dictionary<int, string> stepBranches)
        {
            // First: look for the nearest previous step in the SAME branch
            var prevOrder = stepBranches
                .Where(kv => kv.Value == branchId && kv.Key < currentStepOrder)
                .Select(kv => kv.Key)
                .OrderByDescending(k => k)
                .FirstOrDefault();

            if (prevOrder > 0 && (stepOutputs.ContainsKey(prevOrder) || stepResults.ContainsKey(prevOrder)))
                return prevOrder;

            // Second: fall back to the fork source step (trunk step the branch forked from)
            if (branchFromStep.HasValue)
                return branchFromStep.Value;

            // Third: fall back to nearest trunk step with order < current
            prevOrder = stepBranches
                .Where(kv => kv.Value == "main" && kv.Key < currentStepOrder)
                .Select(kv => kv.Key)
                .OrderByDescending(k => k)
                .FirstOrDefault();

            return prevOrder;
        }

        /// <summary>
        /// Build a StepOrder → BranchId mapping from the project's modules.
        /// Includes executed steps from stepModuleTypes to cover restored state.
        /// </summary>
        private static Dictionary<int, string> BuildStepBranches(
            IEnumerable<ProjectModule> modules,
            Dictionary<int, string>? stepModuleTypes = null)
        {
            var branches = modules.ToDictionary(m => m.StepOrder, m => m.BranchId);
            // Ensure any steps from restored state that aren't in modules are included as "main"
            if (stepModuleTypes is not null)
            {
                foreach (var kv in stepModuleTypes)
                {
                    branches.TryAdd(kv.Key, "main");
                }
            }
            return branches;
        }

        /// <summary>
        /// Compute a branch-prefixed step label (e.g., "A5", "B5") for display in logs.
        /// Pre-fork trunk steps show plain numbers. Post-fork: main = A, branches = B, C...
        /// </summary>
        private static string GetStepLabel(ProjectModule pm, ICollection<ProjectModule> allModules)
        {
            var forkSteps = allModules.Where(m => m.BranchId != "main")
                .Select(m => m.BranchFromStep).Where(f => f.HasValue).Select(f => f!.Value).Distinct().ToList();
            if (forkSteps.Count == 0) return pm.StepOrder.ToString();

            var maxFork = forkSteps.Max();
            if (pm.BranchId == "main" && pm.StepOrder <= maxFork)
                return pm.StepOrder.ToString();

            if (pm.BranchId == "main")
            {
                var mainPostFork = allModules.Where(m => m.BranchId == "main" && m.StepOrder > maxFork)
                    .OrderBy(m => m.StepOrder).ToList();
                var idx = mainPostFork.FindIndex(m => m.StepOrder == pm.StepOrder);
                return $"A{maxFork + 1 + idx}";
            }

            var branchIds = allModules.Where(m => m.BranchId != "main")
                .GroupBy(m => m.BranchId).Select(g => g.Key).OrderBy(b => b).ToList();
            var branchIdx = branchIds.IndexOf(pm.BranchId);
            var letter = (char)('B' + branchIdx);
            var forkStep = pm.BranchFromStep ?? maxFork;
            var branchSteps = allModules.Where(m => m.BranchId == pm.BranchId)
                .OrderBy(m => m.StepOrder).ToList();
            var bIdx = branchSteps.FindIndex(m => m.StepOrder == pm.StepOrder);
            return $"{letter}{forkStep + 1 + bIdx}";
        }

        // ── Execution summary generation ──
        private async Task GenerateExecutionSummaryAsync(
            ProjectExecution execution,
            Dictionary<int, StepOutput> stepOutputs,
            string? userInput,
            UserDbContext db)
        {
            // Find an OpenAI API key from any module in this tenant DB
            var openAiKey = await db.AiModules
                .Include(m => m.ApiKey)
                .Where(m => m.ProviderType == "OpenAI" && m.ApiKey != null && m.ApiKey.EncryptedKey != "")
                .Select(m => m.ApiKey!.EncryptedKey)
                .FirstOrDefaultAsync();

            if (string.IsNullOrEmpty(openAiKey))
                return; // No OpenAI key available, skip summary

            // Build a compact representation of what was done
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(userInput))
                parts.Add($"Input del usuario: {Truncate(userInput, 300)}");

            foreach (var (stepOrder, output) in stepOutputs.OrderBy(kv => kv.Key))
            {
                var sb = new System.Text.StringBuilder();
                sb.Append($"Paso {stepOrder} ({output.Type}): ");
                if (!string.IsNullOrWhiteSpace(output.Title))
                    sb.Append($"titulo=\"{Truncate(output.Title, 100)}\" ");
                if (!string.IsNullOrWhiteSpace(output.Summary))
                    sb.Append($"resumen=\"{Truncate(output.Summary, 200)}\" ");
                else if (!string.IsNullOrWhiteSpace(output.Content))
                    sb.Append($"contenido=\"{Truncate(output.Content, 200)}\" ");
                if (output.Items.Count > 0)
                    sb.Append($"[{output.Items.Count} items: {string.Join(", ", output.Items.Take(3).Select(i => Truncate(i.Content, 80)))}] ");
                if (output.Files.Count > 0)
                    sb.Append($"[{output.Files.Count} archivo(s)] ");
                parts.Add(sb.ToString().TrimEnd());
            }

            var executionContext = string.Join("\n", parts);
            if (string.IsNullOrWhiteSpace(executionContext))
                return;

            var prompt = $@"Resume en 2-4 frases lo que se produjo en esta ejecucion de pipeline. Se conciso y especifico: que contenido se creo, sobre que tema, cuantas imagenes/videos si los hubo, y cualquier dato clave. No uses markdown ni emojis. Solo texto plano.

Datos de la ejecucion:
{executionContext}";

            try
            {
                var client = new OpenAI.Chat.ChatClient(model: "gpt-4o-mini", apiKey: openAiKey);
                var options = new OpenAI.Chat.ChatCompletionOptions { MaxOutputTokenCount = 200 };
                var completion = await client.CompleteChatAsync(
                    [new OpenAI.Chat.UserChatMessage(prompt)], options);

                var summary = completion.Value.Content[0].Text?.Trim();
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    execution.ExecutionSummary = summary;
                    await db.SaveChangesAsync();
                }

                var cost = PricingCatalog.EstimateTextCost("gpt-4o-mini",
                    completion.Value.Usage.InputTokenCount, completion.Value.Usage.OutputTokenCount);
                execution.TotalEstimatedCost += cost;
                await db.SaveChangesAsync();

                await _logger.LogAsync(execution.ProjectId, execution.Id, "info",
                    $"Resumen generado (~${cost:F4})");
            }
            catch (Exception ex)
            {
                await _logger.LogAsync(execution.ProjectId, execution.Id, "warning",
                    $"Error generando resumen: {ex.Message}");
            }
        }

        private static string Truncate(string text, int maxLen)
            => text.Length <= maxLen ? text : text[..maxLen] + "...";

        private static async Task<string?> BuildPreviousSummaryContextAsync(
            UserDbContext db, Guid projectId, Guid currentExecutionId)
        {
            var previousSummaries = await db.ProjectExecutions
                .Where(e => e.ProjectId == projectId
                    && e.Status == "Completed"
                    && e.ExecutionSummary != null
                    && e.Id != currentExecutionId)
                .OrderByDescending(e => e.CompletedAt)
                .Take(10)
                .Select(e => new { e.CompletedAt, e.ExecutionSummary })
                .ToListAsync();

            if (previousSummaries.Count == 0)
                return null;

            var lines = previousSummaries
                .OrderBy(s => s.CompletedAt)
                .Select(s => $"- ({s.CompletedAt:yyyy-MM-dd HH:mm}) {s.ExecutionSummary}");
            return "[Historial de ejecuciones anteriores - NO repitas contenido ya creado]\n"
                + string.Join("\n", lines);
        }

        // ── Publish step handler (via Buffer or Canva) ──
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
            // Scope candidates to the same branch (or trunk) to avoid cross-branch contamination
            var previousText = "";
            string? previousTitle = null;
            var pubBranches = BuildStepBranches(project.ProjectModules, stepModuleTypes);
            var candidateOrders = pubBranches
                .Where(kv => (kv.Value == pm.BranchId || kv.Value == "main") && kv.Key < pm.StepOrder)
                .Select(kv => kv.Key)
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
                await _logger.LogStepProgressAsync(project.Id, pm.Id, "Completed");

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

        // ── Canva Publish step handler ──
        private async Task HandleCanvaPublishStepAsync(
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

            var stepConfig = MergeConfiguration(pm.AiModule.Configuration, pm.Configuration);

            // Read Canva credentials from step/module configuration
            var accessToken = "";
            if (stepConfig.TryGetValue("accessToken", out var atVal))
                accessToken = (atVal is JsonElement atEl ? atEl.GetString() : atVal?.ToString()) ?? "";

            if (string.IsNullOrWhiteSpace(accessToken))
                throw new InvalidOperationException($"Paso {pm.StepOrder}: Access Token de Canva no configurado en el modulo");

            var canvaConfig = new CanvaConfig { AccessToken = accessToken };

            // Read export format from step configuration (png/jpg/pdf/pptx)
            var exportFormat = "png";
            if (stepConfig.TryGetValue("exportFormat", out var efVal))
            {
                var ef = efVal is JsonElement efEl ? efEl.GetString() : efVal?.ToString();
                if (!string.IsNullOrWhiteSpace(ef))
                    exportFormat = ef;
            }

            // Read design title from step configuration or build from previous output
            var title = "PixelAgents Design";
            if (stepConfig.TryGetValue("designTitle", out var dtVal))
            {
                var dt = dtVal is JsonElement dtEl ? dtEl.GetString() : dtVal?.ToString();
                if (!string.IsNullOrWhiteSpace(dt))
                    title = dt;
            }

            // Collect previous text output for autofill data
            var previousText = "";
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
                    if (!string.IsNullOrWhiteSpace(prevOutput.Title) && title == "PixelAgents Design")
                        title = prevOutput.Title;
                    break;
                }
                if (stepResults.TryGetValue(co, out var prevResult))
                {
                    previousText = prevResult.TextOutput ?? "";
                    break;
                }
            }

            // Replace template variables in title
            title = title
                .Replace("{previous_output}", previousText)
                .Replace("{step_number}", pm.StepOrder.ToString());

            // Read brand template ID from step config
            string? brandTemplateId = null;
            if (stepConfig.TryGetValue("brandTemplateId", out var btVal))
            {
                var bt = btVal is JsonElement btEl ? btEl.GetString() : btVal?.ToString();
                if (!string.IsNullOrWhiteSpace(bt))
                    brandTemplateId = bt;
            }

            // Read design type for non-autofill designs
            var designTypeName = "doc";
            if (stepConfig.TryGetValue("designType", out var dtnVal))
            {
                var dtn = dtnVal is JsonElement dtnEl ? dtnEl.GetString() : dtnVal?.ToString();
                if (!string.IsNullOrWhiteSpace(dtn))
                    designTypeName = dtn;
            }

            CanvaPublishResult canvaResult;
            var useAutofill = !string.IsNullOrWhiteSpace(brandTemplateId);

            try
            {
                if (useAutofill)
                {
                    await _logger.LogAsync(projectId, executionId, "info",
                        $"Creando diseno en Canva con autofill (template: {brandTemplateId})...",
                        pm.StepOrder, stepName);

                    // Build autofill data: use previous text as the main content field
                    var autofillData = new Dictionary<string, CanvaAutofillField>();

                    // Read custom autofill fields from step config
                    if (stepConfig.TryGetValue("autofillData", out var afVal) && afVal is JsonElement afEl)
                    {
                        foreach (var prop in afEl.EnumerateObject())
                        {
                            var fieldText = prop.Value.GetString() ?? "";
                            fieldText = fieldText.Replace("{previous_output}", previousText);
                            autofillData[prop.Name] = new CanvaAutofillField { type = "text", text = fieldText };
                        }
                    }

                    // If no custom data, use a default "body" field
                    if (autofillData.Count == 0)
                    {
                        autofillData["body"] = new CanvaAutofillField { type = "text", text = previousText };
                        autofillData["title"] = new CanvaAutofillField { type = "text", text = title };
                    }

                    // Upload images from previous step as assets for autofill
                    var mediaStepOrder = candidateOrders.FirstOrDefault(co =>
                        !stepModuleTypes.TryGetValue(co, out var mt) || mt != "Interaction");
                    if (mediaStepOrder > 0)
                    {
                        var prevStepExec = await db.StepExecutions
                            .Include(s => s.Files)
                            .FirstOrDefaultAsync(s => s.ExecutionId == executionId && s.StepOrder == mediaStepOrder);

                        if (prevStepExec?.Files is not null)
                        {
                            var imageFiles = prevStepExec.Files
                                .Where(f => f.ContentType.StartsWith("image/"))
                                .Take(5)
                                .ToList();

                            var imageNum = 1;
                            foreach (var imgFile in imageFiles)
                            {
                                var filePath = Path.Combine(_mediaRoot, imgFile.FilePath);
                                if (File.Exists(filePath))
                                {
                                    var imgBytes = await File.ReadAllBytesAsync(filePath);
                                    var uploadResult = await _canva.UploadAssetAsync(
                                        canvaConfig, imgBytes, $"image_{imageNum}");

                                    if (uploadResult.JobId is not null)
                                    {
                                        var uploadComplete = await _canva.WaitForAssetUploadAsync(
                                            canvaConfig, uploadResult.JobId);
                                        if (uploadComplete.IsSuccess && uploadComplete.AssetId is not null)
                                        {
                                            autofillData[$"image_{imageNum}"] = new CanvaAutofillField
                                            {
                                                type = "image",
                                                asset_id = uploadComplete.AssetId
                                            };
                                            await _logger.LogAsync(projectId, executionId, "info",
                                                $"Imagen {imageNum} subida a Canva (asset: {uploadComplete.AssetId})",
                                                pm.StepOrder, stepName);
                                        }
                                    }
                                    imageNum++;
                                }
                            }
                        }
                    }

                    canvaResult = await _canva.AutofillAndExportAsync(
                        canvaConfig, brandTemplateId!, title, autofillData, exportFormat);
                }
                else
                {
                    await _logger.LogAsync(projectId, executionId, "info",
                        $"Creando diseno en Canva (tipo: {designTypeName})...",
                        pm.StepOrder, stepName);

                    canvaResult = await _canva.CreateAndExportAsync(
                        canvaConfig, title, designTypeName, exportFormat);
                }
            }
            catch (Exception ex)
            {
                await _logger.LogAsync(projectId, executionId, "error",
                    $"Error publicando via Canva: {ex.Message}", pm.StepOrder, stepName);
                await FailStep(stepExecution, execution, $"Error Canva: {ex.Message}", db);
                throw;
            }

            // Save exported files
            if (canvaResult.IsSuccess && canvaResult.DownloadedFiles.Count > 0)
            {
                var workspace = ResolveWorkspacePath(execution.WorkspacePath);
                Directory.CreateDirectory(workspace);

                foreach (var file in canvaResult.DownloadedFiles)
                {
                    var fullPath = Path.Combine(workspace, file.FileName);
                    await File.WriteAllBytesAsync(fullPath, file.Data);

                    db.ExecutionFiles.Add(new ExecutionFile
                    {
                        Id = Guid.NewGuid(),
                        StepExecutionId = stepExecution.Id,
                        FileName = file.FileName,
                        ContentType = file.ContentType,
                        FilePath = file.FileName,
                        Direction = "output",
                        FileSize = file.Data.Length,
                        CreatedAt = DateTime.UtcNow
                    });
                }
            }

            // Build output text
            string outputText;
            if (canvaResult.IsSuccess)
            {
                outputText = $"Diseno creado en Canva\nDesign ID: {canvaResult.DesignId}";
                if (!string.IsNullOrEmpty(canvaResult.EditUrl))
                    outputText += $"\nEditar: {canvaResult.EditUrl}";
                if (!string.IsNullOrEmpty(canvaResult.ViewUrl))
                    outputText += $"\nVer: {canvaResult.ViewUrl}";
                outputText += $"\nArchivos exportados: {canvaResult.DownloadedFiles.Count} ({exportFormat})";
            }
            else
            {
                outputText = $"Error en Canva: {canvaResult.Error}";
            }

            var publishOutput = new StepOutput
            {
                Type = "text",
                Content = outputText,
                Summary = canvaResult.IsSuccess
                    ? $"Publicado via Canva - Design {canvaResult.DesignId}"
                    : $"Error Canva: {canvaResult.Error}",
                Items = [new OutputItem { Content = outputText, Label = "canva publish" }],
                Metadata = new Dictionary<string, object>
                {
                    ["publishType"] = useAutofill ? "autofill" : "create",
                    ["canvaDesignId"] = canvaResult.DesignId ?? "",
                    ["canvaEditUrl"] = canvaResult.EditUrl ?? "",
                    ["canvaViewUrl"] = canvaResult.ViewUrl ?? "",
                    ["exportFormat"] = exportFormat,
                    ["exportedFiles"] = canvaResult.DownloadedFiles.Count
                }
            };

            stepExecution.OutputData = JsonSerializer.Serialize(publishOutput);
            stepExecution.InputData = JsonSerializer.Serialize(new
            {
                title,
                exportFormat,
                useAutofill,
                brandTemplateId = brandTemplateId ?? "",
                canvaDesignId = canvaResult.DesignId ?? ""
            });

            if (canvaResult.IsSuccess)
            {
                stepExecution.Status = "Completed";
                stepExecution.CompletedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
                await _logger.LogStepProgressAsync(project.Id, pm.Id, "Completed");

                stepOutputs[pm.StepOrder] = publishOutput;
                stepModuleTypes[pm.StepOrder] = "Publish";
                stepResults[pm.StepOrder] = AiResult.Ok(outputText, new Dictionary<string, object>
                {
                    ["canvaDesignId"] = canvaResult.DesignId ?? "",
                    ["exportedFiles"] = canvaResult.DownloadedFiles.Count
                });

                await _logger.LogAsync(projectId, executionId, "success",
                    $"Diseno creado en Canva (design {canvaResult.DesignId}) - {canvaResult.DownloadedFiles.Count} archivos exportados",
                    pm.StepOrder, stepName);
            }
            else
            {
                await _logger.LogAsync(projectId, executionId, "error",
                    $"Error publicando via Canva: {canvaResult.Error}", pm.StepOrder, stepName);
                await FailStep(stepExecution, execution, $"Error Canva: {canvaResult.Error}", db);
                throw new InvalidOperationException(canvaResult.Error);
            }
        }

        // ── Interaction step handler (supports Telegram and WhatsApp) ──
        // Returns true if the pipeline should pause (waiting for response), false if it should continue.
        // When branchId is non-null, only that branch pauses — the main pipeline keeps running.
        private async Task<bool> HandleInteractionStepAsync(
            Project project, ProjectExecution execution, StepExecution stepExecution,
            ProjectModule pm, string? userInput,
            Dictionary<int, AiResult> stepResults,
            Dictionary<int, StepOutput> stepOutputs,
            Dictionary<int, string> stepModuleTypes,
            UserDbContext db, string tenantDbName,
            string? branchId = null)
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
            var sBranches = BuildStepBranches(project.ProjectModules, stepModuleTypes);
            var prevOrder = FindPreviousStepInBranch(
                pm.StepOrder, pm.BranchId, pm.BranchFromStep,
                stepOutputs, stepResults, sBranches);
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
                        var filePath = Path.Combine(ResolveWorkspacePath(execution.WorkspacePath), file.FilePath);
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
                await _logger.LogStepProgressAsync(project.Id, pm.Id, "Completed");

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

            stepExecution.Status = "WaitingForInput";
            stepExecution.InputData = JsonSerializer.Serialize(new { message });

            if (branchId is not null)
            {
                // Branch pause: save to PausedBranches without blocking main pipeline
                var branchPause = new PausedBranchState
                {
                    BranchId = branchId,
                    PausedAtStepOrder = pm.StepOrder,
                    PauseData = pauseState,
                };
                var existingBranches = DeserializePausedBranches(execution.PausedBranches);
                existingBranches.Add(branchPause);
                execution.PausedBranches = JsonSerializer.Serialize(existingBranches);
            }
            else
            {
                // Main pipeline pause: block execution
                execution.PausedAtStepOrder = pm.StepOrder;
                execution.PausedStepData = JsonSerializer.Serialize(pauseState);
                execution.Status = "WaitingForInput";
            }
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
                    BranchId = branchId,
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
                    BranchId = branchId,
                    CreatedAt = DateTime.UtcNow,
                    IsResolved = false,
                });
            }
            await _coreDb.SaveChangesAsync();

            var branchLabel = branchId is not null ? $" [rama '{branchId}']" : "";
            await _logger.LogAsync(project.Id, execution.Id, "info",
                $"Esperando respuesta de {channelName}...{branchLabel}", pm.StepOrder, stepName);
            return true; // caller should pause (branch or main)
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
            var stepBranches = BuildStepBranches(project.ProjectModules, stepModuleTypes);

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
            var workspacePath = ResolveWorkspacePath(execution.WorkspacePath);

            // Load previous execution summaries for context
            var previousSummaryContext = await BuildPreviousSummaryContextAsync(db, execution.ProjectId, execution.Id);

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
                    var stepLabel = GetStepLabel(pm, project.ProjectModules);
                    await _logger.LogAsync(project.Id, execution.Id, "info",
                        $"Ejecutando paso {stepLabel}: {stepName} ({pm.AiModule.ProviderType}/{pm.AiModule.ModelName})",
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

                    // Handle design step (Canva)
                    if (IsDesignStep(pm.AiModule))
                    {
                        await HandleCanvaPublishStepAsync(
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

                    var inputs = ResolveInputs(pm, pauseState.UserInput, stepResults, stepOutputs, pm.AiModule.ModuleType, pm.AiModule.ModelName, stepBranches);

                    if (pm.AiModule.ModuleType == "Text")
                    {
                        var context = new AiExecutionContext
                        {
                            ModuleType = pm.AiModule.ModuleType,
                            ModelName = pm.AiModule.ModelName,
                            ApiKey = apiKey,
                            Input = inputs[0],
                            ProjectContext = project.Context,
                            PreviousExecutionsSummary = previousSummaryContext,
                            Configuration = config,
                            InputFiles = await LoadModuleFilesAsync(pm, db),
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
                        // Load previous step files for image-to-image editing (resume path)
                        List<byte[]>? resumePrevFiles = null;
                        if (pm.InputMapping is not null)
                        {
                            var mappingJson = JsonSerializer.Deserialize<JsonElement>(pm.InputMapping);
                            if (mappingJson.TryGetProperty("field", out var fieldProp) && fieldProp.GetString() == "file")
                            {
                                var prevOrder = FindPreviousStepInBranch(
                                    pm.StepOrder, pm.BranchId, pm.BranchFromStep,
                                    stepOutputs, stepResults, stepBranches);
                                if (stepOutputs.TryGetValue(prevOrder, out var prevOutput) && prevOutput.Files.Count > 0)
                                {
                                    resumePrevFiles = new List<byte[]>();
                                    foreach (var pf in prevOutput.Files.Where(f => f.ContentType.StartsWith("image/")))
                                    {
                                        var pfPath = Path.Combine(workspacePath, $"step_{prevOrder}", pf.FileName);
                                        if (File.Exists(pfPath))
                                            resumePrevFiles.Add(await File.ReadAllBytesAsync(pfPath));
                                    }
                                }
                            }
                        }

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

                            var inputFiles = resumePrevFiles is { Count: > 0 }
                                ? resumePrevFiles
                                : await LoadModuleFilesAsync(pm, db);

                            var context = new AiExecutionContext
                            {
                                ModuleType = pm.AiModule.ModuleType,
                                ModelName = pm.AiModule.ModelName,
                                ApiKey = apiKey,
                                Input = singleInput,
                                ProjectContext = project.Context,
                                Configuration = config,
                                InputFiles = inputFiles,
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
                    await _logger.LogStepProgressAsync(project.Id, pm.Id, "Completed");
                }
                catch (Exception ex)
                {
                    stepExecution.Status = "Failed";
                    stepExecution.ErrorMessage = ex.Message;
                    stepExecution.CompletedAt = DateTime.UtcNow;
                    execution.Status = "Failed";
                    execution.CompletedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                    await _logger.LogStepProgressAsync(project.Id, pm.Id, "Failed");
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

            // Generate execution summary for future context
            try
            {
                await GenerateExecutionSummaryAsync(execution, stepOutputs, pauseState.UserInput, db);
            }
            catch (Exception ex)
            {
                await _logger.LogAsync(project.Id, execution.Id, "warning",
                    $"No se pudo generar resumen: {ex.Message}");
            }

            return execution;
        }

        // ═══════════ ORCHESTRATOR ═══════════

        /// <summary>
        /// Handles an orchestrator step: calls the AI to generate a task plan,
        /// then pauses for user review (first time) or auto-continues if already approved.
        /// </summary>
        private async Task<bool> HandleOrchestratorStepAsync(
            Project project, ProjectExecution execution, StepExecution stepExecution,
            ProjectModule pm, string? userInput,
            Dictionary<int, AiResult> stepResults,
            Dictionary<int, StepOutput> stepOutputs,
            Dictionary<int, string> stepModuleTypes,
            string workspacePath, string? previousSummaryContext,
            UserDbContext db, string tenantDbName, CancellationToken ct)
        {
            var stepName = pm.StepName ?? pm.AiModule.Name;
            await _logger.LogAsync(project.Id, execution.Id, "info",
                $"Orquestador: analizando tareas necesarias...", pm.StepOrder, stepName);

            // 1. Load ALL modules in the project (not just pipeline) so the orchestrator knows its tools
            var allProjectModules = await db.ProjectModules
                .Where(m => m.ProjectId == project.Id && m.IsActive && m.Id != pm.Id)
                .Include(m => m.AiModule)
                .ToListAsync();

            // Also load standalone modules (not assigned to this project but available)
            var allAiModules = await db.AiModules
                .Include(m => m.ApiKey)
                .Where(m => m.IsEnabled)
                .ToListAsync();

            // Build available module list: prefer project-assigned modules, also include all enabled modules
            var seenModuleIds = new HashSet<Guid>();
            var availableModules = new List<AvailableModule>();

            foreach (var projMod in allProjectModules)
            {
                if (IsInteractionStep(projMod.AiModule) || IsPublishStep(projMod.AiModule)
                    || IsDesignStep(projMod.AiModule) || IsOrchestratorStep(projMod.AiModule))
                    continue;

                if (seenModuleIds.Add(projMod.AiModule.Id))
                {
                    availableModules.Add(new AvailableModule
                    {
                        ModuleId = projMod.AiModule.Id.ToString(),
                        Name = projMod.AiModule.Name,
                        ModuleType = projMod.AiModule.ModuleType,
                        Provider = projMod.AiModule.ProviderType,
                        Model = projMod.AiModule.ModelName,
                        Description = projMod.AiModule.Description ?? "",
                    });
                }
            }

            // Add unassigned but enabled modules
            foreach (var aiMod in allAiModules)
            {
                if (seenModuleIds.Add(aiMod.Id) && aiMod.ApiKey is not null)
                {
                    if (aiMod.ModuleType == "Interaction" || aiMod.ModuleType == "Publish"
                        || aiMod.ModuleType == "Design" || aiMod.ModuleType == "Orchestrator")
                        continue;

                    availableModules.Add(new AvailableModule
                    {
                        ModuleId = aiMod.Id.ToString(),
                        Name = aiMod.Name,
                        ModuleType = aiMod.ModuleType,
                        Provider = aiMod.ProviderType,
                        Model = aiMod.ModelName,
                        Description = aiMod.Description ?? "",
                    });
                }
            }

            if (availableModules.Count == 0)
            {
                await FailStep(stepExecution, execution, "Orquestador: no hay modulos disponibles para asignar tareas", db);
                return false;
            }

            await _logger.LogAsync(project.Id, execution.Id, "info",
                $"Orquestador: {availableModules.Count} modulos disponibles", pm.StepOrder, stepName);

            // 2. Load previous feedback if any
            OrchestratorFeedback? feedback = null;
            var orchConfig = MergeConfiguration(pm.AiModule.Configuration, pm.Configuration);
            if (orchConfig.TryGetValue("orchestratorFeedback", out var fbVal))
            {
                try
                {
                    var fbJson = fbVal is JsonElement el ? el.GetRawText() : fbVal?.ToString();
                    if (fbJson is not null)
                        feedback = JsonSerializer.Deserialize<OrchestratorFeedback>(fbJson,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch { /* ignore parse errors */ }
            }

            // 3. Resolve input from previous step
            var input = userInput ?? "";
            if (pm.InputMapping is not null)
            {
                var inputs = ResolveInputs(pm, userInput, stepResults, stepOutputs, "Text", pm.AiModule.ModelName,
                    BuildStepBranches(project.ProjectModules, stepModuleTypes));
                input = string.Join("\n\n", inputs);
            }

            // 4. Call the AI to generate the plan
            var orchestratorApiKey = pm.AiModule.ApiKey?.EncryptedKey;
            if (string.IsNullOrEmpty(orchestratorApiKey))
            {
                await FailStep(stepExecution, execution, "Orquestador: API Key no configurada", db);
                return false;
            }

            var provider = _registry.GetProvider(pm.AiModule.ProviderType);
            if (provider is null)
            {
                await FailStep(stepExecution, execution, $"Orquestador: proveedor '{pm.AiModule.ProviderType}' no disponible", db);
                return false;
            }

            var systemPrompt = OrchestratorSchemaHelper.BuildOrchestratorPrompt(
                availableModules, feedback, project.Context);

            var aiContext = new AiExecutionContext
            {
                ModuleType = "Text",
                ModelName = pm.AiModule.ModelName,
                ApiKey = orchestratorApiKey,
                Input = $"Analyze the following input and create a task plan:\n\n{input}",
                ProjectContext = project.Context,
                PreviousExecutionsSummary = previousSummaryContext,
                SkipOutputSchema = true,
                Configuration = new Dictionary<string, object>
                {
                    ["systemPrompt"] = systemPrompt,
                    ["temperature"] = 0.3f,
                },
            };

            var result = await provider.ExecuteAsync(aiContext);
            stepExecution.EstimatedCost += result.EstimatedCost;

            if (!result.Success)
            {
                await _logger.LogAsync(project.Id, execution.Id, "error",
                    $"Orquestador: error al generar plan: {result.Error}", pm.StepOrder, stepName);
                await FailStep(stepExecution, execution, result.Error!, db);
                return false;
            }

            // 5. Parse the plan
            var plan = OrchestratorSchemaHelper.ParsePlan(result.TextOutput ?? "");
            if (plan is null)
            {
                await _logger.LogAsync(project.Id, execution.Id, "error",
                    $"Orquestador: el modelo no genero un plan JSON valido", pm.StepOrder, stepName);
                await FailStep(stepExecution, execution, "El orquestador no genero un plan valido", db);
                return false;
            }

            // 6. Validate plan
            var validationErrors = OrchestratorSchemaHelper.ValidatePlan(plan, availableModules);
            if (validationErrors.Count > 0)
            {
                var errMsg = "Plan invalido:\n" + string.Join("\n", validationErrors);
                await _logger.LogAsync(project.Id, execution.Id, "error", errMsg, pm.StepOrder, stepName);
                await FailStep(stepExecution, execution, errMsg, db);
                return false;
            }

            await _logger.LogAsync(project.Id, execution.Id, "info",
                $"Orquestador: plan generado con {plan.Tasks.Count} tarea(s): {plan.Summary}", pm.StepOrder, stepName);

            // Save the plan as step output
            var planJson = JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true });
            var stepDir = Path.Combine(workspacePath, $"step_{pm.StepOrder}");
            Directory.CreateDirectory(stepDir);
            await File.WriteAllTextAsync(Path.Combine(stepDir, "plan.json"), planJson);

            var planFile = new ExecutionFile
            {
                Id = Guid.NewGuid(),
                StepExecutionId = stepExecution.Id,
                FileName = "plan.json",
                ContentType = "application/json",
                FilePath = Path.Combine($"step_{pm.StepOrder}", "plan.json"),
                Direction = "Output",
                FileSize = System.Text.Encoding.UTF8.GetByteCount(planJson),
                CreatedAt = DateTime.UtcNow,
            };
            db.ExecutionFiles.Add(planFile);

            stepExecution.OutputData = planJson;
            stepExecution.InputData = JsonSerializer.Serialize(new { input, availableModuleCount = availableModules.Count });

            // 7. Decide: pause for review or auto-continue
            bool autoApproved = feedback is { Approved: true };

            if (autoApproved)
            {
                await _logger.LogAsync(project.Id, execution.Id, "info",
                    $"Orquestador: plan auto-aprobado (feedback previo validado)", pm.StepOrder, stepName);

                // Execute the sub-tasks immediately
                await ExecuteOrchestratorPlanAsync(
                    project, execution, stepExecution, pm, plan,
                    stepResults, stepOutputs, stepModuleTypes,
                    workspacePath, previousSummaryContext, db, tenantDbName, ct);
                return false; // don't pause, continue pipeline
            }

            // Pause for user review
            await _logger.LogAsync(project.Id, execution.Id, "info",
                $"Orquestador: esperando revision del plan...", pm.StepOrder, stepName);

            var pauseState = new PausedPipelineState
            {
                UserInput = userInput,
                StepOutputs = stepOutputs.ToDictionary(kv => kv.Key.ToString(), kv => JsonSerializer.Serialize(kv.Value)),
                StepModuleTypes = stepModuleTypes.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            };

            stepExecution.Status = "WaitingForReview";
            execution.PausedAtStepOrder = pm.StepOrder;
            execution.PausedStepData = JsonSerializer.Serialize(pauseState);
            execution.Status = "WaitingForReview";
            await db.SaveChangesAsync();

            return true; // pause pipeline
        }

        /// <summary>
        /// Executes the sub-tasks in an approved orchestrator plan.
        /// Each task calls the assigned module's provider directly.
        /// </summary>
        private async Task ExecuteOrchestratorPlanAsync(
            Project project, ProjectExecution execution, StepExecution orchestratorStep,
            ProjectModule orchestratorPm, OrchestratorPlan plan,
            Dictionary<int, AiResult> stepResults,
            Dictionary<int, StepOutput> stepOutputs,
            Dictionary<int, string> stepModuleTypes,
            string workspacePath, string? previousSummaryContext,
            UserDbContext db, string tenantDbName, CancellationToken ct)
        {
            var stepName = orchestratorPm.StepName ?? orchestratorPm.AiModule.Name;
            var orchestratorOutputItems = new List<OutputItem>();
            var orchestratorOutputFiles = new List<OutputFile>();
            decimal totalSubCost = 0;

            foreach (var task in plan.Tasks.OrderBy(t => t.Order))
            {
                if (ct.IsCancellationRequested) break;

                await _logger.LogAsync(project.Id, execution.Id, "info",
                    $"Orquestador [{task.TaskId}]: {task.Description} (modulo: {task.ModuleName})",
                    orchestratorPm.StepOrder, stepName);

                // Broadcast task started
                await _logger.LogTaskProgressAsync(project.Id, new OrchestratorTaskProgressEntry(
                    task.TaskId, task.Description, task.ModuleName, task.ModuleType,
                    task.Order, "running", null, null, null, DateTime.UtcNow));

                // Load the target module
                var targetModule = await db.AiModules
                    .Include(m => m.ApiKey)
                    .FirstOrDefaultAsync(m => m.Id == Guid.Parse(task.ModuleId));

                if (targetModule is null)
                {
                    await _logger.LogAsync(project.Id, execution.Id, "error",
                        $"Orquestador [{task.TaskId}]: modulo {task.ModuleId} no encontrado", orchestratorPm.StepOrder, stepName);
                    await _logger.LogTaskProgressAsync(project.Id, new OrchestratorTaskProgressEntry(
                        task.TaskId, task.Description, task.ModuleName, task.ModuleType,
                        task.Order, "error", null, null, "Modulo no encontrado", DateTime.UtcNow));
                    orchestratorOutputItems.Add(new OutputItem { Content = $"ERROR: modulo no encontrado", Label = task.TaskId });
                    continue;
                }

                if (targetModule.ApiKey is null || string.IsNullOrEmpty(targetModule.ApiKey.EncryptedKey))
                {
                    await _logger.LogAsync(project.Id, execution.Id, "error",
                        $"Orquestador [{task.TaskId}]: modulo '{targetModule.Name}' no tiene API Key", orchestratorPm.StepOrder, stepName);
                    await _logger.LogTaskProgressAsync(project.Id, new OrchestratorTaskProgressEntry(
                        task.TaskId, task.Description, task.ModuleName, task.ModuleType,
                        task.Order, "error", null, null, "API Key no configurada", DateTime.UtcNow));
                    orchestratorOutputItems.Add(new OutputItem { Content = $"ERROR: API Key no configurada", Label = task.TaskId });
                    continue;
                }

                var provider = _registry.GetProvider(targetModule.ProviderType);
                if (provider is null)
                {
                    await _logger.LogAsync(project.Id, execution.Id, "error",
                        $"Orquestador [{task.TaskId}]: proveedor '{targetModule.ProviderType}' no disponible", orchestratorPm.StepOrder, stepName);
                    await _logger.LogTaskProgressAsync(project.Id, new OrchestratorTaskProgressEntry(
                        task.TaskId, task.Description, task.ModuleName, task.ModuleType,
                        task.Order, "error", null, null, "Proveedor no disponible", DateTime.UtcNow));
                    orchestratorOutputItems.Add(new OutputItem { Content = $"ERROR: proveedor no disponible", Label = task.TaskId });
                    continue;
                }

                var moduleConfig = !string.IsNullOrEmpty(targetModule.Configuration)
                    ? JsonSerializer.Deserialize<Dictionary<string, object>>(targetModule.Configuration) ?? new()
                    : new Dictionary<string, object>();

                // Truncate input if needed for image/video models
                var taskInput = task.Input;
                if (targetModule.ModuleType is "Image" or "Video" or "VideoSearch" or "VideoEdit")
                {
                    var maxLen = InputAdapter.GetMaxPromptLength(targetModule.ModelName);
                    if (taskInput.Length > maxLen)
                        taskInput = InputAdapter.TruncateAtWord(taskInput, maxLen);
                }

                var aiContext = new AiExecutionContext
                {
                    ModuleType = targetModule.ModuleType,
                    ModelName = targetModule.ModelName,
                    ApiKey = targetModule.ApiKey.EncryptedKey,
                    Input = taskInput,
                    ProjectContext = project.Context,
                    PreviousExecutionsSummary = previousSummaryContext,
                    Configuration = moduleConfig,
                };

                try
                {
                    var result = await provider.ExecuteAsync(aiContext);
                    totalSubCost += result.EstimatedCost;

                    if (!result.Success)
                    {
                        await _logger.LogAsync(project.Id, execution.Id, "error",
                            $"Orquestador [{task.TaskId}]: error: {result.Error}", orchestratorPm.StepOrder, stepName);
                        await _logger.LogTaskProgressAsync(project.Id, new OrchestratorTaskProgressEntry(
                            task.TaskId, task.Description, task.ModuleName, task.ModuleType,
                            task.Order, "error", null, null, result.Error, DateTime.UtcNow));
                        orchestratorOutputItems.Add(new OutputItem { Content = $"ERROR: {result.Error}", Label = task.TaskId });
                        continue;
                    }

                    // Handle text output
                    if (result.TextOutput is not null)
                    {
                        var parsed = OutputSchemaHelper.ParseTextOutput(result.TextOutput);
                        orchestratorOutputItems.Add(new OutputItem
                        {
                            Content = parsed.Content ?? result.TextOutput,
                            Label = $"{task.TaskId}: {task.Description}"
                        });

                        await _logger.LogAsync(project.Id, execution.Id, "success",
                            $"Orquestador [{task.TaskId}]: texto generado", orchestratorPm.StepOrder, stepName);
                    }

                    // Handle file output
                    if (result.FileOutput is not null)
                    {
                        var taskDir = Path.Combine(workspacePath, $"step_{orchestratorPm.StepOrder}", task.TaskId);
                        Directory.CreateDirectory(taskDir);

                        var ext = GetExtension(result.ContentType ?? "application/octet-stream");
                        var fileName = $"output{ext}";
                        var filePath = Path.Combine(taskDir, fileName);
                        await File.WriteAllBytesAsync(filePath, result.FileOutput);

                        var execFile = new ExecutionFile
                        {
                            Id = Guid.NewGuid(),
                            StepExecutionId = orchestratorStep.Id,
                            FileName = $"{task.TaskId}_{fileName}",
                            ContentType = result.ContentType ?? "application/octet-stream",
                            FilePath = Path.Combine($"step_{orchestratorPm.StepOrder}", task.TaskId, fileName),
                            Direction = "Output",
                            FileSize = result.FileOutput.Length,
                            CreatedAt = DateTime.UtcNow,
                        };
                        db.ExecutionFiles.Add(execFile);

                        orchestratorOutputFiles.Add(new OutputFile
                        {
                            FileId = execFile.Id,
                            FileName = execFile.FileName,
                            ContentType = execFile.ContentType,
                            FileSize = execFile.FileSize,
                        });

                        await _logger.LogAsync(project.Id, execution.Id, "success",
                            $"Orquestador [{task.TaskId}]: archivo generado ({result.ContentType}, {result.FileOutput.Length} bytes)",
                            orchestratorPm.StepOrder, stepName);
                    }

                    // Broadcast task completed
                    var fileUrl = result.FileOutput is not null
                        ? Path.Combine($"step_{orchestratorPm.StepOrder}", task.TaskId, $"output{GetExtension(result.ContentType ?? "application/octet-stream")}")
                        : null;
                    await _logger.LogTaskProgressAsync(project.Id, new OrchestratorTaskProgressEntry(
                        task.TaskId, task.Description, task.ModuleName, task.ModuleType,
                        task.Order, "completed", fileUrl, result.ContentType, null, DateTime.UtcNow));
                }
                catch (Exception ex)
                {
                    await _logger.LogAsync(project.Id, execution.Id, "error",
                        $"Orquestador [{task.TaskId}]: excepcion: {ex.Message}", orchestratorPm.StepOrder, stepName);
                    await _logger.LogTaskProgressAsync(project.Id, new OrchestratorTaskProgressEntry(
                        task.TaskId, task.Description, task.ModuleName, task.ModuleType,
                        task.Order, "error", null, null, ex.Message, DateTime.UtcNow));
                    orchestratorOutputItems.Add(new OutputItem { Content = $"ERROR: {ex.Message}", Label = task.TaskId });
                }
            }

            // Build combined output
            var combinedOutput = new StepOutput
            {
                Type = "orchestrator",
                Title = plan.Summary,
                Content = $"Plan ejecutado: {plan.Tasks.Count} tareas, {orchestratorOutputItems.Count} resultados",
                Summary = plan.Summary,
                Items = orchestratorOutputItems,
                Files = orchestratorOutputFiles,
                Metadata = new Dictionary<string, object>
                {
                    ["tasks"] = plan.Tasks.Select(t => new Dictionary<string, object>
                    {
                        ["taskId"] = t.TaskId,
                        ["description"] = t.Description,
                        ["moduleName"] = t.ModuleName,
                        ["moduleType"] = t.ModuleType,
                        ["order"] = t.Order,
                    }).ToList()
                }
            };

            orchestratorStep.EstimatedCost += totalSubCost;
            orchestratorStep.OutputData = JsonSerializer.Serialize(combinedOutput);
            orchestratorStep.Status = "Completed";
            orchestratorStep.CompletedAt = DateTime.UtcNow;

            stepOutputs[orchestratorPm.StepOrder] = combinedOutput;
            stepModuleTypes[orchestratorPm.StepOrder] = "Orchestrator";

            await db.SaveChangesAsync();

            await _logger.LogAsync(project.Id, execution.Id, "success",
                $"Orquestador: todas las tareas completadas (costo sub-tareas: ${totalSubCost:F4})",
                orchestratorPm.StepOrder, stepName);
        }

        /// <summary>
        /// Resume pipeline after user reviews the orchestrator plan.
        /// If approved, executes the sub-tasks and continues the pipeline.
        /// If rejected with comment, stores feedback and marks as failed.
        /// </summary>
        public async Task<ProjectExecution> ResumeFromOrchestratorAsync(
            Guid executionId, bool approved, string? comment,
            UserDbContext db, string tenantDbName, CancellationToken ct = default)
        {
            _logger = _baseLogger.WithDb(db);

            var execution = await db.ProjectExecutions
                .Include(e => e.StepExecutions.OrderBy(s => s.StepOrder))
                .FirstOrDefaultAsync(e => e.Id == executionId && e.Status == "WaitingForReview")
                ?? throw new InvalidOperationException("Ejecucion no encontrada o no esta esperando revision");

            var project = await db.Projects
                .Include(p => p.ProjectModules.Where(pm => pm.IsActive).OrderBy(pm => pm.StepOrder))
                    .ThenInclude(pm => pm.AiModule)
                        .ThenInclude(m => m.ApiKey)
                .FirstOrDefaultAsync(p => p.Id == execution.ProjectId)
                ?? throw new InvalidOperationException("Proyecto no encontrado");

            var pausedStep = execution.PausedAtStepOrder
                ?? throw new InvalidOperationException("No hay paso pausado registrado");

            var orchestratorPm = project.ProjectModules.FirstOrDefault(pm => pm.StepOrder == pausedStep)
                ?? throw new InvalidOperationException($"Paso {pausedStep} no encontrado en el proyecto");

            var orchestratorStepExec = execution.StepExecutions
                .FirstOrDefault(s => s.ProjectModuleId == orchestratorPm.Id && s.Status == "WaitingForReview")
                ?? throw new InvalidOperationException("StepExecution del orquestador no encontrada");

            // Load the plan from step output
            var plan = OrchestratorSchemaHelper.ParsePlan(orchestratorStepExec.OutputData ?? "")
                ?? throw new InvalidOperationException("No se pudo leer el plan del orquestador");

            // Store feedback on the ProjectModule
            var pmConfig = !string.IsNullOrEmpty(orchestratorPm.Configuration)
                ? JsonSerializer.Deserialize<Dictionary<string, object>>(orchestratorPm.Configuration) ?? new()
                : new Dictionary<string, object>();

            OrchestratorFeedback feedback;
            if (pmConfig.TryGetValue("orchestratorFeedback", out var existingFb))
            {
                try
                {
                    var fbJson = existingFb is JsonElement el ? el.GetRawText() : existingFb?.ToString();
                    feedback = JsonSerializer.Deserialize<OrchestratorFeedback>(fbJson ?? "{}",
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
                }
                catch { feedback = new OrchestratorFeedback(); }
            }
            else
            {
                feedback = new OrchestratorFeedback();
            }

            if (!string.IsNullOrWhiteSpace(comment))
            {
                feedback.Comments.Add(new OrchestratorComment
                {
                    Text = comment.Trim(),
                    CreatedAt = DateTime.UtcNow,
                });
            }

            if (approved)
            {
                feedback.Approved = true;
                await _logger.LogAsync(project.Id, execution.Id, "info",
                    "Orquestador: plan aprobado por el usuario" +
                    (!string.IsNullOrWhiteSpace(comment) ? $" con comentario: {comment}" : ""),
                    pausedStep, orchestratorPm.StepName ?? orchestratorPm.AiModule.Name);
            }
            else
            {
                feedback.Approved = false;
                await _logger.LogAsync(project.Id, execution.Id, "info",
                    $"Orquestador: plan rechazado. Comentario: {comment ?? "(sin comentario)"}",
                    pausedStep, orchestratorPm.StepName ?? orchestratorPm.AiModule.Name);
            }

            // Save feedback to ProjectModule.Configuration
            pmConfig["orchestratorFeedback"] = JsonSerializer.Deserialize<JsonElement>(
                JsonSerializer.Serialize(feedback));
            orchestratorPm.Configuration = JsonSerializer.Serialize(pmConfig);
            await db.SaveChangesAsync();

            if (!approved)
            {
                // Mark execution as failed so user can re-run
                orchestratorStepExec.Status = "Rejected";
                orchestratorStepExec.ErrorMessage = comment ?? "Plan rechazado por el usuario";
                orchestratorStepExec.CompletedAt = DateTime.UtcNow;

                execution.PausedAtStepOrder = null;
                execution.PausedStepData = null;
                execution.Status = "Failed";
                execution.CompletedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();

                await _logger.LogAsync(project.Id, execution.Id, "warning",
                    "Pipeline detenido: plan del orquestador rechazado. El comentario se aplicara en la proxima ejecucion.");
                return execution;
            }

            // Approved — execute the sub-tasks
            var pauseState = JsonSerializer.Deserialize<PausedPipelineState>(execution.PausedStepData ?? "{}")
                ?? new PausedPipelineState();

            // Restore previous step outputs
            var stepOutputs = new Dictionary<int, StepOutput>();
            var stepModuleTypes = new Dictionary<int, string>();
            var stepResults = new Dictionary<int, AiResult>();

            foreach (var kv in pauseState.StepOutputs)
            {
                if (int.TryParse(kv.Key, out var key))
                    stepOutputs[key] = JsonSerializer.Deserialize<StepOutput>(kv.Value) ?? new();
            }
            foreach (var kv in pauseState.StepModuleTypes)
            {
                if (int.TryParse(kv.Key, out var key))
                    stepModuleTypes[key] = kv.Value;
            }

            var relativeWorkspace = execution.WorkspacePath;
            var workspacePath = ResolveWorkspacePath(relativeWorkspace);
            var previousSummaryContext = await BuildPreviousSummaryContextAsync(db, project.Id, execution.Id);

            execution.Status = "Running";
            execution.PausedAtStepOrder = null;
            execution.PausedStepData = null;
            await db.SaveChangesAsync();

            await ExecuteOrchestratorPlanAsync(
                project, execution, orchestratorStepExec, orchestratorPm, plan,
                stepResults, stepOutputs, stepModuleTypes,
                workspacePath, previousSummaryContext, db, tenantDbName, ct);

            // Continue with remaining pipeline steps after orchestrator
            var allModules = project.ProjectModules.ToList();
            var mainModules = allModules.Where(m => m.BranchId == "main").OrderBy(m => m.StepOrder).ToList();
            var remainingSteps = mainModules.Where(m => m.StepOrder > pausedStep).ToList();

            if (remainingSteps.Count > 0)
            {
                await _logger.LogAsync(project.Id, execution.Id, "info",
                    $"Continuando pipeline con {remainingSteps.Count} paso(s) restantes...");

                // Continue the pipeline from the next step
                var branchModules = allModules.Where(m => m.BranchId != "main")
                    .GroupBy(m => m.BranchId)
                    .ToDictionary(g => g.Key, g => g.OrderBy(m => m.StepOrder).ToList());

                for (var mi = 0; mi < remainingSteps.Count; mi++)
                {
                    var pm = remainingSteps[mi];
                    var nextModule = mi + 1 < remainingSteps.Count ? remainingSteps[mi + 1] : null;

                    if (ct.IsCancellationRequested)
                    {
                        execution.Status = "Cancelled";
                        execution.CompletedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync();
                        return execution;
                    }

                    var stepExec = new StepExecution
                    {
                        Id = Guid.NewGuid(),
                        ExecutionId = execution.Id,
                        ProjectModuleId = pm.Id,
                        StepOrder = pm.StepOrder,
                        Status = "Running",
                        CreatedAt = DateTime.UtcNow,
                    };
                    db.StepExecutions.Add(stepExec);
                    await db.SaveChangesAsync();

                    try
                    {
                        // Delegate to the appropriate step handler
                        if (IsInteractionStep(pm.AiModule))
                        {
                            var shouldPause = await HandleInteractionStepAsync(
                                project, execution, stepExec, pm, pauseState.UserInput,
                                stepResults, stepOutputs, stepModuleTypes, db, tenantDbName);
                            if (shouldPause) return execution;
                            continue;
                        }
                        if (IsPublishStep(pm.AiModule))
                        {
                            await HandlePublishStepAsync(project, execution, stepExec, pm,
                                stepResults, stepOutputs, stepModuleTypes, db, tenantDbName);
                            continue;
                        }
                        if (IsDesignStep(pm.AiModule))
                        {
                            await HandleCanvaPublishStepAsync(project, execution, stepExec, pm,
                                stepResults, stepOutputs, stepModuleTypes, db, tenantDbName);
                            continue;
                        }

                        // For regular AI steps, use generic execution
                        var apiKey = pm.AiModule.ApiKey?.EncryptedKey
                            ?? throw new InvalidOperationException($"Paso {pm.StepOrder}: ApiKey no configurada");
                        var prov = _registry.GetProvider(pm.AiModule.ProviderType)
                            ?? throw new InvalidOperationException($"Paso {pm.StepOrder}: Proveedor no disponible");

                        var config = MergeConfiguration(pm.AiModule.Configuration, pm.Configuration);
                        var inputs = ResolveInputs(pm, pauseState.UserInput, stepResults, stepOutputs,
                            pm.AiModule.ModuleType, pm.AiModule.ModelName,
                            BuildStepBranches(project.ProjectModules, stepModuleTypes));

                        var ctx = new AiExecutionContext
                        {
                            ModuleType = pm.AiModule.ModuleType,
                            ModelName = pm.AiModule.ModelName,
                            ApiKey = apiKey,
                            Input = inputs[0],
                            ProjectContext = project.Context,
                            PreviousExecutionsSummary = previousSummaryContext,
                            Configuration = config,
                        };

                        var result = await prov.ExecuteAsync(ctx);
                        stepResults[pm.StepOrder] = result;
                        stepModuleTypes[pm.StepOrder] = pm.AiModule.ModuleType;
                        stepExec.EstimatedCost += result.EstimatedCost;

                        if (!result.Success)
                        {
                            await FailStep(stepExec, execution, result.Error!, db);
                            return execution;
                        }

                        if (result.TextOutput is not null)
                        {
                            var stepOutput = OutputSchemaHelper.ParseTextOutput(result.TextOutput, result.Metadata);
                            stepOutputs[pm.StepOrder] = stepOutput;
                            stepExec.OutputData = JsonSerializer.Serialize(stepOutput);
                        }

                        stepExec.Status = "Completed";
                        stepExec.CompletedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync();
                    }
                    catch (Exception ex)
                    {
                        stepExec.Status = "Failed";
                        stepExec.ErrorMessage = ex.Message;
                        stepExec.CompletedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync();
                        execution.Status = "Failed";
                        execution.CompletedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync();
                        return execution;
                    }
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

        // State for a single paused branch
        private class PausedBranchState
        {
            public string BranchId { get; set; } = default!;
            public int PausedAtStepOrder { get; set; }
            public PausedPipelineState PauseData { get; set; } = new();
        }

        private static List<PausedBranchState> DeserializePausedBranches(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<PausedBranchState>();
            try { return JsonSerializer.Deserialize<List<PausedBranchState>>(json) ?? new List<PausedBranchState>(); }
            catch { return new List<PausedBranchState>(); }
        }

        private enum BranchResult { Completed, Failed, Paused, Cancelled }

        /// <summary>
        /// Executes a list of branch steps. Shared by initial execution and resume-after-interaction.
        /// </summary>
        private async Task<BranchResult> ExecuteBranchStepsAsync(
            Project project, ProjectExecution execution, string branchId,
            List<ProjectModule> branchSteps, string? userInput,
            Dictionary<int, AiResult> stepResults,
            Dictionary<int, StepOutput> stepOutputs,
            Dictionary<int, string> stepModuleTypes,
            string workspacePath, string? previousSummaryContext,
            UserDbContext db, string tenantDbName, CancellationToken ct)
        {
            for (var bi = 0; bi < branchSteps.Count; bi++)
            {
                var bpm = branchSteps[bi];
                var nextBranchModule = bi + 1 < branchSteps.Count ? branchSteps[bi + 1] : null;

                if (ct.IsCancellationRequested) return BranchResult.Cancelled;

                var branchStepExec = new StepExecution
                {
                    Id = Guid.NewGuid(),
                    ExecutionId = execution.Id,
                    ProjectModuleId = bpm.Id,
                    StepOrder = bpm.StepOrder,
                    Status = "Running",
                    CreatedAt = DateTime.UtcNow,
                };
                db.StepExecutions.Add(branchStepExec);
                await db.SaveChangesAsync();
                await _logger.LogStepProgressAsync(project.Id, bpm.Id, "Running");

                try
                {
                    var bStepName = bpm.StepName ?? bpm.AiModule.Name;
                    var bStepLabel = GetStepLabel(bpm, project.ProjectModules);
                    await _logger.LogAsync(project.Id, execution.Id, "info",
                        $"[{branchId}] Ejecutando paso {bStepLabel}: {bStepName} ({bpm.AiModule.ProviderType}/{bpm.AiModule.ModelName})",
                        bpm.StepOrder, bStepName);

                    var bConfig = MergeConfiguration(bpm.AiModule.Configuration, bpm.Configuration);

                    // Interaction steps in branches — pause only the branch
                    if (IsInteractionStep(bpm.AiModule))
                    {
                        var shouldPause = await HandleInteractionStepAsync(
                            project, execution, branchStepExec, bpm, userInput,
                            stepResults, stepOutputs, stepModuleTypes, db, tenantDbName,
                            branchId: branchId);
                        if (shouldPause) return BranchResult.Paused;
                        branchStepExec.Status = "Completed";
                        branchStepExec.CompletedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync();
                        continue;
                    }

                    // Publish steps in branches
                    if (IsPublishStep(bpm.AiModule))
                    {
                        await HandlePublishStepAsync(
                            project, execution, branchStepExec, bpm,
                            stepResults, stepOutputs, stepModuleTypes, db, tenantDbName);
                        continue;
                    }

                    // Design steps in branches
                    if (IsDesignStep(bpm.AiModule))
                    {
                        await HandleCanvaPublishStepAsync(
                            project, execution, branchStepExec, bpm,
                            stepResults, stepOutputs, stepModuleTypes, db, tenantDbName);
                        continue;
                    }

                    var bApiKey = bpm.AiModule.ApiKey?.EncryptedKey
                        ?? throw new InvalidOperationException($"[{branchId}] Paso {bpm.StepOrder}: ApiKey no configurada");
                    var bProvider = _registry.GetProvider(bpm.AiModule.ProviderType)
                        ?? throw new InvalidOperationException($"[{branchId}] Paso {bpm.StepOrder}: Proveedor no disponible");

                    var bInputs = ResolveInputs(bpm, userInput, stepResults, stepOutputs, bpm.AiModule.ModuleType, bpm.AiModule.ModelName,
                        BuildStepBranches(project.ProjectModules, stepModuleTypes));

                    if (bpm.AiModule.ModuleType == "Text")
                    {
                        if (nextBranchModule is not null)
                        {
                            var nml = InputAdapter.GetMaxPromptLength(nextBranchModule.AiModule.ModelName);
                            var rule = $"\n\nREGLA DE LONGITUD: maximo {nml} caracteres por item.";
                            if (bConfig.TryGetValue("systemPrompt", out var bex) && bex is string bs)
                                bConfig["systemPrompt"] = bs + rule;
                            else
                                bConfig["systemPrompt"] = rule;
                        }
                        InjectImageCountRule(bConfig);

                        var bCtx = new AiExecutionContext
                        {
                            ModuleType = bpm.AiModule.ModuleType, ModelName = bpm.AiModule.ModelName,
                            ApiKey = bApiKey, Input = bInputs[0], ProjectContext = project.Context,
                            PreviousExecutionsSummary = previousSummaryContext, Configuration = bConfig,
                            InputFiles = await LoadModuleFilesAsync(bpm, db),
                        };

                        var bResult = await bProvider.ExecuteAsync(bCtx);
                        stepResults[bpm.StepOrder] = bResult;
                        stepModuleTypes[bpm.StepOrder] = bpm.AiModule.ModuleType;
                        branchStepExec.EstimatedCost += bResult.EstimatedCost;

                        if (!bResult.Success) throw new InvalidOperationException(bResult.Error ?? "Error en texto");

                        var bOutput = OutputSchemaHelper.ParseTextOutput(bResult.TextOutput ?? "", bResult.Metadata);
                        stepOutputs[bpm.StepOrder] = bOutput;

                        var bDir = Path.Combine(workspacePath, $"branch_{branchId}_step_{bpm.StepOrder}");
                        Directory.CreateDirectory(bDir);
                        await File.WriteAllTextAsync(Path.Combine(bDir, "output.json"), bResult.TextOutput ?? "");
                        db.ExecutionFiles.Add(new ExecutionFile
                        {
                            Id = Guid.NewGuid(), StepExecutionId = branchStepExec.Id,
                            FileName = "output.json", ContentType = "application/json",
                            FilePath = Path.Combine($"branch_{branchId}_step_{bpm.StepOrder}", "output.json"),
                            Direction = "Output",
                            FileSize = System.Text.Encoding.UTF8.GetByteCount(bResult.TextOutput ?? ""),
                            CreatedAt = DateTime.UtcNow,
                        });
                        branchStepExec.OutputData = JsonSerializer.Serialize(bOutput);
                    }
                    else if (bpm.AiModule.ModuleType == "Image")
                    {
                        var bImgPrompt = "";
                        if (bConfig.TryGetValue("imagePrompt", out var bip))
                            bImgPrompt = bip is JsonElement bipEl ? bipEl.GetString() ?? "" : bip?.ToString() ?? "";
                        if (!string.IsNullOrWhiteSpace(bImgPrompt))
                            bInputs = new List<string> { bImgPrompt };

                        List<byte[]>? bPrevFiles = null;
                        if (bpm.InputMapping is not null)
                        {
                            var bMap = JsonSerializer.Deserialize<JsonElement>(bpm.InputMapping);
                            if (bMap.TryGetProperty("field", out var bf) && bf.GetString() == "file")
                            {
                                var bBranches = BuildStepBranches(project.ProjectModules, stepModuleTypes);
                                var bPrevOrd = FindPreviousStepInBranch(
                                    bpm.StepOrder, bpm.BranchId, bpm.BranchFromStep,
                                    stepOutputs, stepResults, bBranches);
                                if (stepOutputs.TryGetValue(bPrevOrd, out var bPrev) && bPrev.Files.Count > 0)
                                {
                                    bPrevFiles = new List<byte[]>();
                                    foreach (var pf in bPrev.Files.Where(f => f.ContentType.StartsWith("image/")))
                                    {
                                        var pfPath = Path.Combine(workspacePath, $"branch_{branchId}_step_{bPrevOrd}", pf.FileName);
                                        if (!File.Exists(pfPath))
                                            pfPath = Path.Combine(workspacePath, $"step_{bPrevOrd}", pf.FileName);
                                        if (File.Exists(pfPath))
                                            bPrevFiles.Add(await File.ReadAllBytesAsync(pfPath));
                                    }
                                }
                            }
                        }

                        var bOutputFiles = new List<OutputFile>();
                        for (var bi2 = 0; bi2 < bInputs.Count; bi2++)
                        {
                            var bSingle = bInputs[bi2];
                            var bMaxLen = InputAdapter.GetMaxPromptLength(bpm.AiModule.ModelName);
                            if (bSingle.Length > bMaxLen) bSingle = InputAdapter.TruncateAtWord(bSingle, bMaxLen);

                            var bImgCtx = new AiExecutionContext
                            {
                                ModuleType = bpm.AiModule.ModuleType, ModelName = bpm.AiModule.ModelName,
                                ApiKey = bApiKey, Input = bSingle, ProjectContext = project.Context,
                                Configuration = bConfig,
                                InputFiles = bPrevFiles is { Count: > 0 } ? bPrevFiles : await LoadModuleFilesAsync(bpm, db),
                            };

                            var bResult = await bProvider.ExecuteAsync(bImgCtx);
                            branchStepExec.EstimatedCost += bResult.EstimatedCost;
                            if (!bResult.Success) throw new InvalidOperationException(bResult.Error ?? "Error en imagen");

                            if (bResult.FileOutput is not null)
                            {
                                var bDir = Path.Combine(workspacePath, $"branch_{branchId}_step_{bpm.StepOrder}");
                                Directory.CreateDirectory(bDir);
                                var ext = GetExtension(bResult.ContentType ?? "application/octet-stream");
                                var fn = bInputs.Count > 1 ? $"output_{bi2 + 1}{ext}" : $"output{ext}";
                                await File.WriteAllBytesAsync(Path.Combine(bDir, fn), bResult.FileOutput);

                                var ef = new ExecutionFile
                                {
                                    Id = Guid.NewGuid(), StepExecutionId = branchStepExec.Id,
                                    FileName = fn, ContentType = bResult.ContentType ?? "application/octet-stream",
                                    FilePath = Path.Combine($"branch_{branchId}_step_{bpm.StepOrder}", fn),
                                    Direction = "Output", FileSize = bResult.FileOutput.Length,
                                    CreatedAt = DateTime.UtcNow,
                                };
                                db.ExecutionFiles.Add(ef);
                                bOutputFiles.Add(new OutputFile { FileId = ef.Id, FileName = fn, ContentType = ef.ContentType, FileSize = ef.FileSize });
                            }
                            stepResults[bpm.StepOrder] = bResult;
                        }

                        stepModuleTypes[bpm.StepOrder] = bpm.AiModule.ModuleType;
                        var bImgOutput = OutputSchemaHelper.BuildImageOutput(bOutputFiles, bpm.AiModule.ModelName);
                        stepOutputs[bpm.StepOrder] = bImgOutput;
                        branchStepExec.OutputData = JsonSerializer.Serialize(bImgOutput);
                        await db.SaveChangesAsync();
                    }
                    else if (bpm.AiModule.ModuleType == "Video")
                    {
                        var bVideoPrompt = "";
                        if (bConfig.TryGetValue("videoPrompt", out var bvp))
                            bVideoPrompt = bvp is JsonElement bvpEl ? bvpEl.GetString() ?? "" : bvp?.ToString() ?? "";
                        if (string.IsNullOrWhiteSpace(bVideoPrompt))
                            throw new InvalidOperationException($"[{branchId}] Prompt de video obligatorio");

                        var bVidCtx = new AiExecutionContext
                        {
                            ModuleType = bpm.AiModule.ModuleType, ModelName = bpm.AiModule.ModelName,
                            ApiKey = bApiKey, Input = bVideoPrompt,
                            ProjectContext = project.Context, Configuration = bConfig,
                        };
                        var bResult = await bProvider.ExecuteAsync(bVidCtx);
                        stepResults[bpm.StepOrder] = bResult;
                        stepModuleTypes[bpm.StepOrder] = bpm.AiModule.ModuleType;
                        branchStepExec.EstimatedCost += bResult.EstimatedCost;
                        if (!bResult.Success) throw new InvalidOperationException(bResult.Error ?? "Error en video");

                        if (bResult.FileOutput is not null)
                        {
                            var bDir = Path.Combine(workspacePath, $"branch_{branchId}_step_{bpm.StepOrder}");
                            Directory.CreateDirectory(bDir);
                            var ext = GetExtension(bResult.ContentType ?? "video/mp4");
                            var fn = $"output{ext}";
                            await File.WriteAllBytesAsync(Path.Combine(bDir, fn), bResult.FileOutput);
                            db.ExecutionFiles.Add(new ExecutionFile
                            {
                                Id = Guid.NewGuid(), StepExecutionId = branchStepExec.Id,
                                FileName = fn, ContentType = bResult.ContentType ?? "video/mp4",
                                FilePath = Path.Combine($"branch_{branchId}_step_{bpm.StepOrder}", fn),
                                Direction = "Output", FileSize = bResult.FileOutput.Length,
                                CreatedAt = DateTime.UtcNow,
                            });
                        }
                        var bVidOutput = new StepOutput { Type = "video" };
                        stepOutputs[bpm.StepOrder] = bVidOutput;
                        branchStepExec.OutputData = JsonSerializer.Serialize(bVidOutput);
                        await db.SaveChangesAsync();
                    }
                    else if (bpm.AiModule.ModuleType == "VideoSearch")
                    {
                        var bSearchQuery = bInputs[0];
                        if (string.IsNullOrWhiteSpace(bSearchQuery))
                            throw new InvalidOperationException($"[{branchId}] Texto de busqueda obligatorio para VideoSearch");

                        var bSearchCtx = new AiExecutionContext
                        {
                            ModuleType = bpm.AiModule.ModuleType, ModelName = bpm.AiModule.ModelName,
                            ApiKey = bApiKey, Input = bSearchQuery,
                            ProjectContext = project.Context, Configuration = bConfig,
                        };
                        var bResult = await bProvider.ExecuteAsync(bSearchCtx);
                        stepResults[bpm.StepOrder] = bResult;
                        stepModuleTypes[bpm.StepOrder] = bpm.AiModule.ModuleType;
                        branchStepExec.EstimatedCost += bResult.EstimatedCost;
                        if (!bResult.Success) throw new InvalidOperationException(bResult.Error ?? "Error en busqueda de video");

                        var bSearchFiles = new List<OutputFile>();
                        if (bResult.FileOutput is not null)
                        {
                            var bDir = Path.Combine(workspacePath, $"branch_{branchId}_step_{bpm.StepOrder}");
                            Directory.CreateDirectory(bDir);
                            var ext = GetExtension(bResult.ContentType ?? "video/mp4");
                            var fn = $"output{ext}";
                            await File.WriteAllBytesAsync(Path.Combine(bDir, fn), bResult.FileOutput);
                            var ef = new ExecutionFile
                            {
                                Id = Guid.NewGuid(), StepExecutionId = branchStepExec.Id,
                                FileName = fn, ContentType = bResult.ContentType ?? "video/mp4",
                                FilePath = Path.Combine($"branch_{branchId}_step_{bpm.StepOrder}", fn),
                                Direction = "Output", FileSize = bResult.FileOutput.Length,
                                CreatedAt = DateTime.UtcNow,
                            };
                            db.ExecutionFiles.Add(ef);
                            bSearchFiles.Add(new OutputFile { FileId = ef.Id, FileName = fn, ContentType = ef.ContentType, FileSize = ef.FileSize });
                        }
                        var bSearchOutput = OutputSchemaHelper.BuildVideoOutput(bSearchFiles, bpm.AiModule.ModelName, bResult.Metadata);
                        stepOutputs[bpm.StepOrder] = bSearchOutput;
                        branchStepExec.OutputData = JsonSerializer.Serialize(bSearchOutput);
                        await db.SaveChangesAsync();
                    }
                    else if (bpm.AiModule.ModuleType == "VideoEdit")
                    {
                        var bEditInput = bInputs[0];
                        if (string.IsNullOrWhiteSpace(bEditInput))
                            throw new InvalidOperationException($"[{branchId}] Input obligatorio para VideoEdit");

                        var bEditCtx = new AiExecutionContext
                        {
                            ModuleType = bpm.AiModule.ModuleType, ModelName = bpm.AiModule.ModelName,
                            ApiKey = bApiKey, Input = bEditInput,
                            ProjectContext = project.Context, Configuration = bConfig,
                        };
                        var bResult = await bProvider.ExecuteAsync(bEditCtx);
                        stepResults[bpm.StepOrder] = bResult;
                        stepModuleTypes[bpm.StepOrder] = bpm.AiModule.ModuleType;
                        branchStepExec.EstimatedCost += bResult.EstimatedCost;
                        if (!bResult.Success) throw new InvalidOperationException(bResult.Error ?? "Error en edicion de video");

                        var bEditFiles = new List<OutputFile>();
                        if (bResult.FileOutput is not null)
                        {
                            var bDir = Path.Combine(workspacePath, $"branch_{branchId}_step_{bpm.StepOrder}");
                            Directory.CreateDirectory(bDir);
                            var ext = GetExtension(bResult.ContentType ?? "video/mp4");
                            var fn = $"output{ext}";
                            await File.WriteAllBytesAsync(Path.Combine(bDir, fn), bResult.FileOutput);
                            var ef = new ExecutionFile
                            {
                                Id = Guid.NewGuid(), StepExecutionId = branchStepExec.Id,
                                FileName = fn, ContentType = bResult.ContentType ?? "video/mp4",
                                FilePath = Path.Combine($"branch_{branchId}_step_{bpm.StepOrder}", fn),
                                Direction = "Output", FileSize = bResult.FileOutput.Length,
                                CreatedAt = DateTime.UtcNow,
                            };
                            db.ExecutionFiles.Add(ef);
                            bEditFiles.Add(new OutputFile { FileId = ef.Id, FileName = fn, ContentType = ef.ContentType, FileSize = ef.FileSize });
                        }
                        var bEditOutput = OutputSchemaHelper.BuildVideoOutput(bEditFiles, bpm.AiModule.ModelName, bResult.Metadata);
                        stepOutputs[bpm.StepOrder] = bEditOutput;
                        branchStepExec.OutputData = JsonSerializer.Serialize(bEditOutput);
                        await db.SaveChangesAsync();
                    }

                    branchStepExec.Status = "Completed";
                    branchStepExec.CompletedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                    await _logger.LogStepProgressAsync(project.Id, bpm.Id, "Completed");
                }
                catch (Exception bex)
                {
                    await _logger.LogAsync(project.Id, execution.Id, "error",
                        $"[{branchId}] Error: {bex.Message}", bpm.StepOrder, bpm.StepName ?? bpm.AiModule.Name);
                    branchStepExec.Status = "Failed";
                    branchStepExec.ErrorMessage = bex.Message;
                    branchStepExec.CompletedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                    await _logger.LogStepProgressAsync(project.Id, bpm.Id, "Failed");
                    return BranchResult.Failed;
                }
            }
            return BranchResult.Completed;
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
                    foreach (var kv in moduleDict) config[kv.Key] = UnwrapJsonElement(kv.Value);
            }

            if (stepConfig is not null)
            {
                var stepDict = JsonSerializer.Deserialize<Dictionary<string, object>>(stepConfig);
                if (stepDict is not null)
                    foreach (var kv in stepDict) config[kv.Key] = UnwrapJsonElement(kv.Value);
            }

            return config;
        }

        /// <summary>
        /// Converts JsonElement values to their native .NET types so that
        /// downstream code can use pattern matching (e.g. "value is string s").
        /// </summary>
        private static object UnwrapJsonElement(object value)
        {
            if (value is not JsonElement el) return value;
            return el.ValueKind switch
            {
                JsonValueKind.String => el.GetString()!,
                JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null!,
                _ => value // arrays/objects stay as JsonElement
            };
        }

        /// <summary>
        /// Loads files attached to a module from disk and returns them as byte arrays.
        /// </summary>
        private async Task<List<byte[]>?> LoadModuleFilesAsync(ProjectModule pm, UserDbContext db)
        {
            var fileRecords = await db.ModuleFiles
                .Where(f => f.AiModuleId == pm.AiModuleId)
                .ToListAsync();

            if (fileRecords.Count == 0) return null;

            var files = new List<byte[]>();
            foreach (var f in fileRecords)
            {
                var fullPath = Path.Combine(_mediaRoot, f.FilePath);
                if (File.Exists(fullPath))
                    files.Add(await File.ReadAllBytesAsync(fullPath));
            }

            return files.Count > 0 ? files : null;
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

        // ── Resume a single branch that was paused for user interaction ──
        public async Task<ProjectExecution> ResumeFromBranchInteractionAsync(
            Guid executionId, string branchId, string responseText,
            UserDbContext db, string tenantDbName, CancellationToken ct = default)
        {
            _logger = _baseLogger.WithDb(db);

            var execution = await db.ProjectExecutions
                .Include(e => e.StepExecutions.OrderBy(s => s.StepOrder))
                .FirstOrDefaultAsync(e => e.Id == executionId)
                ?? throw new InvalidOperationException("Ejecucion no encontrada");

            var project = await db.Projects
                .Include(p => p.ProjectModules.Where(pm => pm.IsActive).OrderBy(pm => pm.StepOrder))
                    .ThenInclude(pm => pm.AiModule)
                        .ThenInclude(m => m.ApiKey)
                .FirstOrDefaultAsync(p => p.Id == execution.ProjectId)
                ?? throw new InvalidOperationException("Proyecto no encontrado");

            // Find the paused branch
            var pausedBranches = DeserializePausedBranches(execution.PausedBranches);
            var branchPause = pausedBranches.FirstOrDefault(b => b.BranchId == branchId)
                ?? throw new InvalidOperationException($"La rama '{branchId}' no esta pausada");

            var pausedStep = branchPause.PausedAtStepOrder;
            var pauseState = branchPause.PauseData;

            // Restore step outputs & module types from pause state
            var stepOutputs = new Dictionary<int, StepOutput>();
            foreach (var kv in pauseState.StepOutputs)
            {
                if (int.TryParse(kv.Key, out var so))
                    stepOutputs[so] = JsonSerializer.Deserialize<StepOutput>(kv.Value) ?? new StepOutput();
            }
            var stepModuleTypes = new Dictionary<int, string>();
            foreach (var kv in pauseState.StepModuleTypes)
            {
                if (int.TryParse(kv.Key, out var so))
                    stepModuleTypes[so] = kv.Value;
            }
            var stepResults = new Dictionary<int, AiResult>();

            // Complete the interaction step with the response
            var interactionStepExec = execution.StepExecutions
                .FirstOrDefault(s => s.StepOrder == pausedStep && s.Status == "WaitingForInput");
            if (interactionStepExec is not null)
            {
                var interactionOutput = new StepOutput
                {
                    Type = "text",
                    Content = responseText,
                    Summary = $"Respuesta del usuario (rama '{branchId}')",
                    Items = [new OutputItem { Content = responseText, Label = "respuesta" }]
                };
                interactionStepExec.Status = "Completed";
                interactionStepExec.OutputData = JsonSerializer.Serialize(interactionOutput);
                interactionStepExec.CompletedAt = DateTime.UtcNow;
                stepOutputs[pausedStep] = interactionOutput;
                stepModuleTypes[pausedStep] = "Interaction";
                stepResults[pausedStep] = AiResult.Ok(responseText, new Dictionary<string, object>());
            }

            // Remove this branch from the paused list
            pausedBranches.RemoveAll(b => b.BranchId == branchId);
            execution.PausedBranches = pausedBranches.Count > 0
                ? JsonSerializer.Serialize(pausedBranches)
                : null;
            await db.SaveChangesAsync();

            await _logger.LogAsync(project.Id, execution.Id, "info",
                $"Respuesta recibida para rama '{branchId}': \"{responseText}\". Reanudando rama...");

            // Get the branch modules that still need executing (after the paused step)
            var branchSteps = project.ProjectModules
                .Where(m => m.BranchId == branchId && m.StepOrder > pausedStep)
                .OrderBy(m => m.StepOrder)
                .ToList();

            var workspacePath = ResolveWorkspacePath(execution.WorkspacePath);
            var previousSummaryContext = await BuildPreviousSummaryContextAsync(db, project.Id, execution.Id);

            var result = await ExecuteBranchStepsAsync(
                project, execution, branchId, branchSteps, pauseState.UserInput,
                stepResults, stepOutputs, stepModuleTypes,
                workspacePath, previousSummaryContext, db, tenantDbName, ct);

            if (result != BranchResult.Paused)
            {
                await _logger.LogAsync(project.Id, execution.Id,
                    result == BranchResult.Completed ? "success" : "warning",
                    result == BranchResult.Completed
                        ? $"Rama '{branchId}' completada correctamente tras respuesta del usuario"
                        : $"Rama '{branchId}' fallo tras respuesta del usuario");
            }

            return execution;
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
                .Include(e => e.StepExecutions)
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

            var workspacePath = ResolveWorkspacePath(execution.WorkspacePath);

            // ── Pre-flight: validate API keys for steps that will re-execute ──
            var retryModules = project.ProjectModules.Where(pm => pm.StepOrder >= fromStepOrder).ToList();
            var validatedKeys = new HashSet<string>();
            var errors = new List<string>();

            foreach (var pm in retryModules)
            {
                var stepName = pm.StepName ?? pm.AiModule.Name;

                if (IsInteractionStep(pm.AiModule) || IsPublishStep(pm.AiModule) || IsDesignStep(pm.AiModule) || IsOrchestratorStep(pm.AiModule) || pm.AiModule.ApiKeyId is null)
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

            // Load previous execution summaries for context
            var previousSummaryContext = await BuildPreviousSummaryContextAsync(db, projectId, executionId);

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
                await _logger.LogStepProgressAsync(projectId, pm.Id, "Running");

                try
                {
                    var stepName = pm.StepName ?? pm.AiModule.Name;
                    var stepLabel = GetStepLabel(pm, project.ProjectModules);
                    await _logger.LogAsync(projectId, executionId, "info",
                        $"Ejecutando paso {stepLabel}: {stepName} ({pm.AiModule.ProviderType}/{pm.AiModule.ModelName})",
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

                    // Handle design step during retry (Canva)
                    if (IsDesignStep(pm.AiModule))
                    {
                        await HandleCanvaPublishStepAsync(
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
                        pm.AiModule.ModuleType, pm.AiModule.ModelName,
                        BuildStepBranches(project.ProjectModules, stepModuleTypes));

                    // For the retry step: enrich input with feedback + previous output
                    if (pm.StepOrder == fromStepOrder && comment is not null)
                    {
                        var prevOutput = previousOutputsByStep.GetValueOrDefault(pm.StepOrder);
                        inputs = EnrichInputsWithFeedback(inputs, comment, prevOutput, pm.AiModule.ModuleType);
                    }

                    var retrySysPrompt = config.TryGetValue("systemPrompt", out var rspVal) && rspVal is string rspStr ? rspStr : null;
                    stepExecution.InputData = inputs.Count == 1
                        ? JsonSerializer.Serialize(new { systemPrompt = retrySysPrompt, projectContext = project.Context, prompt = inputs[0] })
                        : JsonSerializer.Serialize(new { systemPrompt = retrySysPrompt, projectContext = project.Context, prompts = inputs, count = inputs.Count });

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
                            PreviousExecutionsSummary = previousSummaryContext,
                            Configuration = config,
                            InputFiles = await LoadModuleFilesAsync(pm, db),
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
                        // Load previous step files for image-to-image editing (retry path)
                        List<byte[]>? retryPrevFiles = null;
                        if (pm.InputMapping is not null)
                        {
                            var mappingJson = JsonSerializer.Deserialize<JsonElement>(pm.InputMapping);
                            if (mappingJson.TryGetProperty("field", out var fieldProp) && fieldProp.GetString() == "file")
                            {
                                var prevOrder = FindPreviousStepInBranch(
                                    pm.StepOrder, pm.BranchId, pm.BranchFromStep,
                                    stepOutputs, stepResults,
                                    BuildStepBranches(project.ProjectModules, stepModuleTypes));
                                if (stepOutputs.TryGetValue(prevOrder, out var prevOutput) && prevOutput.Files.Count > 0)
                                {
                                    retryPrevFiles = new List<byte[]>();
                                    foreach (var pf in prevOutput.Files.Where(f => f.ContentType.StartsWith("image/")))
                                    {
                                        var pfPath = Path.Combine(workspacePath, $"step_{prevOrder}", pf.FileName);
                                        if (File.Exists(pfPath))
                                            retryPrevFiles.Add(await File.ReadAllBytesAsync(pfPath));
                                    }
                                }
                            }
                        }

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

                            var inputFiles = retryPrevFiles is { Count: > 0 }
                                ? retryPrevFiles
                                : await LoadModuleFilesAsync(pm, db);

                            var context = new AiExecutionContext
                            {
                                ModuleType = pm.AiModule.ModuleType,
                                ModelName = pm.AiModule.ModelName,
                                ApiKey = apiKey,
                                Input = singleInput,
                                ProjectContext = project.Context,
                                Configuration = config,
                                InputFiles = inputFiles,
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
                    await _logger.LogStepProgressAsync(projectId, pm.Id, "Completed");
                }
                catch (OperationCanceledException)
                {
                    await _logger.LogAsync(projectId, executionId, "warning",
                        "Pipeline cancelado por el usuario", pm.StepOrder, pm.StepName ?? pm.AiModule.Name);

                    stepExecution.Status = "Cancelled";
                    stepExecution.CompletedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                    await _logger.LogStepProgressAsync(projectId, pm.Id, "Cancelled");

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
                    await _logger.LogStepProgressAsync(projectId, pm.Id, "Failed");

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

            // Generate execution summary for future context
            try
            {
                await GenerateExecutionSummaryAsync(execution, stepOutputs, originalUserInput, db);
            }
            catch (Exception ex)
            {
                await _logger.LogAsync(projectId, executionId, "warning",
                    $"No se pudo generar resumen: {ex.Message}");
            }

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
