using Server.Services.SearchConsole;

namespace Server.Services.Ai.Handlers;

/// <summary>
/// Descarga datos de rendimiento (Search Analytics) de una propiedad de Google
/// Search Console: clics, impresiones, CTR y posicion media, agrupados por las
/// dimensiones elegidas. La credencial es la API key del modulo (JSON de una
/// cuenta de servicio) y los parametros de la consulta salen de su config.
/// No tiene entradas obligatorias: emite el resultado por "output_text" para
/// alimentar, por ejemplo, un modulo de Texto que proponga articulos.
/// </summary>
public class SearchConsoleModuleHandler : IModuleHandler
{
    public const string Type = "SearchConsole";
    private readonly SearchConsoleService _searchConsole;

    public string ModuleType => Type;

    public SearchConsoleModuleHandler(SearchConsoleService searchConsole) => _searchConsole = searchConsole;

    public async Task<ModuleResult> ExecuteAsync(ModuleExecutionContext ctx)
    {
        var credentials = ctx.Node.AiModule.ApiKey?.EncryptedKey;
        if (string.IsNullOrWhiteSpace(credentials))
            return ModuleResult.Failed("El modulo no tiene API key de Search Console (JSON de la cuenta de servicio)");

        var (options, error) = SearchAnalyticsQuery.FromConfig(
            key => ctx.GetConfig(key), DateOnly.FromDateTime(DateTime.UtcNow));
        if (options is null)
            return ModuleResult.Failed(error ?? "Configuracion de Search Console no valida");

        await ctx.LogInfoAsync(
            $"Search Console: {options.SiteUrl} · {options.StartDate:yyyy-MM-dd} a {options.EndDate:yyyy-MM-dd} · " +
            $"dimensiones [{string.Join(", ", options.Dimensions)}] · tipo {options.SearchType} · " +
            $"{options.Filters.Count} filtro(s) · hasta {options.RowLimit} filas");

        string json;
        try
        {
            json = await _searchConsole.QuerySearchAnalyticsAsync(
                credentials, options.SiteUrl, SearchAnalyticsQuery.BuildRequestBody(options), ctx.CancellationToken);
        }
        catch (SearchConsoleException ex) when (ex.IsSiteAccessDenied)
        {
            return ModuleResult.Failed(ex.Message + " " + await DescribeAccessibleSitesAsync(credentials, ctx.CancellationToken));
        }
        catch (SearchConsoleException ex)
        {
            return ModuleResult.Failed(ex.Message);
        }

        var fetched = SearchAnalyticsQuery.ParseRows(json, options.Dimensions);
        var rows = SearchAnalyticsQuery.ApplyPostProcessing(fetched, options);

        if (rows.Count != fetched.Count)
            await ctx.LogInfoAsync($"Search Console: {fetched.Count} filas recibidas, {rows.Count} tras los filtros por metrica.");
        else
            await ctx.LogInfoAsync($"Search Console: {rows.Count} filas recibidas.");

        if (fetched.Count == options.RowLimit)
            await ctx.LogWarningAsync(
                $"Se ha alcanzado el limite de {options.RowLimit} filas: puede haber mas datos. " +
                "Sube el limite o usa la fila inicial para paginar.");

        var output = new StepOutput
        {
            Type = "text",
            Title = $"Search Console · {options.SiteUrl}",
            Content = SearchAnalyticsQuery.Format(rows, options),
            Summary = $"{rows.Count} filas de {options.SiteUrl} ({options.StartDate:yyyy-MM-dd} a {options.EndDate:yyyy-MM-dd})",
            Metadata = new Dictionary<string, object>
            {
                ["siteUrl"] = options.SiteUrl,
                ["startDate"] = options.StartDate.ToString("yyyy-MM-dd"),
                ["endDate"] = options.EndDate.ToString("yyyy-MM-dd"),
                ["rowCount"] = rows.Count,
                ["format"] = options.OutputFormat,
            },
        };

        return ModuleResult.Completed(output);
    }

    /// <summary>
    /// Ante un "sin permiso" el problema casi siempre es el nombre de la propiedad
    /// (dominio vs prefijo de URL, www o no) o que la cuenta no esta anadida. Listar
    /// las propiedades que si ve la cuenta dice cual de los dos es y que escribir.
    /// </summary>
    private async Task<string> DescribeAccessibleSitesAsync(string credentials, CancellationToken ct)
    {
        try
        {
            var sites = await _searchConsole.ListSitesAsync(credentials, ct);
            return sites.Count == 0
                ? "La cuenta de servicio no tiene acceso a NINGUNA propiedad: falta anadir su email como usuario en Search Console."
                : "Propiedades a las que si tiene acceso (usa una de estas tal cual): " +
                  string.Join(", ", sites.Select(s => $"'{s.SiteUrl}'")) + ".";
        }
        catch (SearchConsoleException ex)
        {
            return $"Tampoco se pudo listar a que propiedades tiene acceso: {ex.Message}";
        }
    }
}
