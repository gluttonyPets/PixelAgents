namespace Server.Services.Ai
{
    public interface IAiProviderRegistry
    {
        IAiProvider? GetProvider(string providerType);
        IEnumerable<string> AvailableProviders { get; }
    }
}
