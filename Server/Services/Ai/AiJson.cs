using System.Text.Encodings.Web;
using System.Text.Json;

namespace Server.Services.Ai;

public static class AiJson
{
    public static readonly JsonSerializerOptions Compact = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static readonly JsonSerializerOptions Indented = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static bool TryParseJsonValue(string? raw, out object? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var trimmed = raw.TrimStart();
        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('[')) return false;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            value = doc.RootElement.Clone();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
