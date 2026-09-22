using System.Text;

namespace Client.Models;

/// <summary>
/// Todo lo que se vuelca en el log de una ejecucion: el detalle (con sus pasos
/// y archivos), los logs que guardo el servidor y el feedback que dejo la gente
/// sobre esa ejecucion.
/// </summary>
public sealed record ExecutionLogExportInput(
    string? ProjectName,
    ExecutionDetailResponse Execution,
    IReadOnlyList<ExecutionLogResponse>? Logs,
    IReadOnlyList<ExecutionFeedbackResponse>? Feedback);

/// <summary>
/// Escribe en texto plano todo lo que paso en una ejecucion del pipeline: la
/// cabecera con estado, tiempos y coste, el prompt y las variables, cada paso
/// con su entrada, su salida, su error y sus archivos, los logs del servidor y
/// el feedback recibido.
///
/// Es para leerlo fuera de la app (adjuntarlo a una incidencia o pegarselo a un
/// modelo), asi que va en castellano, con las horas en local y sin recortar
/// entradas ni salidas: quien abre el fichero quiere la ejecucion entera.
/// </summary>
public static class ExecutionLogExporter
{
    /// <summary>Nombre de fichero sugerido: `log-ejecucion-&lt;proyecto&gt;-&lt;fecha&gt;-&lt;id&gt;.txt`.</summary>
    public static string SuggestFileName(string? projectName, Guid executionId, DateTime createdAt)
    {
        var slug = PipelineExporter.Slugify(projectName);
        var shortId = executionId.ToString("N")[..8];
        return $"log-ejecucion-{slug}-{createdAt.ToLocalTime():yyyyMMdd-HHmm}-{shortId}.txt";
    }

    /// <summary>El log entero de la ejecucion, listo para guardar como fichero.</summary>
    public static string ToText(ExecutionLogExportInput input)
    {
        var exec = input.Execution;
        var sb = new StringBuilder();

        sb.AppendLine("================================================================");
        sb.AppendLine(" PixelAgents - Log de ejecucion");
        sb.AppendLine("================================================================");
        Field(sb, "Proyecto", string.IsNullOrWhiteSpace(input.ProjectName) ? "(sin nombre)" : input.ProjectName);
        Field(sb, "Proyecto id", exec.ProjectId.ToString());
        Field(sb, "Ejecucion", exec.Id.ToString());
        Field(sb, "Estado", $"{StatusLabel(exec.Status)} ({exec.Status})");
        Field(sb, "Inicio", Moment(exec.CreatedAt));
        Field(sb, "Fin", exec.CompletedAt.HasValue ? Moment(exec.CompletedAt.Value) : "(sin terminar)");
        Field(sb, "Duracion", Duration(exec.CreatedAt, exec.CompletedAt));
        Field(sb, "Coste estimado", exec.TotalEstimatedCost > 0 ? $"~${exec.TotalEstimatedCost:F4}" : "-");
        if (!string.IsNullOrWhiteSpace(exec.WorkspacePath)) Field(sb, "Workspace", exec.WorkspacePath);
        Field(sb, "Exportado", Moment(DateTime.UtcNow));

        Section(sb, "PROMPT");
        sb.AppendLine(string.IsNullOrWhiteSpace(exec.UserInput) ? "(sin prompt)" : exec.UserInput.Trim());

        Section(sb, "VARIABLES");
        if (exec.Variables is { Count: > 0 })
        {
            foreach (var kv in exec.Variables.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"- {kv.Key}: {kv.Value}");
        }
        else sb.AppendLine("(ninguna)");

        var steps = exec.Steps.OrderBy(s => s.CreatedAt).ToList();
        Section(sb, $"PASOS ({steps.Count})");
        if (steps.Count == 0) sb.AppendLine("(ningun paso registrado)");
        var index = 0;
        foreach (var step in steps)
        {
            index++;
            sb.AppendLine();
            sb.AppendLine($"[{index}] {step.ModuleName} ({step.ModuleType})");
            Field(sb, "  Estado", $"{StatusLabel(step.Status)} ({step.Status})");
            Field(sb, "  Inicio", Moment(step.CreatedAt));
            Field(sb, "  Fin", step.CompletedAt.HasValue ? Moment(step.CompletedAt.Value) : "(sin terminar)");
            Field(sb, "  Duracion", Duration(step.CreatedAt, step.CompletedAt));
            if (step.EstimatedCost > 0) Field(sb, "  Coste", $"${step.EstimatedCost:F4}");
            Field(sb, "  Modulo id", step.ProjectModuleId.ToString());

            Block(sb, "  Entrada", step.InputData);
            Block(sb, "  Salida", step.OutputData);
            Block(sb, "  Error", step.ErrorMessage);

            if (step.Files is { Count: > 0 })
            {
                sb.AppendLine("  Archivos:");
                foreach (var f in step.Files.OrderBy(f => f.CreatedAt))
                    sb.AppendLine($"    - [{f.Direction}] {f.FileName} ({f.ContentType}, {FileSize(f.FileSize)}) id={f.Id}");
            }
        }

        IReadOnlyList<ExecutionLogResponse> logs = input.Logs ?? [];
        Section(sb, $"LOGS ({logs.Count})");
        if (logs.Count == 0) sb.AppendLine("(sin logs)");
        foreach (var log in logs.OrderBy(l => l.Timestamp))
        {
            var module = string.IsNullOrWhiteSpace(log.ModuleName) ? "" : $" [{log.ModuleName}]";
            sb.AppendLine($"{log.Timestamp.ToLocalTime():HH:mm:ss} [{log.Level}]{module} {Flatten(log.Message)}");
        }

        IReadOnlyList<ExecutionFeedbackResponse> feedback = input.Feedback ?? [];
        Section(sb, $"FEEDBACK ({feedback.Count})");
        if (feedback.Count == 0) sb.AppendLine("(sin feedback)");
        foreach (var fb in feedback.OrderBy(f => f.CreatedAt))
        {
            var rating = fb.Rating == "positive" ? "positivo" : "negativo";
            sb.AppendLine($"{Moment(fb.CreatedAt)} [{rating}] via {fb.Source}");
            if (!string.IsNullOrWhiteSpace(fb.Comment)) sb.AppendLine($"  {fb.Comment.Trim()}");
        }

        return sb.ToString();
    }

    private static void Section(StringBuilder sb, string title)
    {
        sb.AppendLine();
        sb.AppendLine(title);
        sb.AppendLine(new string('-', title.Length));
    }

    private static void Field(StringBuilder sb, string label, string? value)
        => sb.AppendLine($"{label}: {value}");

    /// <summary>Un texto largo (entrada, salida o error) indentado bajo su etiqueta.</summary>
    private static void Block(StringBuilder sb, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        sb.AppendLine($"{label}:");
        var indent = new string(' ', label.Length - label.TrimStart().Length + 4);
        foreach (var line in value.TrimEnd().Replace("\r\n", "\n").Split('\n'))
            sb.AppendLine(indent + line);
    }

    /// <summary>Los logs son de una linea: un salto dentro del mensaje rompe el formato.</summary>
    private static string Flatten(string? message)
        => string.IsNullOrEmpty(message) ? "" : message.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');

    private static string Moment(DateTime value)
        => value.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");

    private static string Duration(DateTime from, DateTime? to)
    {
        if (!to.HasValue) return "-";
        var span = to.Value - from;
        if (span < TimeSpan.Zero) return "-";
        if (span.TotalMinutes < 1) return $"{span.TotalSeconds:F1}s";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m {span.Seconds}s";
        return $"{(int)span.TotalHours}h {span.Minutes}m {span.Seconds}s";
    }

    private static string FileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }

    private static string StatusLabel(string status) => status switch
    {
        "Completed"            => "Completada",
        "Failed"               => "Fallida",
        "Running"              => "En curso",
        "Pending"              => "Pendiente",
        "Cancelled"            => "Cancelada",
        "Skipped"              => "Omitida",
        "WaitingForCheckpoint" => "En checkpoint",
        "WaitingForReview"     => "En revision",
        "WaitingForInput"      => "Esperando",
        _                      => status
    };
}
