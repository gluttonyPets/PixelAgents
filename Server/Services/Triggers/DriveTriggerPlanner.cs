using System.Text;
using Server.Services.GoogleDrive;

namespace Server.Services.Triggers;

/// <summary>
/// Decisiones del trigger "Nuevo archivo en Google Drive", sin E/S: que hacer con
/// el listado de la carpeta segun lo ya visto, y que texto recibe el pipeline.
/// </summary>
public static class DriveTriggerPlanner
{
    /// <summary>Maximo de archivos nuevos que disparan por consulta; el resto espera
    /// a la siguiente, asi una subida masiva no lanza decenas de ejecuciones de golpe.</summary>
    public const int MaxFilesPerCheck = 10;

    /// <summary>Clave de lo vigilado: si cambia (otra carpeta), se rehace la linea base.</summary>
    public static string ConfigKey(string folderId) => $"{TriggerTypes.DriveNewFile}:{folderId}";

    public static string ItemKey(string fileId) => $"drive:{fileId}";

    /// <summary>
    /// Si hace falta linea base (primera consulta o carpeta distinta), no dispara nada:
    /// todo lo listado pasa a "visto". Si no, devuelve los archivos aun no vistos, de
    /// mas antiguo a mas nuevo, como mucho <see cref="MaxFilesPerCheck"/>.
    /// </summary>
    public static DrivePlan Plan(string? storedConfigKey, bool hasBaseline, string configKey,
        IReadOnlyList<DriveFile> listed, IReadOnlySet<string> seenItemKeys)
    {
        if (!hasBaseline || storedConfigKey != configKey)
            return new DrivePlan(true, []);

        var fresh = listed
            .Where(f => !seenItemKeys.Contains(ItemKey(f.Id)))
            .OrderBy(f => f.CreatedTime ?? DateTime.MaxValue)
            .ThenBy(f => f.Name, StringComparer.Ordinal)
            .Take(MaxFilesPerCheck)
            .ToList();
        return new DrivePlan(false, fresh);
    }

    /// <summary>Texto que emite el Trigger: los datos del archivo, para que el pipeline
    /// sepa que ha llegado aunque el archivo no se haya podido adjuntar.</summary>
    public static string BuildUserInput(DriveFile file, string folderName, string? attachmentNote)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Nuevo archivo en Google Drive: {file.Name}");
        sb.AppendLine($"Carpeta: {folderName}");
        if (!string.IsNullOrWhiteSpace(file.MimeType)) sb.AppendLine($"Tipo: {file.MimeType}");
        if (file.Size is { } size) sb.AppendLine($"Tamano: {FormatSize(size)}");
        if (file.CreatedTime is { } created) sb.AppendLine($"Creado: {created:yyyy-MM-dd HH:mm} UTC");
        if (!string.IsNullOrWhiteSpace(file.WebViewLink)) sb.AppendLine($"Enlace: {file.WebViewLink}");
        sb.AppendLine($"ID: {file.Id}");
        if (!string.IsNullOrWhiteSpace(attachmentNote)) sb.AppendLine(attachmentNote);
        return sb.ToString().TrimEnd();
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };
}

public record DrivePlan(bool TakeBaseline, IReadOnlyList<DriveFile> NewFiles);
