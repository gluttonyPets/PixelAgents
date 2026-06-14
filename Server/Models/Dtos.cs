namespace Server.Models
{
    // ── Auth ──
    public record RegisterRequest(string Email, string Password);
    public record LoginRequest(string Email, string Password, bool RememberMe = false);
    public record AuthResponse(string Email, Guid AccountId, string? DbName);

    // ── ApiKey ──
    public record CreateApiKeyRequest(string Name, string ProviderType, string Key);
    public record UpdateApiKeyRequest(string Name, string ProviderType, string? Key);
    public record ApiKeyResponse(Guid Id, string Name, string ProviderType, DateTime CreatedAt, DateTime UpdatedAt, int ModulesCount);

    // ── Rule ──
    public record CreateRuleRequest(string Title, string Content, bool IsActive = true, int SortOrder = 0);
    public record UpdateRuleRequest(string Title, string Content, bool IsActive, int SortOrder);
    public record RuleResponse(Guid Id, string Title, string Content, bool IsActive, int SortOrder, DateTime CreatedAt, DateTime UpdatedAt);

    // ── AiModule ──
    public record CreateAiModuleRequest(
        string Name, string? Description, string ProviderType,
        string ModuleType, string ModelName, Guid? ApiKeyId, string? Configuration);
    public record UpdateAiModuleRequest(
        string Name, string? Description, string ProviderType,
        string ModuleType, string ModelName, Guid? ApiKeyId, string? Configuration, bool IsEnabled);
    public record AiModuleResponse(
        Guid Id, string Name, string? Description, string ProviderType,
        string ModuleType, string ModelName, Guid? ApiKeyId, string? ApiKeyName,
        string? Configuration, bool IsEnabled, DateTime CreatedAt, DateTime UpdatedAt);
    // Uso de un modulo de catalogo a traves de los proyectos.
    public record ModuleUsageResponse(int ProjectCount, List<string> ProjectNames);

    // ── Project ──
    public record CreateProjectRequest(string Name, string? Description, string? Context);
    public record UpdateProjectRequest(string Name, string? Description, string? Context);
    public record GraphLayoutRequest(string? GraphLayout);
    public record ProjectResponse(Guid Id, string Name, string? Description, string? Context, DateTime CreatedAt, DateTime UpdatedAt);
    public record ProjectDetailResponse(
        Guid Id, string Name, string? Description, string? Context, DateTime CreatedAt, DateTime UpdatedAt,
        List<ProjectModuleResponse> Modules, string? GraphLayout = null,
        List<ModuleConnectionResponse>? Connections = null);

    // ── ProjectModule ──
    public record AddProjectModuleRequest(Guid AiModuleId, string? StepName, string? Configuration);
    public record UpdateProjectModuleRequest(string? StepName, string? Configuration, bool IsActive);
    // Reapunta una instancia de modulo (nodo del pipeline) a otro modulo de catalogo,
    // util tras duplicar un modulo compartido con cambios.
    public record ReassignProjectModuleRequest(Guid AiModuleId);
    public record ProjectModuleResponse(
        Guid Id, Guid AiModuleId, string AiModuleName, string ModuleType, string ModelName,
        string? StepName, string? Configuration, bool IsActive,
        double PosX = 0, double PosY = 0,
        List<OrchestratorOutputResponse>? OrchestratorOutputs = null);

    // ── ModuleConnection ──
    public record ModuleConnectionResponse(Guid Id, Guid FromModuleId, string FromPort, Guid ToModuleId, string ToPort, string? Format = null);
    public record SaveGraphRequest(List<NodePositionEntry> Positions, List<ConnectionEntry> Connections, List<SceneCountEntry>? SceneCounts = null, List<ModuleConfigEntry>? ModuleConfigs = null);
    public record NodePositionEntry(Guid ModuleId, double PosX, double PosY);
    public record ConnectionEntry(Guid FromModuleId, string FromPort, Guid ToModuleId, string ToPort, string? Format = null);
    public record SceneCountEntry(Guid ModuleId, int SceneCount);
    public record ModuleConfigEntry(Guid ModuleId, string Key, string Value);

    // ── Execution ──
    public record ExecuteProjectRequest(string? UserInput, bool UseHistory = true);
    public record RetryFromModuleRequest(Guid ProjectModuleId, string? Comment);
    public record OrchestratorReviewRequest(bool Approved, string? Comment);
    public record CheckpointReviewRequest(bool Approved);

    // ── OrchestratorOutput ──
    public record OrchestratorOutputRequest(string Label, string Prompt, string DataType, int SortOrder);
    public record OrchestratorOutputResponse(Guid Id, string OutputKey, string Label, string Prompt,
        string DataType, int SortOrder);
    public record ExecutionResponse(
        Guid Id, Guid ProjectId, string Status, string WorkspacePath,
        DateTime CreatedAt, DateTime? CompletedAt, string? UserInput,
        decimal TotalEstimatedCost);
    public record ExecutionDetailResponse(
        Guid Id, Guid ProjectId, string Status, string WorkspacePath,
        DateTime CreatedAt, DateTime? CompletedAt, string? UserInput,
        decimal TotalEstimatedCost,
        List<StepExecutionResponse> Steps);
    public record StepExecutionResponse(
        Guid Id, Guid ProjectModuleId, string ModuleName, string ModuleType,
        string Status, string? InputData, string? OutputData, string? ErrorMessage,
        DateTime CreatedAt, DateTime? CompletedAt, decimal EstimatedCost,
        List<ExecutionFileResponse> Files);
    public record ExecutionFileResponse(
        Guid Id, string FileName, string ContentType, string FilePath,
        string Direction, long FileSize, DateTime CreatedAt);

    // ── WhatsApp ──
    public record WhatsAppConfigDto(string PhoneNumberId, string AccessToken,
        string RecipientNumber, string WebhookVerifyToken);

    // ── Telegram ──
    public record TelegramConfigDto(string BotToken, string ChatId);

    // ── Instagram (Buffer) / TikTok (Buffer) ──
    public record BufferConfigDto(string ApiKey, string ChannelId);

    // ── Module Files ──
    // Los archivos pertenecen a la INSTANCIA (ProjectModule), no al catalogo.
    // ProjectName/StepName permiten que la biblioteca muestre a que proyecto
    // y paso concreto pertenece cada archivo subido.
    public record ModuleFileResponse(
        Guid Id, Guid ProjectModuleId, string ModuleName, Guid ProjectId, string ProjectName,
        string? StepName, string FileName, string ContentType, long FileSize, DateTime CreatedAt);

    // ── Schedule ──
    public record CreateScheduleRequest(string CronExpression, string TimeZone, string? UserInput, bool UseHistory = true, bool UsePromptQueue = false);
    public record UpdateScheduleRequest(string CronExpression, string TimeZone, string? UserInput, bool IsEnabled, bool UseHistory = true, bool UsePromptQueue = false);
    public record ScheduleResponse(
        Guid Id, Guid ProjectId, bool IsEnabled, string CronExpression, string TimeZone,
        string? UserInput, bool UseHistory, bool UsePromptQueue,
        DateTime? LastRunAt, DateTime? NextRunAt,
        DateTime CreatedAt, DateTime UpdatedAt);

    // ── Planned Prompts ──
    public record GeneratePlannedPromptsRequest(string ModelName, int Count, string Instructions, bool ReplaceExisting = false);
    public record CreatePlannedPromptRequest(string Content);
    public record UpdatePlannedPromptRequest(string Content);
    public record ReorderPlannedPromptsRequest(List<Guid> OrderedIds);
    public record PlannedPromptResponse(
        Guid Id, Guid ProjectId, int OrderIndex, string Content, string Status,
        DateTime CreatedAt, DateTime UpdatedAt, DateTime? UsedAt, Guid? ExecutionId);
}
