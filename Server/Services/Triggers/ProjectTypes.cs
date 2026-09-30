namespace Server.Services.Triggers;

/// <summary>
/// Tipos de pipeline. Un pipeline "Trigger" no espera a que alguien lo lance: lo
/// dispara un evento externo (ver <see cref="TriggerTypes"/>). Su modulo de entrada
/// sigue siendo el modulo "Start"; solo cambia su nombre visible a "Trigger".
/// </summary>
public static class ProjectTypes
{
    public const string Normal = "Normal";
    public const string Trigger = "Trigger";

    /// <summary>Nombre del modulo de entrada segun el tipo de pipeline.</summary>
    public const string StartStepName = "Inicio";
    public const string TriggerStepName = "Trigger";

    public static bool IsValid(string? type) => type is Normal or Trigger;

    /// <summary>Tipo normalizado: cualquier valor desconocido o vacio es Normal.</summary>
    public static string Normalize(string? type) => type == Trigger ? Trigger : Normal;

    public static string EntryStepName(string projectType) =>
        projectType == Trigger ? TriggerStepName : StartStepName;

    /// <summary>
    /// Nombre que debe tener el modulo de entrada tras cambiar el tipo del pipeline.
    /// Solo se renombra si conserva el nombre por defecto del otro tipo: un nombre
    /// puesto a mano por el usuario se respeta.
    /// </summary>
    public static string? RenameEntryStep(string? currentStepName, string newProjectType)
    {
        var target = EntryStepName(newProjectType);
        if (string.IsNullOrWhiteSpace(currentStepName)
            || currentStepName == StartStepName
            || currentStepName == TriggerStepName)
            return target;
        return currentStepName;
    }
}

/// <summary>Catalogo de eventos que pueden disparar un pipeline de tipo Trigger.</summary>
public static class TriggerTypes
{
    /// <summary>Aparece un archivo nuevo en una carpeta de Google Drive.</summary>
    public const string DriveNewFile = "DriveNewFile";

    public static readonly IReadOnlyList<TriggerTypeInfo> All =
    [
        new(DriveNewFile, "Nuevo archivo en Google Drive",
            "Se ejecuta cada vez que aparece un archivo nuevo en una carpeta de Drive."),
    ];

    public static bool IsValid(string? type) => type is not null && All.Any(t => t.Id == type);
}

public record TriggerTypeInfo(string Id, string Label, string Description);
