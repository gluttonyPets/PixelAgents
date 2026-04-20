namespace Server.Models
{
    public class AiExecutionContext
    {
        public string ModuleType { get; set; } = default!;
        public string ModelName { get; set; } = default!;
        public string ApiKey { get; set; } = default!;
        public string Input { get; set; } = default!;
        public string? ProjectContext { get; set; }
        /// <summary>Summaries from previous executions to avoid repeating content.</summary>
        public string? PreviousExecutionsSummary { get; set; }
        /// <summary>Active tenant-level rules joined into a single block. Injected
        /// into the system prompt of every AI call so the model always honours
        /// the global rules configured in /rules.</summary>
        public string? MandatoryRules { get; set; }
        public Dictionary<string, object> Configuration { get; set; } = new();
        public List<byte[]>? InputFiles { get; set; }
        /// <summary>When true, providers skip the default OutputSchema JSON instructions.
        /// Used by the orchestrator which provides its own JSON response format.</summary>
        public bool SkipOutputSchema { get; set; }
    }

    public class AiResult
    {
        public bool Success { get; set; }
        public string? TextOutput { get; set; }
        public byte[]? FileOutput { get; set; }
        public string? ContentType { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
        public string? Error { get; set; }
        /// <summary>Estimated cost in USD for this execution.</summary>
        public decimal EstimatedCost { get; set; }
        /// <summary>Extra files beyond the primary FileOutput (e.g. images 2..N when n>1).</summary>
        public List<byte[]>? AdditionalFiles { get; set; }

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

        public static AiResult OkFiles(IReadOnlyList<byte[]> files, string contentType, Dictionary<string, object>? metadata = null)
        {
            if (files.Count == 0)
                return Fail("Provider devolvio una lista vacia de archivos");
            return new AiResult
            {
                Success = true,
                FileOutput = files[0],
                AdditionalFiles = files.Count > 1 ? files.Skip(1).ToList() : null,
                ContentType = contentType,
                Metadata = metadata ?? new()
            };
        }

        public static AiResult Fail(string error) => new()
        {
            Success = false,
            Error = error
        };
    }
}
