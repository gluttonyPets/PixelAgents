using Microsoft.AspNetCore.Identity;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;

namespace Server.Models
{
    public class ApplicationUser : IdentityUser
    {
        public Guid AccountId { get; set; }
        public Account Account { get; set; } = null!;
    }
}
