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
                // OpenAI — revisado contra developers.openai.com/api/docs/pricing el 2026-08-10.
                ["gpt-5.6"]          = (5.00m,  30.00m),   // alias: resuelve a Sol y factura como Sol
                ["gpt-5.6-sol"]      = (5.00m,  30.00m),
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
                // gpt-image-1.5 / gpt-image-2 (medium quality 1024x1024 approx)
                ["gpt-image-1.5"] = 0.034m,
                ["gpt-image-2"] = 0.032m,

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
        // $32/1M en gpt-image-1.5 y $30/1M en gpt-image-2. Las tablas de abajo son
        // ese producto ya resuelto, igual que la de gpt-image-1.
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
                    "gpt-image-2"      => (GptImage2Detailed,    0.032m),
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
    }
}
