using Server.Models;

namespace Server.Services.Ai.Handlers;

/// <summary>
/// Text generation module. Calls AI provider with structured JSON output.
/// Input: input_prompt (text) -> Output: output_text (text with items)
/// </summary>
public class TextModuleHandler : IModuleHandler
{
    private readonly IAiProviderRegistry _registry;
    public string ModuleType => "Text";

    public TextModuleHandler(IAiProviderRegistry registry) => _registry = registry;

    public async Task<ModuleResult> ExecuteAsync(ModuleExecutionContext ctx)
    {
        var prompt = ctx.GetInputText("input_prompt");
        if (string.IsNullOrWhiteSpace(prompt))
            return ModuleResult.Failed("Sin prompt de entrada");

        var outgoingFormats = ctx.GetOutgoingFormats();
        if (outgoingFormats.Count > 0)
            prompt = OutputSchemaHelper.GetOutputFormatInstruction(outgoingFormats) + "\n\n" + prompt;

        var module = ctx.Node.AiModule;
        var provider = _registry.GetProvider(module.ProviderType);
        if (provider is null)
            return ModuleResult.Failed($"Proveedor '{module.ProviderType}' no disponible");

        var apiKey = module.ApiKey?.EncryptedKey;
        if (string.IsNullOrEmpty(apiKey))
            return ModuleResult.Failed("API Key no configurada");

        // Fan-in: a text module may have N FileUpload (or other file-producing)
        // modules wired into its input port. Read every produced file and forward
        // bytes + MIME so the provider can decide between image vs document blocks.
        // Spreadsheets (.xls/.xlsx) are converted to plain text up-front because
        // no major LLM (Claude/OpenAI/Gemini) accepts binary Excel as an attachment.
        var inputFiles = new List<byte[]>();
        var inputFileMetas = new List<InputFileMeta>();
        var inputFileInfos = ctx.GetInputFiles("input_prompt");
        foreach (var fi in inputFileInfos)
        {
            var bytes = await ctx.ReadOutputFileBytesAsync(fi);
            if (bytes is null) continue;

            var fileName = fi.FileName ?? "";
            var contentType = fi.ContentType ?? "";

            if (SpreadsheetConverter.IsSpreadsheet(fileName, contentType, bytes))
            {
                try
                {
                    var text = SpreadsheetConverter.ConvertToText(bytes, fileName);
                    var textBytes = System.Text.Encoding.UTF8.GetBytes(text);
                    await ctx.LogInfoAsync(
                        $"[Text] Convertido spreadsheet '{fileName}' " +
                        $"({contentType}, {bytes.Length} B) a texto plano ({textBytes.Length} B).");
                    bytes = textBytes;
                    contentType = "text/plain";
                    fileName = Path.ChangeExtension(string.IsNullOrEmpty(fileName) ? "spreadsheet" : fileName, ".txt");
                }
                catch (Exception ex)
                {
                    await ctx.LogInfoAsync(
                        $"[Text] No se pudo convertir spreadsheet '{fileName}': {ex.Message}. " +
                        $"Se reenvian los bytes originales; el modelo probablemente no podra leerlos.");
                }
            }

            inputFiles.Add(bytes);
            inputFileMetas.Add(new InputFileMeta
            {
                FileName = fileName,
                ContentType = contentType,
                FileSize = bytes.Length,
            });
        }

        if (inputFiles.Count > 0)
        {
            var totalBytes = inputFiles.Sum(b => (long)b.Length);
            var perFile = string.Join(", ", inputFileMetas.Select(m => $"{m.FileName} ({m.ContentType}, {m.FileSize} B)"));
            await ctx.LogInfoAsync(
                $"[Text] {module.ProviderType}/{module.ModelName}: adjuntando {inputFiles.Count} archivo(s) " +
                $"[{perFile}], total {totalBytes} B, prompt {prompt.Length} chars.");
        }

        var aiContext = new AiExecutionContext
        {
            ModuleType = module.ModuleType,
            ModelName = module.ModelName,
            ApiKey = apiKey,
            Input = prompt,
            ProjectContext = ctx.Project.Context,
            PreviousExecutionsSummary = ctx.PreviousSummaryContext,
            MandatoryRules = ctx.MandatoryRules,
            Configuration = ctx.Config,
            InputFiles = inputFiles.Count > 0 ? inputFiles : null,
            InputFileMetas = inputFileMetas.Count > 0 ? inputFileMetas : null,
        };

        StepPayloadBuilder.RecordSentPayload(ctx, aiContext, module.ProviderType);

        var result = await provider.ExecuteAsync(aiContext);
        if (!result.Success)
            return ModuleResult.Failed(result.Error ?? "Error en generacion de texto");

        var rawText = result.TextOutput ?? "";
        var stepOutput = new StepOutput
        {
            Type = "text",
            Content = rawText,
            Metadata = result.Metadata ?? new(),
        };

        var producedFiles = new List<ProducedFile>();
        if (!string.IsNullOrEmpty(rawText))
        {
            producedFiles.Add(new ProducedFile
            {
                Data = System.Text.Encoding.UTF8.GetBytes(rawText),
                FileName = "output.txt",
                ContentType = "text/plain",
            });
        }

        return ModuleResult.Completed(stepOutput, result.EstimatedCost, producedFiles);
    }
}
