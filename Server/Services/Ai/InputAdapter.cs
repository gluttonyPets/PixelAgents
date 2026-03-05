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
                _ => 4000
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
