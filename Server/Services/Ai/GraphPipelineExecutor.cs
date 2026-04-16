using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Server.Data;
using Server.Hubs;
using Server.Models;
using Server.Services.Ai.Handlers;
using Server.Services.Telegram;
using Server.Services.WhatsApp;

namespace Server.Services.Ai;

/// <summary>
/// Executes project modules as a dependency graph. Nodes become runnable when all
/// connected input ports have received data.
/// </summary>
public class GraphPipelineExecutor : IPipelineExecutor
{
    private readonly IReadOnlyDictionary<string, IModuleHandler> _handlers;
    private readonly IExecutionLogger _baseLogger;
    private readonly CoreDbContext _coreDb;
    private readonly ITenantDbContextFactory _tenantFactory;
    private readonly TelegramService _telegram;
    private readonly WhatsAppService _whatsApp;
    private readonly string _mediaRoot;
    private IExecutionLogger _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    public GraphPipelineExecutor(
        IEnumerable<IModuleHandler> handlers,
        IExecutionLogger logger,
        CoreDbContext coreDb,
        ITenantDbContextFactory tenantFactory,
        TelegramService telegram,
        WhatsAppService whatsApp,
        IWebHostEnvironment env)
    {
        _handlers = handlers.ToDictionary(h => h.ModuleType, StringComparer.OrdinalIgnoreCase);
        _baseLogger = logger;
        _logger = logger;
        _coreDb = coreDb;
        _tenantFactory = tenantFactory;
        _telegram = telegram;
        _whatsApp = whatsApp;
        _mediaRoot = Path.Combine(env.ContentRootPath, "GeneratedMedia");
    }

    private string ResolveWorkspacePath(string storedPath) =>
        Path.IsPathRooted(storedPath) ? storedPath : Path.Combine(_mediaRoot, storedPath);

    public async Task<ProjectExecution> ExecuteAsync(
        Guid projectId,
        string? userInput,
        UserDbContext db,
        string tenantDbName,
        CancellationToken ct = default,
        bool useHistory = true)
    {
        _logger = _baseLogger.WithDb(db);

        var project = await LoadProjectAsync(projectId, db, ct);
        if (project.ProjectModules.Count == 0)
            throw new InvalidOperationException("El proyecto no tiene modulos asignados");

        var executionId = Guid.NewGuid();
        var relativeWorkspace = Path.Combine(tenantDbName, projectId.ToString(), executionId.ToString());
        var workspacePath = Path.Combine(_mediaRoot, relativeWorkspace);
        Directory.CreateDirectory(workspacePath);

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
        await db.SaveChangesAsync(ct);

        var connections = await LoadConnectionsAsync(projectId, db, ct);
        var modules = project.ProjectModules.Where(pm => pm.IsActive).OrderBy(pm => pm.StepOrder).ToList();
        var graph = BuildGraph(projectId, execution, userInput, modules, connections);
        var previousSummaryContext = useHistory
            ? await BuildPreviousSummaryContextAsync(db, projectId, executionId, ct)
            : null;

        await _logger.LogAsync(projectId, executionId, "info",
            $"Iniciando grafo con {graph.Nodes.Count} modulo(s)");

        var filePaths = new Dictionary<Guid, string>();
        await RunGraphAsync(graph, project, execution, db, tenantDbName, workspacePath, previousSummaryContext, filePaths, ct);

        return execution;
    }

    public async Task<ProjectExecution> RetryFromModuleAsync(
        Guid executionId,
        Guid moduleId,
        string? comment,
        UserDbContext db,
        string tenantDbName,
        CancellationToken ct = default)
    {
        _logger = _baseLogger.WithDb(db);

        var execution = await LoadExecutionAsync(executionId, db, ct);
        var project = await LoadProjectAsync(execution.ProjectId, db, ct);
        var modules = project.ProjectModules.Where(pm => pm.IsActive).OrderBy(pm => pm.StepOrder).ToList();
        var target = modules.FirstOrDefault(pm => pm.Id == moduleId)
            ?? throw new InvalidOperationException("Modulo no encontrado para reintento");

        var connections = await LoadConnectionsAsync(project.Id, db, ct);
        var graph = BuildGraph(project.Id, execution, execution.UserInput, modules, connections);
        var workspacePath = ResolveWorkspacePath(execution.WorkspacePath);
        Directory.CreateDirectory(workspacePath);

        var oldSteps = await db.StepExecutions
            .Include(s => s.Files)
            .Where(s => s.ExecutionId == executionId)
            .ToListAsync(ct);

        var stepsToRemove = oldSteps.Where(s => s.StepOrder >= target.StepOrder).ToList();
        foreach (var step in stepsToRemove)
        {
            db.ExecutionFiles.RemoveRange(step.Files);
            db.StepExecutions.Remove(step);
        }

        execution.Status = "Running";
        execution.CompletedAt = null;
        execution.PausedAtStepOrder = null;
        execution.PausedStepData = null;
        execution.PausedBranches = null;
        await db.SaveChangesAsync(ct);

        var filePaths = await LoadExecutionFilePathsAsync(executionId, db, ct);
        foreach (var step in oldSteps.Except(stepsToRemove).Where(s => s.Status == "Completed"))
            RestoreCompletedStep(graph, step);

        await _logger.LogAsync(project.Id, executionId, "info",
            $"Reintentando desde modulo {target.StepOrder}: {target.StepName ?? target.AiModule.Name}");

        var previousSummaryContext = await BuildPreviousSummaryContextAsync(db, project.Id, executionId, ct);
        if (!string.IsNullOrWhiteSpace(comment))
            previousSummaryContext = $"{previousSummaryContext}\nComentario de reintento: {comment}".Trim();

        await RunGraphAsync(graph, project, execution, db, tenantDbName, workspacePath, previousSummaryContext, filePaths, ct);
        return execution;
    }

    public async Task<ProjectExecution> RetryFromStepAsync(
        Guid executionId,
        int fromStepOrder,
        string? comment,
        UserDbContext db,
        string tenantDbName,
        CancellationToken ct = default)
    {
        var execution = await LoadExecutionAsync(executionId, db, ct);
        var moduleId = await db.ProjectModules
            .Where(pm => pm.ProjectId == execution.ProjectId && pm.StepOrder == fromStepOrder)
            .Select(pm => (Guid?)pm.Id)
            .FirstOrDefaultAsync(ct);

        if (moduleId is null)
            throw new InvalidOperationException($"No existe el paso {fromStepOrder}");

        return await RetryFromModuleAsync(executionId, moduleId.Value, comment, db, tenantDbName, ct);
    }

    public async Task<ProjectExecution> ResumeFromInteractionAsync(
        Guid executionId,
        string responseText,
        UserDbContext db,
        string tenantDbName,
        CancellationToken ct = default)
    {
        _logger = _baseLogger.WithDb(db);

        var (execution, project, graph, workspacePath, filePaths, state) =
            await RebuildPausedGraphAsync(executionId, db, ct);

        if (!graph.Nodes.TryGetValue(state.PausedModuleId, out var node))
            throw new InvalidOperationException("Modulo pausado no encontrado");

        var step = await GetPausedStepAsync(executionId, node.ModuleId, db, ct);
        var output = new StepOutput
        {
            Type = "text",
            Content = responseText,
            Summary = "Respuesta recibida del usuario",
            Items = [new OutputItem { Content = responseText, Label = "respuesta" }],
        };

        node.Output = output;
        node.Status = NodeStatus.Completed;
        if (step is not null)
        {
            step.Status = "Completed";
            step.OutputData = JsonSerializer.Serialize(output, JsonOptions);
            step.CompletedAt = DateTime.UtcNow;
        }

        graph.PropagateOutputs(node);
        execution.Status = "Running";
        execution.PausedAtStepOrder = null;
        execution.PausedStepData = null;
        await db.SaveChangesAsync(ct);

        var previousSummaryContext = await BuildPreviousSummaryContextAsync(db, project.Id, executionId, ct);
        await RunGraphAsync(graph, project, execution, db, tenantDbName, workspacePath, previousSummaryContext, filePaths, ct);
        return execution;
    }

    public Task<ProjectExecution> ResumeFromBranchInteractionAsync(
        Guid executionId,
        string branchId,
        string responseText,
        UserDbContext db,
        string tenantDbName,
        CancellationToken ct = default) =>
        ResumeFromInteractionAsync(executionId, responseText, db, tenantDbName, ct);

    public async Task<ProjectExecution> ResumeFromCheckpointAsync(
        Guid executionId,
        bool approved,
        UserDbContext db,
        string tenantDbName,
        CancellationToken ct = default)
    {
        _logger = _baseLogger.WithDb(db);

        var (execution, project, graph, workspacePath, filePaths, state) =
            await RebuildPausedGraphAsync(executionId, db, ct);

        if (!graph.Nodes.TryGetValue(state.PausedModuleId, out var node))
            throw new InvalidOperationException("Modulo pausado no encontrado");

        var step = await GetPausedStepAsync(executionId, node.ModuleId, db, ct);
        if (!approved)
        {
            node.Status = NodeStatus.Failed;
            execution.Status = "Cancelled";
            execution.CompletedAt = DateTime.UtcNow;
            execution.PausedAtStepOrder = null;
            execution.PausedStepData = null;
            if (step is not null)
            {
                step.Status = "Cancelled";
                step.CompletedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(ct);
            return execution;
        }

        if (node.Output is null && step?.OutputData is not null)
            node.Output = JsonSerializer.Deserialize<StepOutput>(step.OutputData, JsonOptions);

        node.Status = NodeStatus.Completed;
        if (step is not null)
        {
            step.Status = "Completed";
            step.CompletedAt = DateTime.UtcNow;
        }

        graph.PropagateOutputs(node);
        execution.Status = "Running";
        execution.PausedAtStepOrder = null;
        execution.PausedStepData = null;
        await db.SaveChangesAsync(ct);

        var previousSummaryContext = await BuildPreviousSummaryContextAsync(db, project.Id, executionId, ct);
        await RunGraphAsync(graph, project, execution, db, tenantDbName, workspacePath, previousSummaryContext, filePaths, ct);
        return execution;
    }

    public async Task<ProjectExecution> ResumeFromOrchestratorAsync(
        Guid executionId,
        bool approved,
        string? comment,
        UserDbContext db,
        string tenantDbName,
        CancellationToken ct = default)
    {
        var execution = await LoadExecutionAsync(executionId, db, ct);
        if (!approved)
        {
            execution.Status = "Cancelled";
            execution.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return execution;
    }

    public async Task<ProjectExecution> AbortFromInteractionAsync(
        Guid executionId,
        UserDbContext db,
        string tenantDbName)
    {
        _logger = _baseLogger.WithDb(db);

        var execution = await db.ProjectExecutions
            .Include(e => e.StepExecutions)
            .FirstOrDefaultAsync(e => e.Id == executionId)
            ?? throw new InvalidOperationException("Ejecucion no encontrada");

        execution.Status = "Cancelled";
        execution.CompletedAt = DateTime.UtcNow;
        execution.PausedAtStepOrder = null;
        execution.PausedStepData = null;
        execution.PausedBranches = null;

        foreach (var step in execution.StepExecutions.Where(s => s.Status.StartsWith("Waiting")))
        {
            step.Status = "Cancelled";
            step.CompletedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();
        await _logger.LogAsync(execution.ProjectId, execution.Id, "warning",
            "Pipeline abortado por el usuario");
        return execution;
    }

    public async Task SendNextQueuedInteractionAsync(Guid executionId, string chatId)
    {
        var nextQueued = await _coreDb.TelegramCorrelations
            .Where(c => !c.IsResolved && c.ExecutionId == executionId && c.State == "queued")
            .OrderBy(c => c.CreatedAt)
            .FirstOrDefaultAsync();

        if (nextQueued?.QueuedMessageData is null) return;

        var tgConfig = await GetTelegramConfigForCorrelationAsync(nextQueued);
        if (tgConfig is null) return;

        var data = JsonSerializer.Deserialize<JsonElement>(nextQueued.QueuedMessageData);
        var message = data.TryGetProperty("message", out var msg) ? msg.GetString() ?? "" : "";
        await _telegram.SendTextMessageWithOptionsAsync(tgConfig, message, ControlOptions());

        nextQueued.State = "waiting";
        nextQueued.QueuedMessageData = null;
        await _coreDb.SaveChangesAsync();
    }

    public async Task CancelQueuedInteractionsAsync(Guid executionId)
    {
        var queued = await _coreDb.TelegramCorrelations
            .Where(c => !c.IsResolved && c.ExecutionId == executionId && c.State == "queued")
            .ToListAsync();

        foreach (var c in queued)
        {
            c.IsResolved = true;
            c.State = "cancelled";
        }

        if (queued.Count > 0)
            await _coreDb.SaveChangesAsync();
    }

    private async Task RunGraphAsync(
        ExecutionGraph graph,
        Project project,
        ProjectExecution execution,
        UserDbContext db,
        string tenantDbName,
        string workspacePath,
        string? previousSummaryContext,
        Dictionary<Guid, string> filePaths,
        CancellationToken ct)
    {
        var running = new Dictionary<Task<ModuleResult>, ModuleNode>();

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            foreach (var node in graph.GetReadyNodes())
            {
                node.Status = NodeStatus.Running;
                node.StepExecution = await CreateStepExecutionAsync(node, execution, db, ct);
                await _logger.LogStepProgressAsync(project.Id, node.ModuleId, "Running");
                await _logger.LogAsync(project.Id, execution.Id, "step-start",
                    $"{node.ProjectModule.StepName ?? node.AiModule.Name} ({node.AiModule.ProviderType}/{GetEffectiveModelName(node.ProjectModule)})",
                    node.ProjectModule.StepOrder,
                    node.ProjectModule.StepName ?? node.AiModule.Name);

                var task = ExecuteNodeAsync(node, graph, project, execution, tenantDbName, workspacePath,
                    previousSummaryContext, filePaths, ct);
                running[task] = node;
            }

            if (running.Count == 0)
            {
                await FinalizeGraphAsync(graph, execution, db, ct);
                return;
            }

            var completedTask = await Task.WhenAny(running.Keys);
            var completedNode = running[completedTask];
            running.Remove(completedTask);

            ModuleResult result;
            try
            {
                result = await completedTask;
            }
            catch (OperationCanceledException)
            {
                completedNode.Status = NodeStatus.Failed;
                result = ModuleResult.Failed("Pipeline cancelado");
            }
            catch (Exception ex)
            {
                completedNode.Status = NodeStatus.Failed;
                result = ModuleResult.Failed(ex.Message);
            }

            await ProcessNodeResultAsync(completedNode, result, graph, project, execution,
                db, tenantDbName, workspacePath, filePaths, ct);
        }
    }

    private async Task<ModuleResult> ExecuteNodeAsync(
        ModuleNode node,
        ExecutionGraph graph,
        Project project,
        ProjectExecution execution,
        string tenantDbName,
        string workspacePath,
        string? previousSummaryContext,
        Dictionary<Guid, string> filePaths,
        CancellationToken ct)
    {
        if (!_handlers.TryGetValue(node.ModuleType, out var handler))
            return ModuleResult.Failed($"No hay handler registrado para modulo '{node.ModuleType}'");

        var ctx = new ModuleExecutionContext
        {
            Node = node,
            Graph = graph,
            Execution = execution,
            Project = project,
            TenantDbName = tenantDbName,
            WorkspacePath = workspacePath,
            PreviousSummaryContext = previousSummaryContext,
            CancellationToken = ct,
            InputsByPort = node.InputPorts.ToDictionary(p => p.PortId, p => p.ReceivedData.ToList()),
            Config = MergeConfiguration(node.AiModule.Configuration, node.ProjectModule.Configuration),
            ModuleFiles = node.AiModule.Files.Select(f => new ModuleFileInfo
            {
                Id = f.Id,
                FileName = f.FileName,
                ContentType = f.ContentType,
                FilePath = f.FilePath,
                FileSize = f.FileSize,
            }).ToList(),
            MediaRoot = _mediaRoot,
            ExecutionFilePaths = new Dictionary<Guid, string>(filePaths),
        };

        return await handler.ExecuteAsync(ctx);
    }

    private async Task ProcessNodeResultAsync(
        ModuleNode node,
        ModuleResult result,
        ExecutionGraph graph,
        Project project,
        ProjectExecution execution,
        UserDbContext db,
        string tenantDbName,
        string workspacePath,
        Dictionary<Guid, string> filePaths,
        CancellationToken ct)
    {
        var step = node.StepExecution
            ?? throw new InvalidOperationException("StepExecution no inicializado");

        node.Output = result.Output ?? new StepOutput { Type = "empty", Content = "" };
        node.Cost = result.Cost;
        step.EstimatedCost = result.Cost;

        if (result.Status == ModuleResultStatus.Completed)
        {
            node.Status = NodeStatus.Completed;
            await PersistProducedFilesAsync(node, result, db, workspacePath, filePaths, ct);
            step.Status = "Completed";
            step.OutputData = JsonSerializer.Serialize(node.Output, JsonOptions);
            step.CompletedAt = DateTime.UtcNow;
            graph.PropagateOutputs(node);
            await db.SaveChangesAsync(ct);
            await _logger.LogStepProgressAsync(project.Id, node.ModuleId, "Completed");
            await _logger.LogAsync(project.Id, execution.Id, "success",
                $"{node.ProjectModule.StepName ?? node.AiModule.Name} completado",
                node.ProjectModule.StepOrder,
                node.ProjectModule.StepName ?? node.AiModule.Name);
            return;
        }

        if (result.Status == ModuleResultStatus.Paused)
        {
            node.Status = NodeStatus.Paused;
            await PersistProducedFilesAsync(node, result, db, workspacePath, filePaths, ct);
            step.Status = result.PauseReason ?? "Paused";
            step.OutputData = JsonSerializer.Serialize(node.Output, JsonOptions);
            await SendInteractionIfNeededAsync(node, result, project, execution, tenantDbName, ct);
            await db.SaveChangesAsync(ct);
            await _logger.LogStepProgressAsync(project.Id, node.ModuleId, step.Status);
            return;
        }

        node.Status = NodeStatus.Failed;
        step.Status = "Failed";
        step.ErrorMessage = result.Error ?? "Error desconocido";
        step.CompletedAt = DateTime.UtcNow;
        graph.CascadeFailure(node);
        await db.SaveChangesAsync(ct);
        await _logger.LogStepProgressAsync(project.Id, node.ModuleId, "Failed");
        await _logger.LogAsync(project.Id, execution.Id, "error", step.ErrorMessage,
            node.ProjectModule.StepOrder,
            node.ProjectModule.StepName ?? node.AiModule.Name);
    }

    private async Task FinalizeGraphAsync(
        ExecutionGraph graph,
        ProjectExecution execution,
        UserDbContext db,
        CancellationToken ct)
    {
        var paused = graph.Nodes.Values
            .Where(n => n.Status == NodeStatus.Paused)
            .OrderBy(n => n.ProjectModule.StepOrder)
            .ToList();

        if (paused.Count > 0)
        {
            var pausedNode = paused[0];
            execution.Status = pausedNode.ModuleType == "Checkpoint" ? "WaitingForCheckpoint" : "WaitingForInput";
            execution.PausedAtStepOrder = pausedNode.ProjectModule.StepOrder;
            execution.PausedStepData = JsonSerializer.Serialize(PausedGraphState.Capture(graph, pausedNode.ModuleId), JsonOptions);
            await db.SaveChangesAsync(ct);
            return;
        }

        if (graph.Nodes.Values.Any(n => n.Status == NodeStatus.Failed))
        {
            execution.Status = "Failed";
            execution.CompletedAt = DateTime.UtcNow;
            execution.TotalEstimatedCost = graph.Nodes.Values.Sum(n => n.Cost);
            await db.SaveChangesAsync(ct);
            return;
        }

        var pending = graph.Nodes.Values.Where(n => n.Status == NodeStatus.Pending).ToList();
        if (pending.Count > 0)
        {
            execution.Status = "Failed";
            execution.CompletedAt = DateTime.UtcNow;
            await _logger.LogAsync(execution.ProjectId, execution.Id, "error",
                $"Grafo bloqueado: {pending.Count} modulo(s) quedaron pendientes sin entradas completas");
            await db.SaveChangesAsync(ct);
            return;
        }

        execution.Status = "Completed";
        execution.CompletedAt = DateTime.UtcNow;
        execution.TotalEstimatedCost = graph.Nodes.Values.Sum(n => n.Cost);
        execution.ExecutionSummary = BuildExecutionSummary(graph, execution.UserInput);
        await db.SaveChangesAsync(ct);
        await _logger.LogAsync(execution.ProjectId, execution.Id, "success",
            "Pipeline completado correctamente");
    }

    private async Task<StepExecution> CreateStepExecutionAsync(
        ModuleNode node,
        ProjectExecution execution,
        UserDbContext db,
        CancellationToken ct)
    {
        var step = new StepExecution
        {
            Id = Guid.NewGuid(),
            ExecutionId = execution.Id,
            ProjectModuleId = node.ModuleId,
            StepOrder = node.ProjectModule.StepOrder,
            Status = "Running",
            InputData = JsonSerializer.Serialize(DescribeInputs(node), JsonOptions),
            CreatedAt = DateTime.UtcNow,
        };

        db.StepExecutions.Add(step);
        await db.SaveChangesAsync(ct);
        return step;
    }

    private async Task PersistProducedFilesAsync(
        ModuleNode node,
        ModuleResult result,
        UserDbContext db,
        string workspacePath,
        Dictionary<Guid, string> filePaths,
        CancellationToken ct)
    {
        if (result.ProducedFiles.Count == 0 || node.StepExecution is null)
            return;

        var stepDirName = $"step_{node.ProjectModule.StepOrder}";
        var stepDir = Path.Combine(workspacePath, stepDirName);
        Directory.CreateDirectory(stepDir);

        foreach (var produced in result.ProducedFiles)
        {
            var storedName = GetUniqueFileName(stepDir, produced.FileName);
            var diskPath = Path.Combine(stepDir, storedName);
            await File.WriteAllBytesAsync(diskPath, produced.Data, ct);

            var execFile = new ExecutionFile
            {
                Id = Guid.NewGuid(),
                StepExecutionId = node.StepExecution.Id,
                FileName = storedName,
                ContentType = produced.ContentType,
                FilePath = Path.Combine(stepDirName, storedName),
                Direction = "Output",
                FileSize = produced.Data.LongLength,
                CreatedAt = DateTime.UtcNow,
            };

            db.ExecutionFiles.Add(execFile);
            filePaths[execFile.Id] = execFile.FilePath;

            var outputFile = node.Output?.Files.FirstOrDefault(f =>
                f.FileId == Guid.Empty &&
                string.Equals(f.FileName, produced.FileName, StringComparison.OrdinalIgnoreCase));
            if (outputFile is not null)
            {
                outputFile.FileId = execFile.Id;
                outputFile.FileName = storedName;
                outputFile.ContentType = produced.ContentType;
                outputFile.FileSize = produced.Data.LongLength;
            }
        }
    }

    private async Task SendInteractionIfNeededAsync(
        ModuleNode node,
        ModuleResult result,
        Project project,
        ProjectExecution execution,
        string tenantDbName,
        CancellationToken ct)
    {
        if (node.ModuleType != "Interaction")
            return;

        var message = result.Output?.Content ?? "";
        var useTelegram = node.AiModule.ModelName.Equals("telegram", StringComparison.OrdinalIgnoreCase);

        if (useTelegram)
        {
            if (string.IsNullOrWhiteSpace(project.TelegramConfig))
                throw new InvalidOperationException("El proyecto no tiene configuracion de Telegram");

            var config = JsonSerializer.Deserialize<TelegramConfig>(project.TelegramConfig, JsonOptions)
                ?? throw new InvalidOperationException("Configuracion de Telegram invalida");

            var hasActive = await _coreDb.TelegramCorrelations
                .AnyAsync(c => !c.IsResolved && c.ExecutionId == execution.Id && c.State != "queued", ct);

            _coreDb.TelegramCorrelations.Add(new TelegramCorrelation
            {
                Id = Guid.NewGuid(),
                ExecutionId = execution.Id,
                TenantDbName = tenantDbName,
                ChatId = config.ChatId,
                StepOrder = node.ProjectModule.StepOrder,
                CreatedAt = DateTime.UtcNow,
                IsResolved = false,
                State = hasActive ? "queued" : "waiting",
                QueuedMessageData = hasActive ? JsonSerializer.Serialize(new { message }, JsonOptions) : null,
            });

            if (!hasActive)
                await _telegram.SendTextMessageWithOptionsAsync(config, message, ControlOptions());

            await _coreDb.SaveChangesAsync(ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(project.WhatsAppConfig))
            throw new InvalidOperationException("El proyecto no tiene configuracion de WhatsApp");

        var waConfig = JsonSerializer.Deserialize<WhatsAppConfig>(project.WhatsAppConfig, JsonOptions)
            ?? throw new InvalidOperationException("Configuracion de WhatsApp invalida");
        await _whatsApp.SendTextMessageAsync(waConfig, message);

        _coreDb.WhatsAppCorrelations.Add(new WhatsAppCorrelation
        {
            Id = Guid.NewGuid(),
            ExecutionId = execution.Id,
            TenantDbName = tenantDbName,
            RecipientNumber = waConfig.RecipientNumber,
            StepOrder = node.ProjectModule.StepOrder,
            CreatedAt = DateTime.UtcNow,
            IsResolved = false,
        });
        await _coreDb.SaveChangesAsync(ct);
    }

    private async Task<(ProjectExecution Execution, Project Project, ExecutionGraph Graph, string WorkspacePath,
        Dictionary<Guid, string> FilePaths, PausedGraphState State)> RebuildPausedGraphAsync(
            Guid executionId,
            UserDbContext db,
            CancellationToken ct)
    {
        var execution = await LoadExecutionAsync(executionId, db, ct);
        if (string.IsNullOrWhiteSpace(execution.PausedStepData))
            throw new InvalidOperationException("La ejecucion no esta pausada con estado de grafo");

        var state = JsonSerializer.Deserialize<PausedGraphState>(execution.PausedStepData, JsonOptions)
            ?? throw new InvalidOperationException("Estado de grafo pausado invalido");

        var project = await LoadProjectAsync(execution.ProjectId, db, ct);
        var modules = project.ProjectModules.Where(pm => pm.IsActive).OrderBy(pm => pm.StepOrder).ToList();
        var connections = await LoadConnectionsAsync(project.Id, db, ct);
        var graph = BuildGraph(project.Id, execution, state.UserInput ?? execution.UserInput, modules, connections);
        state.RestoreInto(graph);

        var stepExecutions = await db.StepExecutions
            .Where(s => s.ExecutionId == executionId)
            .ToListAsync(ct);
        foreach (var step in stepExecutions)
        {
            if (!graph.Nodes.TryGetValue(step.ProjectModuleId, out var node)) continue;
            node.StepExecution = step;
            if (node.Output is null && !string.IsNullOrWhiteSpace(step.OutputData))
            {
                try { node.Output = JsonSerializer.Deserialize<StepOutput>(step.OutputData, JsonOptions); }
                catch { /* ignore malformed output */ }
            }
        }

        var filePaths = await LoadExecutionFilePathsAsync(executionId, db, ct);
        return (execution, project, graph, ResolveWorkspacePath(execution.WorkspacePath), filePaths, state);
    }

    private async Task<Project> LoadProjectAsync(Guid projectId, UserDbContext db, CancellationToken ct)
    {
        var project = await db.Projects
            .Include(p => p.ProjectModules)
                .ThenInclude(pm => pm.AiModule)
                    .ThenInclude(m => m.ApiKey)
            .Include(p => p.ProjectModules)
                .ThenInclude(pm => pm.AiModule)
                    .ThenInclude(m => m.Files)
            .Include(p => p.ProjectModules)
                .ThenInclude(pm => pm.OrchestratorOutputs)
            .FirstOrDefaultAsync(p => p.Id == projectId, ct);

        return project ?? throw new InvalidOperationException("Proyecto no encontrado");
    }

    private static async Task<ProjectExecution> LoadExecutionAsync(
        Guid executionId,
        UserDbContext db,
        CancellationToken ct) =>
        await db.ProjectExecutions.FirstOrDefaultAsync(e => e.Id == executionId, ct)
        ?? throw new InvalidOperationException("Ejecucion no encontrada");

    private static Task<List<ModuleConnection>> LoadConnectionsAsync(
        Guid projectId,
        UserDbContext db,
        CancellationToken ct) =>
        db.ModuleConnections.Where(c => c.ProjectId == projectId).ToListAsync(ct);

    private static ExecutionGraph BuildGraph(
        Guid projectId,
        ProjectExecution execution,
        string? userInput,
        List<ProjectModule> modules,
        List<ModuleConnection> connections)
    {
        var graph = ExecutionGraph.Build(modules, connections);
        graph.ProjectId = projectId;
        graph.ExecutionId = execution.Id;
        graph.WorkspacePath = execution.WorkspacePath;
        graph.UserInput = userInput;
        return graph;
    }

    private static void RestoreCompletedStep(ExecutionGraph graph, StepExecution step)
    {
        if (!graph.Nodes.TryGetValue(step.ProjectModuleId, out var node)) return;
        if (string.IsNullOrWhiteSpace(step.OutputData)) return;

        try
        {
            node.StepExecution = step;
            node.Output = JsonSerializer.Deserialize<StepOutput>(step.OutputData, JsonOptions);
            node.Status = NodeStatus.Completed;
            if (node.Output is not null)
                graph.PropagateOutputs(node);
        }
        catch { /* ignore malformed historical output */ }
    }

    private static async Task<Dictionary<Guid, string>> LoadExecutionFilePathsAsync(
        Guid executionId,
        UserDbContext db,
        CancellationToken ct)
    {
        var files = await db.ExecutionFiles
            .Where(f => f.StepExecution.ExecutionId == executionId)
            .Select(f => new { f.Id, f.FilePath })
            .ToListAsync(ct);

        return files.ToDictionary(f => f.Id, f => f.FilePath);
    }

    private static async Task<StepExecution?> GetPausedStepAsync(
        Guid executionId,
        Guid moduleId,
        UserDbContext db,
        CancellationToken ct) =>
        await db.StepExecutions
            .FirstOrDefaultAsync(s => s.ExecutionId == executionId && s.ProjectModuleId == moduleId, ct);

    private async Task<TelegramConfig?> GetTelegramConfigForCorrelationAsync(TelegramCorrelation correlation)
    {
        await using var db = _tenantFactory.Create(correlation.TenantDbName);
        var exec = await db.ProjectExecutions.FindAsync(correlation.ExecutionId);
        if (exec is null) return null;
        var proj = await db.Projects.FindAsync(exec.ProjectId);
        if (string.IsNullOrWhiteSpace(proj?.TelegramConfig)) return null;
        return JsonSerializer.Deserialize<TelegramConfig>(proj.TelegramConfig, JsonOptions);
    }

    private static Dictionary<string, object> MergeConfiguration(string? moduleConfig, string? stepConfig)
    {
        var merged = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        MergeJson(moduleConfig, merged);
        MergeJson(stepConfig, merged);
        return merged;
    }

    private static void MergeJson(string? json, Dictionary<string, object> target)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, JsonOptions);
            if (values is null) return;
            foreach (var (key, value) in values)
                target[key] = value.Clone();
        }
        catch { /* ignore malformed config */ }
    }

    private static object DescribeInputs(ModuleNode node) =>
        node.InputPorts.ToDictionary(
            p => p.PortId,
            p => p.ReceivedData.Select(d => new
            {
                d.DataType,
                d.TextContent,
                Files = d.Files?.Select(f => new { f.FileId, f.FileName, f.ContentType, f.FileSize }).ToList(),
                d.SourcePortId,
            }).ToList());

    private static string GetEffectiveModelName(ProjectModule pm)
    {
        var config = MergeConfiguration(pm.AiModule.Configuration, pm.Configuration);
        if (config.TryGetValue("modelName", out var value))
        {
            if (value is JsonElement je && je.ValueKind == JsonValueKind.String)
                return je.GetString() ?? pm.AiModule.ModelName;
            var str = value?.ToString();
            if (!string.IsNullOrWhiteSpace(str))
                return str;
        }
        return pm.AiModule.ModelName;
    }

    private static string GetUniqueFileName(string directory, string fileName)
    {
        var safeName = string.IsNullOrWhiteSpace(fileName) ? "output.bin" : Path.GetFileName(fileName);
        var candidate = safeName;
        var stem = Path.GetFileNameWithoutExtension(safeName);
        var ext = Path.GetExtension(safeName);
        var i = 2;

        while (File.Exists(Path.Combine(directory, candidate)))
        {
            candidate = $"{stem}_{i}{ext}";
            i++;
        }

        return candidate;
    }

    private static List<(string Label, string CallbackData)> ControlOptions() =>
        [
            ("Continuar", "continue"),
            ("Abortar", "abort"),
            ("Reiniciar", "restart"),
        ];

    private static string BuildExecutionSummary(ExecutionGraph graph, string? userInput)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(userInput))
            parts.Add($"Input: {Truncate(userInput, 160)}");

        foreach (var node in graph.Nodes.Values.OrderBy(n => n.ProjectModule.StepOrder))
        {
            if (node.Output is null) continue;
            var value = node.Output.Summary ?? node.Output.Content;
            if (!string.IsNullOrWhiteSpace(value))
                parts.Add($"{node.ProjectModule.StepOrder}. {node.ModuleType}: {Truncate(value, 160)}");
            else if (node.Output.Files.Count > 0)
                parts.Add($"{node.ProjectModule.StepOrder}. {node.ModuleType}: {node.Output.Files.Count} archivo(s)");
        }

        return string.Join(" | ", parts.Take(8));
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";

    private static async Task<string?> BuildPreviousSummaryContextAsync(
        UserDbContext db,
        Guid projectId,
        Guid currentExecutionId,
        CancellationToken ct)
    {
        var previousSummaries = await db.ProjectExecutions
            .Where(e => e.ProjectId == projectId
                && e.Status == "Completed"
                && e.ExecutionSummary != null
                && e.Id != currentExecutionId)
            .OrderByDescending(e => e.CompletedAt)
            .Take(10)
            .Select(e => new { e.CompletedAt, e.ExecutionSummary })
            .ToListAsync(ct);

        if (previousSummaries.Count == 0)
            return null;

        var lines = previousSummaries
            .OrderBy(s => s.CompletedAt)
            .Select(s => $"- ({s.CompletedAt:yyyy-MM-dd HH:mm}) {s.ExecutionSummary}");
        return "[Historial de ejecuciones anteriores - NO repitas contenido ya creado]\n"
            + string.Join("\n", lines);
    }
}
