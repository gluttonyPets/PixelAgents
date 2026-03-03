namespace Server.Services
{
    public interface IAccountService
    {
        Task RegisterAsync(string email, string password);
    }
}
