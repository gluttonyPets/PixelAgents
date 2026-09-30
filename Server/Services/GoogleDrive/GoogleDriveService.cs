using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Server.Services.SearchConsole;

namespace Server.Services.GoogleDrive;

/// <summary>Error de Google Drive con un mensaje apto para mostrar al usuario.</summary>
public class GoogleDriveException(string message) : Exception(message);

public record DriveFolderInfo(string Id, string Name);

/// <summary>Carpeta (o unidad compartida) que ve la cuenta de servicio, para el explorador.</summary>
public record DriveFolderEntry(string Id, string Name, bool IsSharedDrive = false)
{
    /// <summary>Carpetas padre segun Drive; solo sirve para calcular la raiz del explorador.</summary>
    public IReadOnlyList<string> Parents { get; init; } = [];
}

public record DriveFile(string Id, string Name, string MimeType, DateTime? CreatedTime, string? WebViewLink, long? Size);

public record DriveDownload(byte[] Data, string FileName, string ContentType);

/// <summary>
/// Cliente de solo lectura de la API de Google Drive v3. Autentica con una cuenta de
/// servicio igual que Search Console (JWT firmado -> access token). La cuenta de
/// servicio solo ve lo que se le comparte: la carpeta vigilada debe compartirse con
/// su email (lector basta).
/// </summary>
public class GoogleDriveService
{
    public const string Scope = "https://www.googleapis.com/auth/drive.readonly";
    public const string FolderMimeType = "application/vnd.google-apps.folder";
    /// <summary>Archivos mas grandes no se descargan: el pipeline recibe solo sus datos.</summary>
    public const long MaxDownloadBytes = 50L * 1024 * 1024;

    private const string ApiBase = "https://www.googleapis.com/drive/v3";
    private const int MaxListedFiles = 5000;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    // Tokens por cuenta de servicio. Duran 1 h; se renuevan con 5 min de margen.
    private static readonly ConcurrentDictionary<string, (string Token, DateTime ExpiresAt)> TokenCache = new();

    private readonly HttpClient _http;

    public GoogleDriveService(HttpClient http) => _http = http;

    public static ServiceAccountCredentials ParseCredentials(string? json)
    {
        try { return ServiceAccountCredentials.Parse(json, "Google Drive"); }
        catch (SearchConsoleException ex) { throw new GoogleDriveException(ex.Message); }
    }

    /// <summary>Datos de la carpeta; falla con un mensaje claro si no existe, no es una
    /// carpeta o la cuenta de servicio no tiene acceso.</summary>
    public async Task<DriveFolderInfo> GetFolderAsync(string credentialsJson, string folderId, CancellationToken ct = default)
    {
        var creds = ParseCredentials(credentialsJson);
        var url = $"{ApiBase}/files/{Uri.EscapeDataString(folderId)}?fields=id,name,mimeType&supportsAllDrives=true";
        var json = await GetStringAsync(creds, url, folderId, ct);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var mime = ReadString(root, "mimeType");
        if (mime != FolderMimeType)
            throw new GoogleDriveException("El enlace de Drive no es de una carpeta: pega la URL de la carpeta que se vigila.");
        return new DriveFolderInfo(ReadString(root, "id") ?? folderId, ReadString(root, "name") ?? folderId);
    }

    /// <summary>Archivos (no subcarpetas) de la carpeta, sin los de la papelera.</summary>
    public async Task<List<DriveFile>> ListFilesAsync(string credentialsJson, string folderId, CancellationToken ct = default)
    {
        var creds = ParseCredentials(credentialsJson);
        var files = new List<DriveFile>();
        string? pageToken = null;
        do
        {
            var url = $"{ApiBase}/files?q={Uri.EscapeDataString(BuildListQuery(folderId))}" +
                      "&fields=nextPageToken,files(id,name,mimeType,createdTime,webViewLink,size)" +
                      "&orderBy=createdTime&pageSize=200&supportsAllDrives=true&includeItemsFromAllDrives=true&corpora=allDrives" +
                      (pageToken is null ? "" : $"&pageToken={Uri.EscapeDataString(pageToken)}");
            var json = await GetStringAsync(creds, url, folderId, ct);
            var (page, next) = ParseFileList(json);
            files.AddRange(page);
            pageToken = next;
        } while (pageToken is not null && files.Count < MaxListedFiles);

        return files;
    }

    /// <summary>
    /// Carpetas para el explorador. Sin <paramref name="parentId"/> devuelve el nivel
    /// raiz de lo que ve la cuenta de servicio: las unidades compartidas donde es miembro,
    /// las carpetas que le han compartido y las de su propia unidad. Con el, las
    /// subcarpetas directas de esa carpeta. Ordenadas por nombre.
    /// </summary>
    public async Task<List<DriveFolderEntry>> ListFoldersAsync(string credentialsJson, string? parentId, CancellationToken ct = default)
    {
        var creds = ParseCredentials(credentialsJson);
        if (!string.IsNullOrWhiteSpace(parentId))
        {
            var children = await ListFolderQueryAsync(creds, BuildFolderChildrenQuery(parentId), parentId, ct);
            return children.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        // No se usa "sharedWithMe": con cuentas de servicio no siempre devuelve lo que
        // les han compartido. Se piden TODAS las carpetas visibles y son raiz las que no
        // tienen su carpeta padre a la vista (la compartida, no sus subcarpetas).
        var drives = await ListSharedDrivesAsync(creds, ct);
        var visible = await ListFolderQueryAsync(creds,
            $"mimeType = '{FolderMimeType}' and trashed = false", "(raiz)", ct);
        var roots = RootFolders(visible, drives.Select(d => d.Id));
        return drives.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).Concat(roots).ToList();
    }

    /// <summary>
    /// Carpetas del nivel raiz del explorador: las visibles cuyo padre no es visible ni es
    /// una unidad compartida listada (esas cuelgan de la unidad). Ordenadas por nombre.
    /// </summary>
    public static List<DriveFolderEntry> RootFolders(IReadOnlyList<DriveFolderEntry> visible, IEnumerable<string> sharedDriveIds)
    {
        var known = new HashSet<string>(visible.Select(f => f.Id));
        known.UnionWith(sharedDriveIds);
        return visible
            .Where(f => f.Parents.Count == 0 || !f.Parents.Any(known.Contains))
            .GroupBy(f => f.Id).Select(g => g.First())
            .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Consulta de files.list para las subcarpetas directas de una carpeta.</summary>
    public static string BuildFolderChildrenQuery(string parentId) =>
        $"'{EscapeQuery(parentId)}' in parents and mimeType = '{FolderMimeType}' and trashed = false";

    private async Task<List<DriveFolderEntry>> ListFolderQueryAsync(
        ServiceAccountCredentials creds, string query, string context, CancellationToken ct)
    {
        var result = new List<DriveFolderEntry>();
        string? pageToken = null;
        do
        {
            var url = $"{ApiBase}/files?q={Uri.EscapeDataString(query)}" +
                      "&fields=nextPageToken,files(id,name,parents)&pageSize=200" +
                      "&supportsAllDrives=true&includeItemsFromAllDrives=true&corpora=allDrives" +
                      (pageToken is null ? "" : $"&pageToken={Uri.EscapeDataString(pageToken)}");
            var json = await GetStringAsync(creds, url, context, ct);
            var (page, next) = ParseFolderList(json, "files");
            result.AddRange(page);
            pageToken = next;
        } while (pageToken is not null && result.Count < MaxListedFiles);
        return result;
    }

    private async Task<List<DriveFolderEntry>> ListSharedDrivesAsync(ServiceAccountCredentials creds, CancellationToken ct)
    {
        var result = new List<DriveFolderEntry>();
        string? pageToken = null;
        do
        {
            var url = $"{ApiBase}/drives?pageSize=100&fields=nextPageToken,drives(id,name)" +
                      (pageToken is null ? "" : $"&pageToken={Uri.EscapeDataString(pageToken)}");
            var json = await GetStringAsync(creds, url, "(unidades compartidas)", ct);
            var (page, next) = ParseFolderList(json, "drives");
            result.AddRange(page.Select(d => d with { IsSharedDrive = true }));
            pageToken = next;
        } while (pageToken is not null && result.Count < MaxListedFiles);
        return result;
    }

    /// <summary>Lee un listado de files.list ("files") o drives.list ("drives").</summary>
    public static (List<DriveFolderEntry> Folders, string? NextPageToken) ParseFolderList(string json, string arrayName)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var folders = new List<DriveFolderEntry>();
        if (root.TryGetProperty(arrayName, out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in arr.EnumerateArray())
            {
                var id = ReadString(f, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                var parents = f.TryGetProperty("parents", out var pa) && pa.ValueKind == JsonValueKind.Array
                    ? pa.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToList()
                    : new List<string>();
                folders.Add(new DriveFolderEntry(id!, ReadString(f, "name") ?? id!) { Parents = parents });
            }
        }
        var next = ReadString(root, "nextPageToken");
        return (folders, string.IsNullOrWhiteSpace(next) ? null : next);
    }

    /// <summary>
    /// Descarga el archivo. Los documentos nativos de Google (Docs, Hojas, Presentaciones,
    /// Dibujos) se exportan a un formato que los modelos entienden. Devuelve null si el
    /// tipo no se puede descargar o supera <see cref="MaxDownloadBytes"/>.
    /// </summary>
    public async Task<DriveDownload?> DownloadAsync(string credentialsJson, DriveFile file, CancellationToken ct = default)
    {
        if (file.Size is > MaxDownloadBytes) return null;

        string url, contentType, fileName;
        if (file.MimeType.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal))
        {
            var export = ExportFormat(file.MimeType);
            if (export is null) return null;
            url = $"{ApiBase}/files/{Uri.EscapeDataString(file.Id)}/export?mimeType={Uri.EscapeDataString(export.Value.MimeType)}";
            contentType = export.Value.MimeType;
            fileName = file.Name.EndsWith(export.Value.Extension, StringComparison.OrdinalIgnoreCase)
                ? file.Name : file.Name + export.Value.Extension;
        }
        else
        {
            url = $"{ApiBase}/files/{Uri.EscapeDataString(file.Id)}?alt=media&supportsAllDrives=true";
            contentType = string.IsNullOrWhiteSpace(file.MimeType) ? "application/octet-stream" : file.MimeType;
            fileName = file.Name;
        }

        var creds = ParseCredentials(credentialsJson);
        using var timeoutCts = new CancellationTokenSource(Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        using var request = await BuildRequestAsync(creds, url, linked.Token);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(linked.Token);
                throw new GoogleDriveException(
                    $"No se pudo descargar '{file.Name}' de Drive ({(int)response.StatusCode}): {SearchConsoleService.ReadGoogleError(body)}");
            }
            if (response.Content.Headers.ContentLength is > MaxDownloadBytes) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, linked.Token)) > 0)
            {
                if (buffer.Length + read > MaxDownloadBytes) return null;
                buffer.Write(chunk, 0, read);
            }
            return new DriveDownload(buffer.ToArray(), fileName, contentType);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GoogleDriveException($"Drive no respondio a tiempo al descargar '{file.Name}'.");
        }
    }

    /// <summary>Consulta de files.list: hijos directos de la carpeta que no son
    /// carpetas ni estan en la papelera.</summary>
    public static string BuildListQuery(string folderId) =>
        $"'{EscapeQuery(folderId)}' in parents and trashed = false and mimeType != '{FolderMimeType}'";

    private static string EscapeQuery(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");

    /// <summary>Formato al que se exporta un documento nativo de Google; null si no
    /// tiene contenido exportable (formularios, accesos directos, mapas...).</summary>
    public static (string MimeType, string Extension)? ExportFormat(string googleMimeType) => googleMimeType switch
    {
        "application/vnd.google-apps.document" => ("application/pdf", ".pdf"),
        "application/vnd.google-apps.presentation" => ("application/pdf", ".pdf"),
        "application/vnd.google-apps.spreadsheet" => ("text/csv", ".csv"),
        "application/vnd.google-apps.drawing" => ("image/png", ".png"),
        _ => null,
    };

    public static (List<DriveFile> Files, string? NextPageToken) ParseFileList(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var files = new List<DriveFile>();
        if (root.TryGetProperty("files", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in arr.EnumerateArray())
            {
                var id = ReadString(f, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                DateTime? created = DateTime.TryParse(ReadString(f, "createdTime"), null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var c) ? c : null;
                long? size = long.TryParse(ReadString(f, "size"), out var sz) ? sz : null;
                files.Add(new DriveFile(id!, ReadString(f, "name") ?? id!, ReadString(f, "mimeType") ?? "",
                    created, ReadString(f, "webViewLink"), size));
            }
        }
        var next = ReadString(root, "nextPageToken");
        return (files, string.IsNullOrWhiteSpace(next) ? null : next);
    }

    private async Task<string> GetStringAsync(ServiceAccountCredentials creds, string url, string folderId, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        using var request = await BuildRequestAsync(creds, url, linked.Token);

        HttpResponseMessage response;
        try { response = await _http.SendAsync(request, linked.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GoogleDriveException("Google Drive no respondio a tiempo.");
        }

        using (response)
        {
            var json = await response.Content.ReadAsStringAsync(linked.Token);
            if (response.IsSuccessStatusCode) return json;

            var apiMessage = SearchConsoleService.ReadGoogleError(json);
            throw response.StatusCode switch
            {
                HttpStatusCode.NotFound => new GoogleDriveException(
                    $"La carpeta '{folderId}' no existe o la cuenta de servicio {creds.ClientEmail} no tiene acceso. " +
                    "Comparte la carpeta con ese email desde Drive (permiso de lector)."),
                HttpStatusCode.Forbidden => new GoogleDriveException(
                    "Acceso denegado por Google Drive. Comprueba que la 'Google Drive API' esta habilitada en el " +
                    $"proyecto de Google Cloud de la cuenta de servicio. Detalle: {apiMessage}"),
                _ => new GoogleDriveException($"Google Drive respondio {(int)response.StatusCode}: {apiMessage}"),
            };
        }
    }

    private async Task<HttpRequestMessage> BuildRequestAsync(ServiceAccountCredentials creds, string url, CancellationToken ct)
    {
        var token = await GetAccessTokenAsync(creds, ct);
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private async Task<string> GetAccessTokenAsync(ServiceAccountCredentials creds, CancellationToken ct)
    {
        if (TokenCache.TryGetValue(creds.ClientEmail, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
            return cached.Token;

        string assertion;
        try { assertion = SearchConsoleService.BuildJwtAssertion(creds, DateTimeOffset.UtcNow, Scope); }
        catch (SearchConsoleException ex) { throw new GoogleDriveException(ex.Message); }

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
            ["assertion"] = assertion,
        });

        using var response = await _http.PostAsync(creds.TokenUri, content, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new GoogleDriveException(
                $"Google rechazo las credenciales de la cuenta de servicio ({creds.ClientEmail}): {SearchConsoleService.ReadGoogleError(json)}");

        using var doc = JsonDocument.Parse(json);
        var token = ReadString(doc.RootElement, "access_token");
        if (string.IsNullOrWhiteSpace(token))
            throw new GoogleDriveException("Google no devolvio un access token para la cuenta de servicio.");

        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var s) ? s : 3600;
        TokenCache[creds.ClientEmail] = (token!, DateTime.UtcNow.AddSeconds(Math.Max(60, expiresIn - 300)));
        return token!;
    }

    private static string? ReadString(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
