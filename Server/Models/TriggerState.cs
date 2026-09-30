namespace Server.Models
{
    /// <summary>
    /// Estado del vigilante de un pipeline de tipo Trigger. Existe desde la primera
    /// consulta: esa pasada solo toma nota de lo que ya habia (linea base) sin
    /// ejecutar nada, para que activar el trigger no dispare con archivos antiguos.
    /// </summary>
    public class ProjectTriggerState
    {
        public Guid ProjectId { get; set; }
        /// <summary>Que se esta vigilando (tipo de trigger + carpeta). Si cambia, la
        /// linea base se rehace: la carpeta nueva no dispara con sus archivos viejos.</summary>
        public string ConfigKey { get; set; } = default!;
        /// <summary>Cuando se tomo la linea base; null = aun no (p. ej. la configuracion
        /// esta incompleta y solo se ha guardado el error).</summary>
        public DateTime? BaselineAt { get; set; }
        /// <summary>Cuando se activo o cambio la configuracion del trigger. En la linea base,
        /// los archivos creados despues de este momento SI disparan: los ha subido el
        /// usuario tras guardar, aunque la primera consulta aun no hubiera pasado.</summary>
        public DateTime? ArmedAt { get; set; }
        public DateTime? LastCheckedAt { get; set; }
        public DateTime? LastFiredAt { get; set; }
        /// <summary>Ultimo error de la consulta (credenciales, carpeta sin acceso...);
        /// null si la ultima consulta fue bien.</summary>
        public string? LastError { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    /// <summary>
    /// Elemento ya visto por un trigger (p. ej. un archivo de Drive). Se registra
    /// antes de ejecutar el pipeline, asi cada elemento dispara como mucho una vez.
    /// </summary>
    public class TriggerSeenItem
    {
        public Guid ProjectId { get; set; }
        public string ItemKey { get; set; } = default!;
        public DateTime SeenAt { get; set; }
    }
}
