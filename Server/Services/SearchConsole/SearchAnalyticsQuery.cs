using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Server.Services.SearchConsole;

/// <summary>
/// Parametros de una consulta de Search Analytics tal y como los configura el
/// usuario en el modulo "Search Console". Se leen de la config del nodo (ya con
/// las variables <c>{{clave}}</c> aplicadas), asi que cualquier campo de texto
/// puede depender de la ejecucion.
/// </summary>
public sealed class SearchAnalyticsOptions
{
    public string SiteUrl { get; init; } = "";
    public DateOnly StartDate { get; init; }
    public DateOnly EndDate { get; init; }
    public List<string> Dimensions { get; init; } = [];
    public string SearchType { get; init; } = "web";
    public List<SearchAnalyticsFilter> Filters { get; init; } = [];
    public int RowLimit { get; init; } = SearchAnalyticsQuery.DefaultRowLimit;
    public int StartRow { get; init; }
    public string AggregationType { get; init; } = "auto";
    public string DataState { get; init; } = "final";

    // Filtros y orden aplicados sobre las filas ya descargadas: la API solo
    // ordena por clics y no filtra por metricas.
    public long? MinImpressions { get; init; }
    public long? MinClicks { get; init; }
    public double? MinPosition { get; init; }
    public double? MaxPosition { get; init; }
    public string OrderBy { get; init; } = "clicks";

    public string OutputFormat { get; init; } = "table";
}

/// <summary>Filtro de dimension de la API (dimensionFilterGroups).</summary>
public sealed record SearchAnalyticsFilter(string Dimension, string Operator, string Expression);

/// <summary>Fila devuelta por la API: claves por dimension + metricas.</summary>
public sealed class SearchAnalyticsRow
{
    public Dictionary<string, string> Keys { get; init; } = new();
    public double Clicks { get; init; }
    public double Impressions { get; init; }
    public double Ctr { get; init; }
    public double Position { get; init; }
}

/// <summary>
/// Logica pura del modulo Search Console: traduce la config a la peticion de la
/// API, interpreta la respuesta y formatea la salida. No hace red; eso vive en
/// <see cref="SearchConsoleService"/>.
/// </summary>
public static class SearchAnalyticsQuery
{
    public const int DefaultRowLimit = 1000;
    public const int MaxRowLimit = 25000;

    public static readonly string[] ValidDimensions =
        ["query", "page", "country", "device", "date", "searchAppearance"];

    // searchAppearance no se puede usar como filtro junto a otras dimensiones de
    // forma general, pero la API lo acepta como filtro; "date" no es filtrable.
    public static readonly string[] FilterableDimensions =
        ["query", "page", "country", "device", "searchAppearance"];

    public static readonly string[] ValidSearchTypes =
        ["web", "image", "video", "news", "discover", "googleNews"];

    public static readonly string[] ValidAggregationTypes = ["auto", "byPage", "byProperty"];
    public static readonly string[] ValidDataStates = ["final", "all"];
    public static readonly string[] ValidOrderBy = ["clicks", "impressions", "ctr", "position"];
    public static readonly string[] ValidOutputFormats = ["table", "json", "csv"];

    // Alias legibles del operador -> valor de la API.
    private static readonly Dictionary<string, string> OperatorAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["equals"] = "equals",
        ["="] = "equals",
        ["notEquals"] = "notEquals",
        ["!="] = "notEquals",
        ["contains"] = "contains",
        ["notContains"] = "notContains",
        ["includingRegex"] = "includingRegex",
        ["regex"] = "includingRegex",
        ["excludingRegex"] = "excludingRegex",
        ["notRegex"] = "excludingRegex",
    };

    /// <summary>
    /// Construye las opciones a partir de la config. Devuelve el error legible si
    /// algun parametro no es valido, para que el nodo falle con un mensaje claro
    /// en vez de mandar a Google una peticion que va a rechazar.
    /// </summary>
    public static (SearchAnalyticsOptions? Options, string? Error) FromConfig(
        Func<string, string> getConfig, DateOnly today)
    {
        var siteUrl = NormalizeSiteUrl(getConfig("siteUrl"));
        if (siteUrl.Length == 0)
            return (null, "Falta la propiedad de Search Console (siteUrl). Ej: 'sc-domain:ejemplo.com' o 'https://www.ejemplo.com/'.");
        if (siteUrl.Contains("{{"))
            return (null, $"La propiedad '{siteUrl}' tiene una variable sin valor en esta ejecucion.");

        var dataState = Pick(getConfig("dataState"), ValidDataStates, "final");
        var (start, end, dateError) = ResolveDates(
            getConfig("dateRange"), getConfig("startDate"), getConfig("endDate"), dataState, today);
        if (dateError is not null)
            return (null, dateError);

        var dimensions = new List<string>();
        foreach (var raw in SplitList(getConfig("dimensions")))
        {
            var dim = ValidDimensions.FirstOrDefault(d => d.Equals(raw, StringComparison.OrdinalIgnoreCase));
            if (dim is null)
                return (null, $"Dimension '{raw}' no valida. Usa: {string.Join(", ", ValidDimensions)}.");
            if (!dimensions.Contains(dim))
                dimensions.Add(dim);
        }

        var (filters, filterError) = ParseFilters(getConfig("filters"));
        if (filterError is not null)
            return (null, filterError);

        var rowLimit = ParseInt(getConfig("rowLimit")) ?? DefaultRowLimit;
        if (rowLimit < 1 || rowLimit > MaxRowLimit)
            return (null, $"El limite de filas debe estar entre 1 y {MaxRowLimit}.");

        var startRow = ParseInt(getConfig("startRow")) ?? 0;
        if (startRow < 0)
            return (null, "La fila inicial no puede ser negativa.");

        var minPosition = ParseDouble(getConfig("minPosition"));
        var maxPosition = ParseDouble(getConfig("maxPosition"));
        if (minPosition is not null && maxPosition is not null && minPosition > maxPosition)
            return (null, "La posicion minima no puede ser mayor que la maxima.");

        return (new SearchAnalyticsOptions
        {
            SiteUrl = siteUrl,
            StartDate = start,
            EndDate = end,
            Dimensions = dimensions,
            SearchType = Pick(getConfig("searchType"), ValidSearchTypes, "web"),
            Filters = filters,
            RowLimit = rowLimit,
            StartRow = startRow,
            AggregationType = Pick(getConfig("aggregationType"), ValidAggregationTypes, "auto"),
            DataState = dataState,
            MinImpressions = ParseLong(getConfig("minImpressions")),
            MinClicks = ParseLong(getConfig("minClicks")),
            MinPosition = minPosition,
            MaxPosition = maxPosition,
            OrderBy = Pick(getConfig("orderBy"), ValidOrderBy, "clicks"),
            OutputFormat = Pick(getConfig("outputFormat"), ValidOutputFormats, "table"),
        }, null);
    }

    /// <summary>
    /// Las propiedades de prefijo de URL se registran siempre con barra final
    /// ("https://ejemplo.com/") y la API no encuentra "https://ejemplo.com": se
    /// la anadimos si falta. Las de dominio ("sc-domain:...") van tal cual.
    /// </summary>
    public static string NormalizeSiteUrl(string? raw)
    {
        var site = (raw ?? "").Trim();
        if ((site.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
             || site.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            && !site.EndsWith('/') && !site.Contains("{{"))
            site += "/";
        return site;
    }

    /// <summary>
    /// Rango de fechas. Los presets terminan en el ultimo dia con datos: con
    /// dataState "final" Google tarda ~3 dias en cerrar los datos; con "all"
    /// ya hay datos (provisionales) del dia anterior.
    /// </summary>
    public static (DateOnly Start, DateOnly End, string? Error) ResolveDates(
        string? preset, string? startRaw, string? endRaw, string dataState, DateOnly today)
    {
        var key = string.IsNullOrWhiteSpace(preset) ? "last28" : preset.Trim();

        if (key.Equals("custom", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseDate(startRaw, out var s) || !TryParseDate(endRaw, out var e))
                return (default, default, "Con rango personalizado hay que indicar fecha de inicio y de fin (AAAA-MM-DD).");
            if (s > e)
                return (default, default, "La fecha de inicio es posterior a la de fin.");
            return (s, e, null);
        }

        var end = today.AddDays(dataState == "all" ? -1 : -3);
        int? days = key.ToLowerInvariant() switch
        {
            "last7" => 7,
            "last28" => 28,
            "last90" => 90,
            "last180" => 180,
            "last365" => 365,
            _ => null,
        };

        if (days is not null)
            return (end.AddDays(-(days.Value - 1)), end, null);
        if (key.Equals("last16months", StringComparison.OrdinalIgnoreCase))
            return (end.AddMonths(-16).AddDays(1), end, null);

        return (default, default, $"Rango de fechas '{key}' no valido.");
    }

    /// <summary>
    /// Filtros, uno por linea: <c>dimension operador expresion</c>. Ej:
    /// <c>query contains zapatillas</c>, <c>page notContains /blog/</c>,
    /// <c>country equals esp</c>. Lineas vacias o que empiezan por # se ignoran.
    /// </summary>
    public static (List<SearchAnalyticsFilter> Filters, string? Error) ParseFilters(string? raw)
    {
        var filters = new List<SearchAnalyticsFilter>();
        if (string.IsNullOrWhiteSpace(raw))
            return (filters, null);

        foreach (var rawLine in raw.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 3)
                return (filters, $"Filtro '{line}' incompleto. Formato: dimension operador valor (ej: query contains zapatillas).");

            var dim = FilterableDimensions.FirstOrDefault(d => d.Equals(parts[0], StringComparison.OrdinalIgnoreCase));
            if (dim is null)
                return (filters, $"No se puede filtrar por '{parts[0]}'. Usa: {string.Join(", ", FilterableDimensions)}.");

            if (!OperatorAliases.TryGetValue(parts[1], out var op))
                return (filters, $"Operador '{parts[1]}' no valido. Usa: equals, notEquals, contains, notContains, includingRegex, excludingRegex.");

            if (parts[2].Contains("{{"))
                return (filters, $"El filtro '{line}' tiene una variable sin valor en esta ejecucion.");

            filters.Add(new SearchAnalyticsFilter(dim, op, parts[2]));
        }

        return (filters, null);
    }

    /// <summary>Cuerpo JSON de searchAnalytics.query.</summary>
    public static Dictionary<string, object> BuildRequestBody(SearchAnalyticsOptions o)
    {
        var body = new Dictionary<string, object>
        {
            ["startDate"] = o.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["endDate"] = o.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["type"] = o.SearchType,
            ["rowLimit"] = o.RowLimit,
            ["startRow"] = o.StartRow,
            ["aggregationType"] = o.AggregationType,
            ["dataState"] = o.DataState,
        };

        if (o.Dimensions.Count > 0)
            body["dimensions"] = o.Dimensions;

        if (o.Filters.Count > 0)
        {
            body["dimensionFilterGroups"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["groupType"] = "and",
                    ["filters"] = o.Filters.Select(f => new Dictionary<string, string>
                    {
                        ["dimension"] = f.Dimension,
                        ["operator"] = f.Operator,
                        ["expression"] = f.Expression,
                    }).ToList(),
                },
            };
        }

        return body;
    }

    /// <summary>Interpreta la respuesta de la API. Sin filas = lista vacia.</summary>
    public static List<SearchAnalyticsRow> ParseRows(string json, IReadOnlyList<string> dimensions)
    {
        var rows = new List<SearchAnalyticsRow>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("rows", out var rowsEl) || rowsEl.ValueKind != JsonValueKind.Array)
            return rows;

        foreach (var r in rowsEl.EnumerateArray())
        {
            var keys = new Dictionary<string, string>();
            if (r.TryGetProperty("keys", out var keysEl) && keysEl.ValueKind == JsonValueKind.Array)
            {
                var i = 0;
                foreach (var k in keysEl.EnumerateArray())
                {
                    if (i < dimensions.Count)
                        keys[dimensions[i]] = k.GetString() ?? "";
                    i++;
                }
            }

            rows.Add(new SearchAnalyticsRow
            {
                Keys = keys,
                Clicks = Metric(r, "clicks"),
                Impressions = Metric(r, "impressions"),
                Ctr = Metric(r, "ctr"),
                Position = Metric(r, "position"),
            });
        }

        return rows;
    }

    /// <summary>Filtros por metrica y orden, sobre las filas ya descargadas.</summary>
    public static List<SearchAnalyticsRow> ApplyPostProcessing(
        IEnumerable<SearchAnalyticsRow> rows, SearchAnalyticsOptions o)
    {
        var q = rows;
        if (o.MinImpressions is { } minImp) q = q.Where(r => r.Impressions >= minImp);
        if (o.MinClicks is { } minClicks) q = q.Where(r => r.Clicks >= minClicks);
        if (o.MinPosition is { } minPos) q = q.Where(r => r.Position >= minPos);
        if (o.MaxPosition is { } maxPos) q = q.Where(r => r.Position <= maxPos);

        q = o.OrderBy switch
        {
            "impressions" => q.OrderByDescending(r => r.Impressions),
            "ctr" => q.OrderByDescending(r => r.Ctr),
            // Mejor posicion = numero mas bajo.
            "position" => q.OrderBy(r => r.Position),
            _ => q.OrderByDescending(r => r.Clicks),
        };

        return q.ToList();
    }

    /// <summary>Texto de salida del nodo en el formato elegido.</summary>
    public static string Format(IReadOnlyList<SearchAnalyticsRow> rows, SearchAnalyticsOptions o) =>
        o.OutputFormat switch
        {
            "json" => FormatJson(rows, o),
            "csv" => FormatCsv(rows, o),
            _ => FormatTable(rows, o),
        };

    private static string FormatJson(IReadOnlyList<SearchAnalyticsRow> rows, SearchAnalyticsOptions o)
    {
        var payload = new Dictionary<string, object>
        {
            ["siteUrl"] = o.SiteUrl,
            ["startDate"] = o.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["endDate"] = o.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["searchType"] = o.SearchType,
            ["dimensions"] = o.Dimensions,
            ["rowCount"] = rows.Count,
            ["totals"] = new Dictionary<string, object>
            {
                ["clicks"] = rows.Sum(r => r.Clicks),
                ["impressions"] = rows.Sum(r => r.Impressions),
            },
            ["rows"] = rows.Select(r =>
            {
                var item = new Dictionary<string, object>();
                foreach (var d in o.Dimensions)
                    item[d] = r.Keys.GetValueOrDefault(d, "");
                item["clicks"] = r.Clicks;
                item["impressions"] = r.Impressions;
                item["ctr"] = Math.Round(r.Ctr, 4);
                item["position"] = Math.Round(r.Position, 2);
                return item;
            }).ToList(),
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string FormatCsv(IReadOnlyList<SearchAnalyticsRow> rows, SearchAnalyticsOptions o)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", o.Dimensions.Concat(["clicks", "impressions", "ctr", "position"])));
        foreach (var r in rows)
        {
            var cells = o.Dimensions.Select(d => CsvEscape(r.Keys.GetValueOrDefault(d, "")))
                .Concat([
                    Num(r.Clicks), Num(r.Impressions),
                    Num(Math.Round(r.Ctr, 4)), Num(Math.Round(r.Position, 2)),
                ]);
            sb.AppendLine(string.Join(",", cells));
        }
        return sb.ToString().TrimEnd();
    }

    private static string FormatTable(IReadOnlyList<SearchAnalyticsRow> rows, SearchAnalyticsOptions o)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Search Console · {o.SiteUrl} · {o.StartDate:yyyy-MM-dd} a {o.EndDate:yyyy-MM-dd} · tipo {o.SearchType}");
        sb.AppendLine($"Filas: {rows.Count} · Clics: {Num(rows.Sum(r => r.Clicks))} · Impresiones: {Num(rows.Sum(r => r.Impressions))}");

        if (rows.Count == 0)
            return sb.ToString().TrimEnd();

        sb.AppendLine();
        var headers = o.Dimensions.Concat(["clics", "impresiones", "ctr", "posicion"]).ToList();
        sb.AppendLine("| " + string.Join(" | ", headers) + " |");
        sb.AppendLine("|" + string.Concat(headers.Select(_ => " --- |")));
        foreach (var r in rows)
        {
            var cells = o.Dimensions.Select(d => r.Keys.GetValueOrDefault(d, "").Replace("|", "\\|"))
                .Concat([
                    Num(r.Clicks), Num(r.Impressions),
                    (r.Ctr * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%",
                    r.Position.ToString("0.#", CultureInfo.InvariantCulture),
                ]);
            sb.AppendLine("| " + string.Join(" | ", cells) + " |");
        }
        return sb.ToString().TrimEnd();
    }

    // ── Helpers ──

    private static double Metric(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static string Pick(string? raw, string[] valid, string fallback)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return valid.FirstOrDefault(v => v.Equals(raw.Trim(), StringComparison.OrdinalIgnoreCase)) ?? fallback;
    }

    private static IEnumerable<string> SplitList(string? raw) =>
        (raw ?? "").Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool TryParseDate(string? raw, out DateOnly date) =>
        DateOnly.TryParseExact((raw ?? "").Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out date);

    private static int? ParseInt(string? raw) =>
        int.TryParse((raw ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static long? ParseLong(string? raw) =>
        long.TryParse((raw ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    // Admite coma decimal ("10,5"), que es como lo escribe un usuario en español.
    private static double? ParseDouble(string? raw) =>
        double.TryParse((raw ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v : null;

    private static string Num(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    private static string CsvEscape(string v) =>
        v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
}
