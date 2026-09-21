using System.Globalization;
using System.Text.Json;

namespace Server.Services
{
    /// <summary>
    /// Sello del build que se ve en el pie del panel izquierdo: commit y hora.
    ///
    /// La marca de tiempo se guarda siempre en UTC (ISO-8601) y se presenta en
    /// horario de Madrid, que es la zona en la que trabaja el equipo.
    ///
    /// Los datos llegan por dos vias, en este orden de preferencia:
    /// 1. variables de entorno <c>GIT_COMMIT</c> / <c>BUILD_DATE</c>, que se
    ///    refrescan con un simple <c>docker compose up -d</c>;
    /// 2. <c>build-info.json</c>, generado durante el build de la imagen.
    /// La variable manda sobre el fichero porque el fichero puede venir de una capa
    /// de Docker cacheada y quedarse con el commit y la fecha del build anterior,
    /// que es justo lo que hacia que el pie mostrase datos que no tocaban.
    /// </summary>
    public static class BuildInfo
    {
        public const string Unknown = "unknown";

        /// <summary>Zona del equipo. El id de Windows se usa como red de seguridad.</summary>
        private const string MadridIana = "Europe/Madrid";
        private const string MadridWindows = "Romance Standard Time";

        /// <summary>Commit ya acortado y fecha ya formateada, listos para pintar.</summary>
        public sealed record Snapshot(string CommitHash, string BuildDate);

        /// <summary>
        /// Lee el sello del build. <paramref name="env"/> existe para los tests;
        /// en produccion se resuelve contra las variables de entorno del proceso.
        /// </summary>
        public static Snapshot Read(string baseDirectory, Func<string, string?>? env = null)
        {
            env ??= Environment.GetEnvironmentVariable;

            var (fileCommit, fileDate) = ReadFile(Path.Combine(baseDirectory, "build-info.json"));

            return new Snapshot(
                ShortCommit(Pick(env("GIT_COMMIT"), fileCommit)),
                FormatMadrid(Pick(env("BUILD_DATE"), fileDate)));
        }

        /// <summary>
        /// Deja el commit en su forma corta (7 caracteres). Si lo que llega no es un
        /// SHA (por ejemplo "unknown" o una etiqueta), se devuelve tal cual: es mejor
        /// ensenar lo que hay que recortar algo que no es un hash.
        /// </summary>
        public static string ShortCommit(string? value)
        {
            var commit = value?.Trim();
            if (string.IsNullOrEmpty(commit)) return Unknown;

            var isSha = commit.Length >= 7 && commit.All(Uri.IsHexDigit);
            return isSha ? commit[..7].ToLowerInvariant() : commit;
        }

        /// <summary>
        /// Pasa el instante del build a hora de Madrid con formato <c>dd/MM/yyyy HH:mm</c>.
        /// Acepta ISO-8601 ("2026-09-21T14:32:05Z") y el formato antiguo
        /// ("2026-09-21 14:32:05 UTC"). Si no hay forma de interpretarlo, se devuelve
        /// el texto original en vez de inventarse una hora.
        /// </summary>
        public static string FormatMadrid(string? value)
        {
            var raw = value?.Trim();
            if (string.IsNullOrEmpty(raw)) return Unknown;

            if (!TryParseUtc(raw, out var utc)) return raw;

            var tz = MadridTimeZone();
            if (tz is null) return utc.ToString("dd/MM/yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture);

            var madrid = TimeZoneInfo.ConvertTime(utc, tz);
            return madrid.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
        }

        private static bool TryParseUtc(string raw, out DateTimeOffset utc)
        {
            // El formato antiguo del Dockerfile terminaba en " UTC", que no es un
            // offset que sepa parsear .NET: se quita y se asume universal.
            var text = raw.EndsWith(" UTC", StringComparison.OrdinalIgnoreCase)
                ? raw[..^4].TrimEnd()
                : raw;

            var styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, styles, out utc))
            {
                utc = utc.ToUniversalTime();
                return true;
            }

            utc = default;
            return false;
        }

        private static TimeZoneInfo? MadridTimeZone()
        {
            foreach (var id in new[] { MadridIana, MadridWindows })
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
                catch (TimeZoneNotFoundException) { }
                catch (InvalidTimeZoneException) { }
            }
            return null;
        }

        private static (string? Commit, string? Date) ReadFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return (null, null);

                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                return (ReadString(root, "commitHash"), ReadString(root, "buildDate"));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return (null, null);
            }
        }

        private static string? ReadString(JsonElement root, string name) =>
            root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(name, out var prop)
            && prop.ValueKind == JsonValueKind.String
                ? prop.GetString()
                : null;

        /// <summary>Primer valor con contenido y distinto del placeholder "unknown".</summary>
        private static string? Pick(params string?[] candidates) =>
            candidates.FirstOrDefault(c =>
                !string.IsNullOrWhiteSpace(c)
                && !string.Equals(c.Trim(), Unknown, StringComparison.OrdinalIgnoreCase));
    }
}
