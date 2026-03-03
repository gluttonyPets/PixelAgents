using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Server.Models
{
    public class Account
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = default!;
        public string DbName { get; set; } = default!;
        public string Plan { get; set; } = "Basic";
    }
}
