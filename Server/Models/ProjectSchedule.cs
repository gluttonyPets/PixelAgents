namespace Server.Models
{
    public class ProjectSchedule
    {
        public Guid Id { get; set; }
        public Guid ProjectId { get; set; }
        public bool IsEnabled { get; set; } = true;

        /// <summary>Cron expression (e.g. "0 9 * * *" = every day at 09:00 UTC)</summary>
        public string CronExpression { get; set; } = default!;

        /// <summary>IANA timezone (e.g. "Europe/Madrid")</summary>
        public string TimeZone { get; set; } = "UTC";

        /// <summary>Optional input text sent to the pipeline on each scheduled run</summary>
        public string? UserInput { get; set; }

        public DateTime? LastRunAt { get; set; }
        public DateTime? NextRunAt { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public Project Project { get; set; } = null!;
    }
}
