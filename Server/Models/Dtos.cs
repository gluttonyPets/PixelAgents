namespace Server.Models
{
    public record RegisterRequest(string Email, string Password);
    public record LoginRequest(string Email, string Password, bool RememberMe = false);
    public record AuthResponse(string Email, Guid AccountId, string? DbName);
}
