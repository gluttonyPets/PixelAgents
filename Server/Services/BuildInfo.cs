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
    /// 1. <c>build-info.json</c>, que el Dockerfile escribe en la ultima capa
    ///    resolviendo el commit del propio arbol de fuentes;
    /// 2. variables de entorno <c>GIT_COMMIT</c> / <c>BUILD_DATE</c>, de reserva.
    ///
    /// Manda el fichero de la imagen porque describe el codigo que realmente se
    /// esta ejecutando. Una variable de entorno puede traer el commit de otro
    /// checkout, colarse desde un <c>.env</c> viejo o quedarse pegada de un deploy
    /// anterior, y entonces el pie ensena un hash que no corresponde.
    ///
    /// <see cref="Snapshot.RuntimeBuilt"/> no se sella: es la fecha del binario en
    /// ejecucion. Nadie la pasa por parametro, asi que es la unica que no puede
    /// mentir: si el contenedor lleva meses sin reconstruirse, ahi se ve.
    /// </summary>
    public static class BuildInfo
    {
        public const string Unknown = "unknown";

        /// <summary>Zona del equipo. El id de Windows se usa como red de seguridad.</summary>
        private const string MadridIana = "Europe/Madrid";
        private const string MadridWindows = "Romance Standard Time";

        /// <summary>
        /// Commit ya acortado y fecha ya formateada, listos para pintar.
        /// <paramref name="CommitFull"/> y <paramref name="Source"/> son para el
        /// tooltip: cuando el SHA del pie no cuadra con el que se espera, lo
        /// primero que hay que saber es el hash entero y de donde ha salido.
        /// </summary>
        public sealed record Snapshot(
            string CommitHash,
            string BuildDate,
            string CommitFull,
            string Source,
            string RuntimeBuilt);

        /// <summary>De donde ha salido el sello, para el tooltip del pie.</summary>
        public const string SourceImage = "imagen";
        public const string SourceEnvironment = "entorno";
        public const string SourceNone = "sin datos";

        /// <summary>Binario cuya fecha delata cuando se construyo la imagen de verdad.</summary>
        private const string ServerAssembly = "Server.dll";

        /// <summary>
        /// Lee el sello del build. <paramref name="env"/> y
        /// <paramref name="binaryTime"/> existen para los tests; en produccion se
        /// resuelven contra el entorno del proceso y el binario en ejecucion.
        /// </summary>
        public static Snapshot Read(
            string baseDirectory,
            Func<string, string?>? env = null,
            Func<DateTime?>? binaryTime = null)
        {
            env ??= Environment.GetEnvironmentVariable;
            binaryTime ??= () => ServerAssemblyTimeUtc(baseDirectory);

            var (fileCommit, fileDate) = ReadFile(Path.Combine(baseDirectory, "build-info.json"));

            var imageCommit = Usable(fileCommit);
            var commit = imageCommit ?? Usable(env("GIT_COMMIT"));
            var date = Usable(fileDate) ?? Usable(env("BUILD_DATE"));

            var source = imageCommit is not null ? SourceImage
                : commit is not null ? SourceEnvironment
                : SourceNone;

            return new Snapshot(
                ShortCommit(commit),
                FormatMadrid(date),
                commit?.Trim() ?? Unknown,
                source,
                FormatMadrid(binaryTime()));
        }

        /// <summary>
        /// Fecha del <c>Server.dll</c> que se esta ejecutando, que es la del build
        /// de la imagen. Es el contraste honesto del sello: si no coincide, lo que
        /// corre no es lo que dice el sello.
        /// </summary>
        private static DateTime? ServerAssemblyTimeUtc(string baseDirectory)
        {
            try
            {
                var path = Path.Combine(baseDirectory, ServerAssembly);
                return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
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

            return TryParseUtc(raw, out var utc) ? FormatMadrid(utc) : raw;
        }

        /// <summary>Mismo formato, para instantes que ya vienen como fecha.</summary>
        public static string FormatMadrid(DateTime? utc) =>
            utc is null
                ? Unknown
                : FormatMadrid(new DateTimeOffset(DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc)));

        private static string FormatMadrid(DateTimeOffset utc)
        {
            var tz = MadridTimeZone();
            if (tz is null) return utc.ToString("dd/MM/yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture);

            return TimeZoneInfo.ConvertTime(utc, tz)
                .ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
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

        /// <summary>El valor si aporta algo; null si viene vacio o es el placeholder "unknown".</summary>
        private static string? Usable(string? value) =>
            !string.IsNullOrWhiteSpace(value)
            && !string.Equals(value.Trim(), Unknown, StringComparison.OrdinalIgnoreCase)
                ? value
                : null;
    }
}
