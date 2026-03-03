namespace Server.Models
{
    public class AiExecutionContext
    {
        public string ModuleType { get; set; } = default!;
        public string ModelName { get; set; } = default!;
        public string ApiKey { get; set; } = default!;
        public string Input { get; set; } = default!;
        public Dictionary<string, object> Configuration { get; set; } = new();
        public List<byte[]>? InputFiles { get; set; }
    }

    public class AiResult
    {
        public bool Success { get; set; }
        public string? TextOutput { get; set; }
        public byte[]? FileOutput { get; set; }
        public string? ContentType { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
        public string? Error { get; set; }

        public static AiResult Ok(string text, Dictionary<string, object>? metadata = null) => new()
        {
            Success = true,
            TextOutput = text,
            Metadata = metadata ?? new()
        };

        public static AiResult OkFile(byte[] file, string contentType, Dictionary<string, object>? metadata = null) => new()
        {
            Success = true,
            FileOutput = file,
            ContentType = contentType,
            Metadata = metadata ?? new()
        };

        public static AiResult Fail(string error) => new()
        {
            Success = false,
            Error = error
        };
    }
}
