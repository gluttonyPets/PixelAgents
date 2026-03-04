using System.Text;
using System.Text.RegularExpressions;

namespace Server.Services.Ai
{
    public static class InputAdapter
    {
        public static string Adapt(string input, string? sourceModuleType, string targetModuleType, string targetModelName)
        {
            if (string.IsNullOrWhiteSpace(input))
                return input;

            // Solo adaptar cuando cruzamos de Text → Image
            if (sourceModuleType == "Text" && targetModuleType == "Image")
                return ExtractImagePrompt(input, GetMaxPromptLength(targetModelName));

            return input;
        }

        private static string ExtractImagePrompt(string text, int softLimit)
        {
            if (text.Length <= softLimit)
                return text;

            // Limpiar markdown
            var clean = StripMarkdown(text);

            // Estrategia 1: buscar un bloque de prompt de imagen explícito
            var markerMatch = Regex.Match(clean,
                @"(?i)(?:image\s*prompt|imagen?\s*prompt|visual\s*description|prompt\s*para\s*(?:la\s*)?imagen)\s*[:]\s*(.+)",
                RegexOptions.Singleline);

            if (markerMatch.Success)
            {
                var extracted = markerMatch.Groups[1].Value.Trim();
                var endIdx = extracted.IndexOf("\n\n");
                if (endIdx > 0)
                    extracted = extracted[..endIdx].Trim();
                if (extracted.Length > 0)
                    return TruncateAtWord(extracted, softLimit);
            }

            // Estrategia 2: primer párrafo
            var firstPara = clean.IndexOf("\n\n");
            if (firstPara > 0 && firstPara <= softLimit)
                return clean[..firstPara].Trim();

            // Estrategia 3: primeras frases hasta el límite
            var sentences = clean.Split([". ", ".\n"], StringSplitOptions.None);
            var sb = new StringBuilder();
            foreach (var sentence in sentences)
            {
                var s = sentence.Trim();
                if (s.Length == 0) continue;
                if (sb.Length + s.Length + 2 > softLimit) break;
                if (sb.Length > 0) sb.Append(". ");
                sb.Append(s);
            }
            if (sb.Length > 0)
                return sb.ToString();

            // Estrategia 4: cortar en límite de palabra
            return TruncateAtWord(clean, softLimit);
        }

        private static string StripMarkdown(string text)
        {
            // Quitar headers ##
            var result = Regex.Replace(text, @"^#{1,6}\s+", "", RegexOptions.Multiline);
            // Quitar bold/italic
            result = Regex.Replace(result, @"\*{1,3}([^*]+)\*{1,3}", "$1");
            // Quitar listas con - o *
            result = Regex.Replace(result, @"^\s*[-*]\s+", "", RegexOptions.Multiline);
            // Quitar links [text](url)
            result = Regex.Replace(result, @"\[([^\]]+)\]\([^)]+\)", "$1");
            // Colapsar saltos de linea excesivos
            result = Regex.Replace(result, @"\n{3,}", "\n\n");
            return result.Trim();
        }

        public static int GetMaxPromptLength(string modelName)
        {
            return modelName.ToLowerInvariant() switch
            {
                "dall-e-2" => 900,
                "dall-e-3" => 3900,
                var m when m.StartsWith("gpt-image") => 3900,
                _ => 3900
            };
        }

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
