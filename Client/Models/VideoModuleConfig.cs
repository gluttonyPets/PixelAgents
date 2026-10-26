using System.Text.Json;

namespace Client.Models;

/// <summary>
/// Configuracion del modulo de video. Se guarda como JSON en
/// <c>AiModule.Configuration</c>; las claves son las que lee
/// <c>VideoModelOptions</c> en el servidor, que es tambien quien dice que valores
/// admite cada modelo (<see cref="VideoModelOptionsResponse"/>).
/// </summary>
public class VideoModuleConfig
{
    public const string ModuleType = "Video";
    public const string AutoAspect = "auto";

    /// <summary>Todas las claves que puede escribir <see cref="ToJson"/>.</summary>
    public static readonly string[] Keys =
    [
        "videoPrompt", "resolution", "aspectRatio", "duration", "audio",
        "negativePrompt", "seed", "promptEnhance", "frameInterpolation", "videoQuality",
    ];

    public string Prompt { get; set; } = "";
    public int Duration { get; set; }
    public string Resolution { get; set; } = "";
    public string AspectRatio { get; set; } = AutoAspect;

    /// <summary>
    /// Apagado por defecto: el video suele ir debajo de una locucion o de una
    /// musica, y el audio del clip se pisaria con ellas (y en varios modelos
    /// encarece la generacion).
    /// </summary>
    public bool Audio { get; set; }

    public string NegativePrompt { get; set; } = "";

    /// <summary>Texto para poder dejarla vacia (= aleatoria).</summary>
    public string Seed { get; set; } = "";

    public bool PromptEnhance { get; set; }
    public bool FrameInterpolation { get; set; } = true;
    public string Quality { get; set; } = "";

    public static string AspectLabel(string aspect) => aspect switch
    {
        AutoAspect => "Automatico (como la imagen)",
        "16:9" => "16:9 horizontal",
        "9:16" => "9:16 vertical",
        "1:1" => "1:1 cuadrado",
        "4:3" => "4:3 horizontal",
        "3:4" => "3:4 vertical",
        "4:5" => "4:5 vertical",
        "2:3" => "2:3 vertical",
        "21:9" => "21:9 panoramico",
        _ => aspect,
    };

    /// <summary>
    /// Encaja la seleccion en lo que admite el modelo: al abrir un modulo guardado
    /// con otro modelo o con el formato antiguo ("RESOLUTION_480"), y al cambiar de
    /// modelo o de resolucion. Sin opciones (todavia no han llegado) no toca nada.
    /// </summary>
    public void Normalize(VideoModelOptionsResponse? options)
    {
        if (options is null) return;

        var resolution = options.FindResolution(NormalizeResolutionId(Resolution))
            ?? options.FindResolution(options.DefaultResolution)
            ?? options.Resolutions.FirstOrDefault();
        Resolution = resolution?.Id ?? "";

        var durations = options.DurationsFor(Resolution);
        var wanted = Duration > 0 ? Duration : options.DefaultDuration;
        Duration = durations.Length == 0
            ? wanted
            : durations.OrderBy(d => Math.Abs(d - wanted)).ThenBy(d => d).First();

        if (AspectRatio != AutoAspect && resolution?.Aspects.Contains(AspectRatio) != true)
            AspectRatio = AutoAspect;

        if (options.Audio != "optional") Audio = false;

        if (options.Qualities.Length == 0)
            Quality = "";
        else if (!options.Qualities.Any(q => q.Value == Quality))
            Quality = options.DefaultQuality ?? options.Qualities[0].Value;
    }

    /// <summary>
    /// JSON a guardar. Solo lleva las claves que el modelo admite, para que lo
    /// guardado sea lo mismo que se manda. Sin opciones se escribe lo que haya.
    /// </summary>
    public string? ToJson(VideoModelOptionsResponse? options)
    {
        var map = new Dictionary<string, object>();
        if (!string.IsNullOrWhiteSpace(Prompt)) map["videoPrompt"] = Prompt;

        if (!string.IsNullOrWhiteSpace(Resolution)) map["resolution"] = Resolution;
        map["aspectRatio"] = string.IsNullOrWhiteSpace(AspectRatio) ? AutoAspect : AspectRatio;

        if (Duration > 0 && (options is null || options.Durations.Length > 1))
            map["duration"] = Duration;

        if (options is null || options.Audio == "optional") map["audio"] = Audio;

        if ((options is null || options.NegativePromptChars > 0) && !string.IsNullOrWhiteSpace(NegativePrompt))
            map["negativePrompt"] = NegativePrompt.Trim();

        if ((options is null || options.Seed) && long.TryParse(Seed.Trim(), out var seed) && seed >= 0)
            map["seed"] = seed;

        if (options is null || options.PromptEnhance) map["promptEnhance"] = PromptEnhance;
        if (options is null || options.FrameInterpolation) map["frameInterpolation"] = FrameInterpolation;

        if (!string.IsNullOrWhiteSpace(Quality) && (options is null || options.Qualities.Length > 0))
            map["videoQuality"] = Quality;

        return map.Count > 0 ? JsonSerializer.Serialize(map) : null;
    }

    public static VideoModuleConfig FromJson(string? json)
    {
        var cfg = new VideoModuleConfig();
        if (string.IsNullOrWhiteSpace(json)) return cfg;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? Read(string key) =>
                root.TryGetProperty(key, out var v)
                    ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()
                    : null;
            bool? ReadBool(string key) => Read(key) is { } raw && bool.TryParse(raw, out var b) ? b : null;

            cfg.Prompt = Read("videoPrompt") ?? "";
            cfg.Resolution = NormalizeResolutionId(Read("resolution")) ?? "";
            cfg.AspectRatio = Read("aspectRatio") ?? AutoAspect;
            if (double.TryParse(Read("duration"), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var duration))
                cfg.Duration = (int)Math.Round(duration);
            cfg.Audio = ReadBool("audio") ?? false;
            cfg.NegativePrompt = Read("negativePrompt") ?? "";
            cfg.Seed = Read("seed") ?? "";
            cfg.PromptEnhance = ReadBool("promptEnhance") ?? false;
            cfg.FrameInterpolation = ReadBool("frameInterpolation") ?? true;
            cfg.Quality = Read("videoQuality") ?? "";
        }
        catch (JsonException) { }

        return cfg;
    }

    /// <summary>"RESOLUTION_720" (formato de los modulos de Motion 2.0 antiguos) -> "720p".</summary>
    private static string? NormalizeResolutionId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = raw.Trim().ToLowerInvariant().Replace("resolution_", "");
        return value switch
        {
            "2160" or "2160p" => "4k",
            _ when value.All(char.IsDigit) => value + "p",
            _ => value,
        };
    }
}
