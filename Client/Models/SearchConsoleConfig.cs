using System.Text.Json;

namespace Client.Models;

/// <summary>
/// Configuracion del modulo "Search Console" (Search Analytics). Se guarda como
/// JSON en <c>AiModule.Configuration</c>; las claves coinciden con las que lee
/// <c>SearchAnalyticsQuery.FromConfig</c> en el servidor. Los valores numericos
/// van como texto para poder dejarlos vacios (= sin filtro) y para que admitan
/// variables <c>{{clave}}</c>.
/// </summary>
public class SearchConsoleConfig
{
    public const string ModuleType = "SearchConsole";
    public const string Provider = "SearchConsole";

    public string SiteUrl { get; set; } = "";
    public string DateRange { get; set; } = "last28";
    public string StartDate { get; set; } = "";
    public string EndDate { get; set; } = "";
    public List<string> Dimensions { get; set; } = ["query", "page"];
    public string SearchType { get; set; } = "web";
    public string Filters { get; set; } = "";
    public string RowLimit { get; set; } = "1000";
    public string StartRow { get; set; } = "";
    public string AggregationType { get; set; } = "auto";
    public string DataState { get; set; } = "final";
    public string MinImpressions { get; set; } = "";
    public string MinClicks { get; set; } = "";
    public string MinPosition { get; set; } = "";
    public string MaxPosition { get; set; } = "";
    public string OrderBy { get; set; } = "clicks";
    public string OutputFormat { get; set; } = "table";

    public static readonly (string Value, string Label)[] DimensionOptions =
    [
        ("query", "Consulta"),
        ("page", "Pagina"),
        ("country", "Pais"),
        ("device", "Dispositivo"),
        ("date", "Fecha"),
        ("searchAppearance", "Aspecto en la busqueda"),
    ];

    public static readonly (string Value, string Label)[] DateRangeOptions =
    [
        ("last7", "Ultimos 7 dias"),
        ("last28", "Ultimos 28 dias"),
        ("last90", "Ultimos 3 meses"),
        ("last180", "Ultimos 6 meses"),
        ("last365", "Ultimos 12 meses"),
        ("last16months", "Ultimos 16 meses (maximo)"),
        ("custom", "Personalizado"),
    ];

    public static readonly (string Value, string Label)[] SearchTypeOptions =
    [
        ("web", "Web"),
        ("image", "Imagenes"),
        ("video", "Videos"),
        ("news", "Noticias (pestana)"),
        ("discover", "Discover"),
        ("googleNews", "Google Noticias"),
    ];

    public static readonly (string Value, string Label)[] OrderByOptions =
    [
        ("clicks", "Clics (mayor primero)"),
        ("impressions", "Impresiones (mayor primero)"),
        ("ctr", "CTR (mayor primero)"),
        ("position", "Posicion (mejor primero)"),
    ];

    public static readonly (string Value, string Label)[] OutputFormatOptions =
    [
        ("table", "Tabla legible (para un modulo de texto)"),
        ("json", "JSON"),
        ("csv", "CSV"),
    ];

    public void ToggleDimension(string dimension, bool selected)
    {
        if (selected && !Dimensions.Contains(dimension))
        {
            // Mantiene el orden del catalogo: el orden de las dimensiones es el de
            // las columnas de la salida.
            Dimensions.Add(dimension);
            var order = DimensionOptions.Select(d => d.Value).ToList();
            Dimensions.Sort((a, b) => order.IndexOf(a).CompareTo(order.IndexOf(b)));
        }
        else if (!selected)
        {
            Dimensions.Remove(dimension);
        }
    }

    /// <summary>Solo se escriben los valores que no son los de por defecto.</summary>
    public string ToJson()
    {
        var map = new Dictionary<string, string>
        {
            ["siteUrl"] = SiteUrl.Trim(),
            ["dateRange"] = DateRange,
            ["dimensions"] = string.Join(",", Dimensions),
            ["searchType"] = SearchType,
            ["rowLimit"] = RowLimit.Trim(),
            ["aggregationType"] = AggregationType,
            ["dataState"] = DataState,
            ["orderBy"] = OrderBy,
            ["outputFormat"] = OutputFormat,
        };

        void AddIfSet(string key, string value)
        {
            if (!string.IsNullOrWhiteSpace(value)) map[key] = value.Trim();
        }

        if (DateRange == "custom")
        {
            AddIfSet("startDate", StartDate);
            AddIfSet("endDate", EndDate);
        }
        AddIfSet("filters", Filters);
        AddIfSet("startRow", StartRow);
        AddIfSet("minImpressions", MinImpressions);
        AddIfSet("minClicks", MinClicks);
        AddIfSet("minPosition", MinPosition);
        AddIfSet("maxPosition", MaxPosition);

        return JsonSerializer.Serialize(map);
    }

    public static SearchConsoleConfig FromJson(string? json)
    {
        var cfg = new SearchConsoleConfig();
        if (string.IsNullOrWhiteSpace(json)) return cfg;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? Read(string key) =>
                root.TryGetProperty(key, out var v)
                    ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()
                    : null;

            cfg.SiteUrl = Read("siteUrl") ?? "";
            cfg.DateRange = Read("dateRange") ?? cfg.DateRange;
            cfg.StartDate = Read("startDate") ?? "";
            cfg.EndDate = Read("endDate") ?? "";
            if (Read("dimensions") is { } dims)
                cfg.Dimensions = dims.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            cfg.SearchType = Read("searchType") ?? cfg.SearchType;
            cfg.Filters = Read("filters") ?? "";
            cfg.RowLimit = Read("rowLimit") ?? cfg.RowLimit;
            cfg.StartRow = Read("startRow") ?? "";
            cfg.AggregationType = Read("aggregationType") ?? cfg.AggregationType;
            cfg.DataState = Read("dataState") ?? cfg.DataState;
            cfg.MinImpressions = Read("minImpressions") ?? "";
            cfg.MinClicks = Read("minClicks") ?? "";
            cfg.MinPosition = Read("minPosition") ?? "";
            cfg.MaxPosition = Read("maxPosition") ?? "";
            cfg.OrderBy = Read("orderBy") ?? cfg.OrderBy;
            cfg.OutputFormat = Read("outputFormat") ?? cfg.OutputFormat;
        }
        catch (JsonException) { }

        return cfg;
    }
}
