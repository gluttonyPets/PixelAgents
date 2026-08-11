using System.Globalization;
using System.Text.RegularExpressions;

namespace Server.Services.Ai;

/// <summary>
/// Evaluador determinista de las condiciones escritas del modulo Condicional.
///
/// Entiende un mini-lenguaje pensado para escribirse en castellano (o ingles)
/// sobre el texto que llega al nodo. Ejemplos validos:
///
///   contiene "descuento"
///   no contiene "error"
///   empieza por "OK" y longitud &gt; 200
///   el resultado es igual a "aprobado" o contiene "validado"
///   longitud mayor que 500
///   palabras &gt;= 50
///   no esta vacio
///   coincide con /^[0-9]{4}$/
///   numero &gt; 10
///
/// Las comparaciones de texto ignoran mayusculas y acentos. Los terminos se
/// combinan con "y"/"and"/"&amp;&amp;" (todos deben cumplirse) y "o"/"or"/"||"
/// (basta con uno); la coma equivale a "y". No hay precedencia de parentesis:
/// se evalua como una disyuncion de conjunciones (A y B) o (C).
///
/// Cuando la condicion no encaja con esta gramatica el evaluador lo indica con
/// <see cref="EvaluationResult.Parsed"/> = false para que el handler pueda
/// delegarla en la IA en lugar de inventarse un resultado.
/// </summary>
public static class ConditionEvaluator
{
    /// <param name="Parsed">true si la condicion se pudo entender por completo.</param>
    /// <param name="Value">Resultado de la condicion (solo valido si Parsed).</param>
    /// <param name="Explanation">Motivo legible: que termino se cumplio y cual no.</param>
    public sealed record EvaluationResult(bool Parsed, bool Value, string Explanation);

    private enum Op
    {
        Contains, NotContains,
        StartsWith, NotStartsWith,
        EndsWith, NotEndsWith,
        Equals, NotEquals,
        Regex,
        Empty, NotEmpty,
        Length, Words, Number,
    }

    private enum Cmp { Gt, Ge, Lt, Le, Eq, Ne }

    // Palabras que el usuario puede anteponer para referirse a la entrada del
    // nodo ("el texto contiene X"). Se descartan antes de buscar el operador.
    private static readonly string[] Subjects =
    [
        "el texto de entrada", "el texto anterior", "la respuesta anterior",
        "el resultado anterior", "la salida anterior", "el modulo anterior",
        "el contenido", "la respuesta", "el resultado", "la entrada", "la salida",
        "el texto", "el input", "el output", "el valor",
        "contenido", "respuesta", "resultado", "entrada", "salida",
        "texto", "input", "output", "valor",
    ];

    // Operadores ordenados de mas largo a mas corto: el primero que encaja gana,
    // asi "es igual a" no lo captura "es".
    private static readonly (string Keyword, Op Op)[] Operators =
    [
        ("no esta vacio", Op.NotEmpty), ("no esta vacia", Op.NotEmpty),
        ("no esta en blanco", Op.NotEmpty), ("no vacio", Op.NotEmpty),
        ("no vacia", Op.NotEmpty), ("not empty", Op.NotEmpty), ("no is empty", Op.NotEmpty),
        ("esta vacio", Op.Empty), ("esta vacia", Op.Empty), ("esta en blanco", Op.Empty),
        ("is empty", Op.Empty), ("vacio", Op.Empty), ("vacia", Op.Empty), ("empty", Op.Empty),

        ("no contiene", Op.NotContains), ("no incluye", Op.NotContains),
        ("no menciona", Op.NotContains), ("does not contain", Op.NotContains),
        ("not contains", Op.NotContains),
        ("contiene", Op.Contains), ("incluye", Op.Contains), ("menciona", Op.Contains),
        ("contains", Op.Contains),

        ("no empieza por", Op.NotStartsWith), ("no empieza con", Op.NotStartsWith),
        ("no comienza por", Op.NotStartsWith), ("no comienza con", Op.NotStartsWith),
        ("empieza por", Op.StartsWith), ("empieza con", Op.StartsWith),
        ("comienza por", Op.StartsWith), ("comienza con", Op.StartsWith),
        ("starts with", Op.StartsWith),

        ("no termina en", Op.NotEndsWith), ("no termina con", Op.NotEndsWith),
        ("no acaba en", Op.NotEndsWith), ("no acaba con", Op.NotEndsWith),
        ("termina en", Op.EndsWith), ("termina con", Op.EndsWith), ("termina por", Op.EndsWith),
        ("acaba en", Op.EndsWith), ("acaba con", Op.EndsWith), ("ends with", Op.EndsWith),

        ("coincide con", Op.Regex), ("coincide", Op.Regex), ("matches", Op.Regex),
        ("regex", Op.Regex),

        ("numero de palabras", Op.Words), ("palabras", Op.Words), ("words", Op.Words),
        ("numero de caracteres", Op.Length), ("longitud", Op.Length),
        ("length", Op.Length), ("caracteres", Op.Length),
        ("numero", Op.Number), ("number", Op.Number), ("cifra", Op.Number),

        ("no es igual a", Op.NotEquals), ("no es igual que", Op.NotEquals),
        ("distinto de", Op.NotEquals), ("distinto a", Op.NotEquals),
        ("distinta de", Op.NotEquals), ("no es", Op.NotEquals), ("not equals", Op.NotEquals),
        ("es igual a", Op.Equals), ("es igual que", Op.Equals), ("es exactamente", Op.Equals),
        ("igual a", Op.Equals), ("igual que", Op.Equals), ("equals", Op.Equals),
        ("es", Op.Equals),
    ];

    private static readonly (string Keyword, Cmp Cmp)[] Comparators =
    [
        ("mayor o igual que", Cmp.Ge), ("mayor o igual a", Cmp.Ge),
        ("menor o igual que", Cmp.Le), ("menor o igual a", Cmp.Le),
        ("como minimo", Cmp.Ge), ("al menos", Cmp.Ge), ("minimo", Cmp.Ge),
        ("como maximo", Cmp.Le), ("maximo", Cmp.Le),
        ("mayor que", Cmp.Gt), ("mayor de", Cmp.Gt), ("mayor a", Cmp.Gt),
        ("mas de", Cmp.Gt), ("superior a", Cmp.Gt), ("greater than", Cmp.Gt),
        ("menor que", Cmp.Lt), ("menor de", Cmp.Lt), ("menor a", Cmp.Lt),
        ("menos de", Cmp.Lt), ("inferior a", Cmp.Lt), ("less than", Cmp.Lt),
        ("distinto de", Cmp.Ne), ("distinta de", Cmp.Ne), ("no es", Cmp.Ne),
        ("es igual a", Cmp.Eq), ("igual a", Cmp.Eq), ("igual que", Cmp.Eq),
        ("equals", Cmp.Eq), ("igual", Cmp.Eq), ("es", Cmp.Eq),
        (">=", Cmp.Ge), ("<=", Cmp.Le), ("==", Cmp.Eq), ("!=", Cmp.Ne), ("<>", Cmp.Ne),
        (">", Cmp.Gt), ("<", Cmp.Lt), ("=", Cmp.Eq),
    ];

    private static readonly string[] TrueLiterals = ["true", "verdadero", "si", "yes", "1", "siempre"];
    private static readonly string[] FalseLiterals = ["false", "falso", "no", "0", "nunca"];

    /// <summary>Evalua <paramref name="condition"/> contra <paramref name="input"/>.</summary>
    public static EvaluationResult Evaluate(string? condition, string? input)
    {
        var text = input ?? "";
        if (string.IsNullOrWhiteSpace(condition))
            return new EvaluationResult(false, false, "La condicion esta vacia.");

        var raw = StripLeadingIf(condition.Trim());
        var whole = new Text(raw, Normalize(raw));

        var result = false;
        var reasons = new List<string>();

        foreach (var orPart in SplitTop(whole, ["o", "or"], ["||"]))
        {
            // La coma no separa: se usa dentro de numeros decimales y de textos
            // sin comillas ("contiene hola, mundo").
            var terms = SplitTop(orPart, ["y", "and"], ["&&", ";"]);
            var conjunction = true;

            foreach (var term in terms)
            {
                if (!TryEvaluateTerm(term, text, out var termValue, out var reason))
                    return new EvaluationResult(false, false, reason);

                reasons.Add(reason);
                if (!termValue) conjunction = false;
            }

            if (terms.Count > 0 && conjunction) result = true;
        }

        if (reasons.Count == 0)
            return new EvaluationResult(false, false, "No se ha podido interpretar la condicion.");

        return new EvaluationResult(true, result, string.Join("; ", reasons));
    }

    // ── Terminos ──

    private static bool TryEvaluateTerm(Text term, string input, out bool value, out string reason)
    {
        value = false;
        var t = StripSubject(StripParens(term));

        if (t.Norm.Length == 0)
        {
            reason = "Termino vacio en la condicion.";
            return false;
        }

        if (TrueLiterals.Contains(t.Norm)) { value = true; reason = $"'{t.Raw}' = siempre cierto"; return true; }
        if (FalseLiterals.Contains(t.Norm)) { value = false; reason = $"'{t.Raw}' = siempre falso"; return true; }

        var match = Operators.FirstOrDefault(o => StartsWithKeyword(t.Norm, o.Keyword));
        if (match.Keyword is null)
        {
            // Comparacion numerica directa sobre la entrada: "> 10", "al menos 3".
            if (TryParseComparison(t, requireComparator: true, out _, out _, out _))
                return TryEvaluateNumeric(Op.Number, t, input, out value, out reason);

            reason = $"No se entiende el termino '{t.Raw}'.";
            return false;
        }

        var operand = t.Slice(match.Keyword.Length);

        switch (match.Op)
        {
            case Op.Empty:
                value = string.IsNullOrWhiteSpace(input);
                reason = $"esta vacio = {Yn(value)}";
                return true;

            case Op.NotEmpty:
                value = !string.IsNullOrWhiteSpace(input);
                reason = $"no esta vacio = {Yn(value)}";
                return true;

            case Op.Regex:
                return TryEvaluateRegex(operand, input, out value, out reason);

            case Op.Length:
            case Op.Words:
            case Op.Number:
                return TryEvaluateNumeric(match.Op, operand, input, out value, out reason);
        }

        var needle = Unquote(operand);
        if (needle.Norm.Length == 0)
        {
            reason = $"Falta el valor a comparar en '{t.Raw}'.";
            return false;
        }

        var haystack = Normalize(input);
        value = match.Op switch
        {
            Op.Contains => haystack.Contains(needle.Norm, StringComparison.Ordinal),
            Op.NotContains => !haystack.Contains(needle.Norm, StringComparison.Ordinal),
            Op.StartsWith => haystack.TrimStart().StartsWith(needle.Norm, StringComparison.Ordinal),
            Op.NotStartsWith => !haystack.TrimStart().StartsWith(needle.Norm, StringComparison.Ordinal),
            Op.EndsWith => haystack.TrimEnd().EndsWith(needle.Norm, StringComparison.Ordinal),
            Op.NotEndsWith => !haystack.TrimEnd().EndsWith(needle.Norm, StringComparison.Ordinal),
            Op.Equals => haystack.Trim() == needle.Norm,
            Op.NotEquals => haystack.Trim() != needle.Norm,
            _ => false,
        };

        reason = $"{match.Keyword} '{needle.Raw}' = {Yn(value)}";
        return true;
    }

    private static bool TryEvaluateRegex(Text operand, string input, out bool value, out string reason)
    {
        value = false;
        var pattern = Unquote(operand).Raw.Trim();
        if (pattern.Length > 1 && pattern[0] == '/' && pattern[^1] == '/')
            pattern = pattern[1..^1];

        if (pattern.Length == 0)
        {
            reason = "Falta la expresion regular a comparar.";
            return false;
        }

        try
        {
            value = Regex.IsMatch(input, pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            reason = $"Expresion regular invalida ({ex.Message}).";
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            reason = "La expresion regular ha tardado demasiado en evaluarse.";
            return false;
        }

        reason = $"coincide con /{pattern}/ = {Yn(value)}";
        return true;
    }

    private static bool TryEvaluateNumeric(Op op, Text operand, string input, out bool value, out string reason)
    {
        value = false;

        if (!TryParseComparison(operand, requireComparator: false, out var cmp, out var expected, out var cmpLabel))
        {
            reason = $"Falta la comparacion numerica despues de '{op.ToString().ToLowerInvariant()}'.";
            return false;
        }

        double actual;
        string label;
        switch (op)
        {
            case Op.Length:
                actual = input.Trim().Length;
                label = "longitud";
                break;
            case Op.Words:
                actual = input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
                label = "palabras";
                break;
            default:
                var numeric = FirstNumber(input);
                if (numeric is null)
                {
                    reason = "La entrada no contiene ningun numero que comparar.";
                    return false;
                }
                actual = numeric.Value;
                label = "numero";
                break;
        }

        value = cmp switch
        {
            Cmp.Gt => actual > expected,
            Cmp.Ge => actual >= expected,
            Cmp.Lt => actual < expected,
            Cmp.Le => actual <= expected,
            Cmp.Eq => Math.Abs(actual - expected) < 0.000001,
            Cmp.Ne => Math.Abs(actual - expected) >= 0.000001,
            _ => false,
        };

        reason = $"{label} ({Fmt(actual)}) {cmpLabel} {Fmt(expected)} = {Yn(value)}";
        return true;
    }

    /// <param name="requireComparator">true cuando el operador de comparacion es
    /// obligatorio (p. ej. "&gt; 10" suelto); false cuando puede omitirse y se
    /// asume igualdad ("longitud 100").</param>
    private static bool TryParseComparison(
        Text operand, bool requireComparator, out Cmp cmp, out double number, out string label)
    {
        cmp = Cmp.Eq;
        number = 0;
        label = "=";

        var text = operand;
        var match = Comparators.FirstOrDefault(c => StartsWithKeyword(text.Norm, c.Keyword));
        if (match.Keyword is not null)
        {
            cmp = match.Cmp;
            label = match.Keyword;
            text = text.Slice(match.Keyword.Length);
        }
        else if (requireComparator)
        {
            return false;
        }

        var parsed = FirstNumber(text.Raw);
        if (parsed is null) return false;

        number = parsed.Value;
        return true;
    }

    // ── Utilidades de texto ──

    /// <summary>Par (texto original, texto normalizado). Ambos tienen la misma
    /// longitud, asi que un indice sirve para cortar los dos a la vez.</summary>
    private readonly record struct Text(string Raw, string Norm)
    {
        public Text Slice(int start)
        {
            if (start >= Raw.Length) return new Text("", "");
            return new Text(Raw[start..].Trim(), Norm[start..].Trim());
        }
    }

    private static Text StripParens(Text t)
    {
        var raw = t.Raw;
        var norm = t.Norm;
        while (raw.Length > 1 && raw[0] == '(' && raw[^1] == ')')
        {
            raw = raw[1..^1].Trim();
            norm = norm[1..^1].Trim();
        }
        return new Text(raw, norm);
    }

    private static Text StripSubject(Text t)
    {
        var subject = Subjects.FirstOrDefault(s => StartsWithKeyword(t.Norm, s));
        if (subject is null) return t;

        var rest = t.Slice(subject.Length);
        // "el texto" solo era el sujeto si detras queda un operador; si no, el
        // usuario estaba comparando literalmente esa palabra.
        return rest.Norm.Length > 0 ? rest : t;
    }

    private static string StripLeadingIf(string condition)
    {
        var norm = Normalize(condition);
        foreach (var prefix in new[] { "si ", "if ", "solo si ", "unicamente si ", "cuando " })
        {
            if (norm.StartsWith(prefix, StringComparison.Ordinal))
                return condition[prefix.Length..].Trim();
        }
        return condition;
    }

    private static Text Unquote(Text t)
    {
        var raw = t.Raw.Trim();
        var norm = t.Norm.Trim();
        if (raw.Length >= 2 && (raw[0] == '"' || raw[0] == '\'') && raw[^1] == raw[0])
        {
            raw = raw[1..^1];
            norm = norm[1..^1];
        }
        return new Text(raw, norm);
    }

    private static bool StartsWithKeyword(string norm, string keyword)
    {
        if (!norm.StartsWith(keyword, StringComparison.Ordinal)) return false;
        if (norm.Length == keyword.Length) return true;

        // Un operador alfabetico debe terminar en frontera de palabra ("es" no
        // puede casar dentro de "estado"); los simbolicos (>, ==) no lo exigen.
        if (!char.IsLetterOrDigit(keyword[^1])) return true;
        return !char.IsLetterOrDigit(norm[keyword.Length]);
    }

    private static double? FirstNumber(string text)
    {
        var m = Regex.Match(text, @"-?\d+(?:[.,]\d+)?");
        if (!m.Success) return null;
        var value = m.Value.Replace(',', '.');
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    private static string Fmt(double value) =>
        value == Math.Floor(value)
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Yn(bool value) => value ? "si" : "no";

    /// <summary>
    /// Minusculas sin acentos, conservando la longitud del texto original para
    /// poder cortar el original y el normalizado con el mismo indice.
    /// </summary>
    private static string Normalize(string text)
    {
        var chars = new char[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            var c = char.ToLowerInvariant(text[i]);
            chars[i] = c switch
            {
                'á' or 'à' or 'â' or 'ä' or 'ã' => 'a',
                'é' or 'è' or 'ê' or 'ë' => 'e',
                'í' or 'ì' or 'î' or 'ï' => 'i',
                'ó' or 'ò' or 'ô' or 'ö' or 'õ' => 'o',
                'ú' or 'ù' or 'û' or 'ü' => 'u',
                'ñ' => 'n',
                'ç' => 'c',
                '“' or '”' or '«' or '»' => '"',
                '‘' or '’' => '\'',
                _ => c,
            };
        }
        return new string(chars);
    }

    /// <summary>
    /// Parte el texto por los separadores dados respetando comillas y
    /// parentesis. <paramref name="words"/> se exigen como palabra completa;
    /// <paramref name="symbols"/> se buscan tal cual.
    /// </summary>
    private static List<Text> SplitTop(Text text, string[] words, string[] symbols)
    {
        var parts = new List<Text>();
        var norm = text.Norm;
        var start = 0;
        var depth = 0;
        var quote = '\0';

        for (var i = 0; i < norm.Length; i++)
        {
            var c = norm[i];

            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'') { quote = c; continue; }
            if (c == '(') { depth++; continue; }
            if (c == ')') { if (depth > 0) depth--; continue; }
            if (depth > 0) continue;

            var length = MatchSeparator(norm, i, words, symbols);
            if (length == 0) continue;

            parts.Add(Segment(text, start, i));
            i += length - 1;
            start = i + 1;
        }

        parts.Add(Segment(text, start, norm.Length));
        return parts.Where(p => p.Norm.Length > 0).ToList();
    }

    private static Text Segment(Text text, int start, int end) =>
        new(text.Raw[start..end].Trim(), text.Norm[start..end].Trim());

    private static int MatchSeparator(string norm, int index, string[] words, string[] symbols)
    {
        foreach (var symbol in symbols)
        {
            if (index + symbol.Length <= norm.Length
                && norm.AsSpan(index, symbol.Length).SequenceEqual(symbol))
                return symbol.Length;
        }

        // Un separador de palabra necesita frontera a ambos lados para que
        // "yogur" no se parta por la "y".
        if (index > 0 && char.IsLetterOrDigit(norm[index - 1])) return 0;

        foreach (var word in words)
        {
            if (index + word.Length > norm.Length) continue;
            if (!norm.AsSpan(index, word.Length).SequenceEqual(word)) continue;
            if (index + word.Length < norm.Length && char.IsLetterOrDigit(norm[index + word.Length])) continue;
            return word.Length;
        }

        return 0;
    }
}
