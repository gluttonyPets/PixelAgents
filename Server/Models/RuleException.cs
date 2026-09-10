namespace Server.Models
{
    /// <summary>
    /// Excepcion de una regla para un modulo del catalogo: ese <see cref="AiModule"/>
    /// deja de recibir la regla en su system prompt. Se engancha al modulo del
    /// catalogo, no al nodo del pipeline, asi que vale para todos los pipelines
    /// que lo usen.
    ///
    /// <see cref="RuleKey"/> identifica la regla: la clave de una constante
    /// ("text-format", ver <c>BuiltInRules</c>) o "tenant:GUID" para una regla
    /// propia de la tabla Rules.
    /// </summary>
    public class RuleException
    {
        public Guid Id { get; set; }
        public string RuleKey { get; set; } = "";
        public Guid AiModuleId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public AiModule? AiModule { get; set; }
    }
}
