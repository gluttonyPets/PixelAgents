using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Server.Services.Triggers;

/// <summary>
/// Configuracion del trigger "Nuevo archivo en Google Drive" (Project.TriggerConfig).
/// </summary>
public sealed partial class DriveTriggerConfig
{
    public const int DefaultPollMinutes = 5;
    public const int MinPollMinutes = 1;
    public const int MaxPollMinutes = 1440;

    /// <summary>API key "GoogleDrive": JSON de la cuenta de servicio con acceso a la carpeta.</summary>
    [JsonPropertyName("apiKeyId")]
    public Guid? ApiKeyId { get; set; }

    /// <summary>URL de la carpeta de Drive tal como la copia el usuario, o su ID.</summary>
    [JsonPropertyName("folder")]
    public string? Folder { get; set; }

    /// <summary>Cada cuantos minutos se consulta la carpeta.</summary>
    [JsonPropertyName("pollMinutes")]
    public int PollMinutes { get; set; } = DefaultPollMinutes;

    /// <summary>Si es true, el archivo se descarga y el Trigger lo emite junto al texto.</summary>
    [JsonPropertyName("downloadFile")]
    public bool DownloadFile { get; set; } = true;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static DriveTriggerConfig Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new DriveTriggerConfig();
        try { return JsonSerializer.Deserialize<DriveTriggerConfig>(json, JsonOptions) ?? new DriveTriggerConfig(); }
        catch (JsonException) { return new DriveTriggerConfig(); }
    }

    public string Serialize() => JsonSerializer.Serialize(this);

    /// <summary>Intervalo de sondeo acotado a un rango razonable.</summary>
    [JsonIgnore]
    public TimeSpan PollInterval => TimeSpan.FromMinutes(Math.Clamp(PollMinutes, MinPollMinutes, MaxPollMinutes));

    /// <summary>ID de la carpeta, extraido de la URL si hace falta; null si no se reconoce.</summary>
    [JsonIgnore]
    public string? FolderId => ExtractFolderId(Folder);

    /// <summary>
    /// Acepta el ID pelado o las URLs que da Drive al abrir o compartir una carpeta:
    /// .../drive/folders/ID, .../drive/u/0/folders/ID, ...?id=ID.
    /// </summary>
    public static string? ExtractFolderId(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        var value = folder.Trim();

        var m = FolderPathRegex().Match(value);
        if (m.Success) return m.Groups[1].Value;

        m = IdQueryRegex().Match(value);
        if (m.Success) return m.Groups[1].Value;

        return BareIdRegex().IsMatch(value) ? value : null;
    }

    /// <summary>Error de configuracion para mostrar al usuario; null si esta completa.</summary>
    public string? Validate()
    {
        if (ApiKeyId is null)
            return "Elige la API key de Google Drive (cuenta de servicio).";
        if (string.IsNullOrWhiteSpace(Folder))
            return "Indica la carpeta de Google Drive que se vigila.";
        if (FolderId is null)
            return "No se reconoce la carpeta: pega la URL de la carpeta de Drive o su ID.";
        return null;
    }

    [GeneratedRegex(@"/folders/([A-Za-z0-9_-]{10,})")]
    private static partial Regex FolderPathRegex();

    [GeneratedRegex(@"[?&]id=([A-Za-z0-9_-]{10,})")]
    private static partial Regex IdQueryRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{10,}$")]
    private static partial Regex BareIdRegex();
}
