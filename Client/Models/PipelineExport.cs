using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Client.Models;

/// <summary>
/// Un nodo del pipeline con todo lo que el canvas ya tiene resuelto (posicion,
/// configuracion efectiva, puertos, reglas y archivos). El exportador no vuelve
/// a mirar el grafo: se limita a ordenar y traducir lo que recibe aqui.
/// </summary>
public sealed record PipelineExportNode(
    ProjectModuleResponse Module,
    AiModuleResponse? CatalogModule,
    double X,
    double Y,
    int SceneCount,
    IReadOnlyDictionary<string, JsonElement> Config,
    IReadOnlyList<PortDefinition> Ports,
    IReadOnlyList<ActiveRule> Rules,
    IReadOnlyList<ModuleFileResponse>? Files);

/// <summary>Todo lo que hace falta para escribir el JSON de un pipeline.</summary>
public sealed record PipelineExportInput(
    Guid ProjectId,
    string? ProjectName,
    string? ProjectDescription,
    string? ProjectContext,
    IReadOnlyList<PipelineExportNode> Nodes,
    IReadOnlyList<ConnectionEntry> Connections,
    IReadOnlyList<RuleResponse>? TenantRules,
    ScheduleResponse? Schedule);

/// <summary>
/// Vuelca un pipeline entero a un JSON pensado para leerse fuera de la app: se
/// lo pasas a otra persona (o a un modelo) y entiende de que pipeline hablamos
/// sin abrir el editor.
///
/// Por eso el fichero no es el formato interno del grafo: las claves van en
/// castellano, cada nodo lleva su nombre visible ademas del id, las conexiones
/// nombran nodo y puerto en vez de GUIDs sueltos, y hay un resumen con el flujo
/// y el orden en que se recorreria el grafo.
/// </summary>
public static class PipelineExporter
{
    /// <summary>Version del formato del fichero. Subirla si cambia la forma del JSON.</summary>
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // Sin escapar acentos ni "ñ": el fichero se lee a ojo.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Claves de configuracion que no se vuelcan tal cual. Hoy las API Keys
    /// viven en su propia entidad y no en el JSON del modulo, pero un export se
    /// comparte por definicion: si alguna vez alguien pega un token en la
    /// configuracion de un nodo, no sale de aqui.
    /// </summary>
    private static readonly string[] SecretHints =
        ["apikey", "api_key", "token", "secret", "password", "passwd", "credential"];

    private const string Redacted = "(oculto por seguridad)";

    /// <summary>Que hace cada tipo de modulo, en una linea.</summary>
    private static readonly Dictionary<string, string> TypeDescriptions = new()
    {
        ["Start"] = "Punto de entrada: inyecta el prompt del usuario en el grafo",
        ["StaticText"] = "Emite un texto fijo escrito en el nodo",
        ["FileUpload"] = "Emite los archivos subidos al nodo",
        ["FileDirectory"] = "Publica un directorio de ficheros y emite su indice",
        ["Text"] = "Genera texto con un modelo de lenguaje",
        ["Image"] = "Genera imagenes con un modelo de imagen",
        ["Audio"] = "Genera audio (voz) a partir de texto",
        ["Transcription"] = "Transcribe audio a texto",
        ["Embeddings"] = "Genera embeddings a partir de texto",
        ["Scene"] = "Agrupa campos y entradas en un objeto de escena",
        ["Orchestrator"] = "Planifica salidas dinamicas y las reparte a los nodos hijo",
        ["Coordinator"] = "Combina y resume los resultados de varias ramas",
        ["Interaction"] = "Pausa el pipeline y espera respuesta humana (Telegram/WhatsApp)",
        ["Checkpoint"] = "Pausa para revision humana antes de continuar",
        ["Conditional"] = "Evalua una condicion y elige por que rama sigue el pipeline",
        ["Design"] = "Genera disenos con un proveedor grafico",
        ["Publish"] = "Publica el contenido en una red social",
        ["ShopifyBlog"] = "Publica un articulo de blog en Shopify",
        ["SubProject"] = "Ejecuta otro proyecto entero como si fuera un modulo",
    };

    /// <summary>JSON del pipeline, listo para guardar o pegar en un chat.</summary>
    public static string ToJson(PipelineExportInput input)
        => JsonSerializer.Serialize(Prune(Build(input)), Options);

    /// <summary>Nombre de fichero sugerido: `pipeline-<proyecto>-<fecha>.json`.</summary>
    public static string SuggestFileName(string? projectName, DateTime nowUtc)
    {
        var slug = Slugify(projectName);
        return $"pipeline-{slug}-{nowUtc:yyyyMMdd-HHmm}.json";
    }

    private static Dictionary<string, object?> Build(PipelineExportInput input)
    {
        var names = BuildDisplayNames(input.Nodes);
        var nodeById = input.Nodes.ToDictionary(n => n.Module.Id);

        // Solo conexiones entre nodos que siguen en el grafo: una conexion
        // huerfana (nodo borrado y pendiente de confirmar) confundiria mas que
        // ayudar a quien lea el fichero.
        var connections = input.Connections
            .Where(c => nodeById.ContainsKey(c.FromModuleId) && nodeById.ContainsKey(c.ToModuleId))
            .ToList();

        var export = new Dictionary<string, object?>
        {
            ["formato"] = "pixelagents.pipeline",
            ["version"] = SchemaVersion,
            ["exportadoUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["comoLeerlo"] =
                "Cada nodo es un modulo del pipeline y cada conexion lleva la salida de un nodo " +
                "a la entrada de otro. 'resumen.flujo' lista las conexiones en texto y " +
                "'resumen.ordenSugerido' el orden en que se recorreria el grafo.",
            ["proyecto"] = BuildProject(input),
            ["resumen"] = BuildSummary(input.Nodes, connections, names),
            ["nodos"] = input.Nodes.Select(n => BuildNode(n, names[n.Module.Id])).ToList(),
            ["conexiones"] = connections.Select(c => BuildConnection(c, nodeById, names)).ToList(),
        };

        if (input.TenantRules is { Count: > 0 })
        {
            export["reglasDelTenant"] = input.TenantRules
                .OrderBy(r => r.SortOrder)
                .Select(r => new Dictionary<string, object?>
                {
                    ["titulo"] = r.Title,
                    ["contenido"] = r.Content,
                    ["activa"] = r.IsActive,
                })
                .ToList();
        }

        if (input.Schedule is not null)
            export["programacion"] = BuildSchedule(input.Schedule);

        return export;
    }

    private static Dictionary<string, object?> BuildProject(PipelineExportInput input) => new()
    {
        ["id"] = input.ProjectId.ToString(),
        ["nombre"] = input.ProjectName,
        ["descripcion"] = Trimmed(input.ProjectDescription),
        ["contexto"] = Trimmed(input.ProjectContext),
    };

    private static Dictionary<string, object?> BuildSummary(
        IReadOnlyList<PipelineExportNode> nodes,
        IReadOnlyList<ConnectionEntry> connections,
        IReadOnlyDictionary<Guid, string> names)
    {
        var withIncoming = connections.Select(c => c.ToModuleId).ToHashSet();
        var withOutgoing = connections.Select(c => c.FromModuleId).ToHashSet();

        var portLabels = nodes.ToDictionary(
            n => n.Module.Id,
            n => n.Ports.ToDictionary(p => p.Id, p => p.Label));

        string PortLabel(Guid moduleId, string portId) =>
            portLabels.TryGetValue(moduleId, out var ports) && ports.TryGetValue(portId, out var label)
                ? label
                : portId;

        return new Dictionary<string, object?>
        {
            ["nodos"] = nodes.Count,
            ["conexiones"] = connections.Count,
            ["nodosPorTipo"] = nodes
                .GroupBy(n => n.Module.ModuleType)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
                .ToDictionary(g => g.Key, g => g.Count()),
            ["nodosDeEntrada"] = nodes.Where(n => !withIncoming.Contains(n.Module.Id))
                .Select(n => names[n.Module.Id]).ToList(),
            ["nodosFinales"] = nodes.Where(n => !withOutgoing.Contains(n.Module.Id))
                .Select(n => names[n.Module.Id]).ToList(),
            ["nodosSueltos"] = nodes
                .Where(n => !withIncoming.Contains(n.Module.Id) && !withOutgoing.Contains(n.Module.Id))
                .Select(n => names[n.Module.Id]).ToList(),
            ["nodosDesactivados"] = nodes
                .Where(n => !n.Module.IsActive || IsSkipped(n))
                .Select(n => names[n.Module.Id]).ToList(),
            ["flujo"] = connections
                .Select(c => $"{names[c.FromModuleId]} ({PortLabel(c.FromModuleId, c.FromPort)})" +
                             $" -> {names[c.ToModuleId]} ({PortLabel(c.ToModuleId, c.ToPort)})")
                .ToList(),
            ["ordenSugerido"] = TopologicalOrder(nodes, connections).Select(id => names[id]).ToList(),
        };
    }

    private static Dictionary<string, object?> BuildNode(PipelineExportNode node, string displayName)
    {
        var pm = node.Module;

        var entry = new Dictionary<string, object?>
        {
            ["id"] = pm.Id.ToString(),
            ["nombre"] = displayName,
            ["tipo"] = pm.ModuleType,
            ["queHace"] = TypeDescriptions.GetValueOrDefault(pm.ModuleType),
            ["proveedor"] = Trimmed(node.CatalogModule?.ProviderType),
            ["modelo"] = Trimmed(pm.ModelName),
            ["activo"] = pm.IsActive,
            ["omitidoEnEjecucion"] = IsSkipped(node),
            ["posicion"] = new Dictionary<string, object?> { ["x"] = node.X, ["y"] = node.Y },
        };

        // El numero de escenas/imagenes solo dice algo cuando el nodo tiene mas
        // de una; con 1 seria ruido en todos los nodos del fichero.
        if (node.SceneCount > 1)
            entry["salidasMultiples"] = node.SceneCount;

        entry["moduloDeCatalogo"] = new Dictionary<string, object?>
        {
            ["id"] = pm.AiModuleId.ToString(),
            ["nombre"] = pm.AiModuleName,
            ["descripcion"] = Trimmed(node.CatalogModule?.Description),
            ["apiKey"] = Trimmed(node.CatalogModule?.ApiKeyName),
        };

        entry["puertos"] = new Dictionary<string, object?>
        {
            ["entradas"] = node.Ports.Where(p => p.IsInput).Select(BuildPort).ToList(),
            ["salidas"] = node.Ports.Where(p => !p.IsInput).Select(BuildPort).ToList(),
        };

        if (node.Config.Count > 0)
            entry["configuracion"] = BuildConfig(node.Config);

        if (pm.OrchestratorOutputs is { Count: > 0 })
        {
            entry["salidasDelOrquestador"] = pm.OrchestratorOutputs
                .OrderBy(o => o.SortOrder)
                .Select(o => new Dictionary<string, object?>
                {
                    ["clave"] = o.OutputKey,
                    ["etiqueta"] = o.Label,
                    ["tipo"] = PortDataType.GetLabel(o.DataType),
                    ["prompt"] = Trimmed(o.Prompt),
                })
                .ToList();
        }

        if (pm.SubProject is not null)
        {
            entry["subProyecto"] = new Dictionary<string, object?>
            {
                ["id"] = pm.SubProject.Id.ToString(),
                ["nombre"] = pm.SubProject.Name,
                ["pasos"] = pm.SubProject.Steps
                    .Select(s => new Dictionary<string, object?> { ["nombre"] = s.Name, ["tipo"] = s.ModuleType })
                    .ToList(),
            };
        }

        if (node.Files is { Count: > 0 })
        {
            entry["archivos"] = node.Files
                .Select(f => new Dictionary<string, object?>
                {
                    ["nombre"] = f.FileName,
                    ["tipo"] = f.ContentType,
                    ["bytes"] = f.FileSize,
                })
                .ToList();
        }

        if (node.Rules.Count > 0)
        {
            entry["reglasActivas"] = node.Rules
                .Select(r => new Dictionary<string, object?>
                {
                    ["categoria"] = r.Category,
                    ["titulo"] = r.Title,
                    ["texto"] = Trimmed(r.Body) ?? r.Description,
                })
                .ToList();
        }

        return entry;
    }

    private static Dictionary<string, object?> BuildPort(PortDefinition port) => new()
    {
        ["id"] = port.Id,
        ["etiqueta"] = port.Label,
        ["tipoDeDato"] = PortDataType.GetLabel(port.DataType),
        ["obligatorio"] = port.IsRequired ? true : null,
    };

    private static Dictionary<string, object?> BuildConnection(
        ConnectionEntry conn,
        IReadOnlyDictionary<Guid, PipelineExportNode> nodeById,
        IReadOnlyDictionary<Guid, string> names)
    {
        string PortLabel(Guid moduleId, string portId) =>
            nodeById.TryGetValue(moduleId, out var node)
                ? node.Ports.FirstOrDefault(p => p.Id == portId)?.Label ?? portId
                : portId;

        return new Dictionary<string, object?>
        {
            ["desde"] = new Dictionary<string, object?>
            {
                ["nodo"] = names[conn.FromModuleId],
                ["nodoId"] = conn.FromModuleId.ToString(),
                ["puerto"] = PortLabel(conn.FromModuleId, conn.FromPort),
                ["puertoId"] = conn.FromPort,
            },
            ["hacia"] = new Dictionary<string, object?>
            {
                ["nodo"] = names[conn.ToModuleId],
                ["nodoId"] = conn.ToModuleId.ToString(),
                ["puerto"] = PortLabel(conn.ToModuleId, conn.ToPort),
                ["puertoId"] = conn.ToPort,
            },
            // Contrato JSON que el nodo destino espera recibir, si se definio.
            ["formato"] = ParseFormat(conn.Format),
        };
    }

    private static Dictionary<string, object?> BuildSchedule(ScheduleResponse s) => new()
    {
        ["activa"] = s.IsEnabled,
        ["cron"] = s.CronExpression,
        ["zonaHoraria"] = s.TimeZone,
        ["promptFijo"] = Trimmed(s.UserInput),
        ["usaColaDePrompts"] = s.UsePromptQueue,
        ["noRepiteTematicas"] = s.UseHistory,
        ["ultimaEjecucionUtc"] = s.LastRunAt?.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        ["proximaEjecucionUtc"] = s.NextRunAt?.ToString("yyyy-MM-ddTHH:mm:ssZ"),
    };

    private static Dictionary<string, object?> BuildConfig(IReadOnlyDictionary<string, JsonElement> config)
    {
        var result = new Dictionary<string, object?>();
        foreach (var (key, value) in config.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            result[key] = LooksSecret(key) ? Redacted : value;
        return result;
    }

    private static bool LooksSecret(string key)
        => SecretHints.Any(hint => key.Contains(hint, StringComparison.OrdinalIgnoreCase));

    private static bool IsSkipped(PipelineExportNode node)
        => node.Config.TryGetValue("skipped", out var v) &&
           (v.ValueKind == JsonValueKind.True ||
            (v.ValueKind == JsonValueKind.String && string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase)));

    /// <summary>El contrato de una conexion se guarda como texto; sale como JSON si lo es.</summary>
    private static object? ParseFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format)) return null;
        try
        {
            return JsonDocument.Parse(format).RootElement.Clone();
        }
        catch
        {
            return format;
        }
    }

    /// <summary>
    /// Nombre con el que se cita cada nodo en el fichero. Dos nodos pueden
    /// llamarse igual, y entonces "Guion -> Imagen" no diria cual de los dos:
    /// a los repetidos se les anade un ordinal.
    /// </summary>
    private static Dictionary<Guid, string> BuildDisplayNames(IReadOnlyList<PipelineExportNode> nodes)
    {
        var names = new Dictionary<Guid, string>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        var counts = nodes
            .GroupBy(n => BaseName(n))
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        foreach (var node in nodes)
        {
            var name = BaseName(node);
            if (counts[name] > 1)
            {
                var ordinal = seen.GetValueOrDefault(name) + 1;
                seen[name] = ordinal;
                name = $"{name} #{ordinal}";
            }
            names[node.Module.Id] = name;
        }

        return names;

        static string BaseName(PipelineExportNode n)
        {
            var raw = n.Module.StepName ?? n.Module.AiModuleName;
            return string.IsNullOrWhiteSpace(raw) ? n.Module.ModuleType : raw.Trim();
        }
    }

    /// <summary>
    /// Orden en que se recorreria el grafo (Kahn). Si hay un ciclo, los nodos
    /// que quedan sin resolver se anaden al final: mejor listarlos que perderlos.
    /// </summary>
    private static List<Guid> TopologicalOrder(
        IReadOnlyList<PipelineExportNode> nodes,
        IReadOnlyList<ConnectionEntry> connections)
    {
        var ids = nodes.Select(n => n.Module.Id).ToList();
        var indegree = ids.ToDictionary(id => id, _ => 0);
        var outgoing = ids.ToDictionary(id => id, _ => new List<Guid>());

        foreach (var c in connections)
        {
            if (c.FromModuleId == c.ToModuleId) continue;
            outgoing[c.FromModuleId].Add(c.ToModuleId);
            indegree[c.ToModuleId]++;
        }

        var ready = new Queue<Guid>(ids.Where(id => indegree[id] == 0));
        var order = new List<Guid>();

        while (ready.Count > 0)
        {
            var id = ready.Dequeue();
            order.Add(id);
            foreach (var next in outgoing[id])
            {
                if (--indegree[next] == 0) ready.Enqueue(next);
            }
        }

        order.AddRange(ids.Where(id => !order.Contains(id)));
        return order;
    }

    /// <summary>
    /// Quita las claves sin valor. `DefaultIgnoreCondition` no llega a los
    /// diccionarios, y un fichero salpicado de `"descripcion": null` se lee
    /// peor: si algo no esta configurado, no aparece.
    /// </summary>
    private static object? Prune(object? value) => value switch
    {
        Dictionary<string, object?> dict => dict
            .Where(kv => kv.Value is not null)
            .ToDictionary(kv => kv.Key, kv => Prune(kv.Value)),
        IEnumerable<Dictionary<string, object?>> list => list
            .Select(item => Prune(item))
            .ToList(),
        _ => value,
    };

    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Slugify(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "proyecto";

        const string accented = "áàäâãéèëêíìïîóòöôõúùüûñç";
        const string plain    = "aaaaaeeeeiiiiooooouuuunc";

        var sb = new StringBuilder();
        foreach (var raw in value.Trim().ToLowerInvariant())
        {
            var idx = accented.IndexOf(raw);
            var c = idx >= 0 ? plain[idx] : raw;
            if (c < 128 && char.IsLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }

        var slug = sb.ToString().Trim('-');
        if (slug.Length > 40) slug = slug[..40].Trim('-');
        return slug.Length == 0 ? "proyecto" : slug;
    }
}
