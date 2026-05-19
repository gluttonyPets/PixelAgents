using System.Text.RegularExpressions;
using System.Collections.Generic;

namespace Server.Services.Ai
{
    /// <summary>
    /// Utilidades de seguridad para truncar inputs que excedan límites de modelo.
    /// La lógica de adaptación entre tipos ahora la maneja el output estructurado + multi-iteración.
    /// </summary>
    public static class InputAdapter
    {
        public static int GetMaxPromptLength(string modelName)
        {
            return modelName.ToLowerInvariant() switch
            {
                "dall-e-2" => 1000,
                "dall-e-3" => 4000,
                var m when m.StartsWith("gpt-image") => 4000,
                var m when m.StartsWith("leonardo-") => 1500,
                var m when m.StartsWith("imagen-") => 4000,
                _ => 4000
            };
        }

        /// <summary>
        /// Limpia texto de emojis, markdown, saltos de linea y caracteres decorativos.
        /// </summary>
        public static string SanitizePlainText(string text)
        {
            // Remove markdown formatting: **bold**, *italic*, # headings, ``` code blocks
            var s = Regex.Replace(text, @"\*{1,3}([^*]+)\*{1,3}", "$1"); // bold/italic
            s = Regex.Replace(s, @"^#{1,6}\s+", "", RegexOptions.Multiline); // headings
            s = Regex.Replace(s, @"```[\s\S]*?```", ""); // code blocks
            s = Regex.Replace(s, @"`([^`]+)`", "$1"); // inline code
            s = Regex.Replace(s, @"^>\s+", "", RegexOptions.Multiline); // blockquotes
            s = Regex.Replace(s, @"^\s*[-*+]\s+", "", RegexOptions.Multiline); // list bullets

            // Remove emojis (Unicode emoji ranges)
            s = Regex.Replace(s, @"[\u00A9\u00AE\u200D\u203C-\u3299\uFE0F]|[\uD83C-\uDBFF][\uDC00-\uDFFF]", "");
            // Extended emoji ranges: variation selectors, ZWJ, skin tones
            s = Regex.Replace(s, @"\p{So}|\p{Sk}", "");

            // Remove decorative unicode symbols (arrows, bullets, stars, geometric shapes etc)
            s = Regex.Replace(s, @"[\u2190-\u27BF\u2B00-\u2BFF\u25A0-\u25FF\u2600-\u26FF\u2700-\u27BF]", "");

            // Replace newlines/tabs with spaces, collapse multiple spaces
            s = Regex.Replace(s, @"[\r\n\t]+", " ");
            s = Regex.Replace(s, @"\s{2,}", " ");

            return s.Trim();
        }

        private const string VisualMediaRule =
            "IMPORTANT: Any text rendered in the image or video MUST be written in correct Spanish (Castilian). " +
            "Double-check every word for spelling, accents and grammar before rendering. " +
            "No spelling mistakes, no missing accents, no invented words. " +
            "If a word seems uncertain, use a simpler synonym that you are sure is correct.";

        /// <summary>
        /// Returns a spelling/language instruction to prepend to image and video prompts.
        /// </summary>
        public static string GetVisualMediaRule() => VisualMediaRule;

        public static string TruncateAtWord(string text, int maxLength)
        {
            if (text.Length <= maxLength)
                return text;

            var truncated = text[..maxLength];
            var lastSpace = truncated.LastIndexOf(' ');
            if (lastSpace > maxLength * 0.7)
                truncated = truncated[..lastSpace];

            return truncated.TrimEnd();
        }

        // Models with higher image prompt limits than the defaults above, ordered by capacity.
        // These are the models we suggest when a prompt is truncated.
        private static readonly (string ModelId, string DisplayName, int Limit)[] ImageModelsByCapacity =
        [
            ("dall-e-3",         "DALL-E 3 (OpenAI)",                         4_000),
            ("gpt-image-1-mini", "GPT Image 1 Mini (OpenAI)",                 4_000),
            ("gpt-image-1",      "GPT Image 1 (OpenAI)",                      4_000),
            ("gpt-image-1.5",    "GPT Image 1.5 (OpenAI)",                    4_000),
            ("leonardo-phoenix",       "Leonardo Phoenix (LeonardoAI)",       1_500),
            ("leonardo-phoenix-0.9",   "Leonardo Phoenix 0.9 (LeonardoAI)",   1_500),
            ("leonardo-flux-dev",      "Leonardo Flux Dev (LeonardoAI)",      1_500),
            ("leonardo-flux-schnell",  "Leonardo Flux Schnell (LeonardoAI)",  1_500),
            ("gemini-2.5-flash-image",         "Gemini 2.5 Flash Image (Google)", 4_000),
            ("gemini-3.1-flash-image-preview", "Gemini 3.1 Flash Image Preview (Google)", 4_000),
            ("gemini-3-pro-image-preview",     "Gemini 3 Pro Image Preview (Google)", 4_000),
            ("grok-imagine-image",     "Grok Imagine (xAI)",     4_000),
            ("grok-imagine-image-pro", "Grok Imagine Pro (xAI)", 4_000),
        ];

        /// <summary>
        /// Builds a human-readable warning for the execution log when a prompt was
        /// truncated, including the models with higher prompt limits.
        /// </summary>
        public static string BuildTruncationWarning(string modelName, int originalLength, int maxLength)
        {
            var currentLimit = GetMaxPromptLength(modelName);
            var suggestions = new List<string>();

            foreach (var (id, display, limit) in ImageModelsByCapacity)
            {
                if (limit > currentLimit
                    && !string.Equals(id, modelName, StringComparison.OrdinalIgnoreCase))
                {
                    suggestions.Add(display);
                }
            }

            var suggestionText = suggestions.Count > 0
                ? $" Considera cambiar a un modelo con mayor capacidad: {string.Join(", ", suggestions)}."
                : " No hay modelos de imagen con mayor límite de prompt disponibles en el catálogo.";

            return $"[AVISO] El prompt fue recortado de {originalLength:N0} a {maxLength:N0} caracteres "
                 + $"porque el modelo '{modelName}' tiene un límite de {currentLimit:N0} caracteres.{suggestionText}";
        }
    }
}
