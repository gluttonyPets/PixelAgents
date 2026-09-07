namespace Server.Models
{
    /// <summary>
    /// Variable declarada a nivel de pipeline (proyecto): por ejemplo "tematica" o
    /// "keyword". La definicion —nombre y para que sirve— vive en el proyecto; el
    /// VALOR es siempre de cada ejecucion y se arrastra a todos los modulos. No hay
    /// valor fijo: una variable con valor fijo seria parte del prompt, no una variable.
    ///
    /// En los prompts se referencia como <c>{{clave}}</c>. Ademas del reemplazo,
    /// las variables de la ejecucion se inyectan como bloque etiquetado en el
    /// system prompt de cada llamada a IA, para que un modulo pueda usarlas aunque
    /// su prompt no lleve el marcador.
    /// </summary>
    public class ProjectVariable
    {
        public Guid Id { get; set; }
        public Guid ProjectId { get; set; }

        /// <summary>Identificador usado en los prompts como <c>{{clave}}</c>.
        /// Solo letras, digitos y guion bajo; no distingue mayusculas.</summary>
        public string Key { get; set; } = default!;

        /// <summary>Para que sirve la variable. Se envia al modelo junto al valor.</summary>
        public string? Description { get; set; }

        public int SortOrder { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public Project Project { get; set; } = null!;
    }
}
