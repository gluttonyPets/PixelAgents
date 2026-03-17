using System.Text.Json;
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
        private const string TextOutputInstruction = @"IMPORTANTE: Debes responder SIEMPRE en JSON valido con esta estructura exacta (sin texto adicional fuera del JSON):
{
  ""title"": ""titulo corto y atractivo para publicacion"",
  ""content"": ""tu respuesta completa aqui"",
  ""summary"": ""resumen de 1-2 frases"",
  ""items"": [
    { ""content"": ""elemento individual para el siguiente paso"", ""label"": ""etiqueta descriptiva"" }
  ],
  ""metadata"": {}
}

Reglas:
- ""title"" es obligatorio: crea un titulo corto, atractivo y descriptivo (maximo 100 caracteres). Este titulo se usara como descripcion de la publicacion en redes sociales.
- ""content"" es obligatorio: pon aqui tu respuesta principal completa.
- ""summary"" es obligatorio: resumen breve del contenido.
- Si generas multiples elementos (slides, prompts de imagen, secciones, partes), pon cada uno como un objeto en ""items"" con su ""content"" y ""label"".
- Si solo generas un texto unico sin partes, deja ""items"" como array vacio [].
- ""metadata"" es para informacion extra relevante (caption, hashtags, tono, etc). Dejalo como {} si no aplica.
- NO incluyas texto fuera del JSON. Solo el JSON.
- NUNCA hagas preguntas al usuario. Si hay varias opciones posibles, elige la mejor opcion tu mismo y da directamente la respuesta final.
- Se concreto y directo. No pidas aclaraciones, no ofrezcas alternativas, no preguntes preferencias. Decide y responde.

Reglas de formato (OBLIGATORIAS para todos los valores de texto):
- NO uses emojis ni emoticonos de ningun tipo (ni unicode ni shortcodes).
- NO uses formato markdown: nada de **, *, #, ##, ```, >, -, ni listas con viñetas.
- NO uses saltos de linea (\n) dentro de los valores de texto. Escribe todo en una sola linea continua.
- NO uses caracteres especiales decorativos: flechas, bullets, guiones largos, comillas tipograficas, simbolos como estrella, circulo, rombo, triangulo, flecha, etc.
- Usa solo texto plano ASCII basico: letras, numeros, puntuacion normal (. , ; : ! ? ' "").
- Si necesitas separar ideas, usa comas o puntos, nunca saltos de linea ni listas.

Reglas de contenido (OBLIGATORIAS):
- NUNCA menciones marcas, empresas, productos, servicios o nombres comerciales de ningun tipo. Esto incluye marcas de tecnologia, redes sociales, ropa, alimentacion, automocion, software, hardware, o cualquier otro sector.
- Si necesitas referirte a un concepto asociado a una marca, usa una descripcion generica. Por ejemplo: en vez de ""Instagram"" di ""redes sociales"", en vez de ""iPhone"" di ""telefono movil"", en vez de ""Photoshop"" di ""editor de imagenes"".
- Esta regla aplica a todos los campos: title, content, summary, items y metadata.
- No uses nombres de marcas ni siquiera como referencia, comparacion, ejemplo o metafora.";

        public static string GetTextOutputInstruction() => TextOutputInstruction;

        /// <summary>
        /// Parsea la respuesta JSON de un módulo de texto a StepOutput.
        /// Si falla el parseo, crea un StepOutput con el texto raw como content (fallback).
        /// </summary>
        public static StepOutput ParseTextOutput(string rawText, Dictionary<string, object>? providerMetadata = null)
        {
            // Intentar extraer JSON si viene envuelto en markdown code blocks
            var jsonText = ExtractJson(rawText);

            try
            {
                var parsed = JsonSerializer.Deserialize<StepOutput>(jsonText, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (parsed is not null && !string.IsNullOrWhiteSpace(parsed.Content))
                {
                    parsed.Type = "text";
                    if (providerMetadata is not null)
                    {
                        foreach (var kv in providerMetadata)
                            parsed.Metadata[kv.Key] = kv.Value;
                    }
                    return parsed;
                }
            }
            catch { }

            // Fallback: texto no estructurado
            var fallback = new StepOutput
            {
                Type = "text",
                Content = rawText,
                Summary = rawText.Length > 100 ? rawText[..100] + "..." : rawText,
                Metadata = providerMetadata ?? new()
            };
            return fallback;
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
