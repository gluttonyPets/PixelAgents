namespace Server.Models
{
    /// <summary>
    /// Agrupacion de alto nivel del listado /projects: es lo que el usuario ve como
    /// "Proyecto". Solo tiene titulo y descripcion y sirve para organizar varios
    /// pipelines (<see cref="Project"/>) bajo un mismo nombre.
    ///
    /// Es puramente organizativa: no participa en la ejecucion, no aporta contexto ni
    /// configuracion a los pipelines que agrupa, y borrarla no borra ningun pipeline.
    /// </summary>
    public class ProjectGroup
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = default!;
        public string? Description { get; set; }

        /// <summary>Orden manual en el listado; a igualdad, manda la fecha de creacion.</summary>
        public int SortOrder { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        /// <summary>Pipelines agrupados bajo este proyecto.</summary>
        public ICollection<Project> Projects { get; set; } = new List<Project>();
    }
}
