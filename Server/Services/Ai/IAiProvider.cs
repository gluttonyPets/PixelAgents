using Server.Models;

namespace Server.Services.Ai
{
    public interface IAiProvider
    {
        string ProviderType { get; }
        IEnumerable<string> SupportedModuleTypes { get; }
        Task<AiResult> ExecuteAsync(AiExecutionContext context);
    }
}
