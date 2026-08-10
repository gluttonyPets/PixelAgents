namespace Server.Services.Ai;

/// <summary>Estado de un modelo respecto a su retirada anunciada por el proveedor.</summary>
public enum ModelStatus
{
    /// <summary>Sin retirada anunciada.</summary>
    Active,

    /// <summary>Retirada anunciada con fecha futura: sigue funcionando pero hay que migrar.</summary>
    Deprecated,

    /// <summary>La fecha de retirada ya pasó: la API responde 404 / model_not_found.</summary>
    Retired
}

public record ModelLifecycleInfo(
    string Id,
    ModelStatus Status,
    DateOnly? ShutdownDate,
    string? ReplacementId,
    string? Note)
{
    /// <summary>Días que quedan hasta la retirada; null si no hay fecha o ya pasó.</summary>
    public int? DaysUntilShutdown(DateOnly today) =>
        ShutdownDate is { } d && d > today ? d.DayNumber - today.DayNumber : null;
}

/// <summary>
/// Fechas de retirada anunciadas por los proveedores. Es una tabla local a propósito:
/// ninguna API de OpenAI expone el ciclo de vida de sus modelos (GET /v1/models solo
/// devuelve id, created y owned_by), así que la única fuente es la página de
/// deprecations. <see cref="ModelAvailabilityService"/> complementa esto consultando
/// qué modelos responden de verdad con la API key del tenant.
///
/// Los modelos nunca se borran del catálogo: se marcan aquí y la UI avisa. Un pipeline
/// guardado que apunte a un modelo retirado debe seguir siendo visible para que el
/// usuario entienda por qué falla, en vez de desaparecer sin explicación.
///
/// Última revisión contra https://developers.openai.com/api/docs/deprecations: 2026-08-10.
/// </summary>
public static class ModelLifecycle
{
    private static readonly Dictionary<string, ModelLifecycleInfo> Table =
        new(StringComparer.OrdinalIgnoreCase);

    private static void Add(string id, string shutdown, string? replacement, string? note = null)
        => Table[id] = new ModelLifecycleInfo(id, ModelStatus.Deprecated,
            DateOnly.ParseExact(shutdown, "yyyy-MM-dd"), replacement, note);

    static ModelLifecycle()
    {
        // ── OpenAI: imagen ──
        // DALL-E se apagó en mayo de 2026; las llamadas ya fallan.
        Add("dall-e-2", "2026-05-12", "gpt-image-2");
        Add("dall-e-3", "2026-05-12", "gpt-image-2");
        // Toda la familia gpt-image-1.x se retira a la vez a favor de gpt-image-2.
        Add("gpt-image-1", "2026-12-01", "gpt-image-2");
        Add("gpt-image-1-mini", "2026-12-01", "gpt-image-2");
        Add("gpt-image-1.5", "2026-12-01", "gpt-image-2");

        // ── OpenAI: texto ──
        // OpenAI anuncia la retirada sobre el snapshot con fecha, pero el alias sin
        // fecha resuelve a ese mismo snapshot, así que el aviso aplica igual.
        Add("gpt-5", "2026-12-11", "gpt-5.6-sol",
            "OpenAI anuncia la retirada del snapshot gpt-5-2025-08-07, al que resuelve este alias.");
        Add("gpt-5-mini", "2026-12-11", "gpt-5.6-terra",
            "OpenAI anuncia la retirada del snapshot gpt-5-mini-2025-08-07, al que resuelve este alias.");
        Add("o3", "2026-12-11", "gpt-5.6-sol",
            "OpenAI anuncia la retirada del snapshot o3-2025-04-16, al que resuelve este alias.");

        Add("o1", "2026-10-23", "gpt-5.6-sol");
        Add("o3-mini", "2026-10-23", "gpt-5.6-terra");
        Add("o4-mini", "2026-10-23", "gpt-5.6-terra");
        // El alias gpt-4o sigue vigente: solo se retira este snapshot concreto.
        Add("gpt-4o-2024-05-13", "2026-10-23", "gpt-5.6-sol");

        // ── OpenAI: audio / transcripción ──
        Add("gpt-4o-mini-transcribe", "2027-01-20", "gpt-transcribe");
    }

    /// <summary>
    /// Ciclo de vida de un modelo. Devuelve siempre un resultado: los modelos sin
    /// retirada anunciada salen como <see cref="ModelStatus.Active"/>.
    /// </summary>
    public static ModelLifecycleInfo Resolve(string? modelId, DateOnly? today = null)
    {
        var id = modelId?.Trim() ?? "";
        if (id.Length == 0) return new ModelLifecycleInfo("", ModelStatus.Active, null, null, null);

        var now = today ?? DateOnly.FromDateTime(DateTime.UtcNow);

        // Match exacto y, si falla, sobre el alias sin snapshot: "gpt-5-mini-2025-08-07"
        // hereda lo de "gpt-5-mini". Deliberadamente NO se hace match por prefijo libre,
        // que haría que "gpt-5-nano" heredase la retirada de "gpt-5".
        if (!Table.TryGetValue(id, out var info)
            && !Table.TryGetValue(StripSnapshotSuffix(id), out info))
            return new ModelLifecycleInfo(id, ModelStatus.Active, null, null, null);

        var status = info.ShutdownDate is { } d && d <= now
            ? ModelStatus.Retired
            : ModelStatus.Deprecated;

        return info with { Id = id, Status = status };
    }

    /// <summary>
    /// Quita el sufijo de snapshot del final del id, si lo hay. Cada proveedor usa su
    /// formato: OpenAI escribe "gpt-5-mini-2025-08-07" y Anthropic "claude-opus-4-5-20251124".
    /// Devuelve el id tal cual cuando no hay sufijo reconocible.
    /// </summary>
    public static string StripSnapshotSuffix(string id)
    {
        // "-2025-08-07" (11 chars) y "-20251124" (9 chars).
        if (TryStrip(id, 11, [5, 8], out var openAiStyle)) return openAiStyle;
        if (TryStrip(id, 9, [], out var anthropicStyle)) return anthropicStyle;
        return id;
    }

    /// <summary>
    /// Comprueba que los ultimos <paramref name="length"/> caracteres son un guion
    /// seguido de digitos, con guiones adicionales en <paramref name="dashOffsets"/>.
    /// </summary>
    private static bool TryStrip(string id, int length, int[] dashOffsets, out string stripped)
    {
        stripped = id;
        if (id.Length <= length) return false;

        var tail = id[^length..];
        if (tail[0] != '-') return false;

        for (var i = 1; i < length; i++)
        {
            var expectDash = Array.IndexOf(dashOffsets, i) >= 0;
            if (expectDash ? tail[i] != '-' : !char.IsAsciiDigit(tail[i])) return false;
        }

        stripped = id[..^length];
        return true;
    }
}
