using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Server.Services.SearchConsole;

/// <summary>Propiedad de Search Console accesible por la cuenta de servicio.</summary>
public record SearchConsoleSiteDto(string SiteUrl, string PermissionLevel);

/// <summary>Error de Search Console con un mensaje apto para mostrar al usuario.</summary>
public class SearchConsoleException(string message) : Exception(message)
{
    /// <summary>La cuenta de servicio no tiene acceso a la propiedad pedida.</summary>
    public bool IsSiteAccessDenied { get; init; }
}

/// <summary>
/// Credenciales de una cuenta de servicio de Google Cloud: el JSON que se
/// descarga al crear una clave para la cuenta. Se guarda tal cual como API key
/// del proveedor "SearchConsole".
/// </summary>
public sealed record ServiceAccountCredentials(string ClientEmail, string PrivateKey, string TokenUri)
{
    public const string DefaultTokenUri = "https://oauth2.googleapis.com/token";

    public static ServiceAccountCredentials Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new SearchConsoleException("La API key de Search Console esta vacia: pega el JSON de la cuenta de servicio.");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException)
        {
            throw new SearchConsoleException("La API key de Search Console no es un JSON valido: pega el fichero JSON completo de la cuenta de servicio.");
        }

        using (doc)
        {
            var root = doc.RootElement;
            string? Read(string name) =>
                root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() : null;

            var email = Read("client_email");
            var key = Read("private_key");
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(key))
                throw new SearchConsoleException("El JSON de la cuenta de servicio no tiene 'client_email' y 'private_key'.");

            var tokenUri = Read("token_uri");
            return new ServiceAccountCredentials(email!, key!,
                string.IsNullOrWhiteSpace(tokenUri) ? DefaultTokenUri : tokenUri!);
        }
    }
}

/// <summary>
/// Cliente de la API de Google Search Console (solo lectura). Autentica con una
/// cuenta de servicio (JWT firmado con su clave privada -> access token) sin
/// depender de la libreria de Google: es un unico intercambio OAuth.
/// La cuenta de servicio debe estar anadida como usuario de la propiedad en
/// Search Console para poder leer sus datos.
/// </summary>
public class SearchConsoleService
{
    public const string Scope = "https://www.googleapis.com/auth/webmasters.readonly";
    private const string ApiBase = "https://searchconsole.googleapis.com/webmasters/v3";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    // Tokens por cuenta de servicio. Duran 1 h; se renuevan con 5 min de margen.
    private static readonly ConcurrentDictionary<string, (string Token, DateTime ExpiresAt)> TokenCache = new();

    private readonly HttpClient _http;

    public SearchConsoleService(HttpClient http) => _http = http;

    /// <summary>Propiedades a las que tiene acceso la cuenta de servicio.</summary>
    public async Task<List<SearchConsoleSiteDto>> ListSitesAsync(string credentialsJson, CancellationToken ct = default)
    {
        var creds = ServiceAccountCredentials.Parse(credentialsJson);
        var json = await SendAsync(creds, HttpMethod.Get, $"{ApiBase}/sites", null, null, ct);

        var result = new List<SearchConsoleSiteDto>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("siteEntry", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in entries.EnumerateArray())
            {
                var url = e.TryGetProperty("siteUrl", out var u) ? u.GetString() : null;
                var perm = e.TryGetProperty("permissionLevel", out var p) ? p.GetString() : null;
                if (!string.IsNullOrWhiteSpace(url))
                    result.Add(new SearchConsoleSiteDto(url!, perm ?? ""));
            }
        }
        return result.OrderBy(s => s.SiteUrl, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>searchAnalytics.query: devuelve el JSON crudo de la respuesta.</summary>
    public Task<string> QuerySearchAnalyticsAsync(
        string credentialsJson, string siteUrl, object requestBody, CancellationToken ct = default)
    {
        var creds = ServiceAccountCredentials.Parse(credentialsJson);
        var url = $"{ApiBase}/sites/{Uri.EscapeDataString(siteUrl)}/searchAnalytics/query";
        return SendAsync(creds, HttpMethod.Post, url, requestBody, siteUrl, ct);
    }

    private async Task<string> SendAsync(
        ServiceAccountCredentials creds, HttpMethod method, string url, object? body, string? siteUrl,
        CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var token = await GetAccessTokenAsync(creds, linked.Token);

        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, linked.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SearchConsoleException("Search Console no respondio a tiempo.");
        }

        using (response)
        {
            var json = await response.Content.ReadAsStringAsync(linked.Token);
            if (response.IsSuccessStatusCode)
                return json;

            var apiMessage = ReadGoogleError(json);
            throw response.StatusCode switch
            {
                HttpStatusCode.Forbidden or HttpStatusCode.NotFound when siteUrl is not null =>
                    new SearchConsoleException(
                        $"La cuenta de servicio {creds.ClientEmail} no tiene acceso a la propiedad '{siteUrl}'. " +
                        "Anadela como usuario en Search Console (Configuracion > Usuarios y permisos) y revisa que la " +
                        "propiedad este escrita igual que alli ('sc-domain:dominio.com' para propiedades de dominio). " +
                        $"Detalle: {apiMessage}")
                    { IsSiteAccessDenied = true },
                HttpStatusCode.Forbidden =>
                    new SearchConsoleException(
                        $"Acceso denegado por Search Console. Comprueba que la 'Google Search Console API' esta habilitada " +
                        $"en el proyecto de Google Cloud de la cuenta de servicio. Detalle: {apiMessage}"),
                _ => new SearchConsoleException($"Search Console respondio {(int)response.StatusCode}: {apiMessage}"),
            };
        }
    }

    private async Task<string> GetAccessTokenAsync(ServiceAccountCredentials creds, CancellationToken ct)
    {
        if (TokenCache.TryGetValue(creds.ClientEmail, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
            return cached.Token;

        var assertion = BuildJwtAssertion(creds, DateTimeOffset.UtcNow);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
            ["assertion"] = assertion,
        });

        using var response = await _http.PostAsync(creds.TokenUri, content, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new SearchConsoleException(
                $"Google rechazo las credenciales de la cuenta de servicio ({creds.ClientEmail}): {ReadGoogleError(json)}");

        using var doc = JsonDocument.Parse(json);
        var token = doc.RootElement.TryGetProperty("access_token", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(token))
            throw new SearchConsoleException("Google no devolvio un access token para la cuenta de servicio.");

        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var s) ? s : 3600;
        TokenCache[creds.ClientEmail] = (token!, DateTime.UtcNow.AddSeconds(Math.Max(60, expiresIn - 300)));
        return token!;
    }

    /// <summary>JWT RS256 para el grant jwt-bearer de Google (valido 1 h).</summary>
    public static string BuildJwtAssertion(ServiceAccountCredentials creds, DateTimeOffset now)
    {
        var header = new Dictionary<string, string> { ["alg"] = "RS256", ["typ"] = "JWT" };
        var iat = now.ToUnixTimeSeconds();
        var claims = new Dictionary<string, object>
        {
            ["iss"] = creds.ClientEmail,
            ["scope"] = Scope,
            ["aud"] = creds.TokenUri,
            ["iat"] = iat,
            ["exp"] = iat + 3600,
        };

        var unsigned = Base64Url(JsonSerializer.SerializeToUtf8Bytes(header)) + "." +
                       Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims));

        using var rsa = RSA.Create();
        try
        {
            // El JSON trae la clave con "\n" ya decodificados por el parser.
            rsa.ImportFromPem(creds.PrivateKey);
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            throw new SearchConsoleException("La 'private_key' de la cuenta de servicio no es una clave PEM valida.");
        }

        var signature = rsa.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return unsigned + "." + Base64Url(signature);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // Google devuelve {"error":{"message":...}} en la API y
    // {"error":"invalid_grant","error_description":...} en el endpoint de token.
    private static string ReadGoogleError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var m))
                    return m.GetString() ?? json;
                if (err.ValueKind == JsonValueKind.String)
                {
                    var desc = root.TryGetProperty("error_description", out var d) ? d.GetString() : null;
                    return string.IsNullOrWhiteSpace(desc) ? err.GetString() ?? json : $"{err.GetString()}: {desc}";
                }
            }
        }
        catch (JsonException) { }

        return json.Length > 300 ? json[..300] : json;
    }
}
