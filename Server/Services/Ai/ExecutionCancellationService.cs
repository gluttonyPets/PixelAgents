using System.Collections.Concurrent;

namespace Server.Services.Ai
{
    /// <summary>
    /// Manages CancellationTokenSources for active pipeline executions, keyed by projectId.
    /// Registered as singleton.
    /// </summary>
    public class ExecutionCancellationService
    {
        private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _sources = new();

        public CancellationToken Register(Guid projectId)
        {
            Cancel(projectId); // cancel any previous execution for this project
            var cts = new CancellationTokenSource();
            _sources[projectId] = cts;
            return cts.Token;
        }

        public bool Cancel(Guid projectId)
        {
            if (_sources.TryRemove(projectId, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
                return true;
            }
            return false;
        }

        public void Remove(Guid projectId)
        {
            if (_sources.TryRemove(projectId, out var cts))
                cts.Dispose();
        }
    }
}
