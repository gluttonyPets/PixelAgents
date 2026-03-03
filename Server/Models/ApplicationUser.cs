using Microsoft.AspNetCore.Identity;

namespace Server.Models
{
    public class ApplicationUser : IdentityUser
    {
        public Guid AccountId { get; set; }
        public Account Account { get; set; } = null!;
    }
}
