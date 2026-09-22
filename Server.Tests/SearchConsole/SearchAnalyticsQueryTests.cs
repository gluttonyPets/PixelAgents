using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Server.Services.SearchConsole;
using Xunit;

namespace Server.Tests.SearchConsole;

public class SearchAnalyticsQueryTests
{
    private static readonly DateOnly Today = new(2026, 9, 22);

    private static Func<string, string> Config(params (string Key, string Value)[] values)
    {
        var map = values.ToDictionary(v => v.Key, v => v.Value);
        return key => map.GetValueOrDefault(key, "");
    }

    [Fact]
    public void FromConfig_SinPropiedad_Falla()
    {
        var (options, error) = SearchAnalyticsQuery.FromConfig(Config(), Today);

        Assert.Null(options);
        Assert.Contains("siteUrl", error);
    }

    [Fact]
    public void FromConfig_ValoresPorDefecto()
    {
        var (o, error) = SearchAnalyticsQuery.FromConfig(Config(("siteUrl", "sc-domain:ejemplo.com")), Today);

        Assert.Null(error);
        Assert.NotNull(o);
        Assert.Equal("web", o!.SearchType);
        Assert.Equal(1000, o.RowLimit);
        Assert.Equal("final", o.DataState);
        Assert.Equal("table", o.OutputFormat);
        // last28 con datos cerrados: termina 3 dias antes de hoy e incluye 28 dias.
        Assert.Equal(new DateOnly(2026, 9, 19), o.EndDate);
        Assert.Equal(new DateOnly(2026, 8, 23), o.StartDate);
    }

    [Fact]
    public void FromConfig_LeeTodosLosParametros()
    {
        var (o, error) = SearchAnalyticsQuery.FromConfig(Config(
            ("siteUrl", "https://www.ejemplo.com/"),
            ("dateRange", "last7"),
            ("dataState", "all"),
            ("dimensions", "page, query,page"),
            ("searchType", "image"),
            ("filters", "query contains zapatillas\n# comentario\n\npage notContains /blog/"),
            ("rowLimit", "500"),
            ("startRow", "100"),
            ("aggregationType", "byPage"),
            ("minImpressions", "50"),
            ("minClicks", "2"),
            ("minPosition", "8"),
            ("maxPosition", "20,5"),
            ("orderBy", "impressions"),
            ("outputFormat", "json")), Today);

        Assert.Null(error);
        Assert.Equal(["page", "query"], o!.Dimensions);
        Assert.Equal("image", o.SearchType);
        Assert.Equal(2, o.Filters.Count);
        Assert.Equal(new SearchAnalyticsFilter("page", "notContains", "/blog/"), o.Filters[1]);
        Assert.Equal(500, o.RowLimit);
        Assert.Equal(100, o.StartRow);
        Assert.Equal("byPage", o.AggregationType);
        Assert.Equal(50, o.MinImpressions);
        Assert.Equal(2, o.MinClicks);
        Assert.Equal(8, o.MinPosition);
        Assert.Equal(20.5, o.MaxPosition);
        Assert.Equal("impressions", o.OrderBy);
        Assert.Equal("json", o.OutputFormat);
        // "all" admite datos de ayer.
        Assert.Equal(new DateOnly(2026, 9, 21), o.EndDate);
        Assert.Equal(new DateOnly(2026, 9, 15), o.StartDate);
    }

    [Theory]
    [InlineData("dimensions", "keyword", "Dimension 'keyword'")]
    [InlineData("rowLimit", "30000", "limite de filas")]
    [InlineData("filters", "query contains", "incompleto")]
    [InlineData("filters", "date equals 2026-01-01", "No se puede filtrar por 'date'")]
    [InlineData("filters", "query like zapatillas", "Operador 'like'")]
    [InlineData("filters", "query contains {{keyword}}", "variable sin valor")]
    [InlineData("dateRange", "ayer", "Rango de fechas")]
    public void FromConfig_ParametrosInvalidos_DanErrorClaro(string key, string value, string expected)
    {
        var (options, error) = SearchAnalyticsQuery.FromConfig(
            Config(("siteUrl", "sc-domain:ejemplo.com"), (key, value)), Today);

        Assert.Null(options);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void FromConfig_PosicionMinimaMayorQueMaxima_Falla()
    {
        var (_, error) = SearchAnalyticsQuery.FromConfig(Config(
            ("siteUrl", "sc-domain:ejemplo.com"), ("minPosition", "20"), ("maxPosition", "8")), Today);

        Assert.Contains("posicion minima", error);
    }

    [Fact]
    public void FromConfig_PropiedadConVariableSinValor_Falla()
    {
        var (_, error) = SearchAnalyticsQuery.FromConfig(Config(("siteUrl", "{{web}}")), Today);

        Assert.Contains("variable sin valor", error);
    }

    [Fact]
    public void ResolveDates_Personalizado()
    {
        var (start, end, error) = SearchAnalyticsQuery.ResolveDates("custom", "2026-01-01", "2026-01-31", "final", Today);

        Assert.Null(error);
        Assert.Equal(new DateOnly(2026, 1, 1), start);
        Assert.Equal(new DateOnly(2026, 1, 31), end);
    }

    [Theory]
    [InlineData("", "2026-01-31")]
    [InlineData("2026-02-01", "2026-01-31")]
    [InlineData("01/01/2026", "2026-01-31")]
    public void ResolveDates_PersonalizadoInvalido(string start, string end)
    {
        var (_, _, error) = SearchAnalyticsQuery.ResolveDates("custom", start, end, "final", Today);

        Assert.NotNull(error);
    }

    [Fact]
    public void ResolveDates_16Meses()
    {
        var (start, end, _) = SearchAnalyticsQuery.ResolveDates("last16months", null, null, "final", Today);

        Assert.Equal(new DateOnly(2026, 9, 19), end);
        Assert.Equal(new DateOnly(2025, 5, 20), start);
    }

    [Fact]
    public void BuildRequestBody_IncluyeDimensionesYFiltros()
    {
        var (o, _) = SearchAnalyticsQuery.FromConfig(Config(
            ("siteUrl", "sc-domain:ejemplo.com"),
            ("dateRange", "custom"), ("startDate", "2026-01-01"), ("endDate", "2026-01-31"),
            ("dimensions", "query"),
            ("filters", "country = esp")), Today);

        var json = JsonSerializer.Serialize(SearchAnalyticsQuery.BuildRequestBody(o!));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("2026-01-01", root.GetProperty("startDate").GetString());
        Assert.Equal("2026-01-31", root.GetProperty("endDate").GetString());
        Assert.Equal("web", root.GetProperty("type").GetString());
        Assert.Equal(1000, root.GetProperty("rowLimit").GetInt32());
        Assert.Equal("query", root.GetProperty("dimensions")[0].GetString());
        var filter = root.GetProperty("dimensionFilterGroups")[0].GetProperty("filters")[0];
        Assert.Equal("country", filter.GetProperty("dimension").GetString());
        Assert.Equal("equals", filter.GetProperty("operator").GetString());
        Assert.Equal("esp", filter.GetProperty("expression").GetString());
    }

    [Fact]
    public void BuildRequestBody_SinDimensionesNiFiltros_NoLosEnvia()
    {
        var (o, _) = SearchAnalyticsQuery.FromConfig(Config(
            ("siteUrl", "sc-domain:ejemplo.com"), ("dimensions", "")), Today);

        var body = SearchAnalyticsQuery.BuildRequestBody(o!);

        Assert.False(body.ContainsKey("dimensions"));
        Assert.False(body.ContainsKey("dimensionFilterGroups"));
    }

    private const string ApiResponse = """
        {
          "rows": [
            { "keys": ["zapatillas running", "https://ejemplo.com/a"], "clicks": 10, "impressions": 1000, "ctr": 0.01, "position": 12.4 },
            { "keys": ["zapatillas", "https://ejemplo.com/b"], "clicks": 50, "impressions": 400, "ctr": 0.125, "position": 3.1 },
            { "keys": ["botas | trail", "https://ejemplo.com/c"], "clicks": 1, "impressions": 20, "ctr": 0.05, "position": 15 }
          ],
          "responseAggregationType": "byProperty"
        }
        """;

    [Fact]
    public void ParseRows_AsignaClavesPorDimension()
    {
        var rows = SearchAnalyticsQuery.ParseRows(ApiResponse, ["query", "page"]);

        Assert.Equal(3, rows.Count);
        Assert.Equal("zapatillas running", rows[0].Keys["query"]);
        Assert.Equal("https://ejemplo.com/a", rows[0].Keys["page"]);
        Assert.Equal(1000, rows[0].Impressions);
        Assert.Equal(12.4, rows[0].Position);
    }

    [Fact]
    public void ParseRows_SinFilas_DevuelveVacio()
    {
        Assert.Empty(SearchAnalyticsQuery.ParseRows("""{"responseAggregationType":"auto"}""", ["query"]));
    }

    [Fact]
    public void ApplyPostProcessing_FiltraPorMetricasYOrdena()
    {
        var (o, _) = SearchAnalyticsQuery.FromConfig(Config(
            ("siteUrl", "sc-domain:ejemplo.com"),
            ("minImpressions", "100"), ("minPosition", "8"), ("maxPosition", "20"),
            ("orderBy", "impressions")), Today);
        var rows = SearchAnalyticsQuery.ParseRows(ApiResponse, ["query", "page"]);

        var result = SearchAnalyticsQuery.ApplyPostProcessing(rows, o!);

        var only = Assert.Single(result);
        Assert.Equal("zapatillas running", only.Keys["query"]);
    }

    [Fact]
    public void ApplyPostProcessing_OrdenPorPosicion_MejorPrimero()
    {
        var (o, _) = SearchAnalyticsQuery.FromConfig(Config(
            ("siteUrl", "sc-domain:ejemplo.com"), ("orderBy", "position")), Today);
        var rows = SearchAnalyticsQuery.ParseRows(ApiResponse, ["query", "page"]);

        var result = SearchAnalyticsQuery.ApplyPostProcessing(rows, o!);

        Assert.Equal([3.1, 12.4, 15], result.Select(r => r.Position));
    }

    [Fact]
    public void Format_Tabla_EscapaBarrasYMuestraPorcentajes()
    {
        var (o, _) = SearchAnalyticsQuery.FromConfig(Config(
            ("siteUrl", "sc-domain:ejemplo.com"), ("dimensions", "query,page")), Today);
        var rows = SearchAnalyticsQuery.ParseRows(ApiResponse, o!.Dimensions);

        var text = SearchAnalyticsQuery.Format(rows, o);

        Assert.Contains("Filas: 3 · Clics: 61 · Impresiones: 1420", text);
        Assert.Contains("| query | page | clics | impresiones | ctr | posicion |", text);
        Assert.Contains("| zapatillas | https://ejemplo.com/b | 50 | 400 | 12.5% | 3.1 |", text);
        Assert.Contains(@"botas \| trail", text);
    }

    [Fact]
    public void Format_Json_IncluyeFilasConNombresDeDimension()
    {
        var (o, _) = SearchAnalyticsQuery.FromConfig(Config(
            ("siteUrl", "sc-domain:ejemplo.com"), ("dimensions", "query,page"), ("outputFormat", "json")), Today);
        var rows = SearchAnalyticsQuery.ParseRows(ApiResponse, o!.Dimensions);

        using var doc = JsonDocument.Parse(SearchAnalyticsQuery.Format(rows, o));
        var root = doc.RootElement;

        Assert.Equal(3, root.GetProperty("rowCount").GetInt32());
        Assert.Equal(61, root.GetProperty("totals").GetProperty("clicks").GetDouble());
        var first = root.GetProperty("rows")[0];
        Assert.Equal("zapatillas running", first.GetProperty("query").GetString());
        Assert.Equal("https://ejemplo.com/a", first.GetProperty("page").GetString());
    }

    [Fact]
    public void Format_Csv_EscapaComas()
    {
        var (o, _) = SearchAnalyticsQuery.FromConfig(Config(
            ("siteUrl", "sc-domain:ejemplo.com"), ("dimensions", "query"), ("outputFormat", "csv")), Today);
        var rows = SearchAnalyticsQuery.ParseRows(
            """{"rows":[{"keys":["a, b"],"clicks":1,"impressions":2,"ctr":0.5,"position":1.25}]}""", o!.Dimensions);

        var lines = SearchAnalyticsQuery.Format(rows, o).Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        Assert.Equal("query,clicks,impressions,ctr,position", lines[0]);
        Assert.Equal("\"a, b\",1,2,0.5,1.25", lines[1]);
    }
}

public class SearchConsoleCredentialsTests
{
    private static string ServiceAccountJson(RSA rsa) => JsonSerializer.Serialize(new Dictionary<string, string>
    {
        ["type"] = "service_account",
        ["client_email"] = "pixelagents@proyecto.iam.gserviceaccount.com",
        ["private_key"] = rsa.ExportPkcs8PrivateKeyPem(),
        ["token_uri"] = "https://oauth2.googleapis.com/token",
    });

    [Theory]
    [InlineData("")]
    [InlineData("no es json")]
    [InlineData("""{"client_email":"x@y.com"}""")]
    public void Parse_CredencialInvalida_DaErrorClaro(string json)
    {
        Assert.Throws<SearchConsoleException>(() => ServiceAccountCredentials.Parse(json));
    }

    [Fact]
    public void Parse_SinTokenUri_UsaElDeGoogle()
    {
        var creds = ServiceAccountCredentials.Parse("""{"client_email":"x@y.com","private_key":"k"}""");

        Assert.Equal(ServiceAccountCredentials.DefaultTokenUri, creds.TokenUri);
    }

    [Fact]
    public void BuildJwtAssertion_FirmaVerificableConLaClavePublica()
    {
        using var rsa = RSA.Create(2048);
        var creds = ServiceAccountCredentials.Parse(ServiceAccountJson(rsa));
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

        var jwt = SearchConsoleService.BuildJwtAssertion(creds, now);

        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);

        using var header = JsonDocument.Parse(FromBase64Url(parts[0]));
        Assert.Equal("RS256", header.RootElement.GetProperty("alg").GetString());

        using var claims = JsonDocument.Parse(FromBase64Url(parts[1]));
        Assert.Equal(creds.ClientEmail, claims.RootElement.GetProperty("iss").GetString());
        Assert.Equal(SearchConsoleService.Scope, claims.RootElement.GetProperty("scope").GetString());
        Assert.Equal(creds.TokenUri, claims.RootElement.GetProperty("aud").GetString());
        Assert.Equal(1_800_000_000, claims.RootElement.GetProperty("iat").GetInt64());
        Assert.Equal(1_800_003_600, claims.RootElement.GetProperty("exp").GetInt64());

        var valid = rsa.VerifyData(
            Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), FromBase64Url(parts[2]),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        Assert.True(valid);
    }

    [Fact]
    public void BuildJwtAssertion_ClaveNoPem_DaErrorClaro()
    {
        var creds = new ServiceAccountCredentials("x@y.com", "no-es-una-clave", ServiceAccountCredentials.DefaultTokenUri);

        Assert.Throws<SearchConsoleException>(() => SearchConsoleService.BuildJwtAssertion(creds, DateTimeOffset.UtcNow));
    }

    private static byte[] FromBase64Url(string s)
    {
        var b64 = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '='));
    }
}
