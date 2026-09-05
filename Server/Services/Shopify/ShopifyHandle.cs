using System.Globalization;
using System.Text;

namespace Server.Services.Shopify
{
    /// <summary>
    /// Utilidades del "handle" de Shopify (el identificador que forma la URL del
    /// articulo). Shopify obliga a que sea unico: si se repite responde
    /// "Handle has already been taken" y tumba la publicacion, asi que aqui vive la
    /// normalizacion del slug, la generacion de alternativas y la deteccion de ese error.
    /// </summary>
    public static class ShopifyHandle
    {
        /// <summary>Longitud maxima del handle base (Shopify admite mas, pero URLs kilometricas no aportan).</summary>
        public const int MaxLength = 120;

        /// <summary>A partir de este intento se deja el sufijo numerico y se usa la fecha.</summary>
        public const int MaxNumericAttempts = 4;

        /// <summary>Convierte un texto en un slug valido para la URL (handle de Shopify).</summary>
        public static string Slugify(string? text)
        {
            var normalized = (text ?? "").Trim().ToLowerInvariant();
            // Descomponer acentos (á -> a) y descartar los diacriticos.
            normalized = normalized.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (var c in normalized)
            {
                var cat = CharUnicodeInfo.GetUnicodeCategory(c);
                if (cat == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(c)) sb.Append(c);
                else if (c is ' ' or '-' or '_' or '.') sb.Append('-');
            }
            var slug = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "-+", "-").Trim('-');
            if (slug.Length > MaxLength) slug = slug[..MaxLength].TrimEnd('-');
            return slug.Length > 0 ? slug : "articulo";
        }

        /// <summary>
        /// Handle alternativo para el reintento numero <paramref name="attempt"/>
        /// (2 = primer reintento). Los primeros llevan sufijo numerico ("-2", "-3");
        /// pasado <see cref="MaxNumericAttempts"/> se usa la fecha, que corta de raiz
        /// las cadenas largas de colisiones.
        /// </summary>
        public static string Candidate(string? baseHandle, int attempt, DateTime utcNow) =>
            WithSuffix(baseHandle, attempt <= MaxNumericAttempts
                ? attempt.ToString(CultureInfo.InvariantCulture)
                : utcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));

        /// <summary>Anade un sufijo al handle recortando la base para no pasarse de <see cref="MaxLength"/>.</summary>
        public static string WithSuffix(string? baseHandle, string suffix)
        {
            var clean = Slugify(suffix);
            var room = MaxLength - clean.Length - 1;
            var slug = Slugify(baseHandle);
            if (room < 1) return clean;
            if (slug.Length > room) slug = slug[..room].TrimEnd('-');
            if (slug.Length == 0) slug = "articulo";
            return $"{slug}-{clean}";
        }

        /// <summary>
        /// Detecta el userError de Shopify por handle duplicado ("Handle has already
        /// been taken"). Mira campo y mensaje porque Shopify no expone un codigo estable.
        /// </summary>
        public static bool IsTakenError(string? field, string? message)
        {
            var f = field ?? "";
            var m = message ?? "";
            var mentionsHandle =
                f.Contains("handle", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("handle", StringComparison.OrdinalIgnoreCase);
            var taken =
                m.Contains("already been taken", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("has been taken", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("already exists", StringComparison.OrdinalIgnoreCase);
            return mentionsHandle && taken;
        }
    }
}
