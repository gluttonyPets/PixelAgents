using System.Text;
using System.Text.Json;

namespace Server.Services.Ai;

/// <summary>
/// Contrato del indice del modulo Directorio de archivos: parseo, validacion y
/// resolucion de la ruta publica de cada fichero.
///
/// El modulo no sirve para almacenar por almacenar: su valor es el INDICE. Un
/// directorio sin indice, o con ficheros sin explicar, no se acepta, porque el
/// modulo que recibe la salida solo puede elegir un fichero si sabe que es cada
/// uno y de donde bajarlo. Por eso aqui son obligatorios los tres datos:
/// ruta dentro del directorio, descripcion y URL accesible.
///
/// Vive fuera del handler porque tanto el handler como el endpoint publico que
/// expone el directorio necesitan resolver el mismo indice de la misma forma.
/// </summary>
public static class FileDirectoryIndex
{
    public const string ModuleType = "FileDirectory";

    /// <summary>Puerto de salida: el modulo solo emite, no consume nada.</summary>
    public const string OutputPort = "output_index";

    /// <summary>Clave de configuracion con el indice (JSON).</summary>
    public const string IndexConfigKey = "index";

    /// <summary>Clave de configuracion con la URL base del repositorio.</summary>
    public const string BaseUrlConfigKey = "baseUrl";

    /// <summary>Clave de configuracion con el formato de salida.</summary>
    public const string FormatConfigKey = "format";

    /// <summary>Segmento raiz de la URL publica del directorio.</summary>
    public const string PublicRoute = "/api/public/directory";

    // ── Modelo ──

    /// <summary>Una entrada del indice tal y como la escribe el usuario.</summary>
    public sealed class IndexEntry
    {
        /// <summary>Ruta dentro del directorio, con carpetas y subcarpetas.</summary>
        public string? Path { get; set; }

        /// <summary>Que es este fichero. Obligatoria.</summary>
        public string? Description { get; set; }

        /// <summary>URL absoluta propia de la entrada (repositorio externo).</summary>
        public string? Url { get; set; }

        /// <summary>Nombre del fichero subido a este nodo que respalda la entrada.</summary>
        public string? File { get; set; }

        /// <summary>
        /// Id del fichero subido que respalda la entrada. Es la forma preferente
        /// de apuntar a un fichero alojado: el nombre puede repetirse entre
        /// carpetas, el id no. El explorador del inspector siempre lo escribe.
        /// </summary>
        public Guid? FileId { get; set; }
    }

    /// <summary>Un fichero subido al nodo, candidato a respaldar una entrada.</summary>
    public sealed record HostedFile(Guid Id, string FileName);

    /// <summary>Una entrada ya validada, con su ruta accesible resuelta.</summary>
    public sealed record ResolvedEntry(
        string Path,
        string Folder,
        string Name,
        string Description,
        string Url,
        string Source,
        string? SourceFile = null,
        Guid? SourceFileId = null);

    /// <summary>Resultado de resolver un indice completo.</summary>
    public sealed class ParseResult
    {
        public List<ResolvedEntry> Entries { get; } = [];
        public List<string> Errors { get; } = [];
        public string? BaseUrl { get; set; }

        /// <summary>
        /// Carpetas declaradas explicitamente en el indice. El explorador del
        /// inspector las escribe para que una carpeta recien creada no
        /// desaparezca por no tener ficheros todavia.
        /// </summary>
        public List<string> DeclaredFolders { get; } = [];

        public bool IsValid => Errors.Count == 0 && Entries.Count > 0;

        /// <summary>Carpetas del directorio: las que tienen ficheros y las declaradas vacias.</summary>
        public IReadOnlyList<string> Folders => Entries
            .Select(e => e.Folder)
            .Concat(DeclaredFolders)
            .Where(f => !string.IsNullOrEmpty(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Origen desde el que se sirve un fichero del directorio.</summary>
    public static class Sources
    {
        /// <summary>Fichero subido a este nodo y expuesto por nuestra URL publica.</summary>
        public const string Hosted = "hosted";

        /// <summary>Fichero que ya vive en un repositorio externo expuesto.</summary>
        public const string External = "external";
    }

    // ── Parseo ──

    /// <summary>
    /// Lee el indice escrito por el usuario. Acepta tanto el objeto completo
    /// (<c>{ "baseUrl": ..., "files": [...] }</c>) como la lista pelada
    /// (<c>[ ... ]</c>), que es como se escribe cuando no hay URL base.
    /// </summary>
    public static List<IndexEntry> ParseEntries(
        string? indexJson,
        out string? baseUrl,
        out string? parseError,
        out List<string> declaredFolders)
    {
        baseUrl = null;
        parseError = null;
        declaredFolders = [];

        if (string.IsNullOrWhiteSpace(indexJson))
        {
            parseError = "El directorio no tiene indice. Es obligatorio: sin indice, el modulo que recibe la salida no sabe que es cada fichero ni de donde cogerlo.";
            return [];
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(indexJson);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            parseError = $"El indice no es JSON valido: {ex.Message}";
            return [];
        }

        JsonElement filesElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            filesElement = root;
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty(BaseUrlConfigKey, out var baseEl) && baseEl.ValueKind == JsonValueKind.String)
                baseUrl = baseEl.GetString();

            if (root.TryGetProperty("folders", out var foldersEl) && foldersEl.ValueKind == JsonValueKind.Array)
                declaredFolders = foldersEl
                    .EnumerateArray()
                    .Where(f => f.ValueKind == JsonValueKind.String)
                    .Select(f => NormalizePath(f.GetString()))
                    .Where(f => f is not null && !HasTraversal(f))
                    .Select(f => f!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

            if (!TryGetFilesArray(root, out filesElement))
            {
                parseError = "El indice debe traer una lista de ficheros en la propiedad \"files\".";
                return [];
            }
        }
        else
        {
            parseError = "El indice debe ser una lista de ficheros o un objeto con la propiedad \"files\".";
            return [];
        }

        var entries = new List<IndexEntry>();
        foreach (var item in filesElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                parseError = "Cada entrada del indice debe ser un objeto con \"path\" y \"description\".";
                return [];
            }

            entries.Add(new IndexEntry
            {
                Path = ReadString(item, "path", "ruta"),
                Description = ReadString(item, "description", "descripcion", "desc"),
                Url = ReadString(item, "url"),
                File = ReadString(item, "file", "fichero", "archivo"),
                FileId = ReadGuid(item, "fileId"),
            });
        }

        return entries;
    }

    private static bool TryGetFilesArray(JsonElement root, out JsonElement files)
    {
        foreach (var name in new[] { "files", "ficheros", "archivos", "entries" })
        {
            if (root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Array)
            {
                files = el;
                return true;
            }
        }

        files = default;
        return false;
    }

    private static Guid? ReadGuid(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
            return null;
        return Guid.TryParse(el.GetString(), out var parsed) ? parsed : null;
    }

    private static string? ReadString(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
            {
                var value = el.GetString();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }
        return null;
    }

    /// <summary>
    /// Lee una clave de la configuracion del nodo. Se mira primero la del nodo
    /// (el inspector del pipeline) y se cae a la del modulo, igual que hace el
    /// executor al mezclar ambas. El valor puede venir como cadena JSON o como
    /// objeto anidado: en el segundo caso se devuelve su JSON crudo.
    /// </summary>
    public static string? ReadConfig(string? moduleConfig, string? nodeConfig, string key)
    {
        return ReadKey(nodeConfig, key) ?? ReadKey(moduleConfig, key);
    }

    private static string? ReadKey(string? json, string key)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty(key, out var el)) return null;

            var value = el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ── Validacion y resolucion ──

    /// <summary>
    /// Valida el indice y resuelve la ruta accesible de cada fichero.
    ///
    /// El orden de resolucion de la URL es: la <c>url</c> absoluta de la entrada,
    /// luego la URL base del repositorio mas la ruta, y por ultimo el fichero
    /// subido a este nodo, que se expone por nuestra propia URL publica. Si una
    /// entrada no cae en ninguno de los tres casos se rechaza: un fichero
    /// indexado que nadie puede descargar no aporta nada.
    /// </summary>
    /// <param name="indexJson">Indice escrito en la configuracion del modulo.</param>
    /// <param name="configBaseUrl">URL base declarada aparte en la configuracion.</param>
    /// <param name="hostedFiles">Nombres de fichero subidos a este nodo.</param>
    /// <param name="hostedUrlFactory">Construye la URL publica de un fichero alojado.</param>
    public static ParseResult Resolve(
        string? indexJson,
        string? configBaseUrl = null,
        IEnumerable<HostedFile>? hostedFiles = null,
        Func<string, string>? hostedUrlFactory = null)
    {
        var result = new ParseResult();

        var entries = ParseEntries(indexJson, out var indexBaseUrl, out var parseError, out var declaredFolders);
        if (parseError is not null)
        {
            result.Errors.Add(parseError);
            return result;
        }

        result.DeclaredFolders.AddRange(declaredFolders);

        // La URL base del propio indice manda sobre la del inspector: el indice
        // es el documento que viaja con el directorio.
        var baseUrl = FirstNonBlank(indexBaseUrl, configBaseUrl);
        result.BaseUrl = NormalizeBaseUrl(baseUrl);

        if (entries.Count == 0)
        {
            result.Errors.Add("El indice esta vacio: declara al menos un fichero con su ruta y su descripcion.");
            return result;
        }

        var hosted = (hostedFiles ?? []).ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var position = $"Entrada {i + 1}";

            var path = NormalizePath(entry.Path);
            if (path is null)
            {
                result.Errors.Add($"{position}: falta la ruta del fichero (\"path\").");
                continue;
            }

            if (HasTraversal(path))
            {
                result.Errors.Add($"{position} (\"{path}\"): la ruta no puede salir del directorio ni usar segmentos \".\" o \"..\".");
                continue;
            }

            if (!seen.Add(path))
            {
                result.Errors.Add($"{position} (\"{path}\"): ruta repetida en el indice.");
                continue;
            }

            var description = entry.Description?.Trim();
            if (string.IsNullOrWhiteSpace(description))
            {
                result.Errors.Add($"{position} (\"{path}\"): falta la descripcion. El indice tiene que explicar que es cada fichero.");
                continue;
            }

            var (url, source, sourceFile, sourceFileId) =
                ResolveUrl(entry, path, result.BaseUrl, hosted, hostedUrlFactory);
            if (url is null)
            {
                result.Errors.Add(
                    $"{position} (\"{path}\"): no hay ruta accesible. Declara una \"url\" absoluta, " +
                    "una \"baseUrl\" para todo el directorio, o sube el fichero a este modulo.");
                continue;
            }

            var separator = path.LastIndexOf('/');
            var folder = separator < 0 ? "" : path[..separator];
            var name = separator < 0 ? path : path[(separator + 1)..];

            result.Entries.Add(
                new ResolvedEntry(path, folder, name, description!, url, source, sourceFile, sourceFileId));
        }

        return result;
    }

    private static (string? Url, string Source, string? SourceFile, Guid? SourceFileId) ResolveUrl(
        IndexEntry entry,
        string path,
        string? baseUrl,
        List<HostedFile> hosted,
        Func<string, string>? hostedUrlFactory)
    {
        // Un fichero subido al nodo manda sobre la URL base: el explorador
        // escribe su id al subirlo, y ese fichero es el que el usuario puso
        // ahi, aunque el directorio tenga ademas un repositorio externo.
        var match = FindHosted(entry, path, hosted);
        if (match is not null && hostedUrlFactory is not null)
            return (hostedUrlFactory(path), Sources.Hosted, match.FileName, match.Id);

        if (IsAbsoluteUrl(entry.Url))
            return (entry.Url!.Trim(), Sources.External, null, null);

        if (!string.IsNullOrWhiteSpace(baseUrl))
            return ($"{baseUrl}/{EncodePath(path)}", Sources.External, null, null);

        return (null, Sources.External, null, null);
    }

    /// <summary>
    /// Busca el fichero subido que respalda una entrada. Por id primero, que es
    /// lo que escribe el explorador y lo unico que distingue dos ficheros con el
    /// mismo nombre en carpetas distintas; por nombre despues, para los indices
    /// escritos a mano.
    /// </summary>
    private static HostedFile? FindHosted(IndexEntry entry, string path, List<HostedFile> hosted)
    {
        if (hosted.Count == 0) return null;

        if (entry.FileId is { } id)
            return hosted.FirstOrDefault(h => h.Id == id);

        var fileName = entry.File ?? path[(path.LastIndexOf('/') + 1)..];
        return hosted.FirstOrDefault(h =>
            string.Equals(h.FileName, fileName, StringComparison.OrdinalIgnoreCase));
    }

    // ── Rutas ──

    /// <summary>Construye la ruta publica de un fichero alojado en el directorio.</summary>
    public static string BuildPublicPath(string tenant, Guid moduleId, string path) =>
        $"{PublicRoute}/{Uri.EscapeDataString(tenant)}/{moduleId}/{EncodePath(path)}";

    /// <summary>Construye la ruta publica del indice del directorio.</summary>
    public static string BuildPublicIndexPath(string tenant, Guid moduleId) =>
        $"{PublicRoute}/{Uri.EscapeDataString(tenant)}/{moduleId}";

    /// <summary>Antepone la URL publica del servidor a una ruta relativa.</summary>
    public static string Absolutize(string? publicBaseUrl, string path) =>
        string.IsNullOrWhiteSpace(publicBaseUrl) ? path : $"{publicBaseUrl.TrimEnd('/')}{path}";

    /// <summary>
    /// Normaliza una ruta del indice: separadores en "/", sin barras sobrantes
    /// ni segmentos vacios. Devuelve null si no queda nada aprovechable.
    /// </summary>
    public static string? NormalizePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var segments = raw
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        return segments.Count == 0 ? null : string.Join('/', segments);
    }

    /// <summary>True si la ruta intenta salir del directorio.</summary>
    public static bool HasTraversal(string path) =>
        path.Split('/').Any(s => s is "." or "..");

    private static string EncodePath(string path) =>
        string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private static bool IsAbsoluteUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed)
        && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);

    private static string? NormalizeBaseUrl(string? baseUrl) =>
        string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.Trim().TrimEnd('/');

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    // ── Salida ──

    /// <summary>
    /// Renderiza el indice resuelto para el modulo que lo recibe. El formato
    /// markdown agrupa por carpeta y es el que lee bien un modelo; el JSON es
    /// para consumo automatico.
    /// </summary>
    public static string Render(ParseResult result, string format)
    {
        return string.Equals(format, "json", StringComparison.OrdinalIgnoreCase)
            ? RenderJson(result)
            : RenderMarkdown(result);
    }

    private static string RenderMarkdown(ParseResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Indice del directorio de archivos.");
        sb.AppendLine("Cada fichero indica que es y la URL desde la que puedes descargarlo.");
        sb.AppendLine();

        foreach (var group in result.Entries
            .GroupBy(e => e.Folder, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"[{(string.IsNullOrEmpty(group.Key) ? "/" : group.Key)}]");
            foreach (var entry in group.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"- {entry.Name}: {entry.Description}");
                sb.AppendLine($"  URL: {entry.Url}");
            }
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static string RenderJson(ParseResult result)
    {
        var payload = new
        {
            baseUrl = result.BaseUrl,
            fileCount = result.Entries.Count,
            files = result.Entries.Select(e => new
            {
                path = e.Path,
                folder = e.Folder,
                name = e.Name,
                description = e.Description,
                url = e.Url,
                source = e.Source,
            }),
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }
}
