using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Server.Services
{
    public interface IAccountService
    {
        Task RegisterAsync(string email, string password);
    }
}
