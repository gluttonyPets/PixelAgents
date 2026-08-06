using Server.Models;

namespace Server.Services.Ai
{
    /// <summary>
    /// Ensambla el system prompt que comparten todos los proveedores de texto.
    ///
    /// El orden no es estetico: es el que decide cuanto se paga. Los proveedores
    /// con cache automatico de prompt (OpenAI, xAI) cachean por *prefijo comun*,
    /// asi que todo bloque invariante debe ir antes del primer bloque que cambia
    /// entre modulos. Con el orden de aqui, los modulos de una misma ejecucion
    /// comparten prefijo (reglas + contexto + historial) y solo pagan a precio
    /// completo su parte propia.
    ///
    /// De mas estable a menos:
    ///   1. Reglas de formato/contenido  -> constante de compilacion
    ///   2. Reglas obligatorias          -> invariante por tenant
    ///   3. Contexto del proyecto        -> invariante por proyecto
    ///   4. Historial de ejecuciones     -> invariante por ejecucion
    ///   5. Prompt del modulo            -> cambia en cada modulo
    ///   6. Aprendizaje pasado           -> filtrado por modulo, cambia
    ///
    /// El prompt del usuario baja de posicion pero sigue etiquetado como directiva
    /// prioritaria, y queda mas cerca del mensaje de usuario, que es donde mas peso
    /// tiene. No se altera su contenido.
    /// </summary>
    public static class SystemPromptComposer
    {
        public static string Build(AiExecutionContext context)
        {
            var parts = new List<string>(6);

            parts.Add(OutputSchemaHelper.GetTextContentRules());

            if (!string.IsNullOrWhiteSpace(context.MandatoryRules))
                parts.Add(context.MandatoryRules!);

            if (!string.IsNullOrWhiteSpace(context.ProjectContext))
                parts.Add($"[Contexto del proyecto]\n{context.ProjectContext}");

            if (!string.IsNullOrWhiteSpace(context.PreviousExecutionsSummary))
                parts.Add(context.PreviousExecutionsSummary!);

            if (context.Configuration.TryGetValue("systemPrompt", out var sysPrompt) && sysPrompt is string sp)
                parts.Add($"[INSTRUCCION PRINCIPAL - Esta es tu directiva prioritaria, sigue estas instrucciones por encima de cualquier otra regla]\n{sp}");

            if (!string.IsNullOrWhiteSpace(context.PastExecutionsLearning))
                parts.Add(context.PastExecutionsLearning!);

            return string.Join("\n\n", parts);
        }
    }
}
