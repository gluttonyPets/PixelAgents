namespace Client.Models;

// ── Build Info ──
public record BuildInfoResponse(string CommitHash, string BuildDate);

// ── Auth ──
public record RegisterRequest(string Email, string Password);
public record LoginRequest(string Email, string Password, bool RememberMe = false);
public record AuthResponse(string Email, Guid AccountId, string? DbName);

// ── ApiKey ──
public record CreateApiKeyRequest(string Name, string ProviderType, string Key);
public record ApiKeyResponse(Guid Id, string Name, string ProviderType, DateTime CreatedAt);

// ── AiModule ──
public record CreateAiModuleRequest(
    string Name, string? Description, string ProviderType,
    string ModuleType, string ModelName, Guid? ApiKeyId, string? Configuration);
public record UpdateAiModuleApiKeyRequest(Guid? ApiKeyId);
public record AiModuleResponse(
    Guid Id, string Name, string? Description, string ProviderType,
    string ModuleType, string ModelName, Guid? ApiKeyId, string? ApiKeyName,
    string? Configuration, bool IsEnabled, DateTime CreatedAt, DateTime UpdatedAt);

// ── Project ──
public record CreateProjectRequest(string Name, string? Description, string? Context);
public record UpdateProjectRequest(string Name, string? Description, string? Context);
public record ProjectResponse(Guid Id, string Name, string? Description, string? Context, DateTime CreatedAt, DateTime UpdatedAt);
public record ProjectDetailResponse(
    Guid Id, string Name, string? Description, DateTime CreatedAt, DateTime UpdatedAt,
    List<ProjectModuleResponse> Modules)
{
    public string? Context { get; set; }
}

// ── ProjectModule ──
public record AddProjectModuleRequest(Guid AiModuleId, int StepOrder, string? StepName, string? InputMapping, string? Configuration, string BranchId = "main", int? BranchFromStep = null);
public record UpdateProjectModuleRequest(int StepOrder, string? StepName, string? InputMapping, string? Configuration, bool IsActive);
public record ProjectModuleResponse(
    Guid Id, Guid AiModuleId, string AiModuleName, string ModuleType, string ModelName,
    int StepOrder, string? StepName, string? InputMapping, string? Configuration, bool IsActive,
    string BranchId, int? BranchFromStep);

// ── Execution ──
public record ExecuteProjectRequest(string? UserInput);
public record RetryFromStepRequest(int StepOrder, string? Comment);
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
    Guid Id, Guid ProjectModuleId, string ModuleName, int StepOrder,
    string Status, string? InputData, string? OutputData, string? ErrorMessage,
    DateTime CreatedAt, DateTime? CompletedAt, decimal EstimatedCost,
    List<ExecutionFileResponse> Files);
public record ExecutionFileResponse(
    Guid Id, string FileName, string ContentType, string FilePath,
    string Direction, long FileSize, DateTime CreatedAt);

// ── Execution Logs ──
public record ExecutionLogResponse(
    string Level, string Message, int? StepOrder, string? StepName, DateTime Timestamp);

// ── WhatsApp ──
public record WhatsAppConfigDto(string PhoneNumberId, string AccessToken,
    string RecipientNumber, string WebhookVerifyToken);

// ── Telegram ──
public record TelegramConfigDto(string BotToken, string ChatId);

// ── Instagram (Buffer) ──
public record BufferConfigDto(string ApiKey, string ChannelId);

// ── Module Files ──
public record ModuleFileResponse(
    Guid Id, Guid AiModuleId, string ModuleName,
    string FileName, string ContentType, long FileSize, DateTime CreatedAt);

// ── Schedule ──
public record CreateScheduleRequest(string CronExpression, string TimeZone, string? UserInput);
public record UpdateScheduleRequest(string CronExpression, string TimeZone, string? UserInput, bool IsEnabled);
public record ScheduleResponse(
    Guid Id, Guid ProjectId, bool IsEnabled, string CronExpression, string TimeZone,
    string? UserInput, DateTime? LastRunAt, DateTime? NextRunAt,
    DateTime CreatedAt, DateTime UpdatedAt);

// ── Structured Output ──
public class StepOutputDto
{
    public string? Type { get; set; }
    public string? Content { get; set; }
    public string? Summary { get; set; }
    public List<OutputItemDto>? Items { get; set; }
    public List<OutputFileDto>? Files { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
}

public class OutputItemDto
{
    public string Content { get; set; } = "";
    public string? Label { get; set; }
}

public class OutputFileDto
{
    public Guid FileId { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long FileSize { get; set; }
    public string? RevisedPrompt { get; set; }
}
