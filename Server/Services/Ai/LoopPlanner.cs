using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Server.Models;

namespace Server.Services.Ai;

/// <summary>
/// Orquestador loop: el modo del orquestador en el que el NUMERO de partes no lo
/// fija el usuario sino la duracion de la locucion.
///
/// El problema que resuelve es el de una voz en off de 39 s sobre 15 s de video:
/// cada clip de video dura lo que diga su modulo (Motion 2.0 siempre 5 s, Kling lo
/// que se configure entre 3 y 15), asi que para cubrir una voz hay que pedir
/// tantos clips como quepan en ella. El orquestador mide lo que le llega por su
/// entrada Locucion (audio o video), busca dentro de su rectangulo de loop el
/// modulo de duracion que marca el ritmo (<see cref="FindPacer"/>), calcula
/// N = techo(duracion / segundos por clip) y
/// pide a la IA N partes con un valor por cada salida que el usuario definio, a
/// partir del prompt de cada salida. Los modulos del rectangulo se ejecutan N
/// veces, una por parte (<see cref="LoopFrames"/>).
///
/// Que cuadre el numero no basta: el clip 4 tiene que mostrar lo que se dice en
/// el segundo 15-20. Por eso cada parte va con su tramo de tiempo y, si a la
/// Locucion llega tambien el texto, con lo que suena en ese tramo (exacto si trae
/// marcas de tiempo de una transcripcion, repartido por proporcion si es plano).
/// </summary>
public static class LoopPlanner
{
    /// <summary>Clave de configuracion que activa el modo loop en el orquestador.</summary>
    public const string ModeKey = "loopMode";

    /// <summary>Segundos por parte fijados a mano: mandan sobre los del modulo de video.</summary>
    public const string ClipSecondsKey = "loopClipSeconds";

    /// <summary>
    /// Entrada Locucion: la voz en off (audio o video) cuya duracion decide cuantas
    /// vueltas da el loop. Si ademas llega texto (el de la voz en off o una
    /// transcripcion), se reparte entre los tramos.
    /// </summary>
    public const string NarrationPort = "input_narration";

    /// <summary>
    /// Modulos de duracion: los que producen ficheros con duracion (de momento
    /// audio y video). Uno de ellos, dentro del rectangulo, marca el ritmo del loop.
    /// </summary>
    public static bool IsDurationModule(string moduleType) =>
        moduleType is "Audio" or "Video" or "VideoAssembly";

    /// <summary>
    /// ¿Tiene duracion este fichero? De momento, audio y video: son las salidas
    /// de los modulos de duracion y lo unico de lo que se pueden sacar partes.
    /// </summary>
    public static bool IsTimedMedia(string? contentType) =>
        contentType is not null
        && (contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
            || contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase));

    /// <summary>Tope de partes: el mismo que admite un modulo de imagen.</summary>
    public const int MaxParts = MultiImagePrompt.MaxImages;

    /// <summary>Modulo que marca el ritmo del loop.</summary>
    /// <param name="ClipSeconds">Segundos que cubre cada parte.</param>
    /// <param name="NodeName">Nombre del modulo de duracion, para el log; null si
    /// los segundos se fijaron a mano.</param>
    public sealed record Pacer(double ClipSeconds, string? NodeName);

    /// <summary>Tramo de tiempo que cubre una parte, con lo que se dice en el.</summary>
    public sealed record Window(int Index, double Start, double End, string? Narration);

    /// <summary>Frase de una transcripcion con sus marcas de tiempo.</summary>
    public sealed record TimedSegment(double Start, double End, string Text);

    /// <summary>Partes que devuelve la IA: una por tramo, con un valor por salida.</summary>
    public sealed record Plan(string Summary, List<Dictionary<string, string>> Parts);

    /// <summary>
    /// Margen, en segundos, que se perdona al final de la entrada antes de pedir
    /// una parte mas. Los MP3 llevan unas centesimas de relleno (un audio de 12 s
    /// mide 12.05 s) y una parte entera para cubrirlas seria un clip pagado para
    /// nada: ese resto lo cubre el montaje congelando el ultimo fotograma.
    /// </summary>
    public const double ToleranceSeconds = 0.25;

    /// <summary>Cuantas partes hacen falta para cubrir <paramref name="totalSeconds"/>.</summary>
    /// <remarks>
    /// Se redondea hacia arriba porque quedarse corto es peor que pasarse: el
    /// montaje recorta lo que sobra, pero lo que falta es voz sin imagen.
    /// </remarks>
    public static int CountParts(double totalSeconds, double clipSeconds) =>
        Math.Clamp(CountPartsUncapped(totalSeconds, clipSeconds), 1, MaxParts);

    /// <summary>Igual que <see cref="CountParts"/> pero sin el tope, para avisar de que se supera.</summary>
    public static int CountPartsUncapped(double totalSeconds, double clipSeconds)
    {
        if (clipSeconds <= 0 || totalSeconds <= 0) return 1;
        return Math.Max(1, (int)Math.Ceiling((totalSeconds - ToleranceSeconds) / clipSeconds));
    }

    /// <summary>
    /// El modulo de duracion de dentro del rectangulo que marca el ritmo: el de
    /// video, con los segundos de clip que tenga configurados (Motion 2.0 siempre
    /// 5; Kling, de 3 a 15). Si hay varios, el de clip mas corto, que es el que
    /// necesita mas vueltas para cubrir la locucion. Null si ninguno tiene
    /// duracion de clip.
    /// </summary>
    public static Pacer? FindPacer(LoopFrames.FrameInfo frame) =>
        frame.Members
            .Where(m => IsDurationModule(m.ModuleType))
            .Select(m => (Node: m, Seconds: ClipSecondsOf(m)))
            .Where(x => x.Seconds is > 0)
            .OrderBy(x => x.Seconds)
            .Select(x => new Pacer(x.Seconds!.Value, x.Node.ProjectModule.StepName ?? x.Node.AiModule.Name))
            .FirstOrDefault();

    /// <summary>
    /// Segundos del clip que entregara el modulo: los que pida su configuracion (la
    /// del paso manda sobre la del modulo), encajados en los que admite su modelo.
    /// Es el mismo calculo que hace el provider al llamar, asi que el loop pide
    /// exactamente las vueltas que luego se generan.
    /// </summary>
    private static int? ClipSecondsOf(ModuleNode node) =>
        VideoModelOptions.ResolveDuration(
            node.AiModule.ModelName,
            GraphPipelineExecutor.MergeConfiguration(node.AiModule.Configuration, node.ProjectModule.Configuration));

    /// <summary>
    /// Tramos de tiempo de cada parte. Todos duran lo que un clip salvo el ultimo,
    /// que acaba donde acaba la entrada. Si hay locucion se le asigna a cada tramo
    /// el texto que suena en el.
    /// </summary>
    public static List<Window> BuildWindows(int parts, double clipSeconds, double totalSeconds, string? narration)
    {
        var bounds = new List<(double Start, double End)>(parts);
        for (var i = 0; i < parts; i++)
        {
            var start = i * clipSeconds;
            var end = i == parts - 1 ? Math.Max(totalSeconds, start) : Math.Min((i + 1) * clipSeconds, totalSeconds);
            bounds.Add((start, Math.Max(end, start)));
        }

        var texts = DistributeNarration(narration, bounds, totalSeconds);

        return bounds
            .Select((b, i) => new Window(i + 1, b.Start, b.End, texts?[i]))
            .ToList();
    }

    /// <summary>
    /// Reparte la locucion entre los tramos. Con marcas de tiempo (la salida de
    /// una transcripcion) cada palabra cae en el tramo en el que se pronuncia:
    /// se reparten las palabras de cada frase a lo largo de su duracion. Sin
    /// marcas se reparte por proporcion de palabras, que para una voz sintetica
    /// (ritmo casi constante) se desvia poco del momento real.
    /// </summary>
    private static List<string>? DistributeNarration(
        string? narration, List<(double Start, double End)> bounds, double totalSeconds)
    {
        if (string.IsNullOrWhiteSpace(narration) || bounds.Count == 0) return null;

        var buckets = bounds.Select(_ => new List<string>()).ToList();

        var timed = ParseTimestamps(narration);
        if (timed.Count > 0)
        {
            foreach (var segment in timed)
            {
                var words = SplitWords(segment.Text);
                var span = Math.Max(segment.End - segment.Start, 0);
                for (var k = 0; k < words.Count; k++)
                {
                    var at = segment.Start + span * (k + 0.5) / words.Count;
                    buckets[BucketFor(at, bounds)].Add(words[k]);
                }
            }
        }
        else
        {
            var words = SplitWords(narration);
            var total = totalSeconds > 0 ? totalSeconds : bounds[^1].End;
            for (var k = 0; k < words.Count; k++)
            {
                var at = total * (k + 0.5) / words.Count;
                buckets[BucketFor(at, bounds)].Add(words[k]);
            }
        }

        return buckets.Select(b => string.Join(" ", b)).ToList();
    }

    private static int BucketFor(double at, List<(double Start, double End)> bounds)
    {
        for (var i = 0; i < bounds.Count; i++)
            if (at < bounds[i].End) return i;
        return bounds.Count - 1;
    }

    private static List<string> SplitWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();

    // "[00:04.2 - 00:09.8] texto", "[4.2 - 9.8] texto", "[0:00:04,2 --> 0:00:09,8] texto"
    private static readonly Regex TimedLineRx = new(
        @"^\s*\[\s*(?<a>[\d:.,]+)\s*(?:-->|-|–)\s*(?<b>[\d:.,]+)\s*\]\s*(?<t>.*)$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// Frases con marcas de tiempo del texto, en el formato que escribe el modulo
    /// de transcripcion cuando se le piden marcas (una frase por linea,
    /// <c>[inicio - fin] texto</c>). Lista vacia si el texto no las trae.
    /// </summary>
    public static List<TimedSegment> ParseTimestamps(string? text)
    {
        var result = new List<TimedSegment>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        foreach (Match m in TimedLineRx.Matches(text))
        {
            var start = ParseTime(m.Groups["a"].Value);
            var end = ParseTime(m.Groups["b"].Value);
            var body = m.Groups["t"].Value.Trim();
            if (start is null || end is null || body.Length == 0) continue;
            result.Add(new TimedSegment(start.Value, Math.Max(end.Value, start.Value), body));
        }

        return result;
    }

    /// <summary>"75.5", "01:15.5" o "0:01:15,5" a segundos.</summary>
    public static double? ParseTime(string raw)
    {
        var parts = raw.Replace(',', '.').Split(':');
        var seconds = 0d;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                return null;
            seconds = seconds * 60 + value;
        }

        return seconds;
    }

    /// <summary>Marca de tiempo legible, "01:15.5".</summary>
    public static string FormatTime(double seconds)
    {
        var minutes = (int)(seconds / 60);
        var rest = seconds - minutes * 60;
        return $"{minutes:00}:{rest.ToString("00.0", CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// System prompt del loop: la estructura de cada parte (una clave por salida
    /// configurada) y los tramos que tiene que cubrir, con su texto si lo hay.
    /// </summary>
    public static string BuildSystemPrompt(
        IReadOnlyList<OrchestratorOutput> outputs, IReadOnlyList<Window> windows, string? projectContext)
    {
        var fields = string.Join("\n", outputs.Select(o =>
            $"  - \"{o.OutputKey}\" ({o.Label}): {o.Prompt}"));

        var sb = new StringBuilder();
        foreach (var w in windows)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"  Part {w.Index}: {FormatTime(w.Start)} - {FormatTime(w.End)} ({(w.End - w.Start).ToString("0.#", CultureInfo.InvariantCulture)} s)");
            if (!string.IsNullOrWhiteSpace(w.Narration))
                sb.Append(CultureInfo.InvariantCulture, $" | narration heard in this part: \"{w.Narration}\"");
            sb.Append('\n');
        }

        var example = string.Join(", ", outputs.Select(o => $"\"{o.OutputKey}\": \"...\""));
        var contextSection = string.IsNullOrWhiteSpace(projectContext)
            ? ""
            : $"\n\nPROJECT CONTEXT:\n{projectContext}";
        var hasNarration = windows.Any(w => !string.IsNullOrWhiteSpace(w.Narration));

        return $@"You are a pipeline orchestrator working in LOOP mode. The input has a fixed duration and is split into {windows.Count} consecutive time parts. You must produce EXACTLY {windows.Count} parts, in order, one per time part. Each part fills every field listed below.

FIELDS OF EACH PART:
{fields}

TIME PARTS TO COVER:
{sb.ToString().TrimEnd()}
{contextSection}

You MUST respond ONLY with a JSON object matching this exact schema - no markdown, no explanation, no extra text:

{{
  ""summary"": ""Brief description of what was generated"",
  ""parts"": [
    {{ ""index"": 1, {example} }}
  ]
}}

RULES:
1. ""parts"" must contain EXACTLY {windows.Count} objects, with ""index"" from 1 to {windows.Count}, in order. Not one more, not one less.
2. Every part must contain every field key listed above, with non-empty text. Do not invent other keys.
3. {(hasNarration
            ? "Part N must illustrate what the narration says DURING that time part. Follow the narration heard in each part; do not move content to another part."
            : "Spread the content evenly and in order across the parts, so that part N covers what happens during its time part.")}
4. Each field value is sent on its own to a different module, with NO shared context: make it fully self-contained. Never reference other parts or fields (""same as before"", ""as in part 2"").
5. Keep consecutive parts visually coherent (same style, characters and setting) by repeating the shared details briefly in each part.
6. Plain ASCII only - no emojis, no markdown.";
    }

    /// <summary>
    /// Lee el plan que devuelve la IA. Tolera que envuelva el JSON en un bloque de
    /// codigo y que los valores vengan como numero o booleano. Null si no es JSON
    /// o no trae la lista de partes.
    /// </summary>
    public static Plan? ParsePlan(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var json = OutputSchemaHelper.ExtractJson(raw);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var summary = root.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString() ?? ""
                : "";

            if (!root.TryGetProperty("parts", out var partsElement) || partsElement.ValueKind != JsonValueKind.Array)
                return null;

            var parts = new List<(int Order, Dictionary<string, string> Fields)>();
            var position = 0;
            foreach (var part in partsElement.EnumerateArray())
            {
                position++;
                if (part.ValueKind != JsonValueKind.Object) continue;

                var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var order = position;
                foreach (var prop in part.EnumerateObject())
                {
                    if (prop.NameEquals("index"))
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out var idx))
                            order = idx;
                        continue;
                    }

                    var value = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString(),
                        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => prop.Value.GetRawText(),
                        _ => null,
                    };
                    if (value is not null) fields[prop.Name] = value.Trim();
                }

                parts.Add((order, fields));
            }

            // El indice manda sobre la posicion: si la IA las devuelve desordenadas
            // pero numeradas, la parte 3 sigue siendo la del tramo 3.
            return new Plan(summary, parts.OrderBy(p => p.Order).Select(p => p.Fields).ToList());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Problemas del plan frente a lo que se pidio; vacia si es valido.</summary>
    public static List<string> ValidatePlan(Plan plan, IReadOnlyList<OrchestratorOutput> outputs, int expectedParts)
    {
        var errors = new List<string>();

        if (plan.Parts.Count != expectedParts)
            errors.Add($"se pidieron {expectedParts} partes y llegaron {plan.Parts.Count}");

        for (var i = 0; i < plan.Parts.Count; i++)
        {
            foreach (var output in outputs)
            {
                if (!plan.Parts[i].TryGetValue(output.OutputKey, out var value) || string.IsNullOrWhiteSpace(value))
                    errors.Add($"la parte {i + 1} no trae '{output.OutputKey}' ({output.Label})");
            }
        }

        return errors;
    }

    /// <summary>
    /// Texto que viaja por una salida: sus N valores separados con las marcas
    /// <c>===TOMA n===</c>. Con una sola parte va el texto tal cual, porque una
    /// marca suelta no es un reparto (<see cref="MultiImagePrompt.Split"/> pide dos).
    /// </summary>
    public static string JoinParts(IReadOnlyList<string> values)
    {
        if (values.Count == 1) return values[0];

        var sb = new StringBuilder();
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0) sb.Append("\n\n");
            sb.Append(CultureInfo.InvariantCulture, $"===TOMA {i + 1}===\n{values[i]}");
        }

        return sb.ToString();
    }
}
