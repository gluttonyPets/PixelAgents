namespace Server.Services.Ai
{
    /// <summary>
    /// Catálogo de precios por modelo/proveedor para estimar costes de ejecución.
    /// Los precios se expresan en USD.
    /// </summary>
    public static class PricingCatalog
    {
        // ── Text models: price per 1M tokens ──
        private static readonly Dictionary<string, (decimal InputPerMTok, decimal OutputPerMTok)> TextPrices =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // OpenAI — revisado contra developers.openai.com/api/docs/pricing el 2026-10-04.
                ["gpt-6-astra"]      = (10.00m, 50.00m),
                ["gpt-6.1-sol"]      = (2.00m,  10.00m),
                ["gpt-6-sol"]        = (2.00m,  10.00m),
                ["gpt-6-luna"]       = (0.10m,   0.50m),
                // gpt-5.6-sol tiene precio promocional ($4/$20 en vez de $5/$30) hasta el
                // 2026-11-21. Pasada esa fecha hay que volver a revisar la tarifa.
                ["gpt-5.6"]          = (4.00m,  20.00m),   // alias: resuelve a Sol y factura como Sol
                ["gpt-5.6-sol"]      = (4.00m,  20.00m),
                ["gpt-5.6-terra"]    = (2.00m,  12.00m),
                ["gpt-5.6-luna"]     = (0.20m,   1.20m),
                ["gpt-5.5"]          = (5.00m,  30.00m),
                ["gpt-5.5-pro"]      = (30.00m, 180.00m),
                ["gpt-5.4"]          = (2.50m,  15.00m),
                ["gpt-5.4-pro"]      = (30.00m, 180.00m),
                ["gpt-5.4-mini"]     = (0.75m,   4.50m),
                ["gpt-5.4-nano"]     = (0.20m,   1.25m),
                ["gpt-5.3"]          = (1.75m,  14.00m),
                ["gpt-5.2"]          = (1.75m,  14.00m),
                ["gpt-5.1"]          = (1.25m,  10.00m),
                ["gpt-5-pro"]        = (15.00m, 120.00m),
                ["gpt-5"]            = (1.25m,  10.00m),
                ["gpt-5-mini"]       = (0.25m,   2.00m),
                ["gpt-5-nano"]       = (0.05m,   0.40m),
                ["gpt-4o"]           = (2.50m,  10.00m),
                ["gpt-4o-2024-05-13"]= (5.00m,  15.00m),   // el snapshot antiguo sí cuesta 5/15
                ["gpt-4o-mini"]      = (0.15m,   0.60m),
                ["gpt-4.1"]          = (2.00m,   8.00m),
                ["gpt-4.1-mini"]     = (0.40m,   1.60m),
                ["gpt-4.1-nano"]     = (0.10m,   0.40m),
                ["o3"]               = (2.00m,   8.00m),
                ["o3-pro"]           = (20.00m, 80.00m),
                ["o3-mini"]          = (1.10m,   4.40m),
                ["o4-mini"]          = (1.10m,   4.40m),
                ["o1"]               = (15.00m, 60.00m),
                ["o1-pro"]           = (150.00m, 600.00m),

                // Anthropic (las claves cortas hacen match por prefijo con los IDs con fecha,
                // p.ej. "claude-opus-4-5-20251124" empieza por "claude-opus-4-5")
                ["claude-opus-4-6"]             = (5.00m, 25.00m),
                ["claude-sonnet-4-6"]           = (3.00m, 15.00m),
                ["claude-opus-4-5"]             = (5.00m, 25.00m),
                ["claude-sonnet-4-5"]           = (3.00m, 15.00m),
                ["claude-haiku-4-5"]            = (1.00m,  5.00m),
                ["claude-opus-4-1"]             = (15.00m, 75.00m),
                ["claude-sonnet-4-20250514"]    = (3.00m, 15.00m),
                ["claude-opus-4-20250514"]      = (15.00m, 75.00m),
                ["claude-3-5-haiku-20241022"]   = (0.80m,  4.00m),

                // Google Gemini (Gemini Developer API pricing)
                ["gemini-2.5-flash"]   = (0.30m,  2.50m),  // output includes thinking tokens
                ["gemini-2.5-pro"]     = (1.25m, 10.00m),
                ["gemini-2.0-flash-lite"] = (0.075m, 0.30m),  // antes que 2.0-flash para el match por prefijo
                ["gemini-2.0-flash"]   = (0.10m,  0.40m),
                ["gemini-1.5-pro"]     = (1.25m,  5.00m),
                ["gemini-1.5-flash"]   = (0.075m, 0.30m),

                // xAI Grok
                ["grok-4-0709"]                 = (3.00m, 15.00m),
                ["grok-4-1-fast-reasoning"]     = (0.20m,  0.50m),
                ["grok-4-1-fast-non-reasoning"] = (0.20m,  0.50m),
                ["grok-code-fast-1"]            = (0.20m,  0.50m),
                ["grok-3"]             = (3.00m, 15.00m),
                ["grok-3-fast"]        = (5.00m, 25.00m),
                ["grok-3-mini"]        = (0.30m,  0.50m),
                ["grok-3-mini-fast"]   = (0.60m,  4.00m),
                ["grok-2"]             = (2.00m, 10.00m),
                ["grok-2-vision"]      = (2.00m, 10.00m),
            };

        /// <summary>
        /// Tarifa de los modelos que no se facturan ni por tokens de texto ni por
        /// imagen. Cada uno lleva su unidad porque no comparten ninguna: los TTS
        /// clasicos cobran por caracter, las transcripciones por minuto de audio y
        /// los embeddings por token. Mostrarlos todos como "$/1M tokens" seria
        /// mentir en dos de los tres casos.
        /// </summary>
        public record AuxiliaryRate(decimal Amount, string Unit, string? Note = null);

        private const string ElevenLabsPlanNote =
            "Precio de pago por uso; con plan mensual los caracteres incluidos salen mas baratos.";

        private const string LeonardoVideoNote =
            "El coste real de cada ejecucion es el que devuelve la API de Leonardo.";

        private const string LeonardoVideoEstimate =
            "Estimacion orientativa: Leonardo no publica el precio por modelo y cobra en creditos "
            + "segun resolucion, duracion y audio. " + LeonardoVideoNote;

        private static readonly Dictionary<string, AuxiliaryRate> AuxiliaryPrices =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // Embeddings
                ["text-embedding-3-small"] = new(0.02m, "1M tokens"),
                ["text-embedding-3-large"] = new(0.13m, "1M tokens"),
                ["text-embedding-ada-002"] = new(0.10m, "1M tokens"),

                // Audio (TTS)
                ["tts-1"]    = new(15.00m, "1M caracteres"),
                ["tts-1-hd"] = new(30.00m, "1M caracteres"),
                ["gpt-4o-mini-tts"] = new(12.00m, "1M tokens de audio",
                    "Mas $0,60 por 1M de tokens de texto de entrada."),

                // ElevenLabs: tarifa de pago por uso de la API (elevenlabs.io/pricing/api,
                // revisada el 2026-10-25: $0,08 / $0,04 por 1K caracteres). Con un plan
                // mensual los caracteres incluidos salen mas baratos, asi que el coste
                // anotado es un techo, no lo que se paga.
                ["eleven_v4"]              = new(80.00m, "1M caracteres", ElevenLabsPlanNote),
                ["eleven_v4_turbo"]        = new(40.00m, "1M caracteres", ElevenLabsPlanNote),
                ["eleven_v3"]              = new(80.00m, "1M caracteres", ElevenLabsPlanNote),
                ["eleven_multilingual_v2"] = new(80.00m, "1M caracteres", ElevenLabsPlanNote),
                ["eleven_flash_v2_5"]      = new(40.00m, "1M caracteres", ElevenLabsPlanNote),

                // Transcripcion (STT). OpenAI publica el precio por minuto de audio,
                // que es la unidad en la que se piensa al transcribir, aunque por
                // debajo tambien tenga equivalencia en tokens.
                ["gpt-transcribe"]         = new(0.0045m, "minuto"),
                ["whisper-1"]              = new(0.006m, "minuto"),
                ["gpt-4o-transcribe"]      = new(0.006m, "minuto"),
                ["gpt-4o-mini-transcribe"] = new(0.003m, "minuto"),

                // Subtitulos: ElevenLabs cobra 0,22 $ por hora de audio, igual en Scribe
                // que en la alineacion de un texto (elevenlabs.io/pricing/api, 2026-10).
                ["scribe_v2"]              = new(0.0037m, "minuto"),

                // Video: no va aqui sino en VideoRates, porque su tarifa depende de la
                // resolucion y del audio. GetAuxiliaryRate la deriva de alli.
            };

        /// <summary>
        /// Tarifa de un modelo de embeddings, audio o transcripcion. null cuando el
        /// modelo no se factura por uso —Canva se paga por suscripcion, no por
        /// llamada, y la API de Search Console es gratuita— o cuando no hay tarifa
        /// conocida.
        /// </summary>
        public static AuxiliaryRate? GetAuxiliaryRate(string modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName)) return null;

            // Un modelo de video tiene una tarifa por resolucion; la pantalla de modelos
            // muestra la de su resolucion por defecto y sin audio, que es lo que se
            // genera si no se toca nada.
            if (GetVideoRate(modelName, null) is { } video)
                return new AuxiliaryRate(video.PerSecond, "segundo",
                    modelName.StartsWith("leonardo-motion-2", StringComparison.OrdinalIgnoreCase)
                        ? "Leonardo factura en creditos (~25 por clip de 5 s a 480p). " + LeonardoVideoNote
                        : LeonardoVideoEstimate);

            return AuxiliaryPrices.TryGetValue(modelName, out var rate) ? rate : null;
        }

        // ── Video: precio por segundo segun resolucion y audio ──

        /// <summary>
        /// Tarifa por segundo de un modelo de video a una resolucion. Con audio puede
        /// costar mas (Veo lo dobla); <paramref name="PerSecondWithAudio"/> null = el
        /// audio no cambia el precio (o el modelo no lo genera).
        /// </summary>
        public record VideoRate(decimal PerSecond, decimal? PerSecondWithAudio = null)
        {
            public decimal For(bool audio) => audio && PerSecondWithAudio is { } withAudio ? withAudio : PerSecond;
        }

        private static VideoRate R(decimal perSecond, decimal? withAudio = null) => new(perSecond, withAudio);

        /// <summary>
        /// Tarifa por segundo de cada modelo de video en cada una de sus resoluciones
        /// (las de <see cref="VideoModelOptions"/>). Leonardo cobra en creditos y no
        /// publica cuantos gasta cada modelo: solo los da la calculadora de su web, y
        /// la de su API cubre Motion 2.0 y poco mas. Estas cifras son una ESTIMACION en
        /// dolares: Motion 2.0 a ~$0,003/credito; el resto, por la tarifa de cada
        /// proveedor en su propia API cuando la publica y, si no, por la de su gama
        /// (revisadas el 2026-10-26). Sirven para comparar modelos y para el simulador
        /// del alta de modulos; el coste que se apunta en cada ejecucion es el real,
        /// el que devuelve la API.
        /// </summary>
        private static readonly Dictionary<string, Dictionary<string, VideoRate>> VideoRates =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["leonardo-motion-2"]         = new() { ["480p"] = R(0.015m), ["720p"] = R(0.025m) },
                ["leonardo-motion-2-fast"]    = new() { ["480p"] = R(0.010m), ["720p"] = R(0.017m) },

                ["leonardo-veo-3.1"]          = new() { ["720p"] = R(0.20m, 0.40m), ["1080p"] = R(0.20m, 0.40m), ["4k"] = R(0.40m, 0.60m) },
                ["leonardo-veo-3.1-fast"]     = new() { ["720p"] = R(0.10m, 0.15m), ["1080p"] = R(0.10m, 0.15m), ["4k"] = R(0.30m, 0.35m) },
                ["leonardo-veo-3.1-lite"]     = new() { ["720p"] = R(0.05m), ["1080p"] = R(0.08m) },

                ["leonardo-kling-3"]          = new() { ["720p"] = R(0.084m, 0.126m), ["1080p"] = R(0.112m, 0.168m), ["4k"] = R(0.224m, 0.336m) },
                ["leonardo-kling-3-turbo"]    = new() { ["720p"] = R(0.056m, 0.084m), ["1080p"] = R(0.07m, 0.105m) },
                ["leonardo-kling-o3"]         = new() { ["720p"] = R(0.084m, 0.126m), ["1080p"] = R(0.112m, 0.168m) },
                ["leonardo-kling-2.6"]        = new() { ["1080p"] = R(0.07m, 0.14m) },
                ["leonardo-kling-2.5-turbo-pro"] = new() { ["720p"] = R(0.05m), ["1080p"] = R(0.07m) },
                ["leonardo-kling-2.5-turbo-std"] = new() { ["720p"] = R(0.042m) },
                ["leonardo-kling-o1"]         = new() { ["1080p"] = R(0.112m) },

                ["leonardo-seedance-2.5"]     = new() { ["480p"] = R(0.15m), ["720p"] = R(0.30m), ["1080p"] = R(0.65m) },
                ["leonardo-seedance-2"]       = new() { ["480p"] = R(0.12m), ["720p"] = R(0.25m), ["1080p"] = R(0.55m) },
                ["leonardo-seedance-2-fast"]  = new() { ["480p"] = R(0.05m), ["720p"] = R(0.10m) },
                ["leonardo-seedance-2-mini"]  = new() { ["480p"] = R(0.03m), ["720p"] = R(0.06m) },
                ["leonardo-seedance-1-pro"]   = new() { ["480p"] = R(0.025m), ["720p"] = R(0.055m), ["1080p"] = R(0.124m) },
                ["leonardo-seedance-1-pro-fast"] = new() { ["480p"] = R(0.01m), ["720p"] = R(0.022m), ["1080p"] = R(0.05m) },

                ["leonardo-hailuo-03"]        = new() { ["480p"] = R(0.04m), ["768p"] = R(0.08m), ["2k"] = R(0.13m), ["4k"] = R(0.26m) },
                ["leonardo-hailuo-2.3"]       = new() { ["768p"] = R(0.045m), ["1080p"] = R(0.08m) },
                ["leonardo-hailuo-2.3-fast"]  = new() { ["768p"] = R(0.032m), ["1080p"] = R(0.055m) },

                ["leonardo-wan-3"]            = new() { ["480p"] = R(0.05m), ["720p"] = R(0.10m), ["1080p"] = R(0.15m) },
                ["leonardo-wan-2.7"]          = new() { ["720p"] = R(0.10m), ["1080p"] = R(0.15m) },
                ["leonardo-wan-2.6"]          = new() { ["720p"] = R(0.10m), ["1080p"] = R(0.15m) },

                ["leonardo-ltx-2.3-pro"]      = new() { ["1080p"] = R(0.06m), ["1440p"] = R(0.12m), ["4k"] = R(0.24m) },
                ["leonardo-ltx-2.3-fast"]     = new() { ["1080p"] = R(0.04m), ["1440p"] = R(0.08m), ["4k"] = R(0.16m) },

                ["leonardo-flux-3-video"]     = new() { ["720p"] = R(0.08m), ["1080p"] = R(0.12m) },
                ["leonardo-grok-imagine-1.5"] = new() { ["480p"] = R(0.05m), ["720p"] = R(0.07m), ["1080p"] = R(0.10m) },
                ["leonardo-happy-horse-1.1"]  = new() { ["720p"] = R(0.08m), ["1080p"] = R(0.12m) },
                ["leonardo-happy-horse"]      = new() { ["720p"] = R(0.06m), ["1080p"] = R(0.10m) },
                ["leonardo-gemini-omni-1.1-flash"] = new() { ["360p"] = R(0.05m), ["720p"] = R(0.10m), ["1080p"] = R(0.15m), ["4k"] = R(0.30m) },
                ["leonardo-gemini-omni-flash"] = new() { ["720p"] = R(0.10m) },
            };

        /// <summary>
        /// Tarifa de un modelo de video a una resolucion (null = la suya por defecto).
        /// null si el modelo no es de video o no tiene tarifa para esa resolucion.
        /// </summary>
        public static VideoRate? GetVideoRate(string? modelName, string? resolutionId)
        {
            if (string.IsNullOrWhiteSpace(modelName) || !VideoRates.TryGetValue(modelName, out var byResolution))
                return null;

            resolutionId ??= VideoModelOptions.Find(modelName)?.DefaultResolution;
            return resolutionId is not null && byResolution.TryGetValue(resolutionId, out var rate) ? rate : null;
        }

        // ── Image models: fixed price per image ──
        private static readonly Dictionary<string, decimal> ImageFixedPrices =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // DALL-E 3 (standard 1024x1024 default)
                ["dall-e-3"]   = 0.040m,
                // DALL-E 2 (1024x1024 default)
                ["dall-e-2"]   = 0.020m,
                // gpt-image-1 (medium quality 1024x1024 approx)
                ["gpt-image-1"] = 0.042m,
                // gpt-image-1-mini (medium quality 1024x1024 approx)
                ["gpt-image-1-mini"] = 0.015m,
                // gpt-image-1.5 / gpt-image-2 / gpt-image-2.5-* (medium quality 1024x1024 approx)
                ["gpt-image-1.5"] = 0.034m,
                ["gpt-image-2"] = 0.032m,
                ["gpt-image-2.5-sunburst"] = 0.032m,
                ["gpt-image-2.5-flare"] = 0.032m,

                // Gemini Imagen (via Gemini native image gen)
                ["gemini-2.5-flash-image"]         = 0.039m,   // ~1290 tokens de salida por imagen
                ["gemini-3.1-flash-image-preview"] = 0.039m,
                ["gemini-3-pro-image-preview"]     = 0.134m,   // Gemini 3 Pro Image (1K/2K)
                ["gemini-2.0-flash"]          = 0.039m,
                ["gemini-2.0-flash-preview-image-generation"] = 0.039m,
                ["imagen-3.0-generate-002"]   = 0.040m,

                // xAI Grok (Imagine image generation)
                ["grok-imagine-image"]     = 0.070m,
                ["grok-imagine-image-pro"] = 0.070m,

                // Leonardo AI (approximate: ~7 credits at ~$0.003/credit)
                ["leonardo-phoenix"]       = 0.021m,
                ["leonardo-phoenix-0.9"]   = 0.021m,
                ["leonardo-flux-dev"]      = 0.021m,
                ["leonardo-flux-schnell"]  = 0.021m,
            };

        // ── DALL-E 3 detailed pricing by quality+size ──
        private static readonly Dictionary<string, decimal> DallE3Detailed =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["standard-1024x1024"] = 0.040m,
                ["standard-1024x1792"] = 0.080m,
                ["standard-1792x1024"] = 0.080m,
                ["hd-1024x1024"]       = 0.080m,
                ["hd-1024x1792"]       = 0.120m,
                ["hd-1792x1024"]       = 0.120m,
            };

        // ── DALL-E 2 detailed pricing by size ──
        private static readonly Dictionary<string, decimal> DallE2Detailed =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["256x256"]   = 0.016m,
                ["512x512"]   = 0.018m,
                ["1024x1024"] = 0.020m,
            };

        // ── gpt-image-1 detailed pricing by quality+size ──
        private static readonly Dictionary<string, decimal> GptImageDetailed =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["low-1024x1024"]    = 0.011m,
                ["low-1024x1536"]    = 0.016m,
                ["low-1536x1024"]    = 0.016m,
                ["medium-1024x1024"] = 0.042m,
                ["medium-1024x1536"] = 0.063m,
                ["medium-1536x1024"] = 0.063m,
                ["high-1024x1024"]   = 0.167m,
                ["high-1024x1536"]   = 0.250m,
                ["high-1536x1024"]   = 0.248m,
            };

        // ── gpt-image-1.5 y gpt-image-2 detailed pricing by quality+size ──
        // gpt-image factura la imagen como tokens de salida, y los tokens por
        // calidad+tamaño son fijos y publicados: 1024x1024 = 272/1056/4160 tokens
        // (low/medium/high), 1024x1536 = 408/1584/6240, 1536x1024 = 400/1568/6208.
        // El coste por imagen es esos tokens por la tarifa de salida del modelo,
        // que es lo unico que cambia entre versiones: $40/1M en gpt-image-1,
        // $32/1M en gpt-image-1.5 y $30/1M en gpt-image-2 y en los dos gpt-image-2.5
        // (Sunburst y Flare), que comparten tabla con gpt-image-2. Las tablas de abajo
        // son ese producto ya resuelto, igual que la de gpt-image-1.
        private static readonly Dictionary<string, decimal> GptImage15Detailed =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["low-1024x1024"]    = 0.0087m,
                ["low-1024x1536"]    = 0.0131m,
                ["low-1536x1024"]    = 0.0128m,
                ["medium-1024x1024"] = 0.0338m,
                ["medium-1024x1536"] = 0.0507m,
                ["medium-1536x1024"] = 0.0502m,
                ["high-1024x1024"]   = 0.1331m,
                ["high-1024x1536"]   = 0.1997m,
                ["high-1536x1024"]   = 0.1987m,
            };

        private static readonly Dictionary<string, decimal> GptImage2Detailed =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["low-1024x1024"]    = 0.0082m,
                ["low-1024x1536"]    = 0.0122m,
                ["low-1536x1024"]    = 0.0120m,
                ["medium-1024x1024"] = 0.0317m,
                ["medium-1024x1536"] = 0.0475m,
                ["medium-1536x1024"] = 0.0470m,
                ["high-1024x1024"]   = 0.1248m,
                ["high-1024x1536"]   = 0.1872m,
                ["high-1536x1024"]   = 0.1862m,
            };

        // ── gpt-image-1-mini detailed pricing by quality+size ──
        private static readonly Dictionary<string, decimal> GptImageMiniDetailed =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["low-1024x1024"]    = 0.005m,
                ["low-1024x1536"]    = 0.0075m,
                ["low-1536x1024"]    = 0.0075m,
                ["medium-1024x1024"] = 0.015m,
                ["medium-1024x1536"] = 0.0225m,
                ["medium-1536x1024"] = 0.0225m,
                ["high-1024x1024"]   = 0.060m,
                ["high-1024x1536"]   = 0.090m,
                ["high-1536x1024"]   = 0.090m,
            };

        /// <summary>
        /// Estimates cost for a text generation based on token counts.
        /// </summary>
        public static decimal EstimateTextCost(string modelName, int inputTokens, int outputTokens)
        {
            if (!TryResolveTextPrice(modelName, out var prices, out _)) return 0m;

            return (inputTokens / 1_000_000m * prices.InputPerMTok)
                 + (outputTokens / 1_000_000m * prices.OutputPerMTok);
        }

        /// <summary>
        /// True si el modelo tiene tarifa propia en el catalogo. False significa que el
        /// coste que devuelve <see cref="EstimateTextCost"/> viene de un modelo pariente
        /// (o es 0 porque no hay ninguno), y por tanto es orientativo.
        /// </summary>
        public static bool HasExactTextPrice(string modelName) =>
            TryResolveTextPrice(modelName, out _, out var exact) && exact;

        /// <summary>
        /// Tarifa por millon de tokens, o null si no hay ninguna aplicable. Es lo que
        /// necesita la pantalla de precios para mostrar el coste sin tener que estimar
        /// sobre un millon de tokens ficticios.
        /// </summary>
        public static (decimal InputPerMTok, decimal OutputPerMTok)? GetTextRate(string modelName) =>
            TryResolveTextPrice(modelName, out var prices, out _) ? prices : null;

        /// <summary>
        /// Resuelve la tarifa de un modelo de texto: match exacto, luego el mismo id sin
        /// sufijo de snapshot y, como ultimo recurso, la clave mas larga que sea prefijo.
        ///
        /// El prefijo mas largo importa: buscar el primero que encajase hacia que cada
        /// modelo nuevo de una familia heredase la tarifa del miembro mas antiguo
        /// (gpt-5.5 y gpt-5.6-sol cobraban como gpt-5, tres veces por debajo de lo real
        /// en salida, y gpt-5.6-luna diez veces por encima).
        /// </summary>
        private static bool TryResolveTextPrice(
            string modelName,
            out (decimal InputPerMTok, decimal OutputPerMTok) prices,
            out bool exact)
        {
            prices = default;
            exact = false;
            if (string.IsNullOrWhiteSpace(modelName)) return false;

            if (TextPrices.TryGetValue(modelName, out prices))
            {
                exact = true;
                return true;
            }

            var withoutSnapshot = ModelLifecycle.StripSnapshotSuffix(modelName);
            if (!ReferenceEquals(withoutSnapshot, modelName)
                && TextPrices.TryGetValue(withoutSnapshot, out prices))
            {
                exact = true;
                return true;
            }

            var key = TextPrices.Keys
                .Where(k => modelName.StartsWith(k, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(k => k.Length)
                .FirstOrDefault();

            if (key is null) return false;

            prices = TextPrices[key];
            return true;
        }

        /// <summary>
        /// Estimates cost for an image generation.
        /// </summary>
        public static decimal EstimateImageCost(string modelName, Dictionary<string, object>? config = null)
        {
            // rawSize/rawQuality conservan "no configurado" (null) por separado del
            // default de DALL-E: para gpt-image la ausencia de size NO significa
            // 1024x1024, significa "auto", que la API resuelve a retrato.
            string? rawSize = null;
            string? rawQuality = null;

            if (config is not null)
            {
                if (config.TryGetValue("size", out var s) && s is string sStr)
                    rawSize = sStr;
                if (config.TryGetValue("quality", out var q) && q is string qStr)
                    rawQuality = qStr;
            }

            var size = rawSize ?? "1024x1024";
            var quality = rawQuality ?? "standard";

            // DALL-E 3 detailed
            if (modelName.Equals("dall-e-3", StringComparison.OrdinalIgnoreCase))
            {
                var key = $"{quality}-{size}";
                if (DallE3Detailed.TryGetValue(key, out var price))
                    return price;
                return 0.040m;
            }

            // DALL-E 2 detailed
            if (modelName.Equals("dall-e-2", StringComparison.OrdinalIgnoreCase))
            {
                if (DallE2Detailed.TryGetValue(size, out var price))
                    return price;
                return 0.020m;
            }

            // gpt-image: se normaliza con el mismo criterio que usa el provider al
            // construir la peticion, para que el coste estimado no discrepe del real.
            // Antes "standard" y "auto" se resolvian aqui de forma distinta a como
            // los interpretaba la API, y el tamano se asumia cuadrado cuando "auto"
            // devuelve retrato: el estimado salia por debajo de lo facturado.
            if (GptImageOptions.IsGptImage(modelName))
            {
                var key = $"{GptImageOptions.NormalizeQuality(rawQuality)}-{GptImageOptions.NormalizeSizeForPricing(rawSize)}";

                // Cada version de gpt-image tiene su propia tarifa de salida. Antes solo
                // se distinguia el mini y el resto caia en la tabla de gpt-image-1, asi
                // que gpt-image-1.5 se estimaba un 25% mas caro de lo que factura.
                var (table, fallback) = modelName.ToLowerInvariant() switch
                {
                    "gpt-image-2"
                        or "gpt-image-2.5-sunburst"
                        or "gpt-image-2.5-flare" => (GptImage2Detailed, 0.032m),
                    "gpt-image-1.5"    => (GptImage15Detailed,   0.034m),
                    "gpt-image-1-mini" => (GptImageMiniDetailed, 0.015m),
                    _                  => (GptImageDetailed,     0.042m),
                };

                return table.TryGetValue(key, out var price) ? price : fallback;
            }

            // Generic fallback
            if (ImageFixedPrices.TryGetValue(modelName, out var fixedPrice))
                return fixedPrice;

            // Prefix match
            var matchKey = ImageFixedPrices.Keys.FirstOrDefault(k =>
                modelName.StartsWith(k, StringComparison.OrdinalIgnoreCase));
            return matchKey is not null ? ImageFixedPrices[matchKey] : 0m;
        }

        /// <summary>
        /// Valor en USD de un credito de la API de Leonardo. Leonardo no factura en
        /// dolares: cada generacion consume creditos y la respuesta dice cuantos.
        /// Convertirlos aqui es lo que permite apuntar en la ejecucion el gasto REAL
        /// en vez de una estimacion. La referencia es la misma que ya se usaba para
        /// estimar sus imagenes (~$0,003/credito); si cambia el plan contratado,
        /// este es el unico numero que hay que tocar.
        /// </summary>
        public const decimal LeonardoCreditUsd = 0.003m;

        /// <summary>Coste real de una generacion de Leonardo a partir de los creditos que gasto.</summary>
        public static decimal EstimateCostFromLeonardoCredits(int credits) =>
            credits <= 0 ? 0m : credits * LeonardoCreditUsd;

        /// <summary>
        /// Coste estimado de un video: tarifa por segundo (segun modelo, resolucion y
        /// audio, ver <see cref="VideoRates"/>) por su duracion. Solo se aplica a
        /// tarifas por segundo: si un modelo se facturase por clip, multiplicar por
        /// los segundos daria un coste sin sentido sin que nada avisara. Es una
        /// ESTIMACION; cuando el proveedor informa del gasto real (Leonardo lo hace en
        /// creditos), manda ese.
        /// </summary>
        /// <param name="resolutionId">Resolucion del clip; null = la del modelo por defecto.</param>
        public static decimal EstimateVideoCost(string modelName, double seconds, string? resolutionId = null, bool audio = false)
        {
            if (string.IsNullOrWhiteSpace(modelName) || seconds <= 0) return 0m;

            if (GetVideoRate(modelName, resolutionId) is { } video)
                return video.For(audio) * (decimal)seconds;

            var rate = GetAuxiliaryRate(modelName);
            if (rate is null || !rate.Unit.Equals("segundo", StringComparison.OrdinalIgnoreCase))
                return 0m;

            return rate.Amount * (decimal)seconds;
        }
    }
}
