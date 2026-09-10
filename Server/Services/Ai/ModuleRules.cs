using Server.Models;

namespace Server.Services.Ai
{
    /// <summary>
    /// Que reglas recibe cada modulo. Las reglas (constantes del producto y las
    /// propias del tenant) aplican a todos salvo que haya una excepcion para el
    /// modulo del catalogo: la tabla RuleExceptions guarda esos pares
    /// (clave de regla, AiModuleId).
    ///
    /// El grafo carga las reglas y las excepciones una vez por ejecucion; aqui se
    /// resuelve lo que le toca a cada nodo.
    /// </summary>
    public static class ModuleRules
    {
        private static readonly IReadOnlySet<string> Ninguna = new HashSet<string>();

        /// <summary>Claves de regla que este modulo del catalogo no recibe.</summary>
        public static IReadOnlySet<string> SuppressedFor(ExecutionGraph graph, Guid aiModuleId) =>
            graph.RuleExceptionsByModule.TryGetValue(aiModuleId, out var claves) ? claves : Ninguna;

        /// <summary>
        /// Bloque de reglas propias del tenant para este modulo, ya sin las que
        /// tienen excepcion. Null si no le queda ninguna.
        /// </summary>
        public static string? MandatoryRulesFor(ExecutionGraph graph, Guid aiModuleId)
        {
            var suprimidas = SuppressedFor(graph, aiModuleId);
            if (suprimidas.Count == 0) return graph.MandatoryRules;

            return BuildBlock(graph.TenantRules
                .Where(r => !suprimidas.Contains(BuiltInRules.TenantKey(r.Id)))
                .ToList());
        }

        /// <summary>
        /// Junta las reglas del tenant en el bloque que se antepone al system
        /// prompt. Null si la lista viene vacia: un encabezado sin reglas debajo
        /// solo gasta tokens.
        /// </summary>
        public static string? BuildBlock(IReadOnlyList<Rule> rules)
        {
            if (rules.Count == 0) return null;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[REGLAS OBLIGATORIAS - aplican a toda salida del modulo, sin excepcion]");
            foreach (var r in rules)
                sb.AppendLine($"- {r.Title}: {r.Content}");
            return sb.ToString().TrimEnd();
        }
    }
}
