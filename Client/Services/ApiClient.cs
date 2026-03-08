using System.Net.Http.Json;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using Client.Models;

namespace Client.Services;

public class ApiClient
{
    private readonly HttpClient _http;

    public ApiClient(HttpClient http)
    {
        _http = http;
    }

    // ── Auth ──

    public async Task<(bool Ok, string? Error)> RegisterAsync(string email, string password)
    {
        var resp = await SendAsync(HttpMethod.Post, "/api/auth/register", new RegisterRequest(email, password));
        if (resp.IsSuccessStatusCode) return (true, null);
        return (false, await ReadErrorAsync(resp));
    }

    public async Task<(bool Ok, AuthResponse? User, string? Error)> LoginAsync(string email, string password)
    {
        var resp = await SendAsync(HttpMethod.Post, "/api/auth/login", new LoginRequest(email, password));
        if (!resp.IsSuccessStatusCode)
            return (false, null, "Credenciales incorrectas");
        var user = await resp.Content.ReadFromJsonAsync<AuthResponse>();
        return (true, user, null);
    }

    public async Task LogoutAsync()
    {
        await SendAsync(HttpMethod.Post, "/api/auth/logout");
    }

    public async Task<AuthResponse?> GetMeAsync()
    {
        var resp = await SendAsync(HttpMethod.Get, "/api/auth/me");
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<AuthResponse>();
    }

    // ── ApiKeys ──

    public async Task<List<ApiKeyResponse>> GetApiKeysAsync()
    {
        var resp = await SendAsync(HttpMethod.Get, "/api/apikeys");
        if (!resp.IsSuccessStatusCode) return [];
        return await resp.Content.ReadFromJsonAsync<List<ApiKeyResponse>>() ?? [];
    }

    public async Task<(bool Ok, string? Error)> CreateApiKeyAsync(CreateApiKeyRequest req)
    {
        var resp = await SendAsync(HttpMethod.Post, "/api/apikeys", req);
        if (resp.IsSuccessStatusCode) return (true, null);
        return (false, await ReadErrorAsync(resp));
    }

    public async Task DeleteApiKeyAsync(Guid id)
    {
        await SendAsync(HttpMethod.Delete, $"/api/apikeys/{id}");
    }

    // ── Modules ──

    public async Task<List<AiModuleResponse>> GetModulesAsync(string? providerType = null, string? moduleType = null)
    {
        var query = "/api/modules";
        var parts = new List<string>();
        if (providerType is not null) parts.Add($"providerType={providerType}");
        if (moduleType is not null) parts.Add($"moduleType={moduleType}");
        if (parts.Count > 0) query += "?" + string.Join("&", parts);
        var resp = await SendAsync(HttpMethod.Get, query);
        if (!resp.IsSuccessStatusCode) return [];
        return await resp.Content.ReadFromJsonAsync<List<AiModuleResponse>>() ?? [];
    }

    public async Task<(bool Ok, string? Error)> CreateModuleAsync(CreateAiModuleRequest req)
    {
        var resp = await SendAsync(HttpMethod.Post, "/api/modules", req);
        if (resp.IsSuccessStatusCode) return (true, null);
        return (false, await ReadErrorAsync(resp));
    }

    public async Task<(bool Ok, string? Error)> UpdateModuleApiKeyAsync(Guid moduleId, AiModuleResponse current, Guid? newApiKeyId)
    {
        var body = new
        {
            current.Name,
            current.Description,
            current.ProviderType,
            current.ModuleType,
            current.ModelName,
            ApiKeyId = newApiKeyId,
            current.Configuration,
            current.IsEnabled
        };
        var resp = await SendAsync(HttpMethod.Put, $"/api/modules/{moduleId}", body);
        if (resp.IsSuccessStatusCode) return (true, null);
        return (false, await ReadErrorAsync(resp));
    }

    public async Task<(bool Ok, string? Error)> UpdateModuleConfigAsync(Guid moduleId, AiModuleResponse current, string? newConfig)
    {
        var body = new
        {
            current.Name,
            current.Description,
            current.ProviderType,
            current.ModuleType,
            current.ModelName,
            current.ApiKeyId,
            Configuration = newConfig,
            current.IsEnabled
        };
        var resp = await SendAsync(HttpMethod.Put, $"/api/modules/{moduleId}", body);
        if (resp.IsSuccessStatusCode) return (true, null);
        return (false, await ReadErrorAsync(resp));
    }

    public async Task<(bool Ok, string? Error)> UpdateModuleModelAsync(Guid moduleId, AiModuleResponse current, string newModelName)
    {
        var body = new
        {
            current.Name,
            current.Description,
            current.ProviderType,
            current.ModuleType,
            ModelName = newModelName,
            current.ApiKeyId,
            current.Configuration,
            current.IsEnabled
        };
        var resp = await SendAsync(HttpMethod.Put, $"/api/modules/{moduleId}", body);
        if (resp.IsSuccessStatusCode) return (true, null);
        return (false, await ReadErrorAsync(resp));
    }

    public async Task DeleteModuleAsync(Guid id)
    {
        await SendAsync(HttpMethod.Delete, $"/api/modules/{id}");
    }

    // ── Projects ──

    public async Task<List<ProjectResponse>> GetProjectsAsync()
    {
        var resp = await SendAsync(HttpMethod.Get, "/api/projects");
        if (!resp.IsSuccessStatusCode) return [];
        return await resp.Content.ReadFromJsonAsync<List<ProjectResponse>>() ?? [];
    }

    public async Task<(bool Ok, string? Error)> CreateProjectAsync(CreateProjectRequest req)
    {
        var resp = await SendAsync(HttpMethod.Post, "/api/projects", req);
        if (resp.IsSuccessStatusCode) return (true, null);
        return (false, await ReadErrorAsync(resp));
    }

    public async Task<ProjectDetailResponse?> GetProjectDetailAsync(Guid id)
    {
        var resp = await SendAsync(HttpMethod.Get, $"/api/projects/{id}");
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<ProjectDetailResponse>();
    }

    public async Task DeleteProjectAsync(Guid id)
    {
        await SendAsync(HttpMethod.Delete, $"/api/projects/{id}");
    }

    // ── ProjectModules (Pipeline) ──

    public async Task<(bool Ok, string? Error)> AddProjectModuleAsync(Guid projectId, AddProjectModuleRequest req)
    {
        var resp = await SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/modules", req);
        if (resp.IsSuccessStatusCode) return (true, null);
        return (false, await ReadErrorAsync(resp));
    }

    public async Task<(bool Ok, string? Error)> UpdateProjectModuleAsync(Guid projectId, Guid id, UpdateProjectModuleRequest req)
    {
        var resp = await SendAsync(HttpMethod.Put, $"/api/projects/{projectId}/modules/{id}", req);
        if (resp.IsSuccessStatusCode) return (true, null);
        return (false, await ReadErrorAsync(resp));
    }

    public async Task<(bool Ok, string? Error)> SwapStepOrderAsync(Guid projectId, Guid moduleIdA, Guid moduleIdB)
    {
        var resp = await SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/modules/swap",
            new { ModuleIdA = moduleIdA, ModuleIdB = moduleIdB });
        if (resp.IsSuccessStatusCode) return (true, null);
        return (false, await ReadErrorAsync(resp));
    }

    public async Task<(bool Ok, string? Error)> DeleteProjectModuleAsync(Guid projectId, Guid id)
    {
        var resp = await SendAsync(HttpMethod.Delete, $"/api/projects/{projectId}/modules/{id}");
        if (resp.IsSuccessStatusCode) return (true, null);
        return (false, await ReadErrorAsync(resp));
    }

    // ── Executions ──

    public async Task<(bool Ok, ExecutionDetailResponse? Result, string? Error)> ExecuteProjectAsync(Guid projectId, string? userInput)
    {
        var resp = await SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/execute", new ExecuteProjectRequest(userInput));
        if (!resp.IsSuccessStatusCode)
        {
            return (false, null, await ReadErrorAsync(resp));
        }
        var result = await resp.Content.ReadFromJsonAsync<ExecutionDetailResponse>();
        return (true, result, null);
    }

    public async Task<List<ExecutionResponse>> GetExecutionsAsync(Guid projectId)
    {
        var resp = await SendAsync(HttpMethod.Get, $"/api/projects/{projectId}/executions");
        if (!resp.IsSuccessStatusCode) return [];
        return await resp.Content.ReadFromJsonAsync<List<ExecutionResponse>>() ?? [];
    }

    public async Task<ExecutionDetailResponse?> GetExecutionDetailAsync(Guid id)
    {
        var resp = await SendAsync(HttpMethod.Get, $"/api/executions/{id}");
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<ExecutionDetailResponse>();
    }

    public async Task<(bool Ok, ExecutionDetailResponse? Result, string? Error)> RetryFromStepAsync(
        Guid executionId, int stepOrder, string? comment)
    {
        var resp = await SendAsync(HttpMethod.Post,
            $"/api/executions/{executionId}/retry-from-step",
            new RetryFromStepRequest(stepOrder, comment));
        if (!resp.IsSuccessStatusCode)
        {
            return (false, null, await ReadErrorAsync(resp));
        }
        var result = await resp.Content.ReadFromJsonAsync<ExecutionDetailResponse>();
        return (true, result, null);
    }

    public async Task<bool> CancelExecutionAsync(Guid projectId)
    {
        var resp = await SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/cancel");
        return resp.IsSuccessStatusCode;
    }

    // ── Internal ──

    private const int MaxRetries = 2;

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null)
    {
        for (int attempt = 0; ; attempt++)
        {
            var request = new HttpRequestMessage(method, url);
            request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);

            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            try
            {
                return await _http.SendAsync(request);
            }
            catch (HttpRequestException) when (attempt < MaxRetries)
            {
                await Task.Delay(1000 * (attempt + 1));
            }
            catch (HttpRequestException)
            {
                throw new HttpRequestException(
                    $"No se pudo conectar al servidor ({_http.BaseAddress}). " +
                    "Verifica que el servidor este corriendo en el puerto correcto.");
            }
            catch (Exception ex) when (ex.Message.Contains("NetworkError") || ex.Message.Contains("fetch"))
            {
                if (attempt < MaxRetries)
                {
                    await Task.Delay(1000 * (attempt + 1));
                    continue;
                }
                throw new HttpRequestException(
                    $"Error de red al conectar con {_http.BaseAddress}. " +
                    "Verifica que el servidor este corriendo y que el certificado HTTPS sea valido.");
            }
        }
    }

    // ── WhatsApp Config ──

    public async Task<WhatsAppConfigDto?> GetWhatsAppConfigAsync(Guid projectId)
    {
        var resp = await SendAsync(HttpMethod.Get, $"/api/projects/{projectId}/whatsapp-config");
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<WhatsAppConfigDto>();
    }

    public async Task<(bool Ok, string? Error)> SaveWhatsAppConfigAsync(Guid projectId, WhatsAppConfigDto dto)
    {
        var resp = await SendAsync(HttpMethod.Put, $"/api/projects/{projectId}/whatsapp-config", dto);
        if (resp.IsSuccessStatusCode) return (true, null);
        return (false, await ReadErrorAsync(resp));
    }

    // ── Telegram Config ──

    public async Task<TelegramConfigDto?> GetTelegramConfigAsync(Guid projectId)
    {
        var resp = await SendAsync(HttpMethod.Get, $"/api/projects/{projectId}/telegram-config");
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<TelegramConfigDto>();
    }

    public async Task<(bool Ok, string? Error)> SaveTelegramConfigAsync(Guid projectId, TelegramConfigDto dto)
    {
        var resp = await SendAsync(HttpMethod.Put, $"/api/projects/{projectId}/telegram-config", dto);
        if (resp.IsSuccessStatusCode) return (true, null);
        return (false, await ReadErrorAsync(resp));
    }

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage resp)
    {
        try
        {
            var body = await resp.Content.ReadFromJsonAsync<ErrorBody>();
            if (body?.Error is not null) return body.Error;
        }
        catch
        {
            try { return await resp.Content.ReadAsStringAsync(); }
            catch { }
        }
        return resp.ReasonPhrase;
    }

    private record ErrorBody(string? Error);
}
