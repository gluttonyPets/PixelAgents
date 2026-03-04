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

            // Estrategia 1: buscar TODOS los prompts de imagen explícitos y combinarlos
            var promptMatches = Regex.Matches(clean,
                "(?i)(?:image\\s*prompt|imagen?\\s*prompt|visual\\s*description|prompt\\s*(?:de\\s*)?(?:ia|ai)(?:\\s*para\\s*imagen)?|prompt\\s*para\\s*(?:la\\s*)?imagen)\\s*[:]\\s*[\"\\u201C]?(.+?)(?:[\"\\u201D]?\\s*$|\\n)",
                RegexOptions.Multiline);

            if (promptMatches.Count > 0)
            {
                var prompts = new List<string>();
                foreach (Match m in promptMatches)
                {
                    var p = m.Groups[1].Value.Trim().Trim('"', '\u0022', '\u201C', '\u201D');
                    if (p.Length > 10) // descartar fragmentos demasiado cortos
                        prompts.Add(p);
                }

                if (prompts.Count > 0)
                {
                    // Si hay múltiples prompts, combinarlos como escena compuesta
                    if (prompts.Count == 1)
                        return TruncateAtWord(prompts[0], softLimit);

                    var combined = $"A series of {prompts.Count} illustrations: " +
                        string.Join("; ", prompts.Select((p, i) => $"{i + 1}) {p}"));
                    return TruncateAtWord(combined, softLimit);
                }
            }

            // Estrategia 2: buscar líneas que parezcan descripciones visuales
            var visualLines = clean.Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 20 &&
                    Regex.IsMatch(l, @"(?i)(ilustraci[oó]n|imagen|visual|escena|dise[nñ]o|fondo|estilo|colores|icono)", RegexOptions.None))
                .ToList();

            if (visualLines.Count > 0)
            {
                var combined = string.Join(". ", visualLines);
                if (combined.Length > 0)
                    return TruncateAtWord(combined, softLimit);
            }

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
