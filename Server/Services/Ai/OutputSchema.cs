using System.Text.Json.Serialization;

namespace Server.Services.Ai
{
    public class StepOutput
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = default!;

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("summary")]
        public string? Summary { get; set; }

        [JsonPropertyName("items")]
        public List<OutputItem> Items { get; set; } = [];

        [JsonPropertyName("files")]
        public List<OutputFile> Files { get; set; } = [];

        [JsonPropertyName("metadata")]
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    public class OutputItem
    {
        [JsonPropertyName("content")]
        public string Content { get; set; } = default!;

        [JsonPropertyName("label")]
        public string? Label { get; set; }
    }

    public class OutputFile
    {
        [JsonPropertyName("fileId")]
        public Guid FileId { get; set; }

        [JsonPropertyName("fileName")]
        public string FileName { get; set; } = default!;

        [JsonPropertyName("contentType")]
        public string ContentType { get; set; } = default!;

        [JsonPropertyName("fileSize")]
        public long FileSize { get; set; }

        [JsonPropertyName("revisedPrompt")]
        public string? RevisedPrompt { get; set; }
    }

    public static class OutputSchemaHelper
    {
        /// <summary>
        /// Construye un StepOutput para un módulo de imagen con múltiples archivos.
        /// </summary>
        public static StepOutput BuildImageOutput(List<OutputFile> files, string modelName)
        {
            return new StepOutput
            {
                Type = "image",
                Files = files,
                Metadata = new Dictionary<string, object>
                {
                    ["model"] = modelName,
                    ["count"] = files.Count
                }
            };
        }

        /// <summary>
        /// Construye un StepOutput para un módulo de video.
        /// </summary>
        public static StepOutput BuildVideoOutput(List<OutputFile> files, string modelName, Dictionary<string, object>? extraMeta = null)
        {
            var meta = new Dictionary<string, object>
            {
                ["model"] = modelName,
                ["count"] = files.Count
            };
            if (extraMeta is not null)
                foreach (var kv in extraMeta) meta[kv.Key] = kv.Value;

            return new StepOutput
            {
                Type = "video",
                Files = files,
                Metadata = meta
            };
        }

        /// <summary>
        /// Construye un StepOutput para un módulo de audio.
        /// </summary>
        public static StepOutput BuildAudioOutput(List<OutputFile> files, string modelName, string? voice = null)
        {
            var meta = new Dictionary<string, object> { ["model"] = modelName };
            if (voice is not null) meta["voice"] = voice;
            return new StepOutput
            {
                Type = "audio",
                Files = files,
                Metadata = meta
            };
        }

        public static string ExtractJson(string text)
        {
            var trimmed = text.Trim();

            // Quitar ```json ... ``` o ``` ... ```
            if (trimmed.StartsWith("```"))
            {
                var firstNewLine = trimmed.IndexOf('\n');
                if (firstNewLine > 0)
                    trimmed = trimmed[(firstNewLine + 1)..];
                if (trimmed.EndsWith("```"))
                    trimmed = trimmed[..^3];
                return trimmed.Trim();
            }

            return trimmed;
        }
    }
}
