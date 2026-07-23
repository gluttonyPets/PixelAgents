using System.Text;
using System.Text.Json;

namespace Server.Services.Ai;

/// <summary>
/// Construye el bloque de "Aprendizaje de ejecuciones pasadas" que se inyecta —como
/// una capa aparte y claramente etiquetada— en el system prompt de cada módulo. Nunca
/// modifica el prompt configurado por el usuario: es un bloque adicional.
///
/// Los aprendizajes activos se guardan en <c>ProjectLearningDoc.ActiveLearningsJson</c>
/// como lista de { "module": "&lt;nombre&gt;|general", "text": "..." }. Aquí se filtran por
/// el módulo que va a ejecutarse (los "general" siempre entran) y se formatean.
/// </summary>
public static class LearningInjection
{
    public const string Header = "=== APRENDIZAJE DE EJECUCIONES PASADAS ===";
    private const string Subtitle =
        "(No forma parte de la configuración del usuario. Guía destilada de abortos anteriores para acercarte a sus gustos.)";

    public sealed record ActiveLearning(string Module, string Text);

    /// <summary>
    /// Devuelve el bloque etiquetado con los aprendizajes que aplican a este módulo, o
    /// null si no hay ninguno. <paramref name="moduleNames"/> son los identificadores por
    /// los que puede referirse el aprendizaje (nombre del AiModule y/o StepName).
    /// </summary>
    public static string? BuildBlock(string? activeLearningsJson, params string?[] moduleNames)
    {
        var learnings = Parse(activeLearningsJson);
        if (learnings.Count == 0) return null;

        var names = moduleNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var relevant = learnings
            .Where(l => IsGeneral(l.Module) || names.Contains(l.Module.Trim()))
            .Select(l => l.Text.Trim())
            .Where(t => t.Length > 0)
            .Distinct()
            .ToList();

        if (relevant.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine(Header);
        sb.AppendLine(Subtitle);
        foreach (var t in relevant)
            sb.AppendLine($"- {t}");
        return sb.ToString().TrimEnd();
    }

    public static List<ActiveLearning> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return new();

            var list = new List<ActiveLearning>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var module = item.TryGetProperty("module", out var m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString() ?? "general" : "general";
                var text = item.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString() ?? "" : "";
                if (!string.IsNullOrWhiteSpace(text))
                    list.Add(new ActiveLearning(string.IsNullOrWhiteSpace(module) ? "general" : module, text));
            }
            return list;
        }
        catch
        {
            return new();
        }
    }

    private static bool IsGeneral(string module) =>
        string.IsNullOrWhiteSpace(module)
        || string.Equals(module, "general", StringComparison.OrdinalIgnoreCase)
        || string.Equals(module, "all", StringComparison.OrdinalIgnoreCase)
        || string.Equals(module, "todos", StringComparison.OrdinalIgnoreCase);
}
