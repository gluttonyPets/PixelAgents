namespace Server.Services.Ai;

/// <summary>
/// Thrown by an AI provider when the failure is transient (network timeout, service
/// temporarily unavailable) and the caller should retry later rather than give up.
/// </summary>
public class TransientProviderException : Exception
{
    public TransientProviderException(string message, Exception inner)
        : base(message, inner) { }
}
