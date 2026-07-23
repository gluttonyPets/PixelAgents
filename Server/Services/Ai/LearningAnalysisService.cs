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

        private const int MaxImages = 4;
        private const int MaxOutputChars = 1500;

        public LearningAnalysisService(
            ITenantDbContextFactory factory,
            IAiProviderRegistry registry,
            ILogger<LearningAnalysisService> log)
        {
            _factory = factory;
            _registry = registry;
            _log = log;
        }

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

                // Config del analista: si está desactivado o sin modelo, no hacemos nada.
                if (!project.LearningEnabled
                    || string.IsNullOrWhiteSpace(project.AnalystModelProvider)
                    || string.IsNullOrWhiteSpace(project.AnalystModelName))
                    return;

                var providerType = project.AnalystModelProvider!;
                var modelName = project.AnalystModelName!;

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
                var images = LoadImages(steps, providerType, modelName, tenantDbName, exec.WorkspacePath);

                var existingDoc = await db.ProjectLearningDocs
                    .FirstOrDefaultAsync(d => d.ProjectId == project.Id, ct);

                var prompt = BuildAnalysisPrompt(
                    userComment: feedback.Comment ?? "",
                    userInput: exec.UserInput,
                    moduleNames: moduleNames,
                    stepsText: stepsText,
                    hasImages: images.Count > 0,
                    currentDoc: existingDoc?.Content ?? "",
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
                if (!string.IsNullOrWhiteSpace(parsed.UpdatedDoc))
                {
                    if (existingDoc is null)
                    {
                        existingDoc = new ProjectLearningDoc
                        {
                            Id = Guid.NewGuid(),
                            ProjectId = project.Id,
                            Content = parsed.UpdatedDoc!.Trim(),
                            ActiveLearningsJson = parsed.ActiveLearningsJson,
                            UpdatedAt = DateTime.UtcNow,
                        };
                        db.ProjectLearningDocs.Add(existingDoc);
                    }
                    else
                    {
                        existingDoc.Content = parsed.UpdatedDoc!.Trim();
                        existingDoc.ActiveLearningsJson = parsed.ActiveLearningsJson;
                        existingDoc.UpdatedAt = DateTime.UtcNow;
                    }
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
            string tenantDbName, string workspacePath)
        {
            if (!VisionCapability.IsVisionCapable(providerType, modelName)) return new();

            var images = new List<byte[]>();
            var mediaRoot = Path.Combine(Directory.GetCurrentDirectory(), "GeneratedMedia");

            foreach (var s in steps)
            {
                foreach (var f in (s.Files ?? new List<ExecutionFile>())
                             .Where(f => f.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true))
                {
                    if (images.Count >= MaxImages) return images;
                    var bytes = TryReadFile(mediaRoot, workspacePath, tenantDbName, f.FilePath);
                    if (bytes is not null) images.Add(bytes);
                }
            }
            return images;
        }

        private static byte[]? TryReadFile(string mediaRoot, string workspacePath, string tenantDbName, string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return null;
            var candidates = new List<string>
            {
                Path.Combine(mediaRoot, filePath),
                Path.Combine(mediaRoot, tenantDbName, filePath),
                Path.Combine(workspacePath, filePath),
                filePath,
            };
            if (!Path.IsPathRooted(workspacePath))
                candidates.Insert(0, Path.Combine(mediaRoot, workspacePath, filePath));

            foreach (var c in candidates)
            {
                try { if (File.Exists(c)) return File.ReadAllBytes(c); }
                catch { /* siguiente candidato */ }
            }
            return null;
        }

        private static string Truncate(string s, int max) =>
            s.Length <= max ? s : s[..max] + "…";

        private static string BuildAnalysisPrompt(
            string userComment, string? userInput, List<string> moduleNames, string stepsText,
            bool hasImages, string currentDoc, string currentActiveLearnings)
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
entender el fallo, atribuirlo al/los módulo(s) responsable(s), y DESTILAR el aprendizaje en el
documento vivo del proyecto, sin duplicar ni contradecir lo que ya existe.

COMENTARIO DEL USUARIO (qué ha ido mal):
""{userComment}""

PETICIÓN/INPUT ORIGINAL DE LA EJECUCIÓN:
{(string.IsNullOrWhiteSpace(userInput) ? "(sin input)" : userInput)}

MÓDULOS DEL PIPELINE (referéncialos EXACTAMENTE por estos nombres): {modulesList}

LO QUE PRODUJO CADA PASO:
{stepsText}

{imageInstruction}

DOCUMENTO VIVO ACTUAL DEL PROYECTO (markdown; puede estar vacío):
---INICIO DOC---
{(string.IsNullOrWhiteSpace(currentDoc) ? "(vacío)" : currentDoc)}
---FIN DOC---

APRENDIZAJES ACTIVOS ACTUALES (JSON):
{currentActiveLearnings}

INSTRUCCIONES:
1. Atribuye la culpa al/los módulo(s). El fallo puede estar AGUAS ARRIBA (p.ej. una imagen mala
   porque el módulo de texto escribió mal el prompt de imagen). Usa los nombres EXACTOS de la lista.
2. Destila UNA conclusión accionable y concreta (no genérica) que evite que vuelva a pasar.
3. Reconcilia con el documento: si ya está dicho -> ""reinforced"" o ""skipped_duplicate""; si contradice
   algo escrito -> ""resolved_contradiction"" (actualiza la nota vieja); si es nuevo -> ""added"";
   si mejora una nota existente -> ""updated"".
4. Devuelve el documento COMPLETO actualizado (markdown, en español, conciso y consolidado; NO acumules
   ejemplos sin fin: agrupa y resume). Organiza por secciones (p.ej. Texto/Caption, Imágenes, Hashtags,
   Estilo general).
5. Devuelve la lista de aprendizajes activos consolidada, cada uno etiquetado con el módulo al que aplica
   (usa el nombre exacto) o ""general"". Mantenla ACOTADA: fusiona/elimina lo redundante.

Responde EXCLUSIVAMENTE con un único objeto JSON válido, sin texto fuera del JSON y sin markdown, con esta forma EXACTA:
{{
  ""attributions"": [{{ ""module"": ""<nombre>"", ""reason"": ""<por qué>"", ""confidence"": 0.0 }}],
  ""imageCritique"": ""<texto o null>"",
  ""conclusion"": ""<conclusión destilada>"",
  ""docAction"": ""added|reinforced|updated|skipped_duplicate|resolved_contradiction"",
  ""docChange"": ""<qué añadiste/cambiaste o por qué lo descartaste>"",
  ""updatedDoc"": ""<documento markdown COMPLETO actualizado>"",
  ""activeLearnings"": [{{ ""module"": ""<nombre>|general"", ""text"": ""<aprendizaje conciso>"" }}]
}}";
        }

        private sealed class AnalysisResult
        {
            public string? AttributionsJson { get; set; }
            public string? ImageCritique { get; set; }
            public string? Conclusion { get; set; }
            public string? DocAction { get; set; }
            public string? DocChange { get; set; }
            public string? UpdatedDoc { get; set; }
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
                    && !root.TryGetProperty("updatedDoc", out _)
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
                UpdatedDoc = Str("updatedDoc"),
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
