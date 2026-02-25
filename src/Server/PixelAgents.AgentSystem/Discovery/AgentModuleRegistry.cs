using PixelAgents.AgentSystem.Abstractions;

namespace PixelAgents.AgentSystem.Discovery;

public class AgentModuleRegistry
{
    private readonly Dictionary<string, IAgentModule> _modules = new(StringComparer.OrdinalIgnoreCase);

    public void Register(IAgentModule module)
    {
        _modules[module.ModuleKey] = module;
    }

    public IAgentModule? GetModule(string moduleKey)
    {
        _modules.TryGetValue(moduleKey, out var module);
        return module;
    }

    public IReadOnlyCollection<IAgentModule> GetAllModules() => _modules.Values.ToList().AsReadOnly();

    public bool HasModule(string moduleKey) => _modules.ContainsKey(moduleKey);

    public IAgentModule? FindModuleForTask(string taskType)
    {
        return _modules.Values.FirstOrDefault(m => m.CanHandle(taskType));
    }
}
