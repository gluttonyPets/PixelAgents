namespace Server.Services.Ai
{
    /// <summary>
    /// Punto unico de verdad para los parametros de gpt-image que deciden el coste.
    ///
    /// gpt-image factura la imagen como tokens de salida y el tramo de calidad
    /// multiplica por 4 entre "medium" y "high" (1.584 vs 6.240 tokens a 1024x1536).
    /// Si la peticion no lleva "quality", la API aplica "auto", que resuelve a high.
    /// Por eso nunca se deja el parametro sin enviar.
    ///
    /// Ademas el vocabulario no coincide con el de DALL-E ("standard"/"hd"), y la UI
    /// guardaba valores de DALL-E tambien para modulos gpt-image. Normalizar aqui
    /// evita que el provider y el estimador de coste discrepen.
    /// </summary>
    public static class GptImageOptions
    {
        public const string DefaultQuality = "medium";
        public const string DefaultSize = "1024x1536";

        public static bool IsGptImage(string? modelName) =>
            !string.IsNullOrWhiteSpace(modelName)
            && modelName!.StartsWith("gpt-image", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Traduce el valor guardado en la config al vocabulario real de gpt-image.
        /// "standard" y "auto" caen en el default en vez de en "high", que era el
        /// comportamiento anterior y el origen del sobrecoste.
        /// </summary>
        public static string NormalizeQuality(string? configured)
        {
            var q = configured?.Trim().ToLowerInvariant();
            return q switch
            {
                "low" => "low",
                "medium" => "medium",
                "high" => "high",
                "hd" => "high",
                _ => DefaultQuality
            };
        }

        /// <summary>
        /// Tamano efectivo para estimar coste. "auto" (o ausente) no es 1024x1024:
        /// la API tiende a devolver retrato 1024x1536, que es mas caro. Estimar con
        /// el cuadrado hacia que el coste mostrado en la UI se quedara corto.
        /// </summary>
        public static string NormalizeSizeForPricing(string? configured)
        {
            var s = configured?.Trim().ToLowerInvariant();
            return s switch
            {
                "1024x1024" => "1024x1024",
                "1536x1024" => "1536x1024",
                "1024x1536" => "1024x1536",
                _ => DefaultSize
            };
        }
    }
}
