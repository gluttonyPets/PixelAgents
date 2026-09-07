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

        /// <summary>
        /// Que clase de dato es (ver <see cref="ProjectVariableTypes"/>). El valor
        /// siempre viaja como texto: el tipo solo decide como se pide al lanzar la
        /// ejecucion, texto libre o elegido de una lista.
        /// </summary>
        public string Type { get; set; } = ProjectVariableTypes.Text;

        /// <summary>
        /// Para las variables de tipo carpeta, el nodo Directorio de archivos del que
        /// salen las opciones. null = todos los directorios del pipeline.
        /// </summary>
        public Guid? SourceModuleId { get; set; }

        /// <summary>
        /// Carpeta elegida al declarar una variable de tipo carpeta: es lo que la
        /// biblioteca entrega mientras la ejecucion no diga otra cosa.
        ///
        /// No es el "valor fijo" que las variables no tienen: no se mete en ningun
        /// prompt por su cuenta. Es la seleccion de que parte de la biblioteca viaja,
        /// que se hace una vez y cada ejecucion puede cambiar en su desplegable.
        /// </summary>
        public string? FolderPath { get; set; }

        public int SortOrder { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public Project Project { get; set; } = null!;
    }

    /// <summary>
    /// Tipos de variable. El tipo no cambia lo que llega a los modulos (siempre el
    /// valor como texto): cambia como se pide ese valor. "text" se escribe a mano;
    /// "folder" se elige de las carpetas que tiene la biblioteca del pipeline, que es
    /// donde equivocarse escribiendo cuesta una ejecucion fallida.
    /// </summary>
    public static class ProjectVariableTypes
    {
        /// <summary>Texto libre: el comportamiento de siempre.</summary>
        public const string Text = "text";

        /// <summary>Carpeta de un nodo Directorio de archivos del pipeline. La carpeta
        /// se elige al declarar la variable (<see cref="ProjectVariable.FolderPath"/>) y
        /// cada ejecucion puede cambiarla.</summary>
        public const string Folder = "folder";

        public static readonly string[] All = [Text, Folder];

        /// <summary>Tipo valido a partir de lo que llegue; null si no se reconoce.</summary>
        public static string? Normalize(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return Text;
            var value = raw.Trim().ToLowerInvariant();
            return All.Contains(value) ? value : null;
        }

        public static bool IsFolder(string? type) =>
            string.Equals(type, Folder, StringComparison.OrdinalIgnoreCase);
    }
}
