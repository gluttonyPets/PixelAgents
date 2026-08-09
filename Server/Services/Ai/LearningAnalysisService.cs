using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Server.Data;
using Server.Models;
using Server.Services;

namespace Server.Services.Ai
{
    /// <summary>
    /// Analista de aprendizaje. Cuando se aborta una ejecución con comentario, procesa
    /// el feedback con el modelo configurado en el proyecto: identifica el/los módulo(s)
    /// culpables, critica la imagen si la hay contra la definición del usuario, y destila
    /// una conclusión que reconcilia (dedup / contradicción) con el documento vivo del
    /// proyecto. Deja un registro histórico por cada análisis (LearningEntry).
    ///
    /// Es autónomo y self-contained: crea su propia conexión de tenant, así que puede
    /// invocarse en segundo plano (fire-and-forget) sin depender del scope de la petición.
    /// </summary>
    public interface ILearningAnalysisService
    {
        /// <summary>Analiza un abort con comentario. No lanza: registra el error si falla.</summary>
        Task AnalyzeAbortAsync(string tenantDbName, Guid feedbackId, CancellationToken ct = default);
    }

    public class LearningAnalysisService : ILearningAnalysisService
    {
        private readonly ITenantDbContextFactory _factory;
        private readonly IAiProviderRegistry _registry;
        private readonly ILogger<LearningAnalysisService> _log;
        private readonly string _mediaRoot;

        private const int MaxImages = 4;
        private const int MaxOutputChars = 1500;

        public LearningAnalysisService(
            ITenantDbContextFactory factory,
            IAiProviderRegistry registry,
            IWebHostEnvironment env,
            ILogger<LearningAnalysisService> log)
        {
            _factory = factory;
            _registry = registry;
            _log = log;
            // Misma raíz que GraphPipelineExecutor para resolver los ficheros generados.
            _mediaRoot = Path.Combine(env.ContentRootPath, "GeneratedMedia");
        }

        private string ResolveWorkspacePath(string storedPath) =>
            Path.IsPathRooted(storedPath) ? storedPath : Path.Combine(_mediaRoot, storedPath);

        public async Task AnalyzeAbortAsync(string tenantDbName, Guid feedbackId, CancellationToken ct = default)
        {
            try
            {
                await using var db = _factory.Create(tenantDbName);

                var feedback = await db.ExecutionFeedbacks.FirstOrDefaultAsync(f => f.Id == feedbackId, ct);
                if (feedback is null) return;

                var exec = await db.ProjectExecutions
                    .Include(e => e.StepExecutions).ThenInclude(s => s.Files)
                    .Include(e => e.StepExecutions).ThenInclude(s => s.ProjectModule).ThenInclude(pm => pm.AiModule)
                    .FirstOrDefaultAsync(e => e.Id == feedback.ExecutionId, ct);
                if (exec is null) return;

                var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == exec.ProjectId, ct);
                if (project is null) return;

                // Config del analista: si está desactivado, no hacemos nada.
                if (!project.LearningEnabled)
                    return;

                string providerType;
                string modelName;

                // Sin modelo configurado -> modelo por defecto (gpt-4o-mini si hay key de OpenAI,
                // si no el primer multimodal disponible). Así el aprendizaje funciona sin setup.
                if (string.IsNullOrWhiteSpace(project.AnalystModelProvider)
                    || string.IsNullOrWhiteSpace(project.AnalystModelName))
                {
                    var providersWithKeys = await db.ApiKeys
                        .Where(k => k.EncryptedKey != null && k.EncryptedKey != "")
                        .Select(k => k.ProviderType)
                        .Distinct()
                        .ToListAsync(ct);

                    var def = AnalystDefaults.Resolve(providersWithKeys);
                    if (def is null)
                    {
                        await RecordEntryAsync(db, project.Id, exec.Id, feedback, "(por defecto)",
                            status: "error",
                            error: "No hay API Key de ningún proveedor con modelo analista por defecto (OpenAI, Anthropic o Google).",
                            ct: ct);
                        return;
                    }
                    providerType = def.Value.Provider;
                    modelName = def.Value.Model;
                }
                else
                {
                    providerType = project.AnalystModelProvider!;
                    modelName = project.AnalystModelName!;
                }

                var apiKey = await db.ApiKeys
                    .Where(k => k.ProviderType == providerType)
                    .OrderBy(k => k.CreatedAt)
                    .Select(k => k.EncryptedKey)
                    .FirstOrDefaultAsync(ct);

                var provider = _registry.GetProvider(providerType);

                if (string.IsNullOrEmpty(apiKey) || provider is null)
                {
                    await RecordEntryAsync(db, project.Id, exec.Id, feedback, $"{providerType}/{modelName}",
                        status: "error",
                        error: $"No hay API Key o proveedor '{providerType}' disponible para el analista.",
                        ct: ct);
                    return;
                }

                // ── Reunir el contexto de la ejecución ──
                var steps = exec.StepExecutions
                    .Where(s => s.Status != "Skipped")
                    .OrderBy(s => s.CreatedAt)
                    .ToList();

                var moduleNames = steps
                    .Select(s => s.ProjectModule?.AiModule?.Name)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => n!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var stepsText = BuildStepsText(steps);
                var images = LoadImages(steps, providerType, modelName, exec.WorkspacePath);

                var existingDoc = await db.ProjectLearningDocs
                    .FirstOrDefaultAsync(d => d.ProjectId == project.Id, ct);

                var prompt = BuildAnalysisPrompt(
                    userComment: feedback.Comment ?? "",
                    userInput: exec.UserInput,
                    moduleNames: moduleNames,
                    stepsText: stepsText,
                    hasImages: images.Count > 0,
                    currentActiveLearnings: existingDoc?.ActiveLearningsJson ?? "[]");

                // ── Llamar al modelo analista ──
                var aiCtx = new AiExecutionContext
                {
                    ModuleType = "Text",
                    ModelName = modelName,
                    ApiKey = apiKey!,
                    Input = prompt,
                    Configuration = new() { ["maxTokens"] = 4000 },
                    InputFiles = images.Count > 0 ? images : null,
                    CancellationToken = ct,
                };

                AiResult result;
                try
                {
                    result = await provider.ExecuteAsync(aiCtx);
                }
                catch (Exception ex)
                {
                    await RecordEntryAsync(db, project.Id, exec.Id, feedback, $"{providerType}/{modelName}",
                        status: "error", error: $"Fallo llamando al analista: {ex.Message}", ct: ct);
                    return;
                }

                if (!result.Success || string.IsNullOrWhiteSpace(result.TextOutput))
                {
                    await RecordEntryAsync(db, project.Id, exec.Id, feedback, $"{providerType}/{modelName}",
                        status: "error", error: result.Error ?? "El analista no devolvió respuesta.", ct: ct);
                    return;
                }

                var parsed = ParseAnalysis(result.TextOutput!);
                if (parsed is null)
                {
                    await RecordEntryAsync(db, project.Id, exec.Id, feedback, $"{providerType}/{modelName}",
                        status: "error", error: "No se pudo interpretar la respuesta del analista.", ct: ct);
                    return;
                }

                // ── Persistir documento vivo + histórico ──
                // El markdown lo generamos NOSOTROS desde los aprendizajes activos (agrupado por
                // módulo), no el modelo: así queda limpio y sin marcadores repetidos.
                var content = BuildMarkdown(parsed.ActiveLearningsJson);
                if (existingDoc is null)
                {
                    existingDoc = new ProjectLearningDoc
                    {
                        Id = Guid.NewGuid(),
                        ProjectId = project.Id,
                        Content = content,
                        ActiveLearningsJson = parsed.ActiveLearningsJson,
                        UpdatedAt = DateTime.UtcNow,
                    };
                    db.ProjectLearningDocs.Add(existingDoc);
                }
                else
                {
                    existingDoc.Content = content;
                    existingDoc.ActiveLearningsJson = parsed.ActiveLearningsJson;
                    existingDoc.UpdatedAt = DateTime.UtcNow;
                }

                await RecordEntryAsync(db, project.Id, exec.Id, feedback, $"{providerType}/{modelName}",
                    status: "ok", error: null, ct: ct,
                    attributionsJson: parsed.AttributionsJson,
                    imageCritique: parsed.ImageCritique,
                    conclusion: parsed.Conclusion,
                    docAction: parsed.DocAction ?? "none",
                    docChange: parsed.DocChange,
                    saveImmediately: false);

                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "LearningAnalysisService falló para feedback {FeedbackId}", feedbackId);
            }
        }

        // ─────────────────────────── helpers ───────────────────────────

        private static async Task RecordEntryAsync(
            UserDbContext db, Guid projectId, Guid executionId, ExecutionFeedback feedback,
            string analystModel, string status, string? error, CancellationToken ct,
            string? attributionsJson = null, string? imageCritique = null, string? conclusion = null,
            string docAction = "none", string? docChange = null, bool saveImmediately = true)
        {
            db.LearningEntries.Add(new LearningEntry
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                ExecutionId = executionId,
                FeedbackId = feedback.Id,
                AnalystModel = analystModel,
                UserComment = feedback.Comment,
                AttributionsJson = attributionsJson,
                ImageCritique = imageCritique,
                Conclusion = conclusion,
                DocAction = docAction,
                DocChange = docChange,
                Status = status,
                Error = error,
                CreatedAt = DateTime.UtcNow,
            });
            if (saveImmediately)
                await db.SaveChangesAsync(ct);
        }

        private static string BuildStepsText(List<StepExecution> steps)
        {
            var sb = new StringBuilder();
            int i = 1;
            foreach (var s in steps)
            {
                var name = s.ProjectModule?.AiModule?.Name ?? "(módulo)";
                var type = s.ProjectModule?.AiModule?.ModuleType ?? "";
                sb.AppendLine($"### Paso {i} — Módulo \"{name}\" (tipo: {type}, estado: {s.Status})");

                if (!string.IsNullOrWhiteSpace(s.OutputData))
                    sb.AppendLine($"Salida producida:\n{Truncate(s.OutputData, MaxOutputChars)}");

                var imgs = (s.Files ?? new List<ExecutionFile>())
                    .Where(f => f.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
                    .ToList();
                if (imgs.Count > 0)
                    sb.AppendLine($"(este módulo generó {imgs.Count} imagen(es))");

                sb.AppendLine();
                i++;
            }
            return sb.ToString().Trim();
        }

        private List<byte[]> LoadImages(List<StepExecution> steps, string providerType, string modelName,
            string storedWorkspacePath)
        {
            if (!VisionCapability.IsVisionCapable(providerType, modelName)) return new();

            var images = new List<byte[]>();
            var workspacePath = ResolveWorkspacePath(storedWorkspacePath);

            foreach (var s in steps)
            {
                foreach (var f in (s.Files ?? new List<ExecutionFile>())
                             .Where(f => f.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true))
                {
                    if (images.Count >= MaxImages) return images;
                    var bytes = TryReadFile(workspacePath, f.FilePath);
                    if (bytes is not null) images.Add(bytes);
                }
            }
            return images;
        }

        /// <summary>
        /// Resuelve un fichero de salida. FilePath es relativo al workspace de la ejecución
        /// (p.ej. "module_xxx/imagen.png"). Se prueban varias raíces por robustez.
        /// </summary>
        private byte[]? TryReadFile(string workspacePath, string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return null;
            var candidates = new List<string>
            {
                Path.Combine(workspacePath, filePath),  // caso normal: workspace + ruta relativa
                Path.Combine(_mediaRoot, filePath),     // fallback: bajo GeneratedMedia
                filePath,                                // ruta absoluta legacy
            };

            foreach (var c in candidates)
            {
                try { if (File.Exists(c)) return File.ReadAllBytes(c); }
                catch { /* siguiente candidato */ }
            }
            _log.LogWarning("LearningAnalysis: no se encontró la imagen {FilePath} (workspace {Workspace})", filePath, workspacePath);
            return null;
        }

        private static string Truncate(string s, int max) =>
            s.Length <= max ? s : s[..max] + "…";

        private static string BuildAnalysisPrompt(
            string userComment, string? userInput, List<string> moduleNames, string stepsText,
            bool hasImages, string currentActiveLearnings)
        {
            var modulesList = moduleNames.Count > 0
                ? string.Join(", ", moduleNames.Select(m => $"\"{m}\""))
                : "(sin módulos identificados)";

            var imageInstruction = hasImages
                ? @"Se adjuntan las imágenes generadas en esta ejecución. Analízalas visualmente: contrasta lo que ves
con la definición/petición del usuario y con su comentario, describe QUÉ se generó mal y QUÉ se debería haber hecho
para ajustarse a su gusto (rellena ""imageCritique"")."
                : @"Esta ejecución no aporta imágenes analizables; deja ""imageCritique"" en null.";

            return
$@"Eres un ANALISTA DE APRENDIZAJE de un sistema de generación de contenido por pipelines.
El usuario ha ABORTADO una ejecución y ha dejado un comentario de qué ha ido mal. Tu trabajo es
entender el fallo, atribuirlo al/los módulo(s) responsable(s) y DESTILAR el aprendizaje, sin duplicar
ni contradecir lo que ya existe.

COMENTARIO DEL USUARIO (qué ha ido mal):
""{userComment}""

PETICIÓN/INPUT ORIGINAL DE LA EJECUCIÓN:
{(string.IsNullOrWhiteSpace(userInput) ? "(sin input)" : userInput)}

MÓDULOS DEL PIPELINE (referéncialos EXACTAMENTE por estos nombres): {modulesList}

LO QUE PRODUJO CADA PASO:
{stepsText}

{imageInstruction}

APRENDIZAJES ACTIVOS ACTUALES (JSON, [] si está vacío):
{currentActiveLearnings}

INSTRUCCIONES:
1. Atribuye la culpa al/los módulo(s). El fallo puede estar AGUAS ARRIBA (p.ej. una imagen mala
   porque el módulo de texto escribió mal el prompt de imagen). Usa los nombres EXACTOS de la lista.
2. Destila UNA conclusión accionable y concreta (no genérica) que evite que vuelva a pasar.
3. Reconcilia con los aprendizajes actuales: si ya está dicho -> ""reinforced"" o ""skipped_duplicate"";
   si contradice algo -> ""resolved_contradiction"" (sustituye la nota vieja); si es nuevo -> ""added"";
   si mejora una nota existente -> ""updated"".
4. Devuelve la lista COMPLETA de aprendizajes activos ya consolidada (la actual con tu cambio aplicado):
   cada aprendizaje con el módulo EXACTO al que aplica, o ""general"" si aplica a todos los módulos.
   Un aprendizaje etiquetado con un módulo SOLO se le inyectará a ese módulo; ""general"" se inyecta a todos.
   Mantén la lista ACOTADA: fusiona/elimina lo redundante, no acumules sin fin. Texto conciso y accionable.

Responde EXCLUSIVAMENTE con un único objeto JSON válido, sin texto fuera del JSON, sin markdown y SIN
marcadores como ---INICIO DOC---. Forma EXACTA:
{{
  ""attributions"": [{{ ""module"": ""<nombre>"", ""reason"": ""<por qué>"", ""confidence"": 0.0 }}],
  ""imageCritique"": ""<texto o null>"",
  ""conclusion"": ""<conclusión destilada>"",
  ""docAction"": ""added|reinforced|updated|skipped_duplicate|resolved_contradiction"",
  ""docChange"": ""<qué añadiste/cambiaste o por qué lo descartaste>"",
  ""activeLearnings"": [{{ ""module"": ""<nombre exacto>|general"", ""text"": ""<aprendizaje conciso>"" }}]
}}";
        }

        /// <summary>
        /// Genera el documento markdown (legible/descargable) a partir de los aprendizajes activos,
        /// agrupados por módulo. Lo generamos NOSOTROS (no el modelo) para que sea limpio, muestre
        /// claramente qué es general y qué es de cada módulo, y no acumule marcadores ni ruido.
        /// </summary>
        private static string BuildMarkdown(string? activeLearningsJson)
        {
            var learnings = LearningInjection.Parse(activeLearningsJson);
            var sb = new StringBuilder();
            sb.AppendLine("# Aprendizaje del proyecto");
            sb.AppendLine();
            sb.AppendLine($"_Actualizado: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC_");
            sb.AppendLine();

            if (learnings.Count == 0)
            {
                sb.AppendLine("(todavía no hay aprendizajes registrados)");
                return sb.ToString().TrimEnd();
            }

            // Generales primero, luego por módulo.
            var general = learnings.Where(l => IsGeneralScope(l.Module)).ToList();
            if (general.Count > 0)
            {
                sb.AppendLine("## General (todos los módulos)");
                foreach (var l in general) sb.AppendLine($"- {l.Text.Trim()}");
                sb.AppendLine();
            }

            foreach (var group in learnings
                         .Where(l => !IsGeneralScope(l.Module))
                         .GroupBy(l => l.Module.Trim(), StringComparer.OrdinalIgnoreCase)
                         .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"## Módulo: {group.Key}");
                foreach (var l in group) sb.AppendLine($"- {l.Text.Trim()}");
                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }

        private static bool IsGeneralScope(string module) =>
            string.IsNullOrWhiteSpace(module)
            || string.Equals(module, "general", StringComparison.OrdinalIgnoreCase)
            || string.Equals(module, "all", StringComparison.OrdinalIgnoreCase)
            || string.Equals(module, "todos", StringComparison.OrdinalIgnoreCase);

        private sealed class AnalysisResult
        {
            public string? AttributionsJson { get; set; }
            public string? ImageCritique { get; set; }
            public string? Conclusion { get; set; }
            public string? DocAction { get; set; }
            public string? DocChange { get; set; }
            public string? ActiveLearningsJson { get; set; }
        }

        private static AnalysisResult? ParseAnalysis(string raw)
        {
            var json = ExtractJsonObject(raw);
            if (json is null) return null;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Tolerancia: algunos modelos envuelven la salida en {"content": "..."}.
                if (root.ValueKind == JsonValueKind.Object
                    && !root.TryGetProperty("activeLearnings", out _)
                    && root.TryGetProperty("content", out var inner)
                    && inner.ValueKind == JsonValueKind.String)
                {
                    var innerJson = ExtractJsonObject(inner.GetString() ?? "");
                    if (innerJson is not null)
                    {
                        doc.Dispose();
                        using var doc2 = JsonDocument.Parse(innerJson);
                        return FromElement(doc2.RootElement);
                    }
                }

                return FromElement(root);
            }
            catch
            {
                return null;
            }
        }

        private static AnalysisResult FromElement(JsonElement root)
        {
            string? Str(string prop) =>
                root.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() : null;

            string? RawArray(string prop) =>
                root.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Array
                    ? v.GetRawText() : null;

            return new AnalysisResult
            {
                AttributionsJson = RawArray("attributions"),
                ImageCritique = root.TryGetProperty("imageCritique", out var ic) && ic.ValueKind == JsonValueKind.String
                    ? ic.GetString() : null,
                Conclusion = Str("conclusion"),
                DocAction = Str("docAction"),
                DocChange = Str("docChange"),
                ActiveLearningsJson = RawArray("activeLearnings"),
            };
        }

        private static string? ExtractJsonObject(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var cleaned = raw.Trim();
            if (cleaned.StartsWith("```"))
            {
                var nl = cleaned.IndexOf('\n');
                if (nl >= 0) cleaned = cleaned[(nl + 1)..];
                var fence = cleaned.LastIndexOf("```", StringComparison.Ordinal);
                if (fence >= 0) cleaned = cleaned[..fence];
            }
            var start = cleaned.IndexOf('{');
            var end = cleaned.LastIndexOf('}');
            if (start < 0 || end <= start) return null;
            return cleaned.Substring(start, end - start + 1);
        }
    }
}
