using Client.Models;

namespace Client.Services;

public class AuthStateService
{
    private AuthResponse? _user;

    public AuthResponse? User => _user;
    public bool IsAuthenticated => _user is not null;

    public event Action? OnChange;

    public void SetUser(AuthResponse? user)
    {
        _user = user;
        OnChange?.Invoke();
    }
}
