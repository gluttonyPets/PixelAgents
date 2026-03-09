using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Server.Data;
using Server.Models;

namespace Server.Services.Telegram
{
    /// <summary>
    /// Background service that polls Telegram for updates using getUpdates (long polling).
    /// Active when no WebhookBaseUrl is configured (i.e., local development).
    /// </summary>
    public class TelegramPollingService : BackgroundService
    {
        private readonly IServiceProvider _services;
        private readonly IConfiguration _config;
        private readonly ILogger<TelegramPollingService> _logger;
        private const string ApiBase = "https://api.telegram.org";

        public TelegramPollingService(
            IServiceProvider services,
            IConfiguration config,
            ILogger<TelegramPollingService> logger)
        {
            _services = services;
            _config = config;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Only run polling if no webhook base URL is configured
            var webhookBaseUrl = _config["Telegram:WebhookBaseUrl"] ?? _config["BaseUrl"] ?? "";
            if (!string.IsNullOrWhiteSpace(webhookBaseUrl))
            {
                _logger.LogInformation("[TG-Polling] WebhookBaseUrl is set ({Url}), polling disabled. Using webhook mode.", webhookBaseUrl);
                return;
            }

            _logger.LogInformation("[TG-Polling] No WebhookBaseUrl configured. Starting long polling mode for Telegram.");

            // Wait a bit for app startup to complete
            await Task.Delay(3000, stoppingToken);

            // Collect all unique bot tokens from all tenants
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var botTokens = await CollectBotTokensAsync();
                    if (botTokens.Count == 0)
                    {
                        await Task.Delay(10000, stoppingToken);
                        continue;
                    }

                    // Poll each bot token in parallel
                    var tasks = botTokens.Select(token => PollBotAsync(token, stoppingToken));
                    await Task.WhenAll(tasks);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[TG-Polling] Error in polling loop");
                    await Task.Delay(5000, stoppingToken);
                }
            }
        }

        private readonly Dictionary<string, long> _offsets = new();

        private async Task PollBotAsync(string botToken, CancellationToken ct)
        {
            var offset = _offsets.GetValueOrDefault(botToken, 0);

            try
            {
                using var scope = _services.CreateScope();
                var http = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient();

                var url = $"{ApiBase}/bot{botToken}/getUpdates?timeout=10&offset={offset}";
                var response = await http.GetAsync(url, ct);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning("[TG-Polling] getUpdates failed: {Status} {Body}", (int)response.StatusCode, errorBody);
                    await Task.Delay(5000, ct);
                    return;
                }

                var body = await response.Content.ReadAsStringAsync(ct);
                var doc = JsonDocument.Parse(body);

                if (!doc.RootElement.TryGetProperty("result", out var results))
                    return;

                foreach (var update in results.EnumerateArray())
                {
                    // Update offset to acknowledge this update
                    if (update.TryGetProperty("update_id", out var updateIdProp))
                    {
                        var updateId = updateIdProp.GetInt64();
                        if (updateId >= offset)
                            offset = updateId + 1;
                    }

                    // Process the update using the shared handler
                    try
                    {
                        using var handlerScope = _services.CreateScope();
                        var handler = handlerScope.ServiceProvider.GetRequiredService<TelegramUpdateHandler>();
                        await handler.ProcessUpdateAsync(update);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[TG-Polling] Error processing update");
                    }
                }

                _offsets[botToken] = offset;

                // Also remove webhook for this bot token to avoid conflicts
                // (only need to do this once, but it's idempotent)
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Normal shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TG-Polling] Error polling bot");
                await Task.Delay(5000, ct);
            }
        }

        private async Task<HashSet<string>> CollectBotTokensAsync()
        {
            var tokens = new HashSet<string>();

            try
            {
                using var scope = _services.CreateScope();
                var coreDb = scope.ServiceProvider.GetRequiredService<CoreDbContext>();
                var factory = scope.ServiceProvider.GetRequiredService<ITenantDbContextFactory>();

                // Get all tenant database names from Accounts
                var tenants = await coreDb.Accounts
                    .Select(a => a.DbName)
                    .Where(d => d != null && d != "")
                    .Distinct()
                    .ToListAsync();

                foreach (var tenantDbName in tenants)
                {
                    if (string.IsNullOrWhiteSpace(tenantDbName)) continue;

                    try
                    {
                        await using var db = factory.Create(tenantDbName);
                        var configs = await db.Projects
                            .Where(p => p.TelegramConfig != null && p.TelegramConfig != "")
                            .Select(p => p.TelegramConfig)
                            .ToListAsync();

                        foreach (var configJson in configs)
                        {
                            if (string.IsNullOrWhiteSpace(configJson)) continue;
                            try
                            {
                                var config = JsonSerializer.Deserialize<TelegramConfig>(configJson);
                                if (!string.IsNullOrWhiteSpace(config?.BotToken))
                                    tokens.Add(config.BotToken);
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TG-Polling] Error collecting bot tokens");
            }

            return tokens;
        }
    }
}
