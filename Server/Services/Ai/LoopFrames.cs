using System.Globalization;
using System.Text.Json;
using Server.Models;
using Server.Services.Ai.Handlers;

namespace Server.Services.Ai;

/// <summary>
/// Rectangulo de loop ("Bucle"): el nodo que marca en el editor que modulos
/// repite el orquestador loop.
///
/// El rectangulo no ejecuta nada por si mismo. Los modulos cuya posicion cae
/// dentro de el forman el CUERPO del loop: el executor ejecuta cada uno N veces,
/// una por parte, y en la vuelta i le da la parte i de lo que le llega (ver
/// <see cref="SliceInputs"/>). N lo decide el orquestador loop que alimenta el
/// cuerpo desde fuera (<see cref="LoopPlanner"/>). La unica salida del
/// rectangulo junta, en orden, lo que producen los ultimos modulos del cuerpo.
///
/// Que modulo esta dentro se decide por geometria, con la esquina superior
/// izquierda del nodo, igual que lo pinta el editor: mover un nodo dentro o fuera
/// del rectangulo lo mete o lo saca del loop, sin mas configuracion.
/// </summary>
public static class LoopFrames
{
    public const string ModuleType = "LoopFrame";

    /// <summary>Puerto interno por el que el rectangulo recibe la salida del cuerpo.
    /// No existe en el editor: lo cablea <see cref="Wire"/> al construir el grafo.</summary>
    public const string BodyPort = "input_body";

    /// <summary>La unica salida del rectangulo.</summary>
    public const string OutputPort = "output";

    public const string WidthKey = "frameWidth";
    public const string HeightKey = "frameHeight";
    public const double DefaultWidth = 620;
    public const double DefaultHeight = 260;

    /// <summary>Vueltas en paralelo como maximo: las colas de video no dan para mas.</summary>
    public const int MaxParallelParts = 3;

    /// <summary>Lo que el grafo sabe de un rectangulo.</summary>
    public sealed class FrameInfo
    {
        public required ModuleNode Frame { get; init; }
        public List<ModuleNode> Members { get; } = [];
        /// <summary>Orquestador loop que decide cuantas vueltas; null si no hay ninguno conectado.</summary>
        public ModuleNode? Driver { get; set; }
    }

    /// <summary>Tamano del rectangulo guardado en su configuracion.</summary>
    public static (double Width, double Height) ReadSize(string? configuration)
    {
        var width = DefaultWidth;
        var height = DefaultHeight;
        if (string.IsNullOrWhiteSpace(configuration)) return (width, height);

        try
        {
            using var doc = JsonDocument.Parse(configuration);
            width = ReadNumber(doc.RootElement, WidthKey) ?? width;
            height = ReadNumber(doc.RootElement, HeightKey) ?? height;
        }
        catch (JsonException) { /* config malformada: tamano por defecto */ }

        return (width, height);
    }

    private static double? ReadNumber(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var d) && d > 0 => d,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var s) && s > 0 => s,
            _ => null,
        };
    }

    /// <summary>¿Cae el modulo dentro del rectangulo? Se mira su esquina superior izquierda.</summary>
    public static bool Contains(ProjectModule frame, ProjectModule module)
    {
        if (module.Id == frame.Id) return false;
        var (width, height) = ReadSize(frame.Configuration);
        return module.PosX >= frame.PosX && module.PosX <= frame.PosX + width
            && module.PosY >= frame.PosY && module.PosY <= frame.PosY + height;
    }

    /// <summary>
    /// Resuelve los rectangulos del grafo: sus miembros, el orquestador que los
    /// controla y una conexion interna desde cada ultimo modulo del cuerpo hasta
    /// el rectangulo, para que este espere a que el cuerpo acabe y reciba lo que
    /// produce. Se llama al construir el grafo, despues de cablear las conexiones.
    /// </summary>
    public static void Wire(ExecutionGraph graph)
    {
        var frames = graph.Nodes.Values.Where(n => n.ModuleType == ModuleType).ToList();

        foreach (var frameNode in frames)
        {
            var info = new FrameInfo { Frame = frameNode };

            foreach (var node in graph.Nodes.Values)
            {
                if (node.ModuleType is ModuleType or "Start") continue;
                // Un modulo pertenece a un solo rectangulo: el primero que lo reclama.
                if (node.LoopFrame is not null) continue;
                if (!Contains(frameNode.ProjectModule, node.ProjectModule)) continue;
                info.Members.Add(node);
            }

            var members = info.Members.Select(m => m.ModuleId).ToHashSet();

            // El orquestador loop de fuera que alimenta el cuerpo.
            info.Driver = graph.Nodes.Values
                .Where(n => n.ModuleType == "Orchestrator" && !members.Contains(n.ModuleId) && IsLoopMode(n))
                .FirstOrDefault(n => n.OutputPorts.SelectMany(p => p.Connections)
                    .Any(c => members.Contains(c.TargetNode.ModuleId)));

            foreach (var member in info.Members)
                member.LoopFrame = info;
            frameNode.OwnedLoopFrame = info;

            // Los ultimos del cuerpo: no alimentan a otro miembro. En orden de
            // arriba a abajo, que es como se leen en el rectangulo.
            var sinks = info.Members
                .Where(m => !m.OutputPorts.SelectMany(p => p.Connections)
                    .Any(c => members.Contains(c.TargetNode.ModuleId)))
                .OrderBy(m => m.ProjectModule.PosY)
                .ThenBy(m => m.ProjectModule.PosX)
                .ToList();

            if (sinks.Count == 0) continue;

            var bodyPort = new InputPort { PortId = BodyPort, DataType = "any", IsRequired = true, AllowMultiple = true };
            frameNode.InputPorts.Add(bodyPort);

            foreach (var sink in sinks)
            {
                var output = sink.OutputPorts.First();
                var conn = new PortConnection
                {
                    ConnectionId = Guid.Empty,
                    SourceNode = sink,
                    SourcePortId = output.PortId,
                    TargetNode = frameNode,
                    TargetPortId = BodyPort,
                };
                output.Connections.Add(conn);
                bodyPort.Connections.Add(conn);
            }
        }
    }

    private static bool IsLoopMode(ModuleNode node) =>
        ReadBool(node.ProjectModule.Configuration, LoopPlanner.ModeKey)
        ?? ReadBool(node.AiModule.Configuration, LoopPlanner.ModeKey)
        ?? false;

    private static bool? ReadBool(string? configuration, string key)
    {
        if (string.IsNullOrWhiteSpace(configuration)) return null;
        try
        {
            using var doc = JsonDocument.Parse(configuration);
            if (!doc.RootElement.TryGetProperty(key, out var value)) return null;
            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String when bool.TryParse(value.GetString(), out var b) => b,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Vueltas del loop: las partes que planifico el orquestador. Sin orquestador
    /// (o sin su dato) el cuerpo se ejecuta una vez, como si no hubiera rectangulo.
    /// </summary>
    public static int PartsFor(FrameInfo info)
    {
        var metadata = info.Driver?.Output?.Metadata;
        if (metadata is null || !metadata.TryGetValue("parts", out var raw)) return 1;

        var parts = raw switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            JsonElement { ValueKind: JsonValueKind.Number } je when je.TryGetInt32(out var n) => n,
            _ => int.TryParse(raw?.ToString(), out var p) ? p : 1,
        };

        return Math.Clamp(parts, 1, LoopPlanner.MaxParts);
    }

    /// <summary>
    /// Entradas de la vuelta <paramref name="part"/> (base 0) de <paramref name="parts"/>.
    /// Lo que llega repartido en tantas partes como vueltas se corta: un texto con
    /// sus N tomas da la toma i (mas el contexto comun), una lista de N ficheros da
    /// el fichero i. Lo que no llega repartido (un estilo, una referencia, un audio
    /// suelto) se pasa entero a todas las vueltas.
    /// </summary>
    public static Dictionary<string, List<PortData>> SliceInputs(
        Dictionary<string, List<PortData>> inputs, int part, int parts)
    {
        if (parts <= 1) return inputs;

        return inputs.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Select(d => SliceOne(d, part, parts)).ToList());
    }

    private static PortData SliceOne(PortData data, int part, int parts)
    {
        var files = data.Files is { Count: > 0 } && data.Files.Count == parts
            ? [data.Files[part]]
            : data.Files;

        var text = data.TextContent;
        if (!string.IsNullOrWhiteSpace(text))
        {
            var split = MultiImagePrompt.Split([text]);
            if (split.Segments.Count == parts)
                text = string.IsNullOrWhiteSpace(split.Common)
                    ? split.Segments[part]
                    : $"{split.Common}\n\n{split.Segments[part]}";
        }

        return new PortData
        {
            DataType = data.DataType,
            TextContent = text,
            Files = files,
            FullOutput = data.FullOutput,
            SourcePortId = data.SourcePortId,
        };
    }

    /// <summary>
    /// Junta los resultados de las N vueltas en el resultado del modulo. Si falla
    /// una vuelta falla el modulo diciendo cual: entregar las otras correria las
    /// partes siguientes una posicion y descuadraria los clips con la voz. Los
    /// ficheros se renombran con el numero de parte (p01_, p02_...) para que no
    /// choquen y conserven el orden, y el texto de cada vuelta viaja como una
    /// toma, para que el siguiente modulo del cuerpo lo vuelva a cortar igual.
    /// </summary>
    public static ModuleResult MergeResults(IReadOnlyList<ModuleResult> results)
    {
        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            if (r.Status == ModuleResultStatus.Failed)
                return ModuleResult.Failed($"Parte {i + 1}/{results.Count}: {r.Error}");
            if (r.Status == ModuleResultStatus.Paused)
                return ModuleResult.Failed(
                    $"Parte {i + 1}/{results.Count}: un modulo que se pausa (revision, interaccion) no puede ir dentro de un loop");
        }

        if (results.Count == 1) return results[0];

        var produced = new List<ProducedFile>();
        var files = new List<OutputFile>();
        var texts = new List<string>();

        for (var i = 0; i < results.Count; i++)
        {
            var prefix = $"p{(i + 1).ToString("00", CultureInfo.InvariantCulture)}_";
            var output = results[i].Output;
            var producedNames = results[i].ProducedFiles.Select(f => f.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var p in results[i].ProducedFiles)
                produced.Add(new ProducedFile { Data = p.Data, FileName = prefix + p.FileName, ContentType = p.ContentType });

            foreach (var f in output?.Files ?? [])
            {
                // Solo se renombran los ficheros nuevos: los que ya existian (con
                // FileId) se resuelven por su id y cambiarles el nombre los perderia.
                var renamed = f.FileId == Guid.Empty && producedNames.Contains(f.FileName);
                files.Add(new OutputFile
                {
                    FileId = f.FileId,
                    FileName = renamed ? prefix + f.FileName : f.FileName,
                    ContentType = f.ContentType,
                    FileSize = f.FileSize,
                    RevisedPrompt = f.RevisedPrompt,
                });
            }

            texts.Add(output is null ? "" : PortDataResolver.BuildFullText(output));
        }

        var first = results[0].Output;
        var metadata = first?.Metadata is null
            ? new Dictionary<string, object>()
            : new Dictionary<string, object>(first.Metadata);
        metadata["loopParts"] = results.Count;

        var merged = new StepOutput
        {
            Type = first?.Type ?? "text",
            Title = first?.Title,
            Summary = first?.Summary,
            Content = texts.All(string.IsNullOrWhiteSpace) ? first?.Content : LoopPlanner.JoinParts(texts),
            Files = files,
            Metadata = metadata,
        };

        // Una rama solo se corta si se corta en todas las vueltas.
        var blocked = results
            .Select(r => r.BlockedOutputPorts.ToHashSet(StringComparer.OrdinalIgnoreCase))
            .Aggregate((a, b) => { a.IntersectWith(b); return a; });

        return new ModuleResult
        {
            Status = ModuleResultStatus.Completed,
            Output = merged,
            Cost = results.Sum(r => r.Cost),
            ProducedFiles = produced,
            BlockedOutputPorts = blocked.ToList(),
        };
    }
}
