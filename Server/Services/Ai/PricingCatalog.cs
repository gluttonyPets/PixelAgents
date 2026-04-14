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
                // OpenAI
                ["gpt-5.4"]          = (2.50m,  15.00m),
                ["gpt-5.4-pro"]      = (30.00m, 180.00m),
                ["gpt-5.2"]          = (2.00m,  10.00m),
                ["gpt-5"]            = (2.00m,  10.00m),
                ["gpt-5-mini"]       = (0.50m,   2.00m),
                ["gpt-5-nano"]       = (0.15m,   0.60m),
                ["gpt-4o"]           = (5.00m,  15.00m),
                ["gpt-4o-mini"]      = (0.25m,   1.00m),
                ["gpt-4.1"]          = (2.00m,   8.00m),
                ["gpt-4.1-mini"]     = (0.40m,   1.60m),
                ["gpt-4.1-nano"]     = (0.10m,   0.40m),
                ["o3"]               = (2.00m,   8.00m),
                ["o3-mini"]          = (1.10m,   4.40m),
                ["o4-mini"]          = (1.10m,   4.40m),

                // Anthropic
                ["claude-sonnet-4-20250514"]    = (3.00m, 15.00m),
                ["claude-opus-4-20250514"]      = (5.00m, 25.00m),
                ["claude-3-5-haiku-20241022"]   = (0.80m,  4.00m),

                // Google Gemini (Gemini Developer API pricing)
                ["gemini-2.0-flash"]   = (0.10m,  0.40m),
                ["gemini-2.5-flash"]   = (0.30m,  2.50m),  // output includes thinking tokens
                ["gemini-2.5-pro"]     = (1.25m, 10.00m),

                // xAI Grok
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

                // Gemini Imagen (via Gemini native image gen)
                ["gemini-2.0-flash"]          = 0.039m,
                ["gemini-2.0-flash-preview-image-generation"] = 0.039m,
                ["imagen-3.0-generate-002"]   = 0.040m,

                // xAI Grok (Imagine image generation)
                ["grok-imagine-image"]     = 0.070m,

                // Leonardo AI (approximate: ~7 credits at ~$0.003/credit)
                ["leonardo-phoenix"]       = 0.021m,
                ["leonardo-phoenix-0.9"]   = 0.021m,
                ["leonardo-flux-dev"]      = 0.021m,
                ["leonardo-flux-schnell"]  = 0.021m,
            };

        // ── Video models: price per second ──
        private static readonly Dictionary<string, decimal> VideoPerSecondPrices =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // Google Veo
                ["veo-2"]               = 0.35m,
                ["veo-3.1-generate-preview"] = 0.50m,

                // OpenAI Sora ($0.10/s for 720p — approximate for higher res)
                ["sora-2"]     = 0.10m,
                ["sora-2-pro"] = 0.10m,
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
            // Try exact match first, then prefix match
            if (!TextPrices.TryGetValue(modelName, out var prices))
            {
                var key = TextPrices.Keys.FirstOrDefault(k => modelName.StartsWith(k, StringComparison.OrdinalIgnoreCase));
                if (key is null) return 0m;
                prices = TextPrices[key];
            }

            return (inputTokens / 1_000_000m * prices.InputPerMTok)
                 + (outputTokens / 1_000_000m * prices.OutputPerMTok);
        }

        /// <summary>
        /// Estimates cost for an image generation.
        /// </summary>
        public static decimal EstimateImageCost(string modelName, Dictionary<string, object>? config = null)
        {
            var size = "1024x1024";
            var quality = "standard";

            if (config is not null)
            {
                if (config.TryGetValue("size", out var s) && s is string sStr)
                    size = sStr;
                if (config.TryGetValue("quality", out var q) && q is string qStr)
                    quality = qStr;
            }

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

            // gpt-image-1-mini detailed
            if (modelName.Equals("gpt-image-1-mini", StringComparison.OrdinalIgnoreCase))
            {
                if (quality == "hd") quality = "high";
                if (quality == "auto") quality = "medium";
                var key = $"{quality}-{size}";
                if (GptImageMiniDetailed.TryGetValue(key, out var price))
                    return price;
                return 0.015m;
            }

            // gpt-image detailed (gpt-image-1, gpt-image-1.5, etc.)
            if (modelName.StartsWith("gpt-image", StringComparison.OrdinalIgnoreCase))
            {
                // Map "hd" quality alias to "high"
                if (quality == "hd") quality = "high";
                if (quality == "auto") quality = "medium";
                var key = $"{quality}-{size}";
                if (GptImageDetailed.TryGetValue(key, out var price))
                    return price;
                return 0.042m;
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
        /// Estimates cost for a video search (e.g. Pexels). Always free.
        /// </summary>
        public static decimal EstimateVideoSearchCost(string modelName) => 0m;

        /// <summary>
        /// Estimates cost for a Json2Video video edit based on duration in seconds.
        /// Approximate: ~1 credit per second at HD, plan-dependent.
        /// </summary>
        public static decimal EstimateVideoEditCost(int durationSeconds)
        {
            // Json2Video pricing is credit-based (~1 credit/sec at HD).
            // Starter plan: $19.95/600 credits ≈ $0.033/credit
            return durationSeconds * 0.033m;
        }

        /// <summary>
        /// Estimates cost for a video generation based on duration in seconds.
        /// </summary>
        public static decimal EstimateVideoCost(string modelName, int durationSeconds = 8)
        {
            decimal perSecond;
            if (!VideoPerSecondPrices.TryGetValue(modelName, out perSecond))
            {
                var key = VideoPerSecondPrices.Keys.FirstOrDefault(k =>
                    modelName.StartsWith(k, StringComparison.OrdinalIgnoreCase));
                if (key is null) return 0m;
                perSecond = VideoPerSecondPrices[key];
            }

            return perSecond * durationSeconds;
        }
    }
}
