using Server.Models;

namespace Server.Services.Ai.Handlers;

/// <summary>
/// Speech-to-Text module.
///
/// Con la casilla de marcas de tiempo la salida es una frase por linea con su
/// tramo (<c>[00:04.2 - 00:09.8] texto</c>): es lo que lee el orquestador loop
/// por su puerto de locucion para saber que se dice en cada parte.
/// </summary>
public class TranscriptionModuleHandler : IModuleHandler
{
    private readonly IAiProviderRegistry _registry;
    public string ModuleType => "Transcription";

    public TranscriptionModuleHandler(IAiProviderRegistry registry) => _registry = registry;

    public async Task<ModuleResult> ExecuteAsync(ModuleExecutionContext ctx)
    {
        var module = ctx.Node.AiModule;
        var provider = _registry.GetProvider(module.ProviderType);
        if (provider is null)
            return ModuleResult.Failed($"Proveedor '{module.ProviderType}' no disponible");

        var apiKey = module.ApiKey?.EncryptedKey;
        if (string.IsNullOrEmpty(apiKey))
            return ModuleResult.Failed("API Key no configurada");

        // Load audio file from input
        var inputFiles = new List<byte[]>();
        var inputMetas = new List<InputFileMeta>();
        double? audioSeconds = null;
        var audioFiles = ctx.GetInputFiles("input_audio");
        foreach (var fi in audioFiles)
        {
            var bytes = await ctx.ReadOutputFileBytesAsync(fi);
            if (bytes is null) continue;

            inputFiles.Add(bytes);
            inputMetas.Add(new InputFileMeta { FileName = fi.FileName, ContentType = fi.ContentType ?? "", FileSize = bytes.Length });
            audioSeconds ??= await VideoAssembler.ProbeDurationAsync(ctx.ResolveOutputFilePath(fi), ctx.CancellationToken);
        }

        if (inputFiles.Count == 0)
            return ModuleResult.Failed("Sin audio de entrada. Conecta un modulo de audio al puerto 'Audio'.");

        if (inputFiles.Count > 1)
            await ctx.LogWarningAsync(
                $"Llegan {inputFiles.Count} audios y se transcribe solo el primero. "
                + "Si son las tomas de una locucion, transcribe el audio completo o une las tomas antes.");

        var config = new Dictionary<string, object>(ctx.Config);
        if (audioSeconds is double seconds)
            config[OpenAiTranscription.AudioSecondsKey] = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var aiContext = new AiExecutionContext
        {
            ModuleType = module.ModuleType,
            ModelName = module.ModelName,
            ApiKey = apiKey,
            Input = "",
            Configuration = config,
            InputFiles = inputFiles,
            InputFileMetas = inputMetas,
            CancellationToken = ctx.CancellationToken,
        };

        var result = await provider.ExecuteAsync(aiContext);
        if (!result.Success)
            return ModuleResult.Failed(result.Error ?? "Error en transcripcion");

        if (result.Metadata.TryGetValue("timestampsUnsupported", out var unsupported) && unsupported is true)
            await ctx.LogWarningAsync(
                $"El modelo {module.ModelName} no devuelve marcas de tiempo: se entrega el texto sin ellas "
                + "(whisper-1 si las da)");

        var output = new StepOutput
        {
            Type = "text",
            Content = result.TextOutput ?? "",
        };

        return ModuleResult.Completed(output, result.EstimatedCost);
    }
}
