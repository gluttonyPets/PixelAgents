namespace Client.Models;

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
public record AiModuleResponse(
    Guid Id, string Name, string? Description, string ProviderType,
    string ModuleType, string ModelName, Guid? ApiKeyId, string? ApiKeyName,
    string? Configuration, bool IsEnabled, DateTime CreatedAt, DateTime UpdatedAt);

// ── Project ──
public record CreateProjectRequest(string Name, string? Description, string? Context);
public record ProjectResponse(Guid Id, string Name, string? Description, string? Context, DateTime CreatedAt, DateTime UpdatedAt);
public record ProjectDetailResponse(
    Guid Id, string Name, string? Description, string? Context, DateTime CreatedAt, DateTime UpdatedAt,
    List<ProjectModuleResponse> Modules);

// ── ProjectModule ──
public record AddProjectModuleRequest(Guid AiModuleId, int StepOrder, string? StepName, string? InputMapping, string? Configuration);
public record ProjectModuleResponse(
    Guid Id, Guid AiModuleId, string AiModuleName, string ModuleType,
    int StepOrder, string? StepName, string? InputMapping, string? Configuration, bool IsActive);

// ── Execution ──
public record ExecuteProjectRequest(string? UserInput);
public record ExecutionResponse(
    Guid Id, Guid ProjectId, string Status, string WorkspacePath,
    DateTime CreatedAt, DateTime? CompletedAt);
public record ExecutionDetailResponse(
    Guid Id, Guid ProjectId, string Status, string WorkspacePath,
    DateTime CreatedAt, DateTime? CompletedAt,
    List<StepExecutionResponse> Steps);
public record StepExecutionResponse(
    Guid Id, Guid ProjectModuleId, string ModuleName, int StepOrder,
    string Status, string? InputData, string? OutputData, string? ErrorMessage,
    DateTime CreatedAt, DateTime? CompletedAt, List<ExecutionFileResponse> Files);
public record ExecutionFileResponse(
    Guid Id, string FileName, string ContentType, string FilePath,
    string Direction, long FileSize, DateTime CreatedAt);
