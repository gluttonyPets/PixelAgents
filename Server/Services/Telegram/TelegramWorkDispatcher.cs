using Server.Data;
using Server.Services.Ai;

namespace Server.Services.Telegram
{
    /// <summary>
    /// Dependencias con las que corre un trabajo lanzado desde Telegram. En segundo plano
    /// salen de un scope propio: el del update se cierra en cuanto se contesta a Telegram.
    /// </summary>
    public sealed record TelegramWork(
        CoreDbContext CoreDb,
        ITenantDbContextFactory Factory,
        IPipelineExecutor Executor,
        TelegramService Telegram);

    /// <summary>
    /// Lanza fuera del procesado del update lo que ejecuta un pipeline (continuar, reiniciar,
    /// siguiente ejecucion). Antes se esperaba dentro del bucle de Telegram y, mientras el
    /// pipeline corria, ningun otro boton de ningun pipeline se atendia.
    /// </summary>
    public interface ITelegramWorkDispatcher
    {
        /// <summary>
        /// Programa el trabajo y vuelve. La implementacion de pruebas lo ejecuta en linea y
        /// devuelve la tarea para poder esperarlo.
        /// </summary>
        Task DispatchAsync(string description, Func<TelegramWork, Task> work);
    }

    /// <summary>Implementacion real: cada trabajo en su propio scope con Task.Run.</summary>
    public sealed class BackgroundTelegramWorkDispatcher : ITelegramWorkDispatcher
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger<BackgroundTelegramWorkDispatcher> _log;

        public BackgroundTelegramWorkDispatcher(IServiceScopeFactory scopes, ILogger<BackgroundTelegramWorkDispatcher> log)
        {
            _scopes = scopes;
            _log = log;
        }

        public Task DispatchAsync(string description, Func<TelegramWork, Task> work)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    var sp = scope.ServiceProvider;
                    await work(new TelegramWork(
                        sp.GetRequiredService<CoreDbContext>(),
                        sp.GetRequiredService<ITenantDbContextFactory>(),
                        sp.GetRequiredService<IPipelineExecutor>(),
                        sp.GetRequiredService<TelegramService>()));
                }
                catch (Exception ex)
                {
                    // El trabajo ya avisa por Telegram de sus errores; esto es la ultima red.
                    _log.LogError(ex, "[TG-Work] {Description} failed", description);
                }
            });
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Ejecuta el trabajo en linea con las dependencias del handler. Es lo que usa el handler
    /// cuando nadie le inyecta otro dispatcher (pruebas).
    /// </summary>
    internal sealed class InlineTelegramWorkDispatcher : ITelegramWorkDispatcher
    {
        private readonly TelegramWork _deps;

        public InlineTelegramWorkDispatcher(TelegramWork deps) => _deps = deps;

        public Task DispatchAsync(string description, Func<TelegramWork, Task> work) => work(_deps);
    }
}
