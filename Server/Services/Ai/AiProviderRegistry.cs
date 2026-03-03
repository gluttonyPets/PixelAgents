namespace Server.Services.Ai
{
    public class AiProviderRegistry : IAiProviderRegistry
    {
        private readonly Dictionary<string, IAiProvider> _providers;

        public AiProviderRegistry(IEnumerable<IAiProvider> providers)
        {
            _providers = providers.ToDictionary(
                p => p.ProviderType,
                p => p,
                StringComparer.OrdinalIgnoreCase);
        }

        public IAiProvider? GetProvider(string providerType)
        {
            _providers.TryGetValue(providerType, out var provider);
            return provider;
        }

        public IEnumerable<string> AvailableProviders => _providers.Keys;
    }
}
