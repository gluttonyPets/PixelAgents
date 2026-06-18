namespace Server.Models
{
    /// <summary>
    /// Credencial reutilizable de una red social (Buffer). Se define una vez en la
    /// seccion "Redes sociales" y los proyectos la referencian por Id, en lugar de
    /// repetir el token y el canal en cada proyecto.
    /// </summary>
    public class SocialConnection
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = default!;

        /// <summary>Plataforma destino: "instagram", "tiktok" o "pinterest".</summary>
        public string Platform { get; set; } = default!;

        /// <summary>Token de la API de Buffer.</summary>
        public string ApiKey { get; set; } = default!;

        /// <summary>Id del canal de Buffer al que se publica.</summary>
        public string ChannelId { get; set; } = default!;

        /// <summary>Nombre del canal (solo para mostrar en la UI).</summary>
        public string? ChannelName { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
