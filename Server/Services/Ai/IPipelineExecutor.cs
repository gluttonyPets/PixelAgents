using Server.Data;
using Server.Models;

namespace Server.Services.Ai
{
    public interface IPipelineExecutor
    {
        Task<ProjectExecution> ExecuteAsync(Guid projectId, string? userInput, UserDbContext db, string tenantDbName, CancellationToken ct = default, bool useHistory = true);
        Task<ProjectExecution> RetryFromModuleAsync(Guid executionId, Guid moduleId, string? comment, UserDbContext db, string tenantDbName, CancellationToken ct = default);
        Task<ProjectExecution> ResumeFromInteractionAsync(Guid executionId, string responseText, UserDbContext db, string tenantDbName, CancellationToken ct = default);
        Task<ProjectExecution> AbortFromInteractionAsync(Guid executionId, UserDbContext db, string tenantDbName);
        Task<ProjectExecution> ResumeFromOrchestratorAsync(Guid executionId, bool approved, string? comment, UserDbContext db, string tenantDbName, CancellationToken ct = default);
        Task<ProjectExecution> ResumeFromCheckpointAsync(Guid executionId, bool approved, UserDbContext db, string tenantDbName, CancellationToken ct = default);
        Task SendNextQueuedInteractionAsync(Guid executionId, string chatId);
        Task CancelQueuedInteractionsAsync(Guid executionId);

        /// <summary>
        /// Returns information about the output of the currently paused interaction
        /// so the caller can decide whether the user is editing an image or text and
        /// which models are eligible.
        /// </summary>
        Task<PausedOutputInfo?> GetPausedOutputInfoAsync(Guid executionId, UserDbContext db, CancellationToken ct = default);

        /// <summary>
        /// Runs an out-of-band edit on the paused interaction's output (image or text)
        /// using the given provider + model. The tenant must have an API Key for the
        /// provider configured. Does not advance the pipeline; the original interaction
        /// stays paused until the user explicitly continues.
        /// </summary>
        Task<EditOutputResult> EditPausedOutputAsync(Guid executionId, string providerType, string modelName, string editPrompt, UserDbContext db, string tenantDbName, CancellationToken ct = default);
    }

    public class PausedOutputInfo
    {
        /// <summary>"image" when the paused output carries image files, otherwise "text".</summary>
        public string OutputKind { get; set; } = "text";
        public string? TextContent { get; set; }
        public int ImageFileCount { get; set; }
    }

    public class EditOutputResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public string? TextResponse { get; set; }
        public List<EditOutputFile> Files { get; set; } = [];
    }

    public class EditOutputFile
    {
        public byte[] Data { get; set; } = [];
        public string FileName { get; set; } = "output";
        public string ContentType { get; set; } = "application/octet-stream";
    }
}
