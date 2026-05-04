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
        private const string TextContentRules = @"Reglas generales:
- NUNCA hagas preguntas al usuario. Si hay varias opciones posibles, elige la mejor opcion tu mismo y da directamente la respuesta final.
- Se concreto y directo. No pidas aclaraciones, no ofrezcas alternativas, no preguntes preferencias. Decide y responde.

Reglas de formato (OBLIGATORIAS):
- NO uses emojis ni emoticonos de ningun tipo (ni unicode ni shortcodes).
- NO uses formato markdown: nada de **, *, #, ##, ```, >, -, ni listas con vinetas.
- NO uses caracteres especiales decorativos: flechas, bullets, guiones largos, comillas tipograficas, simbolos como estrella, circulo, rombo, triangulo, flecha, etc.
- Usa solo texto plano ASCII basico: letras, numeros, puntuacion normal (. , ; : ! ? ' "").

Reglas de contenido (OBLIGATORIAS):
- NUNCA menciones marcas, empresas, productos, servicios o nombres comerciales de ningun tipo. Esto incluye marcas de tecnologia, redes sociales, ropa, alimentacion, automocion, software, hardware, o cualquier otro sector.
- Si necesitas referirte a un concepto asociado a una marca, usa una descripcion generica. Por ejemplo: en vez de ""Instagram"" di ""redes sociales"", en vez de ""iPhone"" di ""telefono movil"", en vez de ""Photoshop"" di ""editor de imagenes"".
- No uses nombres de marcas ni siquiera como referencia, comparacion, ejemplo o metafora.";

        /// <summary>
        /// Normas de comportamiento, formato ASCII y veto de marcas que se
        /// inyectan en el system prompt de todo modulo de texto. Antes vivian
        /// dentro del esquema JSON obligatorio; se mantienen ahora como reglas
        /// independientes al usar texto plano.
        /// </summary>
        public static string GetTextContentRules() => TextContentRules;

        /// <summary>
        /// Instruccion que obliga al modelo de texto a producir su salida como
        /// un unico objeto JSON que cumpla los contratos declarados en las
        /// aristas salientes del grafo. Cuando hay varios contratos distintos
        /// (varias conexiones de salida con diferentes formatos) se listan para
        /// que el modelo sintetice uno compatible con todos.
        /// </summary>
        public static string GetOutputFormatInstruction(IReadOnlyList<string> formats)
        {
            if (formats.Count == 0) return "";
            var body = formats.Count == 1
                ? formats[0]
                : string.Join("\n\n---\n\n", formats.Select((f, i) => $"Contrato {i + 1}:\n{f}"));
            return "IMPORTANTE: Tu respuesta debe ser EXCLUSIVAMENTE un JSON valido (sin texto fuera del JSON, sin markdown) que cumpla el siguiente contrato acordado con el modulo siguiente del pipeline. Respeta los nombres de las claves, los tipos y la forma del esquema; rellena con contenido concreto los campos descritos.\n\n" + body;
        }

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
