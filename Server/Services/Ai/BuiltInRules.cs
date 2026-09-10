namespace Server.Services.Ai
{
    /// <summary>
    /// Reglas constantes del producto: comportamiento, formato ASCII y veto de
    /// marcas. No viven en la BD del tenant, se anteponen al system prompt de
    /// todo modulo con salida de texto (<see cref="SystemPromptComposer"/>).
    ///
    /// Estan troceadas por clave para poder excluir una sola en un modulo
    /// concreto: las excepciones (tabla RuleExceptions) guardan estas claves, y
    /// <see cref="Compose"/> deja fuera las suprimidas. Sin excepciones el texto
    /// resultante es identico al de antes de trocearlo.
    ///
    /// Las mismas claves las usa el cliente para pintarlas
    /// (ActiveRulesRegistry.BuiltInTextRules); si cambia una, cambian las dos.
    /// </summary>
    public static class BuiltInRules
    {
        public const string BehaviorKey = "text-behavior";
        public const string FormatKey = "text-format";
        public const string BrandsKey = "text-brands";

        /// <summary>Prefijo de la clave de una regla propia del tenant en una excepcion.</summary>
        public const string TenantKeyPrefix = "tenant:";

        /// <summary>Bloque "text-behavior" tal cual viaja al modelo.</summary>
        private const string Behavior = @"Reglas generales:
- NUNCA hagas preguntas al usuario. Si hay varias opciones posibles, elige la mejor opcion tu mismo y da directamente la respuesta final.
- Se concreto y directo. No pidas aclaraciones, no ofrezcas alternativas, no preguntes preferencias. Decide y responde.";

        /// <summary>Bloque "text-format" tal cual viaja al modelo.</summary>
        private const string Format = @"Reglas de formato (OBLIGATORIAS):
- NO uses emojis ni emoticonos de ningun tipo (ni unicode ni shortcodes).
- NO uses formato markdown: nada de **, *, #, ##, ```, >, -, ni listas con vinetas.
- NO uses caracteres especiales decorativos: flechas, bullets, guiones largos, comillas tipograficas, simbolos como estrella, circulo, rombo, triangulo, flecha, etc.
- Usa solo texto plano ASCII basico: letras, numeros, puntuacion normal (. , ; : ! ? ' "").";

        /// <summary>Bloque "text-brands" tal cual viaja al modelo.</summary>
        private const string Brands = @"Reglas de contenido (OBLIGATORIAS):
- NUNCA menciones marcas, empresas, productos, servicios o nombres comerciales de ningun tipo. Esto incluye marcas de tecnologia, redes sociales, ropa, alimentacion, automocion, software, hardware, o cualquier otro sector.
- Si necesitas referirte a un concepto asociado a una marca, usa una descripcion generica. Por ejemplo: en vez de ""Instagram"" di ""redes sociales"", en vez de ""iPhone"" di ""telefono movil"", en vez de ""Photoshop"" di ""editor de imagenes"".
- No uses nombres de marcas ni siquiera como referencia, comparacion, ejemplo o metafora.";

        /// <summary>Las constantes en el orden en que se envian.</summary>
        public static IReadOnlyList<(string Key, string Text)> All { get; } =
        [
            (BehaviorKey, Behavior),
            (FormatKey, Format),
            (BrandsKey, Brands),
        ];

        /// <summary>Clave con la que se excepciona una regla propia del tenant.</summary>
        public static string TenantKey(Guid ruleId) => TenantKeyPrefix + ruleId;

        /// <summary>Id de la regla del tenant que hay detras de una clave, si lo es.</summary>
        public static Guid? TenantRuleId(string key) =>
            key.StartsWith(TenantKeyPrefix, StringComparison.Ordinal)
            && Guid.TryParse(key[TenantKeyPrefix.Length..], out var id)
                ? id
                : null;

        public static bool IsBuiltInKey(string key) =>
            All.Any(r => string.Equals(r.Key, key, StringComparison.Ordinal));

        /// <summary>
        /// Texto de las constantes que aplican, saltando las suprimidas para el
        /// modulo. Devuelve cadena vacia si se han suprimido todas.
        /// </summary>
        public static string Compose(IReadOnlyCollection<string>? suppressed = null)
        {
            var aplican = suppressed is null || suppressed.Count == 0
                ? All
                : All.Where(r => !suppressed.Contains(r.Key)).ToList();

            return string.Join("\n\n", aplican.Select(r => r.Text));
        }
    }
}
