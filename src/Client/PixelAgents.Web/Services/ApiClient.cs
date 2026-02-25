using System.Net.Http.Json;
using PixelAgents.Shared.DTOs;

namespace PixelAgents.Web.Services;

public class ApiClient
{
    private readonly HttpClient _http;

    public ApiClient(HttpClient http)
    {
        _http = http;
    }

    // Workspaces
    public async Task<WorkspaceDto?> GetWorkspaceAsync(Guid id)
        => await _http.GetFromJsonAsync<WorkspaceDto>($"api/workspaces/{id}");

    public async Task<WorkspaceDto?> CreateWorkspaceAsync(CreateWorkspaceRequest request)
    {
        var response = await _http.PostAsJsonAsync("api/workspaces", request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<WorkspaceDto>();
    }

    public async Task<List<AgentDto>> GetAgentsAsync(Guid workspaceId)
        => await _http.GetFromJsonAsync<List<AgentDto>>($"api/workspaces/{workspaceId}/agents") ?? [];

    // Pipelines
    public async Task<PipelineDto?> GetPipelineAsync(Guid id)
        => await _http.GetFromJsonAsync<PipelineDto>($"api/pipelines/{id}");

    public async Task<PipelineDto?> ExecutePipelineAsync(ExecutePipelineRequest request)
    {
        var response = await _http.PostAsJsonAsync("api/pipelines/execute", request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PipelineDto>();
    }

    // Content
    public async Task<ContentProjectDto?> CreateContentProjectAsync(CreateContentProjectRequest request)
    {
        var response = await _http.PostAsJsonAsync("api/content/projects", request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ContentProjectDto>();
    }

    // Modules
    public async Task<List<ModuleInfoDto>> GetModulesAsync()
        => await _http.GetFromJsonAsync<List<ModuleInfoDto>>("api/modules") ?? [];
}

public record ModuleInfoDto(
    string ModuleKey,
    string DisplayName,
    string Description,
    string Role,
    string Personality
);
