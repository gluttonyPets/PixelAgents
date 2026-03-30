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
//