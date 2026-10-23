using System.Globalization;
using System.Text.Json;
using Server.Models;
using Server.Services.Ai.Handlers;

namespace Server.Services.Ai;

/// <summary>
/// Rectangulo de loop: el area de fondo, propia de cada orquestador en modo loop,
/// que marca en el editor que modulos se repiten.
///
/// No es un modulo: se guarda en la configuracion del orquestador
/// (<see cref="FrameKey"/>: posicion y tamano en el lienzo). Los modulos cuya
/// esquina superior izquierda cae dentro forman el CUERPO del loop: el executor
/// ejecuta cada uno N veces, una por parte, y en la vuelta i le da la parte i de
/// lo que le llega (<see cref="SliceInputs"/>). N lo decide el propio orquestador
/// (<see cref="LoopPlanner"/>).
///
/// La salida del rectangulo es una salida del orquestador
/// (<see cref="OutputPort"/>), aunque el editor la dibuje en el borde del
/// rectangulo. No se entrega cuando termina el orquestador sino cuando ha
/// terminado todo el cuerpo (<see cref="ExecutionGraph"/>): junta, en orden de
/// parte, lo que producen los ultimos modulos de dentro (los clips 1..N).
/// </summary>
public static class LoopFrames
{
    /// <summary>Clave de la configuracion del orquestador con el rectangulo:
    /// <c>{"x":..,"y":..,"w":..,"h":..}</c> en coordenadas del lienzo.</summary>
    public const string FrameKey = "loopFrame";

    /// <summary>Salida del loop: puerto del orquestador que el editor pinta en el rectangulo.</summary>
    public const string OutputPort = "output_loop";

    /// <summary>
    /// Vueltas a la vez. Una: las vueltas se hacen una a una, en orden, que es
    /// como se entiende un loop y lo que aguantan las colas de los proveedores de
    /// video. Cada vuelta tiene su propio limite de tiempo.
    /// </summary>
    public const int MaxParallelParts = 1;

    /// <summary>Area del rectangulo en el lienzo.</summary>
    public readonly record struct Rect(double X, double Y, double Width, double Height)
    {
        /// <summary>¿Cae dentro el modulo? Se mira su esquina superior izquierda, igual que en el editor.</summary>
        public bool Contains(double x, double y) =>
            x >= X && x <= X + Width && y >= Y && y <= Y + Height;
    }

    /// <summary>Lo que el grafo sabe de un rectangulo.</summary>
    public sealed class FrameInfo
    {
        /// <summary>Orquestador loop dueno del rectangulo: decide cuantas vueltas hay.</summary>
        public required ModuleNode Driver { get; init; }
        public required Rect Area { get; init; }
        public List<ModuleNode> Members { get; } = [];
        /// <summary>Ultimos modulos del cuerpo (no alimentan a otro de dentro), de arriba a abajo.</summary>
        public List<ModuleNode> Sinks { get; } = [];
        /// <summary>Ya se entrego la salida del loop (para no repetirla al reanudar).</summary>
        public bool Delivered { get; set; }
    }

    /// <summary>Rectangulo guardado en la configuracion del orquestador, o null si no tiene.</summary>
    public static Rect? ReadFrame(string? configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration)) return null;

        try
        {
            using var doc = JsonDocument.Parse(configuration);
            if (!doc.RootElement.TryGetProperty(FrameKey, out var frame)) return null;

            // El editor guarda el objeto; por si llega como texto JSON, se acepta tambien.
            if (frame.ValueKind == JsonValueKind.String)
            {
                using var inner = JsonDocument.Parse(frame.GetString() ?? "");
                return ReadRect(inner.RootElement);
            }

            return ReadRect(frame);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Rect? ReadRect(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var x = ReadNumber(e, "x");
        var y = ReadNumber(e, "y");
        var w = ReadNumber(e, "w");
        var h = ReadNumber(e, "h");
        return x is null || y is null || w is not > 0 || h is not > 0
            ? null
            : new Rect(x.Value, y.Value, w.Value, h.Value);
    }

    private static double? ReadNumber(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var s) => s,
            _ => null,
        };
    }

    /// <summary>
    /// Resuelve los rectangulos del grafo: cada orquestador en modo loop con
    /// rectangulo, los modulos que quedan dentro y los ultimos del cuerpo. Se
    /// llama al construir el grafo, despues de cablear las conexiones.
    /// </summary>
    public static void Wire(ExecutionGraph graph)
    {
        var owners = graph.Nodes.Values
            .Where(n => n.ModuleType == "Orchestrator" && IsLoopMode(n))
            .Select(n => (Node: n, Area: ReadFrame(n.ProjectModule.Configuration)))
            .Where(o => o.Area is not null)
            .ToList();

        foreach (var (owner, area) in owners)
        {
            var info = new FrameInfo { Driver = owner, Area = area!.Value };

            foreach (var node in graph.Nodes.Values)
            {
                if (node == owner || node.ModuleType == "Start") continue;
                // Un modulo pertenece a un solo loop, y un orquestador loop no se
                // repite dentro de otro.
                if (node.LoopFrame is not null || owners.Any(o => o.Node == node)) continue;
                if (!info.Area.Contains(node.ProjectModule.PosX, node.ProjectModule.PosY)) continue;
                info.Members.Add(node);
            }

            var members = info.Members.Select(m => m.ModuleId).ToHashSet();
            info.Sinks.AddRange(info.Members
                .Where(m => !m.OutputPorts.SelectMany(p => p.Connections)
                    .Any(c => members.Contains(c.TargetNode.ModuleId)))
                .OrderBy(m => m.ProjectModule.PosY)
                .ThenBy(m => m.ProjectModule.PosX));

            foreach (var member in info.Members)
                member.LoopFrame = info;
            owner.OwnedLoopFrame = info;
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
    /// Salida del loop una vez terminado el cuerpo: los ficheros de los ultimos
    /// modulos de dentro (ya en orden de parte: p01_, p02_...) y su texto. Null
    /// mientras quede algun modulo de dentro por terminar.
    /// </summary>
    public static PortData? BuildOutput(FrameInfo info)
    {
        if (info.Members.Any(m => m.Status is not (NodeStatus.Completed or NodeStatus.Skipped)))
            return null;

        var outputs = info.Sinks.Select(s => s.Output).Where(o => o is not null).Cast<StepOutput>().ToList();
        var texts = outputs
            .Select(PortDataResolver.BuildFullText)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        return new PortData
        {
            DataType = "any",
            TextContent = texts.Count == 0 ? null : string.Join("\n\n", texts),
            Files = outputs.SelectMany(o => o.Files).ToList(),
            SourcePortId = OutputPort,
        };
    }

    /// <summary>
    /// Vueltas del loop: las partes que planifico el orquestador. Sin ese dato el
    /// cuerpo se ejecuta una vez, como si no hubiera rectangulo.
    /// </summary>
    public static int PartsFor(FrameInfo info)
    {
        var metadata = info.Driver.Output?.Metadata;
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
