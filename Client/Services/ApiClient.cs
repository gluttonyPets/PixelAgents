using System.Net.Http.Json;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using Client.Models;

namespace Client.Services;

public class ApiClient
{
    private readonly HttpClient _http;
    private readonly AuthStateService _auth;

    public ApiClient(HttpClient http, AuthStateService auth)
    {
        _http = http;
        _auth = auth;
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

    public async Task<(bool Ok, string? Error)> UpdateApiKeyAsync(Guid id, UpdateApiKeyRequest req)
    {
        var resp = await SendAsync(HttpMethod.Put, $"/api/apikeys/{id}", req);
        if (resp.IsSuccessStatusCode) return (true, null);
        return (false, await ReadErrorAsync(resp));
    }

    public async Task DeleteApiKeyAsync(Guid id)
    {
        await SendAsync(HttpMethod.Delete, $"/api/apikeys/{id}");
    }

    // ── Rules ──

    public async Task<List<RuleResponse>> GetRulesAsync()
    {
        var resp = await SendAsync(HttpMethod.Get, "/api/rules");
        if (!resp.IsSuccessStatusCode) return [];
        return await resp.Content.ReadFromJsonAsync<List<RuleResponse>>() ?? [];
    }

    public async Task<(bool Ok, string? Error)> CreateRuleAsync(CreateRuleRequest req)
    {
        var resp = await SendAsync(HttpMethod.Post, "/api/rules", req);
        if (resp.IsSuccessStatusCode) return (true, null);
        return (false, await ReadErrorAsync(resp));
    }

    public async Task<(bool Ok, string? Error)> UpdateRuleAsync(Guid id, UpdateRuleRequest req)
    {
        var resp = await SendAsync(HttpMethod.Put, $"/api/rules/{id}", req);
        if (resp.IsSuccessStatusCode) return (true, null);
        return (false, await ReadErrorAsync(resp));
    }

    public async Task DeleteRuleAsync(Guid id)
    {
        await SendAsync(HttpMethod.Delete, $"/api/rules/{id}");
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

    public async Task<AiModuleResponse?> GetModuleAsync(Guid id)
    {
        var resp = await SendAsync(HttpMethod.Get, $"/api/modules/{id}");
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<AiModuleResponse>();
    }

    public async Task<(bool Ok, string? Error, Guid? ModuleId)> CreateModuleAsync(CreateAiModuleRequest req)
    {
        var resp = await SendAsync(HttpMethod.Post, "/api/modules", req);
        if (resp.IsSuccessStatusCode)
        {
            var created = await resp.Content.ReadFromJsonAsync<AiModuleResponse>();
            return (true, null, created?.Id);
        }
        return (false, await ReadErrorAsync(resp), null);
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

    // Cuenta en cuantos proyectos se usa un modulo de catalogo (opcionalmente excluyendo uno).
    public async Task<ModuleUsageResponse> GetModuleUsageAsync(Guid moduleId, Guid? excludeProjectId = null)
    {
        var query = $"/api/modules/{moduleId}/usage";
        if (excludeProjectId is not null) query += $"?excludeProjectId={excludeProjectId}";
        var resp = await SendAsync(HttpMethod.Get, query);
        if (!resp.IsSuccessStatusCode) return new ModuleUsageResponse(0, []);
        return await resp.Content.ReadFromJsonAsync<ModuleUsageResponse>() ?? new ModuleUsageResponse(0, []);
    }

    // Reapunta un nodo del pipeline a otro modulo de catalogo (mismo tipo).
    public async Task<(bool Ok, string? Error)> ReassignProjectModuleAsync(Guid projectId, Guid projectModuleId, Guid newAiModuleId)
    {
        var resp = await SendAsync(HttpMethod.Put,
            $"/api/projects/{projectId}/modules/{projectModuleId}/reassign",
            new ReassignProjectModuleRequest(newAiModuleId));
        if (resp.IsSuccessStatusCode) return (true, null);
        return (false, await ReadErrorAsync(resp));
    }

    // ── Module Files ──

    public async Task<List<ModuleFileResponse>> UploadModuleFilesAsync(Guid projectModuleId, MultipartFormDataContent content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/project-modules/{projectModuleId}/files");
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        request.Content = content;
        var resp = await _http.SendAsync(request);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(await ReadErrorAsync(resp) ?? "Error al subir archivos");
        return await resp.Content.ReadFromJsonAsync<List<ModuleFileResponse>>() ?? [];
    }

    public async Task<List<ModuleFileResponse>> GetModuleFilesAsync(Guid projectModuleId)
    {
        var resp = await SendAsync(HttpMethod.Get, $"/api/project-modules/{projectModuleId}/files");
        if (!resp.IsSuccessStatusCode) return [];
        return await resp.Content.ReadFromJsonAsync<List<ModuleFileResponse>>() ?? [];
    }

    public async Task<List<ModuleFileResponse>> GetAllModuleFilesAsync()
    {
        var resp = await SendAsync(HttpMethod.Get, "/api/module-files");
        if (!resp.IsSuccessStatusCode) return [];
        return await resp.Content.ReadFromJsonAsync<List<ModuleFileResponse>>() ?? [];
    }

    public async Task DeleteModuleFileAsync(Guid fileId)
    {
        await SendAsync(HttpMethod.Delete, $"/api/module-files/{fileId}");
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

    public async Task<(bool Ok, string? Error)> UpdateProjectAsync(Guid id, UpdateProjectRequest req)
    {
        var resp = await SendAsync(HttpMethod.Put, $"/api/projects/{id}", req);
        if (resp.IsSuccessStatusCode) return (true, null);
        return (false, await ReadErrorAsync(resp));
    }

    public async Task<ProjectResponse?> DuplicateProjectAsync(Guid id)
    {
        var resp = await SendAsync(HttpMethod.Post, $"/api/projects/{id}/duplicate");
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<ProjectResponse>();
    }

    public async Task<(bool Ok, string? Error)> SaveGraphLayoutAsync(Guid projectId, string? graphLayout)
    {
        var resp = await SendAsync(HttpMethod.Put, $"/api/projects/{projectId}/graph", new { graphLayout });
        if (resp.IsSuccessStatusCode) return (true, null);
        return (false, await ReadErrorAsync(resp));
    }

    public async Task<(bool Ok, string? Error)> SaveGraphAsync(Guid projectId, SaveGraphRequest req)
    {
        var resp = await SendAsync(HttpMethod.Put, $"/api/projects/{projectId}/graph/save", req);
        if (resp.IsSuccessStatusCode) return (true, null);
        return (false, await ReadErrorAsync(resp));
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

    public async Task<(bool Ok, string? Error)> DeleteProjectModuleAsync(Guid projectId, Guid id)
    {
        var resp = await SendAsync(HttpMethod.Delete, $"/api/projects/{projectId}/modules/{id}");
        if (resp.IsSuccessStatusCode) return (true, null);
        return (false, await ReadErrorAsync(resp));
    }

    // ── Executions ──

    public async Task<(bool Ok, string? Error)> ExecuteProjectAsync(Guid projectId, string? userInput, bool useHistory = true)
    {
        var resp = await SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/execute", new ExecuteProjectRequest(userInput, useHistory));
        if (!resp.IsSuccessStatusCode && (int)resp.StatusCode != 202)
        {
            return (false, await ReadErrorAsync(resp));
        }
        return (true, null);
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

    public async Task<List<ExecutionLogResponse>?> GetExecutionLogsAsync(Guid executionId)
    {
        var resp = await SendAsync(HttpMethod.Get, $"/api/executions/{executionId}/logs");
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<List<ExecutionLogResponse>>();
    }

    public async Task<(bool Ok, string? Error)> RetryFromModuleAsync(
        Guid executionId, Guid projectModuleId, string? comment)
    {
        var resp = await SendAsync(HttpMethod.Post,
            $"/api/executions/{executionId}/retry-from-module",
            new RetryFromModuleRequest(projectModuleId, comment));
        if (!resp.IsSuccessStatusCode && (int)resp.StatusCode != 202)
        {
            return (false, await ReadErrorAsync(resp));
        }
        return (true, null);
    }

    public async Task<(bool Ok, string? Error)> OrchestratorReviewAsync(
        Guid executionId, bool approved, string? comment)
    {
        var resp = await SendAsync(HttpMethod.Post,
            $"/api/executions/{executionId}/orchestrator-review",
            new OrchestratorReviewRequest(approved, comment));
        if (!resp.IsSuccessStatusCode && (int)resp.StatusCode != 202)
        {
            return (false, await ReadErrorAsync(resp));
        }
        return (true, null);
    }

    public async Task<(bool Ok, string? Error)> CheckpointReviewAsync(
        Guid executionId, bool approved)
    {
        var resp = await SendAsync(HttpMethod.Post,
            $"/api/executions/{executionId}/checkpoint-review",
            new CheckpointReviewRequest(approved));
        if (!resp.IsSuccessStatusCode && (int)resp.StatusCode != 202)
        {
            return (false, await ReadErrorAsync(resp));
        }
        return (true, null);
    }

    // ── OrchestratorOutput CRUD ──

    public async Task<List<OrchestratorOutputResponse>> GetOrchestratorOutputsAsync(Guid projectId, Guid moduleId)
    {
        var resp = await SendAsync(HttpMethod.Get, $"/api/projects/{projectId}/modules/{moduleId}/orchestrator-outputs");
        if (!resp.IsSuccessStatusCode) return new();
        return await resp.Content.ReadFromJsonAsync<List<OrchestratorOutputResponse>>() ?? new();
    }

    public async Task<(OrchestratorOutputResponse? Output, string? Error)> CreateOrchestratorOutputAsync(
        Guid projectId, Guid moduleId, OrchestratorOutputRequest req)
    {
        var resp = await SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/modules/{moduleId}/orchestrator-outputs", req);
        if (!resp.IsSuccessStatusCode) return (null, await ReadErrorAsync(resp));
        return (await resp.Content.ReadFromJsonAsync<OrchestratorOutputResponse>(), null);
    }

    public async Task<(OrchestratorOutputResponse? Output, string? Error)> UpdateOrchestratorOutputAsync(
        Guid projectId, Guid moduleId, Guid outputId, OrchestratorOutputRequest req)
    {
        var resp = await SendAsync(HttpMethod.Put, $"/api/projects/{projectId}/modules/{moduleId}/orchestrator-outputs/{outputId}", req);
        if (!resp.IsSuccessStatusCode) return (null, await ReadErrorAsync(resp));
        return (await resp.Content.ReadFromJsonAsync<OrchestratorOutputResponse>(), null);
    }

    public async Task<(bool Ok, string? Error)> DeleteOrchestratorOutputAsync(
        Guid projectId, Guid moduleId, Guid outputId)
    {
        var resp = await SendAsync(HttpMethod.Delete, $"/api/projects/{projectId}/modules/{moduleId}/orchestrator-outputs/{outputId}");
        if (!resp.IsSuccessStatusCode) return (false, await ReadErrorAsync(resp));
        return (true, null);
    }

    public async Task<bool> CancelExecutionAsync(Guid projectId)
    {
        var resp = await SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/cancel");
        return resp.IsSuccessStatusCode;
    }

    // ── Build Info ──

    public async Task<BuildInfoResponse?> GetBuildInfoAsync()
    {
        var resp = await SendAsync(HttpMethod.Get, "/api/build-info");
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<BuildInfoResponse>();
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
                var response = await _http.SendAsync(request);
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized &&
                    !url.StartsWith("/api/auth/"))
                {
                    _auth.RequireLogin();
                }
                return response;
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

    // ── Buffer Channels ──

    public async Task<(List<BufferChannelDto>? Channels, string? Error)> GetBufferChannelsAsync(string apiKey)
    {
        var encoded = Uri.EscapeDataString(apiKey);
        var resp = await SendAsync(HttpMethod.Get, $"/api/buffer/channels?apiKey={encoded}");
        if (resp.IsSuccessStatusCode)
        {
            var list = await resp.Content.ReadFromJsonAsync<List<BufferChannelDto>>();
            return (list, null);
        }
        return (null, await ReadErrorAsync(resp));
    }

    // ── Social Connections (Redes sociales) ──

    public async Task<List<SocialConnectionResponse>> GetSocialConnectionsAsync()
    {
        var resp = await SendAsync(HttpMethod.Get, "/api/social-connections");
        if (!resp.IsSuccessStatusCode) return [];
        return await resp.Content.ReadFromJsonAsync<List<SocialConnectionResponse>>() ?? [];
    }

    public async Task<(bool Ok, string? Error)> CreateSocialConnectionAsync(CreateSocialConnectionRequest req)
    {
        var resp = await SendAsync(HttpMethod.Post, "/api/social-connections", req);
        if (resp.IsSuccessStatusCode) return (true, null);
        return (false, await ReadErrorAsync(resp));
    }

    public async Task<(bool Ok, string? Error)> UpdateSocialConnectionAsync(Guid id, UpdateSocialConnectionRequest req)
    {
        var resp = await SendAsync(HttpMethod.Put, $"/api/social-connections/{id}", req);
        if (resp.IsSuccessStatusCode) return (true, null);
        return (false, await ReadErrorAsync(resp));
    }

    public async Task DeleteSocialConnectionAsync(Guid id)
    {
        await SendAsync(HttpMethod.Delete, $"/api/social-connections/{id}");
    }

    // ── Messaging Connections (Mensajeria) ──

    public async Task<List<MessagingConnectionResponse>> GetMessagingConnectionsAsync()
    {
        var resp = await SendAsync(HttpMethod.Get, "/api/messaging-connections");
        if (!resp.IsSuccessStatusCode) return [];
        return await resp.Content.ReadFromJsonAsync<List<MessagingConnectionResponse>>() ?? [];
    }

    public async Task<(bool Ok, string? Error)> CreateMessagingConnectionAsync(CreateMessagingConnectionRequest req)
    {
        var resp = await SendAsync(HttpMethod.Post, "/api/messaging-connections", req);
        if (resp.IsSuccessStatusCode) return (true, null);
        return (false, await ReadErrorAsync(resp));
    }

    public async Task<(bool Ok, string? Error)> UpdateMessagingConnectionAsync(Guid id, UpdateMessagingConnectionRequest req)
    {
        var resp = await SendAsync(HttpMethod.Put, $"/api/messaging-connections/{id}", req);
        if (resp.IsSuccessStatusCode) return (true, null);
        return (false, await ReadErrorAsync(resp));
    }

    public async Task DeleteMessagingConnectionAsync(Guid id)
    {
        await SendAsync(HttpMethod.Delete, $"/api/messaging-connections/{id}");
    }

    // ── Project ↔ Connections assignment ──

    public async Task<ProjectConnectionsDto?> GetProjectConnectionsAsync(Guid projectId)
    {
        var resp = await SendAsync(HttpMethod.Get, $"/api/projects/{projectId}/connections");
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<ProjectConnectionsDto>();
    }

    public async Task<(bool Ok, string? Error)> SaveProjectConnectionsAsync(Guid projectId, ProjectConnectionsDto dto)
    {
        var resp = await SendAsync(HttpMethod.Put, $"/api/projects/{projectId}/connections", dto);
        if (resp.IsSuccessStatusCode) return (true, null);
        return (false, await ReadErrorAsync(resp));
    }

    // ── Shopify Connections ──

    public async Task<List<ShopifyConnectionResponse>> GetShopifyConnectionsAsync()
    {
        var resp = await SendAsync(HttpMethod.Get, "/api/shopify-connections");
        if (!resp.IsSuccessStatusCode) return [];
        return await resp.Content.ReadFromJsonAsync<List<ShopifyConnectionResponse>>() ?? [];
    }

    public async Task<(bool Ok, string? Error)> CreateShopifyConnectionAsync(CreateShopifyConnectionRequest req)
    {
        var resp = await SendAsync(HttpMethod.Post, "/api/shopify-connections", req);
        if (resp.IsSuccessStatusCode) return (true, null);
        return (false, await ReadErrorAsync(resp));
    }

    public async Task<(bool Ok, string? Error)> UpdateShopifyConnectionAsync(Guid id, UpdateShopifyConnectionRequest req)
    {
        var resp = await SendAsync(HttpMethod.Put, $"/api/shopify-connections/{id}", req);
        if (resp.IsSuccessStatusCode) return (true, null);
        return (false, await ReadErrorAsync(resp));
    }

    public async Task DeleteShopifyConnectionAsync(Guid id)
    {
        await SendAsync(HttpMethod.Delete, $"/api/shopify-connections/{id}");
    }

    public async Task<(List<ShopifyBlogDto>? Blogs, string? Error)> GetProjectShopifyBlogsAsync(Guid projectId)
    {
        var resp = await SendAsync(HttpMethod.Get, $"/api/projects/{projectId}/shopify/blogs");
        if (resp.IsSuccessStatusCode)
            return (await resp.Content.ReadFromJsonAsync<List<ShopifyBlogDto>>(), null);
        return (null, await ReadErrorAsync(resp));
    }

    // ── Schedule ──

    public async Task<ScheduleResponse?> GetScheduleAsync(Guid projectId)
    {
        var resp = await SendAsync(HttpMethod.Get, $"/api/projects/{projectId}/schedule");
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<ScheduleResponse>();
    }

    public async Task<(bool Ok, ScheduleResponse? Result, string? Error)> CreateScheduleAsync(Guid projectId, CreateScheduleRequest req)
    {
        var resp = await SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/schedule", req);
        if (!resp.IsSuccessStatusCode)
            return (false, null, await ReadErrorAsync(resp));
        var result = await resp.Content.ReadFromJsonAsync<ScheduleResponse>();
        return (true, result, null);
    }

    public async Task<(bool Ok, ScheduleResponse? Result, string? Error)> UpdateScheduleAsync(Guid projectId, UpdateScheduleRequest req)
    {
        var resp = await SendAsync(HttpMethod.Put, $"/api/projects/{projectId}/schedule", req);
        if (!resp.IsSuccessStatusCode)
            return (false, null, await ReadErrorAsync(resp));
        var result = await resp.Content.ReadFromJsonAsync<ScheduleResponse>();
        return (true, result, null);
    }

    public async Task<(bool Ok, string? Error)> DeleteScheduleAsync(Guid projectId)
    {
        var resp = await SendAsync(HttpMethod.Delete, $"/api/projects/{projectId}/schedule");
        if (resp.IsSuccessStatusCode) return (true, null);
        return (false, await ReadErrorAsync(resp));
    }

    // ── Prompt Builder ──

    public async Task<List<PromptBuilderModelOption>> GetPromptBuilderModelsAsync()
    {
        var resp = await SendAsync(HttpMethod.Get, "/api/prompt-builder/models");
        if (!resp.IsSuccessStatusCode) return new();
        return await resp.Content.ReadFromJsonAsync<List<PromptBuilderModelOption>>() ?? new();
    }

    public async Task<(bool Ok, List<string>? Questions, string? Error)> GeneratePromptBuilderQuestionsAsync(
        PromptBuilderQuestionsRequest req)
    {
        var resp = await SendAsync(HttpMethod.Post, "/api/prompt-builder/questions", req);
        if (!resp.IsSuccessStatusCode)
            return (false, null, await ReadErrorAsync(resp));
        var result = await resp.Content.ReadFromJsonAsync<PromptBuilderQuestionsResponse>();
        return (true, result?.Questions ?? new(), null);
    }

    public async Task<(bool Ok, string? Prompt, string? Error)> ComposePromptBuilderAsync(
        PromptBuilderComposeRequest req)
    {
        var resp = await SendAsync(HttpMethod.Post, "/api/prompt-builder/compose", req);
        if (!resp.IsSuccessStatusCode)
            return (false, null, await ReadErrorAsync(resp));
        var result = await resp.Content.ReadFromJsonAsync<PromptBuilderComposeResponse>();
        return (true, result?.Prompt, null);
    }

    public async Task<(bool Ok, string? Prompt, string? Error)> AddPromptBuilderAsync(
        PromptBuilderAddRequest req)
    {
        var resp = await SendAsync(HttpMethod.Post, "/api/prompt-builder/add", req);
        if (!resp.IsSuccessStatusCode)
            return (false, null, await ReadErrorAsync(resp));
        var result = await resp.Content.ReadFromJsonAsync<PromptBuilderComposeResponse>();
        return (true, result?.Prompt, null);
    }

    // ── Planned Prompts ──

    public async Task<List<PlannerModelOption>> GetPlannerModelsAsync()
    {
        var resp = await SendAsync(HttpMethod.Get, "/api/planner/models");
        if (!resp.IsSuccessStatusCode) return new();
        return await resp.Content.ReadFromJsonAsync<List<PlannerModelOption>>() ?? new();
    }

    public async Task<List<PlannedPromptResponse>> GetPlannedPromptsAsync(Guid projectId)
    {
        var resp = await SendAsync(HttpMethod.Get, $"/api/projects/{projectId}/planned-prompts");
        if (!resp.IsSuccessStatusCode) return new();
        return await resp.Content.ReadFromJsonAsync<List<PlannedPromptResponse>>() ?? new();
    }

    public async Task<(bool Ok, List<PlannedPromptResponse>? Result, string? Error)> GeneratePlannedPromptsAsync(
        Guid projectId, GeneratePlannedPromptsRequest req)
    {
        var resp = await SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/planned-prompts/generate", req);
        if (!resp.IsSuccessStatusCode)
            return (false, null, await ReadErrorAsync(resp));
        var result = await resp.Content.ReadFromJsonAsync<List<PlannedPromptResponse>>();
        return (true, result, null);
    }

    public async Task<(bool Ok, PlannedPromptResponse? Result, string? Error)> CreatePlannedPromptAsync(
        Guid projectId, string content)
    {
        var resp = await SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/planned-prompts",
            new CreatePlannedPromptRequest(content));
        if (!resp.IsSuccessStatusCode)
            return (false, null, await ReadErrorAsync(resp));
        var result = await resp.Content.ReadFromJsonAsync<PlannedPromptResponse>();
        return (true, result, null);
    }

    public async Task<(bool Ok, PlannedPromptResponse? Result, string? Error)> UpdatePlannedPromptAsync(
        Guid projectId, Guid promptId, string content)
    {
        var resp = await SendAsync(HttpMethod.Put, $"/api/projects/{projectId}/planned-prompts/{promptId}",
            new UpdatePlannedPromptRequest(content));
        if (!resp.IsSuccessStatusCode)
            return (false, null, await ReadErrorAsync(resp));
        var result = await resp.Content.ReadFromJsonAsync<PlannedPromptResponse>();
        return (true, result, null);
    }

    public async Task<(bool Ok, string? Error)> DeletePlannedPromptAsync(Guid projectId, Guid promptId)
    {
        var resp = await SendAsync(HttpMethod.Delete, $"/api/projects/{projectId}/planned-prompts/{promptId}");
        if (resp.IsSuccessStatusCode) return (true, null);
        return (false, await ReadErrorAsync(resp));
    }

    public async Task<(bool Ok, List<PlannedPromptResponse>? Result, string? Error)> ReorderPlannedPromptsAsync(
        Guid projectId, List<Guid> orderedIds)
    {
        var resp = await SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/planned-prompts/reorder",
            new ReorderPlannedPromptsRequest(orderedIds));
        if (!resp.IsSuccessStatusCode)
            return (false, null, await ReadErrorAsync(resp));
        var result = await resp.Content.ReadFromJsonAsync<List<PlannedPromptResponse>>();
        return (true, result, null);
    }

    public async Task<(bool Ok, PlannedPromptResponse? Result, string? Error)> ExecutePlannedPromptAsync(
        Guid projectId, Guid promptId)
    {
        var resp = await SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/planned-prompts/{promptId}/execute");
        if (!resp.IsSuccessStatusCode)
            return (false, null, await ReadErrorAsync(resp));
        var result = await resp.Content.ReadFromJsonAsync<PlannedPromptResponse>();
        return (true, result, null);
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
