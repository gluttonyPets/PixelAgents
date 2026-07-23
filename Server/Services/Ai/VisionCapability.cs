namespace Server.Services.Ai;

/// <summary>
/// Heurística de si un modelo de texto puede analizar imágenes (multimodal). La usa el
/// analista de aprendizaje para decidir si adjunta las imágenes generadas, y la UI para
/// avisar cuando el modelo elegido no las podrá "ver".
/// </summary>
public static class VisionCapability
{
    public static bool IsVisionCapable(string providerType, string modelName)
    {
        if (string.IsNullOrWhiteSpace(providerType) || string.IsNullOrWhiteSpace(modelName))
            return false;

        if (providerType.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
            return modelName.Contains("gpt-4o", StringComparison.OrdinalIgnoreCase)
                || modelName.Contains("gpt-4.1", StringComparison.OrdinalIgnoreCase)
                || modelName.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)
                || modelName.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
                || modelName.StartsWith("o4", StringComparison.OrdinalIgnoreCase);
        if (providerType.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
            return modelName.StartsWith("claude-", StringComparison.OrdinalIgnoreCase);
        if (providerType.Equals("Google", StringComparison.OrdinalIgnoreCase))
            return modelName.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase);
        if (providerType.Equals("xAI", StringComparison.OrdinalIgnoreCase))
            return modelName.Contains("vision", StringComparison.OrdinalIgnoreCase);
        return false;
    }
}
