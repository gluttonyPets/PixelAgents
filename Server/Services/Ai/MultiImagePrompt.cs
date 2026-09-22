using System.Text.Json;
using System.Text.RegularExpressions;

namespace Server.Services.Ai;

/// <summary>
/// Reparto de un prompt en varias imagenes.
///
/// La API de imagenes de OpenAI (y las equivalentes) entiende "n" como numero de
/// MUESTRAS del mismo prompt, no como numero de partes distintas: las n imagenes
/// se generan de forma independiente y todas reciben el prompt entero. Por eso
/// pedir n=2 sobre un prompt que describe dos slides devuelve dos veces la misma
/// composicion con las dos slides dentro, no una slide por imagen.
///
/// La unica forma de obtener N imagenes con contenidos distintos es hacer N
/// llamadas con N prompts distintos. Esta clase es el contrato entre las dos
/// puntas de esa cadena:
///   - el modulo de texto que planifica recibe <see cref="BuildPlannerInstruction"/>
///     y escribe un bloque por imagen separado por marcas;
///   - el modulo de imagen usa <see cref="Split"/> para deshacer ese texto en
///     partes y lanzar una llamada por parte.
///
/// El texto planificado puede llegar de dos formas, porque el editor permite las
/// dos: con las marcas <c>===IMAGEN n===</c>, o como el JSON del contrato que
/// declare la conexion (por ejemplo <c>{"tomas":[{"prompt":"..."}, ...]}</c>)
/// cuando el mismo modulo de texto alimenta ademas a un orquestador. En el
/// segundo caso cada elemento de la lista es el prompt de una imagen.
/// </summary>
public static class MultiImagePrompt
{
    /// <summary>Tope de imagenes por modulo, el mismo que admite el editor.</summary>
    public const int MaxImages = 20;

    /// <summary>Resultado de repartir el texto de entrada.</summary>
    /// <param name="Common">Texto anterior a la primera marca: contexto que se
    /// antepone a TODAS las imagenes (estilo, paleta, referencias compartidas).</param>
    /// <param name="Segments">Un prompt por imagen, en orden. Vacio si el texto
    /// no venia segmentado.</param>
    public sealed record SplitResult(string Common, List<string> Segments);

    // Palabras que abren una marca de escena. Se aceptan varias porque el texto
    // lo escribe un modelo: la instruccion pide "IMAGEN n", pero conviene tolerar
    // los sinonimos que suele usar por su cuenta.
    private const string Keywords = "IMAGEN|IMAGENES|IMAGE|SLIDE|ESCENA|PARTE";

    // Una marca ocupa el principio de su linea. Sin el ancla, cualquier "imagen 2"
    // dentro de una frase partiria el prompt por la mitad.
    private static readonly Regex MarkerRx = new(
        $@"^[ \t]*[=\-*#_]{{0,10}}[ \t]*(?:{Keywords})[ \t]*\#?[ \t]*(?<n>\d{{1,2}})[ \t]*[=\-*#_]{{0,10}}[ \t]*[:.\)\-]?[ \t]*",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// Cuantas imagenes debe producir un modulo de imagen. Se leen las tres claves
    /// que ha ido usando el editor: "n" es la que escribe hoy, "numberOfImages" la
    /// que esperan algunos proveedores y "sceneCount" la que decide cuantos puertos
    /// de salida se pintan. Si solo esta la ultima, mandan los puertos: generar una
    /// imagen sola dejaria puertos conectados sin datos.
    /// </summary>
    public static int ReadImageCount(IReadOnlyDictionary<string, object>? config)
    {
        if (config is null) return 1;

        foreach (var key in new[] { "n", "numberOfImages", "sceneCount" })
        {
            if (!config.TryGetValue(key, out var raw)) continue;
            var parsed = ParseInt(raw);
            if (parsed is int value)
                return Math.Clamp(value, 1, MaxImages);
        }

        return 1;
    }

    /// <summary>
    /// Igual que <see cref="ReadImageCount"/> pero leyendo la configuracion cruda
    /// de otro modulo del grafo (la del catalogo mas la del paso, que manda).
    /// Lo necesita el modulo de texto para saber cuantas imagenes le va a pedir
    /// el modulo de imagen que tiene detras.
    /// </summary>
    public static int ReadImageCountFromJson(string? moduleConfig, string? stepConfig)
    {
        var merged = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        MergeJson(moduleConfig, merged);
        MergeJson(stepConfig, merged);
        return ReadImageCount(merged);
    }

    private static void MergeJson(string? json, Dictionary<string, object> target)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            var values = System.Text.Json.JsonSerializer
                .Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(json);
            if (values is null) return;
            foreach (var (key, value) in values)
                target[key] = value.Clone();
        }
        catch { /* config malformada: se ignora, como en el resto del executor */ }
    }

    /// <summary>Marca que separa el prompt de la imagen indicada (base 1).</summary>
    public static string BuildMarker(int index) => $"===IMAGEN {index}===";

    /// <summary>
    /// Instruccion que se inyecta en el modulo de texto que alimenta a un modulo
    /// de imagen con mas de una salida. Sin ella el planificador escribe un unico
    /// prompt compuesto y no hay nada que repartir.
    /// </summary>
    public static string BuildPlannerInstruction(int count)
    {
        var ejemplo = string.Join("\n", Enumerable.Range(1, Math.Min(count, 3))
            .Select(i => $"{BuildMarker(i)}\n(prompt completo y autocontenido de la imagen {i})"));

        return $"""
            IMPORTANTE - PLANIFICACION MULTI-IMAGEN: tu texto alimenta un modulo que va a generar {count} imagenes INDEPENDIENTES, con una llamada distinta por imagen.
            Escribe {count} prompts de imagen separados EXACTAMENTE con estas marcas, cada una sola en su linea:
            {ejemplo}

            Reglas de la separacion:
            - Cada bloque describe UNA sola imagen y tiene que ser autocontenido: el modelo que lo lea no vera los demas bloques ni sabra que existen.
            - No repartas una misma composicion entre bloques ni metas todas las partes en cada bloque.
            - Lo que escribas ANTES de la marca {BuildMarker(1)} se envia como contexto comun a todas las imagenes: usalo solo para lo que compartan (estilo, paleta, formato, referencias comunes) y manten esa parte BREVE, de dos a cuatro lineas.
            - No describas ahi el contenido de ninguna imagen concreta: cada modelo de imagen recibe un prompt corto (unos 4000 caracteres) y lo que sobra se recorta, asi que todo lo que gaste el contexto comun se lo quita a los bloques.
            - Numera las marcas de 1 a {count}, en orden y sin saltarte ninguna.
            - Las marcas son separadores tecnicos, no decoracion: escribelas tal cual aunque otras reglas pidan texto plano sin simbolos.
            """;
    }

    /// <summary>
    /// Variante de <see cref="BuildPlannerInstruction"/> para cuando la conexion
    /// ya declara un contrato JSON propio: ahi las marcas romperian el JSON, asi
    /// que el reparto se pide sobre la lista del contrato (un elemento por imagen)
    /// y <see cref="Split"/> la deshace igual que las marcas.
    /// </summary>
    public static string BuildJsonPlannerInstruction(int count)
    {
        return $"""
            IMPORTANTE - PLANIFICACION MULTI-IMAGEN DENTRO DEL CONTRATO JSON: el JSON que devuelvas alimenta un modulo que va a generar {count} imagenes INDEPENDIENTES, con una llamada distinta por imagen.
            - La lista principal del contrato debe traer EXACTAMENTE {count} elementos, en orden, uno por imagen.
            - Cada elemento tiene que ser autocontenido: el modelo que genere la imagen i solo vera ese elemento, no los demas ni sabra que existen.
            - No repartas una misma composicion entre varios elementos ni metas el contenido de todas las imagenes en cada elemento.
            - El estilo, la paleta y el formato compartidos van repetidos de forma BREVE en cada elemento, o en una clave suelta fuera de la lista: dentro de cada elemento describe solo SU imagen.
            - No uses las marcas ===IMAGEN n=== aqui: romperian el JSON del contrato.
            """;
    }

    /// <summary>
    /// Reparte los textos de entrada de un modulo de imagen. Cada texto viene de
    /// una conexion distinta (fan-in): normalmente uno trae los prompts segmentados
    /// y el resto son contexto (por ejemplo el indice del modulo Directorio), asi
    /// que los no segmentados se acumulan enteros en la parte comun en vez de caer
    /// dentro de una sola escena.
    /// </summary>
    public static SplitResult Split(IEnumerable<string?> texts)
    {
        var common = new List<string>();
        var segments = new List<string>();

        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;

            var (preamble, parts) = SplitOne(text!);
            if (!string.IsNullOrWhiteSpace(preamble)) common.Add(preamble.Trim());
            segments.AddRange(parts);
        }

        return new SplitResult(string.Join("\n\n", common), segments);
    }

    /// <summary>Reparte un unico texto. Sin marcas validas devuelve el texto entero como preambulo.</summary>
    private static (string Preamble, List<string> Segments) SplitOne(string text)
    {
        var markers = new List<Match>();
        var expected = 1;
        foreach (Match m in MarkerRx.Matches(text))
        {
            // Solo se aceptan marcas correlativas empezando en 1. Es lo que separa
            // una segmentacion real de una frase que empieza por "Imagen 2 ...".
            if (!int.TryParse(m.Groups["n"].Value, out var index) || index != expected) continue;
            markers.Add(m);
            expected++;
        }

        // Con una sola marca no hay reparto por marcas. Antes de rendirse se
        // prueba el contrato JSON, que es la otra forma en que llega planificado.
        if (markers.Count < 2) return SplitJson(text) ?? (text, []);

        var segments = new List<string>(markers.Count);
        for (var i = 0; i < markers.Count; i++)
        {
            var start = markers[i].Index + markers[i].Length;
            var end = i + 1 < markers.Count ? markers[i + 1].Index : text.Length;
            var body = text[start..end].Trim();
            if (!string.IsNullOrWhiteSpace(body)) segments.Add(body);
        }

        // Si alguna marca venia vacia se pierde la correspondencia marca-imagen,
        // asi que es preferible no repartir a repartir mal.
        if (segments.Count != markers.Count) return (text, []);

        return (text[..markers[0].Index], segments);
    }

    /// <summary>Claves que suele usar el contrato JSON para el texto de cada parte.</summary>
    private static readonly string[] PromptKeys =
    [
        "prompt", "texto", "text", "content", "contenido",
        "descripcion", "description", "imagen", "image", "escena", "scene", "toma",
    ];

    /// <summary>
    /// Reparto alternativo para el texto que llega como JSON: el que escribe un
    /// modulo de texto cuya conexion declara un contrato (ver
    /// <c>OutputSchemaHelper.GetOutputFormatInstruction</c>). Se busca la primera
    /// lista con dos o mas elementos y cada elemento pasa a ser el prompt de una
    /// imagen; el resto de claves sueltas del objeto raiz son contexto comun.
    /// Devuelve null si el texto no es JSON o no hay lista que repartir, para que
    /// el llamante lo trate como un texto normal.
    /// </summary>
    private static (string Preamble, List<string> Segments)? SplitJson(string text)
    {
        var trimmed = StripCodeFence(text);
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '[')) return null;

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            root = doc.RootElement.Clone();
        }
        catch (JsonException) { return null; }

        if (root.ValueKind == JsonValueKind.Array)
        {
            var items = ReadJsonItems(root);
            return items.Count >= 2 ? ("", items) : null;
        }

        if (root.ValueKind != JsonValueKind.Object) return null;

        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Array) continue;

            var items = ReadJsonItems(prop.Value);
            if (items.Count < 2) continue;

            // Lo que acompana a la lista (estilo, paleta, formato) vale para todas.
            var common = new List<string>();
            foreach (var other in root.EnumerateObject())
            {
                if (other.NameEquals(prop.Name)) continue;
                var scalar = ReadJsonScalar(other.Value);
                if (!string.IsNullOrWhiteSpace(scalar)) common.Add($"{other.Name}: {scalar}");
            }

            return (string.Join("\n", common), items);
        }

        return null;
    }

    /// <summary>
    /// Texto de cada elemento de la lista. Si algun elemento se queda vacio se
    /// devuelve la lista vacia: se pierde la correspondencia elemento-imagen y es
    /// preferible no repartir a repartir mal (igual que con las marcas).
    /// </summary>
    private static List<string> ReadJsonItems(JsonElement array)
    {
        var items = new List<string>();

        foreach (var element in array.EnumerateArray())
        {
            string? value;
            if (element.ValueKind == JsonValueKind.Object)
            {
                // Primero la clave que nombra el prompt; si el objeto usa otro
                // nombre se vuelcan sus campos simples, que es lo que describe
                // la imagen de todas formas.
                value = PromptKeys
                    .Select(key => element.TryGetProperty(key, out var p) ? ReadJsonScalar(p) : null)
                    .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

                if (string.IsNullOrWhiteSpace(value))
                    value = string.Join("\n", element.EnumerateObject()
                        .Select(p => (p.Name, Value: ReadJsonScalar(p.Value)))
                        .Where(p => !string.IsNullOrWhiteSpace(p.Value))
                        .Select(p => $"{p.Name}: {p.Value}"));
            }
            else
            {
                value = ReadJsonScalar(element);
            }

            if (string.IsNullOrWhiteSpace(value)) return [];
            items.Add(value.Trim());
        }

        return items;
    }

    /// <summary>Valor simple de un elemento JSON como texto; null si no lo es.</summary>
    private static string? ReadJsonScalar(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True or JsonValueKind.False => element.GetRawText(),
        _ => null,
    };

    /// <summary>Quita el ```json ... ``` con el que algunos modelos envuelven el JSON.</summary>
    private static string StripCodeFence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;

        var firstBreak = trimmed.IndexOf('\n');
        if (firstBreak < 0) return trimmed;

        var body = trimmed[(firstBreak + 1)..];
        var fence = body.LastIndexOf("```", StringComparison.Ordinal);
        return (fence >= 0 ? body[..fence] : body).Trim();
    }

    private static int? ParseInt(object? raw) => raw switch
    {
        int i => i,
        long l => (int)l,
        double d => (int)d,
        System.Text.Json.JsonElement je => je.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Number when je.TryGetInt32(out var n) => n,
            System.Text.Json.JsonValueKind.String when int.TryParse(je.GetString(), out var s) => s,
            _ => null,
        },
        string s when int.TryParse(s, out var parsed) => parsed,
        _ => null,
    };
}
