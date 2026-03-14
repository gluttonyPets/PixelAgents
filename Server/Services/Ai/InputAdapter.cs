using System.Text.RegularExpressions;

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
    }
}
