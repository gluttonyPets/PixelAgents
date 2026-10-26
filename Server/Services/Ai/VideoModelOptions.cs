using System.Globalization;
using System.Text.Json;
using Server.Models;

namespace Server.Services.Ai;

/// <summary>
/// Lo que se puede configurar de cada modelo de video: duraciones, resoluciones
/// con sus formatos, audio y los extras que no todos aceptan (prompt negativo,
/// semilla, mejora de prompt, interpolacion, calidad).
///
/// Es la UNICA fuente de esos limites. El provider construye la peticion con
/// ella, el orquestador loop lee de aqui cuantos segundos dura cada clip y la
/// pantalla de modulos la pide por <c>/api/models/video-options</c> para ofrecer
/// solo lo que el modelo admite. Cada API rechaza con un 400 sin contexto
/// cualquier combinacion que no este en su lista (un 1080p de 10 s en Hailuo 2.3
/// Fast, un 832x480 en Veo), asi que lo que guarde el modulo se encaja siempre
/// en el valor permitido mas cercano antes de llamar.
///
/// Los limites salen de la especificacion OpenAPI v2 de Leonardo
/// (docs.leonardo.ai/reference/creategeneration, revisada el 2026-10-26).
/// </summary>
public static class VideoModelOptions
{
    // Claves de la configuracion del modulo.
    public const string DurationKey = "duration";
    public const string ResolutionKey = "resolution";
    public const string AspectRatioKey = "aspectRatio";
    public const string AudioKey = "audio";
    public const string NegativePromptKey = "negativePrompt";
    public const string SeedKey = "seed";
    public const string PromptEnhanceKey = "promptEnhance";
    public const string FrameInterpolationKey = "frameInterpolation";
    public const string QualityKey = "videoQuality";

    /// <summary>Formato que sigue la proporcion de la imagen de entrada.</summary>
    public const string AutoAspect = "auto";

    public enum AudioSupport
    {
        /// <summary>El modelo no genera audio.</summary>
        None,
        /// <summary>Lo genera si se pide; el modulo lo deja apagado por defecto.</summary>
        Optional,
        /// <summary>Lo genera siempre y no se puede apagar (Hailuo 03).</summary>
        Always,
    }

    /// <summary>Un formato de salida: proporcion y tamano exacto en pixeles.</summary>
    public sealed record SizeOption(string Aspect, int Width, int Height)
    {
        public double Ratio => (double)Width / Height;
    }

    /// <param name="Durations">
    /// Duraciones que admite el modelo EN ESTA resolucion, cuando son menos que las
    /// generales (Veo solo hace 1080p y 4K a 8 s). null = las del modelo.
    /// </param>
    public sealed record ResolutionOption(string Id, string Label, SizeOption[] Sizes, int[]? Durations = null);

    public sealed record ChoiceOption(string Value, string Label);

    /// <param name="SendDuration">
    /// false en los modelos sin parametro de duracion (Motion 2.0 siempre entrega
    /// 5 s): mandarlo seria un campo que la API no conoce.
    /// </param>
    /// <param name="NegativePromptChars">Tope del prompt negativo; 0 = no lo admite.</param>
    /// <param name="SeedMax">Semilla maxima; 0 = no admite semilla.</param>
    /// <param name="ImageAsReference">
    /// true en los modelos sin fotograma inicial (Gemini Omni Flash): la imagen de
    /// entrada va como imagen de referencia, que es lo unico que aceptan.
    /// </param>
    public sealed record Spec(
        string ModelId,
        int[] Durations,
        int DefaultDuration,
        ResolutionOption[] Resolutions,
        string DefaultResolution,
        AudioSupport Audio,
        bool SendDuration = true,
        int NegativePromptChars = 0,
        long SeedMax = 0,
        bool PromptEnhance = false,
        bool FrameInterpolation = false,
        ChoiceOption[]? Qualities = null,
        string? DefaultQuality = null,
        bool ImageAsReference = false);

    /// <summary>Lo que se le manda al proveedor, ya encajado en lo que admite el modelo.</summary>
    public sealed record Resolved(
        int Duration,
        ResolutionOption Resolution,
        SizeOption Size,
        bool? Audio,
        string? NegativePrompt,
        long? Seed,
        bool? PromptEnhance,
        bool? FrameInterpolation,
        string? Quality);

    private static SizeOption S(string aspect, int width, int height) => new(aspect, width, height);

    private static int[] Range(int from, int to, int step = 1) =>
        Enumerable.Range(0, (to - from) / step + 1).Select(i => from + i * step).ToArray();

    private static readonly ResolutionOption[] MotionResolutions =
    [
        new("480p", "480p", [S("16:9", 832, 480), S("9:16", 480, 832), S("4:5", 576, 720), S("2:3", 512, 768)]),
        new("720p", "720p", [S("16:9", 1280, 720), S("9:16", 720, 1280)]),
    ];

    // Veo solo hace 1080p y 4K a 8 s: es una restriccion de Google que la
    // especificacion de Leonardo no repite, y cortar aqui es mas barato que
    // descubrirla con un 400 a mitad de pipeline.
    private static ResolutionOption[] VeoResolutions(bool with4K)
    {
        var list = new List<ResolutionOption>
        {
            new("720p", "720p", [S("16:9", 1280, 720), S("9:16", 720, 1280)]),
            new("1080p", "1080p", [S("16:9", 1920, 1080), S("9:16", 1080, 1920)], [8]),
        };
        if (with4K)
            list.Add(new("4k", "4K", [S("16:9", 3840, 2160), S("9:16", 2160, 3840)], [8]));
        return list.ToArray();
    }

    private static readonly ResolutionOption KlingStandard =
        new("720p", "720p", [S("16:9", 1280, 720), S("1:1", 960, 960), S("9:16", 720, 1280)]);

    private static readonly ResolutionOption KlingPro =
        new("1080p", "1080p", [S("16:9", 1920, 1080), S("1:1", 1440, 1440), S("9:16", 1080, 1920)]);

    private static readonly ResolutionOption[] SeedanceResolutions =
    [
        new("480p", "480p", [S("16:9", 864, 480), S("1:1", 640, 640), S("9:16", 480, 864)]),
        new("720p", "720p", [S("16:9", 1280, 720), S("1:1", 960, 960), S("9:16", 720, 1280)]),
        new("1080p", "1080p", [S("16:9", 1920, 1080), S("1:1", 1440, 1440), S("9:16", 1080, 1920)]),
    ];

    private static readonly ResolutionOption[] Seedance1Resolutions =
    [
        new("480p", "480p", [S("16:9", 864, 480), S("4:3", 736, 544), S("1:1", 640, 640),
            S("3:4", 544, 736), S("9:16", 480, 864), S("21:9", 960, 416)]),
        new("720p", "720p", [S("16:9", 1248, 704), S("4:3", 1120, 832), S("1:1", 960, 960),
            S("3:4", 832, 1120), S("9:16", 704, 1248), S("21:9", 1504, 640)]),
        new("1080p", "1080p", [S("16:9", 1920, 1088), S("4:3", 1664, 1248), S("1:1", 1440, 1440),
            S("3:4", 1248, 1664), S("9:16", 1088, 1920), S("21:9", 2176, 928)]),
    ];

    // Hailuo 2.3 (y su Fast) hace 10 s solo a 768p; a 1080p, unicamente 6 s.
    private static readonly ResolutionOption[] Hailuo23Resolutions =
    [
        new("768p", "768p", [S("16:9", 1376, 768), S("1:1", 768, 768), S("9:16", 768, 1376)]),
        new("1080p", "1080p", [S("16:9", 1920, 1080), S("1:1", 1080, 1080), S("9:16", 1080, 1920)], [6]),
    ];

    private static ResolutionOption[] LtxResolutions(bool fast) =>
    [
        new("1080p", "1080p", [S("16:9", 1920, 1080), S("9:16", 1080, 1920)]),
        // LTX Fast solo pasa de 10 s a 1080p.
        new("1440p", "1440p", [S("16:9", 2560, 1440), S("9:16", 1440, 2560)], fast ? [6, 8, 10] : null),
        new("4k", "4K", [S("16:9", 3840, 2160), S("9:16", 2160, 3840)], fast ? [6, 8, 10] : null),
    ];

    private static readonly Spec[] All =
    [
        new("leonardo-motion-2", [5], 5, MotionResolutions, "480p", AudioSupport.None,
            SendDuration: false, NegativePromptChars: 1000, SeedMax: 2_147_483_637,
            PromptEnhance: true, FrameInterpolation: true),
        new("leonardo-motion-2-fast", [5], 5, MotionResolutions, "480p", AudioSupport.None,
            SendDuration: false, NegativePromptChars: 1000, SeedMax: 4_294_967_295,
            PromptEnhance: true, FrameInterpolation: true),

        new("leonardo-veo-3.1", [4, 6, 8], 8, VeoResolutions(with4K: true), "720p", AudioSupport.Optional,
            NegativePromptChars: 1000, SeedMax: 4_294_967_295),
        new("leonardo-veo-3.1-fast", [4, 6, 8], 8, VeoResolutions(with4K: true), "720p", AudioSupport.Optional,
            NegativePromptChars: 1000, SeedMax: 4_294_967_295),
        new("leonardo-veo-3.1-lite", [4, 6, 8], 8, VeoResolutions(with4K: false), "720p", AudioSupport.Optional,
            NegativePromptChars: 1000, SeedMax: 4_294_967_295),

        new("leonardo-kling-3", Range(3, 15), 5,
            [KlingStandard, KlingPro,
             new("4k", "4K", [S("16:9", 3840, 2160), S("1:1", 2880, 2880), S("9:16", 2160, 3840)])],
            "1080p", AudioSupport.Optional),
        new("leonardo-kling-3-turbo", Range(3, 15), 5, [KlingStandard, KlingPro], "1080p", AudioSupport.Optional),
        new("leonardo-kling-o3", Range(3, 15), 5, [KlingStandard, KlingPro], "1080p", AudioSupport.Optional),

        new("leonardo-seedance-2", Range(4, 15), 8, SeedanceResolutions, "720p", AudioSupport.Optional,
            SeedMax: 4_294_967_295),
        new("leonardo-seedance-2-fast", Range(4, 15), 8, SeedanceResolutions[..2], "720p", AudioSupport.Optional,
            SeedMax: 4_294_967_295),

        new("leonardo-hailuo-03", Range(5, 15), 6,
            [
                new("480p", "480p", [S("16:9", 856, 480), S("4:3", 640, 480), S("1:1", 480, 480),
                    S("3:4", 480, 640), S("9:16", 480, 856), S("21:9", 1120, 480)]),
                new("768p", "768p", [S("16:9", 1376, 768), S("4:3", 1024, 768), S("1:1", 768, 768),
                    S("3:4", 768, 1024), S("9:16", 768, 1376), S("21:9", 1792, 768)]),
                new("2k", "2K", [S("16:9", 2560, 1440), S("4:3", 1920, 1440), S("1:1", 1440, 1440),
                    S("3:4", 1440, 1920), S("9:16", 1440, 2560), S("21:9", 3360, 1440)]),
                new("4k", "4K", [S("16:9", 3840, 2160), S("4:3", 2880, 2160), S("1:1", 2160, 2160),
                    S("3:4", 2160, 2880), S("9:16", 2160, 3840), S("21:9", 5040, 2160)]),
            ],
            "768p", AudioSupport.Always,
            Qualities: [new("STANDARD", "Standard"), new("ACCELERATED", "Accelerated"), new("TURBO", "Turbo")],
            DefaultQuality: "TURBO"),
        new("leonardo-hailuo-2.3-fast", [6, 10], 6, Hailuo23Resolutions, "768p", AudioSupport.None),
        new("leonardo-hailuo-2.3", [6, 10], 6, Hailuo23Resolutions, "768p", AudioSupport.None),

        new("leonardo-wan-3", Range(2, 30), 5,
            [
                new("480p", "480p", [S("16:9", 854, 480), S("4:3", 736, 552), S("1:1", 640, 640),
                    S("3:4", 552, 736), S("9:16", 480, 854)]),
                new("720p", "720p", [S("16:9", 1280, 720), S("4:3", 1104, 828), S("1:1", 960, 960),
                    S("3:4", 828, 1104), S("9:16", 720, 1280)]),
                new("1080p", "1080p", [S("16:9", 1920, 1080), S("4:3", 1656, 1242), S("1:1", 1440, 1440),
                    S("3:4", 1242, 1656), S("9:16", 1080, 1920)]),
            ],
            "480p", AudioSupport.Optional, SeedMax: 2_147_483_647,
            Qualities: [new("STANDARD", "Standard"), new("ACCELERATED", "Accelerated")],
            DefaultQuality: "ACCELERATED"),

        new("leonardo-ltx-2.3-pro", [6, 8, 10], 6, LtxResolutions(fast: false), "1080p", AudioSupport.Optional,
            PromptEnhance: true),
        new("leonardo-ltx-2.3-fast", Range(6, 20, 2), 6, LtxResolutions(fast: true), "1080p", AudioSupport.Optional,
            PromptEnhance: true),

        new("leonardo-grok-imagine-1.5", Range(3, 15), 6,
            [
                new("480p", "480p", [S("16:9", 736, 400), S("1:1", 544, 544), S("9:16", 400, 736)]),
                new("720p", "720p", [S("16:9", 1280, 720), S("1:1", 960, 960), S("9:16", 720, 1280)]),
                new("1080p", "1080p", [S("16:9", 1888, 1072), S("1:1", 1424, 1424), S("9:16", 1072, 1888)]),
            ],
            "480p", AudioSupport.Optional),

        new("leonardo-kling-2.6", [5, 10], 5, [KlingPro], "1080p", AudioSupport.Optional),
        new("leonardo-kling-2.5-turbo-pro", [5, 10], 5, [KlingStandard, KlingPro], "1080p", AudioSupport.None,
            PromptEnhance: true),
        new("leonardo-kling-2.5-turbo-std", [5, 10], 5, [KlingStandard], "720p", AudioSupport.None,
            PromptEnhance: true),
        new("leonardo-kling-o1", Range(3, 10), 5, [KlingPro], "1080p", AudioSupport.None),

        new("leonardo-seedance-2-mini", Range(4, 15), 8, SeedanceResolutions[..2], "720p", AudioSupport.Optional,
            SeedMax: 4_294_967_295),
        new("leonardo-seedance-2.5", Range(4, 30), 8, SeedanceResolutions, "720p", AudioSupport.Optional,
            SeedMax: 4_294_967_295),
        new("leonardo-seedance-1-pro", [4, 6, 8, 10], 8, Seedance1Resolutions, "720p", AudioSupport.None,
            SeedMax: 4_294_967_295, PromptEnhance: true),
        new("leonardo-seedance-1-pro-fast", [4, 6, 8, 10], 8, Seedance1Resolutions, "720p", AudioSupport.None,
            SeedMax: 4_294_967_295, PromptEnhance: true),

        new("leonardo-wan-2.7", Range(2, 10), 5, [KlingStandard, KlingPro], "720p", AudioSupport.None),
        new("leonardo-wan-2.6", [5, 10, 15], 5,
            [
                new("720p", "720p", [S("16:9", 1280, 720), S("4:3", 960, 720), S("1:1", 960, 960),
                    S("3:4", 720, 960), S("9:16", 720, 1280)]),
                new("1080p", "1080p", [S("16:9", 1920, 1080), S("4:3", 1440, 1080), S("1:1", 1440, 1440),
                    S("3:4", 1080, 1440), S("9:16", 1080, 1920)]),
            ],
            "720p", AudioSupport.None, NegativePromptChars: 500, SeedMax: 2_147_483_647),

        new("leonardo-flux-3-video", Range(5, 20), 8,
            [
                new("720p", "720p", [S("16:9", 1280, 720), S("4:3", 1112, 834), S("1:1", 960, 960),
                    S("3:4", 834, 1112), S("9:16", 720, 1280), S("2:1", 1360, 680), S("21:9", 1470, 630)]),
                new("1080p", "1080p", [S("16:9", 1920, 1080), S("4:3", 1440, 1080), S("1:1", 1440, 1440),
                    S("3:4", 1080, 1440), S("9:16", 1080, 1920), S("2:1", 2160, 1080), S("21:9", 2520, 1080)]),
            ],
            "720p", AudioSupport.Optional),

        new("leonardo-happy-horse-1.1", Range(3, 15), 5,
            [
                new("720p", "720p", [S("16:9", 1280, 720), S("4:3", 1108, 832), S("1:1", 960, 960),
                    S("3:4", 832, 1108), S("9:16", 720, 1280)]),
                new("1080p", "1080p", [S("16:9", 1920, 1080), S("4:3", 1662, 1248), S("1:1", 1440, 1440),
                    S("3:4", 1248, 1662), S("9:16", 1080, 1920)]),
            ],
            "1080p", AudioSupport.Optional, SeedMax: 2_147_483_647, PromptEnhance: true),
        // Happy Horse 1.0 no tiene motion_has_audio (su audio_setting es para editar
        // un video existente), asi que aqui no se le pide audio.
        new("leonardo-happy-horse", Range(3, 15), 5,
            [
                new("720p", "720p", [S("16:9", 1280, 720), S("4:3", 960, 720), S("1:1", 720, 720),
                    S("3:4", 720, 960), S("9:16", 720, 1280)]),
                new("1080p", "1080p", [S("16:9", 1920, 1080), S("4:3", 1440, 1080), S("1:1", 1080, 1080),
                    S("3:4", 1080, 1440), S("9:16", 1080, 1920)]),
            ],
            "1080p", AudioSupport.None, SeedMax: 2_147_483_647, PromptEnhance: true),

        new("leonardo-gemini-omni-1.1-flash", Range(3, 10), 5,
            [
                new("360p", "360p", [S("16:9", 640, 360), S("9:16", 360, 640)]),
                new("720p", "720p", [S("16:9", 1280, 720), S("9:16", 720, 1280)]),
                new("1080p", "1080p", [S("16:9", 1920, 1080), S("9:16", 1080, 1920)]),
                new("4k", "4K", [S("16:9", 3840, 2160), S("9:16", 2160, 3840)]),
            ],
            "720p", AudioSupport.Optional),
        new("leonardo-gemini-omni-flash", Range(3, 10), 5,
            [new("720p", "720p", [S("16:9", 1280, 720), S("9:16", 720, 1280)])],
            "720p", AudioSupport.None, ImageAsReference: true),
    ];

    private static readonly Dictionary<string, Spec> ById =
        All.ToDictionary(s => s.ModelId, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<Spec> Specs => All;

    /// <summary>Opciones de un modelo de video, o null si no es uno de ellos.</summary>
    public static Spec? Find(string? modelId) =>
        !string.IsNullOrWhiteSpace(modelId) && ById.TryGetValue(modelId, out var spec) ? spec : null;

    /// <summary>Las opciones de todos los modelos, tal y como las pinta la pantalla de modulos.</summary>
    public static List<VideoModelOptionsResponse> Describe() =>
        All.Select(spec => new VideoModelOptionsResponse(
                spec.ModelId,
                spec.Durations,
                spec.DefaultDuration,
                spec.Resolutions
                    .Select(r =>
                    {
                        var rate = PricingCatalog.GetVideoRate(spec.ModelId, r.Id);
                        return new VideoResolutionResponse(r.Id, r.Label, r.Sizes.Select(x => x.Aspect).ToArray(), r.Durations,
                            rate?.PerSecond, rate?.PerSecondWithAudio);
                    })
                    .ToArray(),
                spec.DefaultResolution,
                spec.Audio.ToString().ToLowerInvariant(),
                spec.NegativePromptChars,
                spec.SeedMax > 0,
                spec.PromptEnhance,
                spec.FrameInterpolation,
                (spec.Qualities ?? []).Select(q => new VideoChoiceResponse(q.Value, q.Label)).ToArray(),
                spec.DefaultQuality))
            .ToList();

    /// <summary>
    /// Segundos del clip que entregara el modulo con esta configuracion: la duracion
    /// elegida, encajada en las que admite el modelo a la resolucion elegida. null si
    /// el modelo no es de video.
    /// </summary>
    public static int? ResolveDuration(string? modelId, IReadOnlyDictionary<string, object>? config)
    {
        var spec = Find(modelId);
        if (spec is null) return null;
        config ??= new Dictionary<string, object>();
        return ResolveDuration(spec, ResolveResolution(spec, config), config);
    }

    /// <summary>
    /// Toda la configuracion del modulo encajada en lo que admite el modelo.
    /// <paramref name="imageSize"/> es el tamano de la imagen de entrada: con el
    /// formato en "auto" se elige el de proporcion mas parecida.
    /// </summary>
    public static Resolved Resolve(Spec spec, IReadOnlyDictionary<string, object> config, (int Width, int Height)? imageSize)
    {
        var resolution = ResolveResolution(spec, config);
        var duration = ResolveDuration(spec, resolution, config);

        bool? audio = spec.Audio switch
        {
            AudioSupport.None => null,
            AudioSupport.Always => true,
            _ => ReadBool(config, AudioKey) ?? false,
        };

        string? negative = null;
        if (spec.NegativePromptChars > 0
            && ReadString(config, NegativePromptKey) is { Length: > 0 } rawNegative)
            negative = rawNegative.Length > spec.NegativePromptChars
                ? rawNegative[..spec.NegativePromptChars]
                : rawNegative;

        long? seed = null;
        if (spec.SeedMax > 0 && ReadLong(config, SeedKey) is { } rawSeed && rawSeed >= 0 && rawSeed <= spec.SeedMax)
            seed = rawSeed;

        string? quality = null;
        if (spec.Qualities is { Length: > 0 } qualities)
        {
            var requested = ReadString(config, QualityKey);
            quality = qualities.FirstOrDefault(q => q.Value.Equals(requested, StringComparison.OrdinalIgnoreCase))?.Value
                ?? spec.DefaultQuality
                ?? qualities[0].Value;
        }

        return new Resolved(
            duration,
            resolution,
            ResolveSize(resolution, ReadString(config, AspectRatioKey), imageSize),
            audio,
            negative,
            seed,
            spec.PromptEnhance ? ReadBool(config, PromptEnhanceKey) ?? false : null,
            spec.FrameInterpolation ? ReadBool(config, FrameInterpolationKey) ?? true : null,
            quality);
    }

    /// <summary>
    /// Resolucion elegida. Acepta "720p" y tambien el "RESOLUTION_720" que guardaban
    /// los modulos de Motion 2.0 antes de que la resolucion fuese por modelo.
    /// </summary>
    public static ResolutionOption ResolveResolution(Spec spec, IReadOnlyDictionary<string, object> config)
    {
        var requested = NormalizeResolutionId(ReadString(config, ResolutionKey));
        return spec.Resolutions.FirstOrDefault(r => r.Id.Equals(requested, StringComparison.OrdinalIgnoreCase))
            ?? spec.Resolutions.FirstOrDefault(r => r.Id.Equals(spec.DefaultResolution, StringComparison.OrdinalIgnoreCase))
            ?? spec.Resolutions[0];
    }

    /// <summary>Duraciones que admite el modelo a esa resolucion.</summary>
    public static int[] AllowedDurations(Spec spec, ResolutionOption resolution) =>
        resolution.Durations ?? spec.Durations;

    private static int ResolveDuration(Spec spec, ResolutionOption resolution, IReadOnlyDictionary<string, object> config)
    {
        var allowed = AllowedDurations(spec, resolution);
        var requested = ReadLong(config, DurationKey) is { } raw ? (int)Math.Clamp(raw, 0, 3600) : spec.DefaultDuration;
        return Nearest(allowed, requested);
    }

    /// <summary>
    /// Formato de salida. Con una proporcion concreta se usa esa (o la mas parecida
    /// si esta resolucion no la tiene); en "auto", la mas parecida a la imagen de
    /// entrada, y si no se puede leer su tamano, la primera de la lista (16:9).
    /// </summary>
    public static SizeOption ResolveSize(ResolutionOption resolution, string? aspect, (int Width, int Height)? imageSize)
    {
        var sizes = resolution.Sizes;
        if (!string.IsNullOrWhiteSpace(aspect) && !aspect.Equals(AutoAspect, StringComparison.OrdinalIgnoreCase))
        {
            var exact = sizes.FirstOrDefault(s => s.Aspect == aspect.Trim());
            if (exact is not null) return exact;
            if (ParseAspect(aspect) is { } wanted) return Closest(sizes, wanted);
        }

        if (imageSize is { Width: > 0, Height: > 0 } img)
            return Closest(sizes, (double)img.Width / img.Height);

        return sizes[0];
    }

    private static SizeOption Closest(SizeOption[] sizes, double ratio) =>
        sizes.OrderBy(s => Math.Abs(Math.Log(s.Ratio / ratio))).First();

    private static int Nearest(int[] allowed, int requested) =>
        allowed.OrderBy(d => Math.Abs(d - requested)).ThenBy(d => d).First();

    private static double? ParseAspect(string aspect)
    {
        var parts = aspect.Split(':');
        return parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var h)
            && w > 0 && h > 0
            ? w / h
            : null;
    }

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

    private static object? Unwrap(object? value) => value is JsonElement je
        ? je.ValueKind switch
        {
            JsonValueKind.String => je.GetString(),
            JsonValueKind.Number => je.TryGetInt64(out var l) ? (object)l : je.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        }
        : value;

    private static string? ReadString(IReadOnlyDictionary<string, object> config, string key) =>
        config.TryGetValue(key, out var raw) ? Unwrap(raw)?.ToString()?.Trim() : null;

    private static bool? ReadBool(IReadOnlyDictionary<string, object> config, string key) =>
        config.TryGetValue(key, out var raw)
            ? Unwrap(raw) switch
            {
                bool b => b,
                string s when bool.TryParse(s, out var parsed) => parsed,
                _ => null,
            }
            : null;

    private static long? ReadLong(IReadOnlyDictionary<string, object> config, string key) =>
        config.TryGetValue(key, out var raw)
            ? Unwrap(raw) switch
            {
                long l => l,
                int i => i,
                double d => (long)Math.Round(d),
                decimal m => (long)Math.Round(m),
                string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => null,
            }
            : null;
}
