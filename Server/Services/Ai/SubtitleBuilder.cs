using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Server.Services.Ai;

/// <summary>
/// Subtitulos estilo TikTok a partir de los tiempos de cada palabra: pocas
/// palabras a la vez, grandes, y la que suena resaltada con un pequeno "pop".
///
/// Aqui no hay IA. Se escribe un fichero ASS (el formato de subtitulos con estilo:
/// fuente, tamano, borde, posicion, color y animacion por palabra) y ffmpeg lo
/// pinta en cada fotograma con libass (<see cref="SubtitleBurner"/>). Ademas sale
/// un SRT normal, sin estilo, por si se quiere subir aparte.
///
/// Para resaltar la palabra activa, cada grupo se escribe una vez por palabra:
/// el mismo texto, en el mismo sitio, con otra palabra en color, y cada copia
/// dura lo que suena su palabra. Asi se ve un grupo fijo con el color avanzando.
/// </summary>
public static class SubtitleBuilder
{
    public const string FontName = "Poppins ExtraBold";

    // Claves de la configuracion del modulo.
    public const string WordsKey = "subtitleWords";
    public const string ColorKey = "subtitleColor";
    public const string SizeKey = "subtitleSize";
    public const string PositionKey = "subtitlePosition";
    public const string UppercaseKey = "subtitleUppercase";
    public const string LanguageKey = "language";

    /// <summary>Un grupo tampoco junta palabras separadas por un silencio mas largo que esto.</summary>
    public const double MaxGapSeconds = 0.7;

    /// <summary>Lo que sigue en pantalla el grupo tras su ultima palabra si nadie lo pisa.</summary>
    public const double HoldSeconds = 0.3;

    /// <summary>Caracteres por grupo para que quepa en dos lineas como mucho.</summary>
    public const int MaxGroupChars = 20;

    /// <summary>Como se ven los subtitulos. Los porcentajes son de la altura del video.</summary>
    public record Style(
        int WordsPerGroup = 3,
        string HighlightColor = "#FFE600",
        double SizePercent = 6.5,
        double PositionPercent = 65,
        bool Uppercase = true)
    {
        public static Style FromConfig(IReadOnlyDictionary<string, object> config)
        {
            var d = new Style();
            return new Style(
                (int)Math.Clamp(ReadDouble(config, WordsKey) ?? d.WordsPerGroup, 1, 5),
                ReadString(config, ColorKey) is { } c && IsHexColor(c) ? c.ToUpperInvariant() : d.HighlightColor,
                Math.Clamp(ReadDouble(config, SizeKey) ?? d.SizePercent, 3, 10),
                Math.Clamp(ReadDouble(config, PositionKey) ?? d.PositionPercent, 15, 90),
                ReadBool(config, UppercaseKey) ?? d.Uppercase);
        }
    }

    /// <summary>
    /// Reparte las palabras en grupos de como mucho <paramref name="maxWords"/>.
    /// Un grupo se corta antes tras un punto, interrogacion..., tras una coma si ya
    /// tiene dos palabras, en un silencio largo o si no cabria en dos lineas: asi
    /// cada grupo se lee de un vistazo y no mezcla dos frases.
    /// </summary>
    public static List<List<ElevenLabsTimings.Word>> Group(IReadOnlyList<ElevenLabsTimings.Word> words, int maxWords)
    {
        var groups = new List<List<ElevenLabsTimings.Word>>();
        var current = new List<ElevenLabsTimings.Word>();

        foreach (var word in words)
        {
            if (current.Count > 0)
            {
                var prev = current[^1].Text;
                var chars = current.Sum(w => w.Text.Length + 1) + word.Text.Length;
                var cut = current.Count >= maxWords
                    || EndsSentence(prev)
                    || (prev.EndsWith(',') && current.Count >= 2)
                    || word.Start - current[^1].End > MaxGapSeconds
                    || chars > MaxGroupChars;
                if (cut)
                {
                    groups.Add(current);
                    current = [];
                }
            }
            current.Add(word);
        }

        if (current.Count > 0) groups.Add(current);
        return groups;
    }

    /// <summary>Fichero ASS para un video de <paramref name="width"/>x<paramref name="height"/>.</summary>
    public static string BuildAss(IReadOnlyList<List<ElevenLabsTimings.Word>> groups, Style style, int width, int height)
    {
        var size = (int)Math.Round(height * style.SizePercent / 100);
        var outline = Math.Max(2, (int)Math.Round(size * 0.09));
        var shadow = Math.Max(1, (int)Math.Round(size * 0.04));
        var margin = (int)Math.Round(width * 0.06);
        var x = width / 2;
        var y = (int)Math.Round(height * style.PositionPercent / 100);
        var highlight = AssColor(style.HighlightColor);

        var sb = new StringBuilder();
        sb.AppendLine("[Script Info]");
        sb.AppendLine("ScriptType: v4.00+");
        sb.AppendLine(Inv($"PlayResX: {width}"));
        sb.AppendLine(Inv($"PlayResY: {height}"));
        sb.AppendLine("WrapStyle: 0");
        sb.AppendLine("ScaledBorderAndShadow: yes");
        sb.AppendLine();
        sb.AppendLine("[V4+ Styles]");
        sb.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, "
            + "Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, "
            + "Alignment, MarginL, MarginR, MarginV, Encoding");
        // Blanco con borde negro y algo de sombra: se lee sobre cualquier fondo.
        // Alineacion 5 (centro): \pos coloca el centro del texto donde se pide.
        sb.AppendLine(Inv($"Style: TikTok,{FontName},{size},&H00FFFFFF,&H00FFFFFF,&H00000000,&H64000000,0,0,0,0,100,100,0,0,1,{outline},{shadow},5,{margin},{margin},0,1"));
        sb.AppendLine();
        sb.AppendLine("[Events]");
        sb.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

        for (var g = 0; g < groups.Count; g++)
        {
            var group = groups[g];
            var groupEnd = GroupEnd(groups, g);
            var texts = group.Select(w => Display(w.Text, style.Uppercase)).ToList();

            for (var i = 0; i < group.Count; i++)
            {
                var start = group[i].Start;
                var end = i == group.Count - 1 ? groupEnd : group[i + 1].Start;
                if (end - start < 0.01) continue;

                var line = new StringBuilder(Inv($"{{\\pos({x},{y})}}"));
                for (var k = 0; k < texts.Count; k++)
                {
                    if (k > 0) line.Append(' ');
                    if (k == i)
                        // La palabra que suena: en color y con un pop (crece y vuelve a su
                        // tamano; si se quedara grande le comeria el espacio a la siguiente).
                        line.Append(Inv($"{{\\1c{highlight}\\t(0,80,\\fscx114\\fscy114)\\t(80,160,\\fscx100\\fscy100)}}"))
                            .Append(texts[k]).Append("{\\r}");
                    else
                        line.Append(texts[k]);
                }

                sb.AppendLine(Inv($"Dialogue: 0,{AssTime(start)},{AssTime(end)},TikTok,,0,0,0,,{line}"));
            }
        }

        return sb.ToString();
    }

    /// <summary>Frase de la vista previa del formulario: una palabra por fotograma.</summary>
    public static readonly string[] PreviewWords = "¿Sabes qué siente tu perro cuando te vas de casa?".Split(' ');

    /// <summary>
    /// ASS de la vista previa: la frase de muestra con una palabra por segundo, asi
    /// el fotograma del segundo k es el momento en que suena la palabra k. Es el
    /// mismo <see cref="BuildAss"/> que el del video, para que se vea igual.
    /// </summary>
    public static string BuildPreviewAss(Style style, int width, int height)
    {
        var words = PreviewWords.Select((w, i) => new ElevenLabsTimings.Word(w, i, i + 1)).ToList();
        return BuildAss(Group(words, style.WordsPerGroup), style, width, height);
    }

    /// <summary>SRT sin estilo: un grupo por subtitulo, con el texto tal cual.</summary>
    public static string BuildSrt(IReadOnlyList<List<ElevenLabsTimings.Word>> groups)
    {
        var sb = new StringBuilder();
        for (var g = 0; g < groups.Count; g++)
        {
            sb.AppendLine((g + 1).ToString(CultureInfo.InvariantCulture));
            sb.AppendLine($"{SrtTime(groups[g][0].Start)} --> {SrtTime(GroupEnd(groups, g))}");
            sb.AppendLine(string.Join(' ', groups[g].Select(w => w.Text)));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>"#RRGGBB" en el orden de color de ASS (&amp;HBBGGRR&amp;).</summary>
    public static string AssColor(string hex)
    {
        var h = hex.TrimStart('#');
        return $"&H{h[4..6]}{h[2..4]}{h[0..2]}&".ToUpperInvariant();
    }

    public static string AssTime(double seconds)
    {
        var cs = (long)Math.Round(Math.Max(0, seconds) * 100);
        return Inv($"{cs / 360000}:{cs / 6000 % 60:00}:{cs / 100 % 60:00}.{cs % 100:00}");
    }

    public static string SrtTime(double seconds)
    {
        var ms = (long)Math.Round(Math.Max(0, seconds) * 1000);
        return Inv($"{ms / 3600000:00}:{ms / 60000 % 60:00}:{ms / 1000 % 60:00},{ms % 1000:000}");
    }

    /// <summary>
    /// Cuando se quita un grupo: un poco despues de su ultima palabra, sin pisar el
    /// siguiente. Si el siguiente empieza casi enseguida se enlazan sin hueco, para
    /// que no parpadee.
    /// </summary>
    private static double GroupEnd(IReadOnlyList<List<ElevenLabsTimings.Word>> groups, int g)
    {
        var end = groups[g][^1].End + HoldSeconds;
        return g + 1 < groups.Count ? Math.Min(end, groups[g + 1][0].Start) : end;
    }

    private static bool EndsSentence(string word) =>
        word.Length > 0 && ".?!…;:".Contains(word[^1]);

    /// <summary>Texto que se pinta: sin llaves ni barras (abririan etiquetas de ASS).</summary>
    private static string Display(string word, bool uppercase)
    {
        var text = word.Replace('{', '(').Replace('}', ')').Replace('\\', '/');
        return uppercase ? text.ToUpperInvariant() : text;
    }

    private static bool IsHexColor(string value) => Regex.IsMatch(value, "^#[0-9A-Fa-f]{6}$");

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    private static double? ReadDouble(IReadOnlyDictionary<string, object> config, string key) =>
        config.TryGetValue(key, out var raw) ? raw switch
        {
            double d => d,
            long l => l,
            int i => i,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) => p,
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Number } je => je.GetDouble(),
            _ => null,
        } : null;

    private static string? ReadString(IReadOnlyDictionary<string, object> config, string key) =>
        config.TryGetValue(key, out var raw) ? raw switch
        {
            string s when !string.IsNullOrWhiteSpace(s) => s.Trim(),
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } je => je.GetString()?.Trim(),
            _ => null,
        } : null;

    private static bool? ReadBool(IReadOnlyDictionary<string, object> config, string key) =>
        config.TryGetValue(key, out var raw) ? raw switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var p) => p,
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.True } => true,
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.False } => false,
            _ => null,
        } : null;
}
