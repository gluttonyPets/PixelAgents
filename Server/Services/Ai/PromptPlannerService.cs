using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Server.Data;
using Server.Models;

namespace Server.Services.Ai
{
    public interface IPromptPlannerService
    {
        /// <summary>
        /// Busca las <paramref name="count"/> mejores tematicas dentro de lo que pide el
        /// usuario ("Halloween", "recetas de otono"...) y planifica una ejecucion por
        /// tema: titulo, descripcion y valor de las variables del pipeline. Evita los
        /// temas que ya estan en la cola o se han usado hace poco.
        /// </summary>
        /// <param name="replacePending">Los pendientes se van a borrar: no cuentan como
        /// temas ya planificados que haya que evitar.</param>
        Task<PromptPlannerResult> GenerateAsync(
            UserDbContext db,
            Guid projectId,
            string modelName,
            int count,
            string instructions,
            bool replacePending = false,
            CancellationToken ct = default);

        /// <summary>
        /// Redacta UNA sola ejecucion planificada a partir de la idea suelta del usuario
        /// (y del borrador que ya tenga escrito a mano, si lo hay). No guarda nada: la
        /// propuesta vuelve a la cola para que el usuario la revise antes de anadirla.
        /// </summary>
        Task<PromptPlannerResult> DraftAsync(
            UserDbContext db,
            Guid projectId,
            string modelName,
            string idea,
            string? currentContent = null,
            IReadOnlyDictionary<string, string>? currentVariables = null,
            CancellationToken ct = default);

        IReadOnlyList<PromptPlannerModelOption> GetAvailableModels();
    }

    /// <summary>
    /// Una ejecucion planificada: el prompt, el valor que el planificador propone
    /// para cada variable del pipeline (vacio cuando el proyecto no declara ninguna)
    /// y el titulo corto de su tematica, si el modelo lo ha dado.
    /// </summary>
    public record PlannedPromptDraft(string Content, Dictionary<string, string> Variables, string? Topic = null);

    public record PromptPlannerResult(bool Success, List<PlannedPromptDraft> Prompts, string? Error);

    public record PromptPlannerModelOption(string Provider, string ModelName, string DisplayName);

    public class PromptPlannerService : IPromptPlannerService
    {
        private const string PlannerProvider = "OpenAI";

        // Los modelos actuales van primero: el primero de la lista es el que la UI
        // preselecciona. GPT-6.1 Sol va delante porque rinde mas que 5.6 Terra y
        // cuesta menos ($2/$10 frente a $2/$12 por 1M tokens).
        private static readonly IReadOnlyList<PromptPlannerModelOption> _availableModels = new List<PromptPlannerModelOption>
        {
            new(PlannerProvider, "gpt-6.1-sol", "GPT-6.1 Sol"),
            new(PlannerProvider, "gpt-6-luna", "GPT-6 Luna"),
            new(PlannerProvider, "gpt-6-astra", "GPT-6 Astra"),
            new(PlannerProvider, "gpt-5.6-terra", "GPT-5.6 Terra"),
            new(PlannerProvider, "gpt-5.6-luna", "GPT-5.6 Luna"),
            new(PlannerProvider, "gpt-5.6-sol", "GPT-5.6 Sol"),
            new(PlannerProvider, "gpt-5.4", "GPT-5.4"),
            new(PlannerProvider, "gpt-4o", "GPT-4o"),
            new(PlannerProvider, "gpt-4o-mini", "GPT-4o mini"),
            new(PlannerProvider, "gpt-4-turbo", "GPT-4 Turbo"),
            new(PlannerProvider, "gpt-4", "GPT-4"),
            new(PlannerProvider, "gpt-3.5-turbo", "GPT-3.5 Turbo"),
        };

        /// <summary>Ejecuciones ya gastadas que se le recuerdan al planificador para
        /// que no vuelva a proponer el mismo tema.</summary>
        private const int RecentUsedTopics = 30;

        /// <summary>Tope de temas a evitar: con la cola entera y las ultimas usadas basta,
        /// y una lista mas larga solo encarece cada llamada.</summary>
        private const int MaxTopicsToAvoid = 60;

        /// <summary>Lo que se cita de una ejecucion sin titulo de tema.</summary>
        private const int TopicExcerptLength = 160;

        private readonly IAiProviderRegistry _registry;
        private readonly ILogger<PromptPlannerService> _log;

        public PromptPlannerService(IAiProviderRegistry registry, ILogger<PromptPlannerService> log)
        {
            _registry = registry;
            _log = log;
        }

        public IReadOnlyList<PromptPlannerModelOption> GetAvailableModels() => _availableModels;

        public async Task<PromptPlannerResult> GenerateAsync(
            UserDbContext db,
            Guid projectId,
            string modelName,
            int count,
            string instructions,
            bool replacePending = false,
            CancellationToken ct = default)
        {
            if (count < 1 || count > 50)
                return new PromptPlannerResult(false, new(), "La cantidad debe estar entre 1 y 50");

            if (string.IsNullOrWhiteSpace(instructions))
                return new PromptPlannerResult(false, new(), "Di sobre que tematica quieres las proximas ejecuciones");

            var alreadyPlanned = await LoadPlannedTopicsAsync(db, projectId, includePending: !replacePending, ct);
            var today = DateOnly.FromDateTime(DateTime.UtcNow);

            return await AskModelAsync(
                db, projectId, modelName, count,
                (project, variables, folders) => BuildPlannerPrompt(
                    count, instructions, project.Context, variables, alreadyPlanned, today, folders),
                ct);
        }

        public async Task<PromptPlannerResult> DraftAsync(
            UserDbContext db,
            Guid projectId,
            string modelName,
            string idea,
            string? currentContent = null,
            IReadOnlyDictionary<string, string>? currentVariables = null,
            CancellationToken ct = default)
        {
            // Sin idea no hay nada que redactar, salvo que el usuario ya haya escrito
            // el prompt a mano y solo quiera que la IA lo pula.
            if (string.IsNullOrWhiteSpace(idea) && string.IsNullOrWhiteSpace(currentContent))
                return new PromptPlannerResult(false, new(), "Escribe la idea de la ejecucion o el prompt a mano");

            return await AskModelAsync(
                db, projectId, modelName, 1,
                (project, variables, folders) => BuildDraftPrompt(
                    idea, project.Context, variables, currentContent, currentVariables, folders),
                ct);
        }

        /// <summary>
        /// Lo comun a generar la cola entera y a redactar una sola ejecucion: validar
        /// modelo y API Key, cargar las variables del pipeline, llamar al proveedor y
        /// leer la respuesta con el contrato de <see cref="PlannerSchema"/>.
        /// </summary>
        private async Task<PromptPlannerResult> AskModelAsync(
            UserDbContext db,
            Guid projectId,
            string modelName,
            int expected,
            Func<Project, IReadOnlyList<ProjectVariable>, IReadOnlyDictionary<string, List<string>>, string> buildPrompt,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(modelName))
                return new PromptPlannerResult(false, new(), "Falta indicar el modelo");

            var modelOption = _availableModels.FirstOrDefault(m =>
                string.Equals(m.ModelName, modelName, StringComparison.OrdinalIgnoreCase));
            if (modelOption is null)
                return new PromptPlannerResult(false, new(), $"Modelo '{modelName}' no soportado por el planificador");

            var project = await db.Projects.FindAsync(new object?[] { projectId }, ct);
            if (project is null)
                return new PromptPlannerResult(false, new(), "Proyecto no encontrado");

            var apiKeyEntity = await db.ApiKeys
                .Where(k => k.ProviderType == modelOption.Provider)
                .OrderBy(k => k.CreatedAt)
                .FirstOrDefaultAsync(ct);

            if (apiKeyEntity is null || string.IsNullOrEmpty(apiKeyEntity.EncryptedKey))
                return new PromptPlannerResult(false, new(),
                    $"No hay API Key de {modelOption.Provider} configurada. Anade una en Configuracion > API Keys.");

            var provider = _registry.GetProvider(modelOption.Provider);
            if (provider is null)
                return new PromptPlannerResult(false, new(), $"Proveedor '{modelOption.Provider}' no disponible");

            // El planificador tiene que rellenar tambien las variables del pipeline:
            // sin valor, cada ejecucion programada correria con el valor por defecto.
            var variables = await db.ProjectVariables
                .Where(v => v.ProjectId == projectId)
                .OrderBy(v => v.SortOrder).ThenBy(v => v.CreatedAt)
                .ToListAsync(ct);

            // Las variables de tipo carpeta no se inventan: el planificador elige entre
            // las carpetas que la biblioteca tiene de verdad, y lo que no sea una de
            // ellas se descarta al leer la respuesta.
            var folderOptions = await LoadFolderOptionsAsync(db, projectId, variables, ct);

            var systemPrompt = buildPrompt(project, variables, folderOptions);

            // OJO: el contexto del proyecto ya va embebido en el prompt que construyen
            // BuildPlannerPrompt / BuildDraftPrompt. Si ademas se pasara por ProjectContext,
            // el provider lo volveria a inyectar en el system prompt y se pagaria el mismo
            // texto dos veces en cada llamada.
            var aiContext = new AiExecutionContext
            {
                ModuleType = "Text",
                ModelName = modelOption.ModelName,
                ApiKey = apiKeyEntity.EncryptedKey,
                Input = systemPrompt,
                Configuration = new(),
            };

            try
            {
                var result = await provider.ExecuteAsync(aiContext);
                if (!result.Success)
                    return new PromptPlannerResult(false, new(), result.Error ?? "Error generando prompts");

                var raw = result.TextOutput ?? "";
                var prompts = PlannerSchema.ParsePrompts(raw, expected, variables, folderOptions);
                if (prompts.Count == 0)
                    return new PromptPlannerResult(false, new(), "El modelo no devolvio ningun prompt valido");

                return new PromptPlannerResult(true, prompts, null);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "PromptPlannerService failed for project {ProjectId}", projectId);
                return new PromptPlannerResult(false, new(), ex.Message);
            }
        }

        /// <summary>
        /// Prompt del planificador de tematicas. El usuario dice sobre que quiere las
        /// proximas ejecuciones ("Halloween") y cuantas; el modelo piensa muchas ideas
        /// dentro de eso, se queda con las mejores —distintas entre si y sin repetir lo
        /// ya planificado— y escribe una ejecucion por tema. Al final va el contrato de
        /// salida (<see cref="PlannerSchema"/>) con el titulo del tema y las variables.
        /// </summary>
        /// <param name="alreadyPlanned">Temas que ya estan en la cola o se han usado hace
        /// poco: el modelo no los puede repetir.</param>
        /// <param name="today">Fecha de hoy, para que una peticion de temporada o de
        /// actualidad (Halloween, rebajas...) salga a tiempo y no desfasada.</param>
        public static string BuildPlannerPrompt(
            int count, string instructions, string? projectContext,
            IReadOnlyList<ProjectVariable> variables,
            IReadOnlyList<string>? alreadyPlanned = null,
            DateOnly? today = null,
            IReadOnlyDictionary<string, List<string>>? folderOptions = null)
        {
            var ctxBlock = string.IsNullOrWhiteSpace(projectContext)
                ? ""
                : $"\n\nContexto del proyecto:\n{projectContext.Trim()}";

            var dateBlock = today is { } date
                ? $"\n\nFecha de hoy: {date:yyyy-MM-dd}. Tenla en cuenta si lo que pide el usuario es de temporada o de actualidad."
                : "";

            var avoidBlock = alreadyPlanned is { Count: > 0 }
                ? "\nTematicas que ya estan planificadas o se han usado hace poco. NO las repitas ni propongas variaciones casi iguales:\n"
                    + string.Join("\n", alreadyPlanned.Select(t => $"- {t}")) + "\n"
                : "";

            var temas = count == 1 ? "la mejor tematica" : $"las {count} mejores tematicas";
            var candidatas = Math.Max(count * 3, 10);

            return
$@"Eres un estratega de contenidos que planifica las proximas ejecuciones de un pipeline de IA.
El usuario te dice sobre que quiere las proximas ejecuciones y tu trabajo es encontrar {temas}
dentro de lo que pide y dedicar una ejecucion a cada una.{ctxBlock}{dateBlock}

Lo que pide el usuario para las proximas ejecuciones:
{instructions.Trim()}
{avoidBlock}
Como elegir las tematicas:
1. Piensa primero, sin escribirlas, al menos {candidatas} ideas candidatas dentro de lo que pide el usuario.
2. Quedate con {temas}: las mas atractivas y utiles para el publico del proyecto, concretas
   (nada generico tipo ""ideas de Halloween"") y distintas entre si, cada una con un angulo diferente.
3. Ordenalas en el orden en que conviene ejecutarlas.
4. Para cada tematica escribe su ""prompt"": la descripcion de esa ejecucion con el tema concreto,
   el enfoque y los puntos clave que debe cubrir. Es la entrada que recibira el pipeline en esa
   ejecucion, asi que debe ser autocontenida y estar lista para ejecutarse. No repitas las
   instrucciones generales del proyecto: el pipeline ya las conoce.

" + PlannerSchema.BuildInstruction(count, variables, folderOptions, withTopic: true);
        }

        /// <summary>
        /// Prompt del asistente de una ejecucion suelta: el usuario esta anadiendo una
        /// ejecucion a mano y pide ayuda para redactarla. Se le pasa el contexto del
        /// proyecto, lo que lleve escrito (para pulirlo en vez de tirarlo) y su idea,
        /// y se le exige el mismo contrato de salida que al planificador pero con una
        /// unica ejecucion.
        /// </summary>
        public static string BuildDraftPrompt(
            string idea,
            string? projectContext,
            IReadOnlyList<ProjectVariable> variables,
            string? currentContent = null,
            IReadOnlyDictionary<string, string>? currentVariables = null,
            IReadOnlyDictionary<string, List<string>>? folderOptions = null)
        {
            var ctxBlock = string.IsNullOrWhiteSpace(projectContext)
                ? ""
                : $"\n\nContexto del proyecto:\n{projectContext}\n";

            var draftBlock = string.IsNullOrWhiteSpace(currentContent)
                ? ""
                : $"\n\nBorrador que el usuario ya ha escrito a mano (mejoralo, no lo tires):\n{currentContent}\n";

            // Lo que el usuario ya haya fijado a mano manda: la IA rellena lo que falta
            // y respeta esos valores en el prompt que redacta.
            var fixedValues = (currentVariables ?? ExecutionVariables.None)
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                .Where(kv => variables.Any(v => string.Equals(v.Key, kv.Key, StringComparison.OrdinalIgnoreCase)))
                .Select(kv => $"- {kv.Key}: {kv.Value}")
                .ToList();

            var fixedBlock = fixedValues.Count == 0
                ? ""
                : "\n\nValores que el usuario ya ha fijado y NO debes cambiar:\n"
                    + string.Join("\n", fixedValues) + "\n";

            var ideaBlock = string.IsNullOrWhiteSpace(idea)
                ? "El usuario no ha descrito nada mas: quedate con su borrador y dejalo listo para ejecutarse."
                : idea.Trim();

            return
$@"Eres un asistente que redacta UNA ejecucion planificada para un pipeline de IA.
El prompt debe ser autocontenido, claro y listo para ejecutarse tal cual en una corrida.{ctxBlock}{draftBlock}{fixedBlock}

Idea del usuario para esta ejecucion:
{ideaBlock}

" + PlannerSchema.BuildInstruction(1, variables, folderOptions);
        }

        /// <summary>
        /// Temas que el planificador no debe repetir: los pendientes de la cola (salvo
        /// que se vayan a reemplazar) y las ultimas ejecuciones planificadas ya gastadas.
        /// De cada una se cita su titulo de tema o, si no tiene, el principio del prompt.
        /// </summary>
        public static async Task<List<string>> LoadPlannedTopicsAsync(
            UserDbContext db,
            Guid projectId,
            bool includePending,
            CancellationToken ct = default)
        {
            var pending = includePending
                ? await db.PlannedPrompts
                    .Where(p => p.ProjectId == projectId && p.Status == PlannedPromptStatus.Pending)
                    .OrderBy(p => p.OrderIndex).ThenBy(p => p.CreatedAt)
                    .Take(MaxTopicsToAvoid)
                    .ToListAsync(ct)
                : [];

            var used = await db.PlannedPrompts
                .Where(p => p.ProjectId == projectId && p.Status == PlannedPromptStatus.Used)
                .OrderByDescending(p => p.UsedAt ?? p.UpdatedAt)
                .Take(RecentUsedTopics)
                .ToListAsync(ct);

            return pending.Concat(used)
                .Select(p => TopicLabel(p.Topic, p.Content))
                .Where(label => label.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxTopicsToAvoid)
                .ToList();
        }

        /// <summary>Como se cita una ejecucion: su tema o la primera linea del prompt, recortada.</summary>
        public static string TopicLabel(string? topic, string? content)
        {
            if (PlannerSchema.NormalizeTopic(topic) is { } title) return title;
            if (string.IsNullOrWhiteSpace(content)) return "";

            var firstLine = content.Trim().Split('\n')[0].Trim();
            return firstLine.Length <= TopicExcerptLength
                ? firstLine
                : firstLine[..TopicExcerptLength].TrimEnd() + "...";
        }

        /// <summary>
        /// Carpetas que puede elegir cada variable de tipo carpeta: las del nodo
        /// Directorio que la variable indique o, si no indica ninguno, las de todas las
        /// bibliotecas del pipeline. Es la misma lista que ve el usuario en su
        /// desplegable, para que planificar a mano y planificar con IA elijan de lo mismo.
        /// </summary>
        private static async Task<IReadOnlyDictionary<string, List<string>>> LoadFolderOptionsAsync(
            UserDbContext db,
            Guid projectId,
            IReadOnlyList<ProjectVariable> variables,
            CancellationToken ct)
        {
            var folderVariables = variables
                .Where(v => ProjectVariableTypes.IsFolder(v.Type))
                .ToList();

            var options = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (folderVariables.Count == 0) return options;

            var directories = await db.ProjectModules
                .Include(pm => pm.AiModule)
                .Where(pm => pm.ProjectId == projectId
                    && pm.AiModule.ModuleType == FileDirectoryIndex.ModuleType)
                .ToListAsync(ct);

            var foldersByNode = directories.ToDictionary(
                node => node.Id,
                node => FileDirectoryIndex.ReadFolders(FileDirectoryIndex.ReadConfig(
                    null, node.Configuration, FileDirectoryIndex.IndexConfigKey)));

            foreach (var variable in folderVariables)
            {
                var folders = foldersByNode
                    .Where(kv => variable.SourceModuleId is not { } source || kv.Key == source)
                    .SelectMany(kv => kv.Value)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                options[variable.Key.Trim()] = folders;
            }

            return options;
        }
    }
}
