using Server.Models;

namespace Server.Services.Ai.Handlers;

/// <summary>
/// Texto a voz.
///
/// Si el texto llega repartido en tomas (marcas <c>===TOMA n===</c> o el JSON
/// del contrato, el mismo reparto que usan los modulos de imagen y video) se
/// genera UN AUDIO POR TOMA, en orden: <c>voz_1.mp3</c>, <c>voz_2.mp3</c>... Es
/// lo que necesita el montaje para cuadrar cada frase con su clip, porque asi
/// se conoce la duracion real de cada una en vez de adivinar donde empieza cada
/// frase dentro de una locucion unica.
///
/// El texto anterior a la primera marca NO se locuta: en un guion repartido es
/// contexto (estilo, indicaciones), no voz en off. Sin marcas se locuta el
/// texto entero en un solo audio.
///
/// Si falla una toma falla el modulo entero, al contrario que el modulo de
/// video: un hueco correria todas las voces siguientes un clip hacia delante,
/// y repetir una locucion cuesta centimos.
/// </summary>
public class AudioModuleHandler : IModuleHandler
{
    public const string InputPort = "input_text";

    private readonly IAiProviderRegistry _registry;
    public string ModuleType => "Audio";

    public AudioModuleHandler(IAiProviderRegistry registry) => _registry = registry;

    public async Task<ModuleResult> ExecuteAsync(ModuleExecutionContext ctx)
    {
        var takes = BuildTakes(ctx.GetAllInputTexts(InputPort), out var droppedPreamble);
        if (takes.Count == 0)
            return ModuleResult.Failed("Sin texto de entrada para TTS");

        var module = ctx.Node.AiModule;
        var provider = _registry.GetProvider(module.ProviderType);
        if (provider is null)
            return ModuleResult.Failed($"Proveedor '{module.ProviderType}' no disponible");

        var apiKey = module.ApiKey?.EncryptedKey;
        if (string.IsNullOrEmpty(apiKey))
            return ModuleResult.Failed("API Key no configurada");

        if (droppedPreamble)
            await ctx.LogInfoAsync("El texto anterior a la primera toma es contexto y no se locuta");

        if (takes.Count > 1)
            await ctx.LogInfoAsync($"Generando {takes.Count} locuciones, una por toma");

        var producedFiles = new List<ProducedFile>();
        var outputFiles = new List<OutputFile>();
        var totalCost = 0m;
        var totalChars = 0;

        for (var i = 0; i < takes.Count; i++)
        {
            var aiContext = new AiExecutionContext
            {
                ModuleType = module.ModuleType,
                ModelName = module.ModelName,
                ApiKey = apiKey,
                Input = takes[i],
                Configuration = ctx.Config,
                CancellationToken = ctx.CancellationToken,
            };

            var result = await provider.ExecuteAsync(aiContext);
            if (!result.Success || result.FileOutput is not { Length: > 0 })
            {
                var error = result.Error ?? "el proveedor no devolvio audio";
                return ModuleResult.Failed(takes.Count > 1 ? $"Toma {i + 1}: {error}" : error);
            }

            var fileName = takes.Count > 1 ? $"voz_{i + 1}.mp3" : "output.mp3";
            var contentType = result.ContentType ?? "audio/mpeg";

            producedFiles.Add(new ProducedFile { Data = result.FileOutput, FileName = fileName, ContentType = contentType });
            outputFiles.Add(new OutputFile { FileName = fileName, ContentType = contentType, FileSize = result.FileOutput.Length });

            totalCost += result.EstimatedCost;
            totalChars += takes[i].Length;
        }

        var output = OutputSchemaHelper.BuildAudioOutput(outputFiles, module.ModelName, ctx.GetConfig("voice", OpenAiSpeech.DefaultVoice));
        output.Metadata["takes"] = takes.Count;
        output.Metadata["characters"] = totalChars;

        return ModuleResult.Completed(output, totalCost, producedFiles);
    }

    /// <summary>
    /// Textos a locutar, uno por toma. Se limpian de markdown y simbolos porque
    /// el TTS los leeria en voz alta ("asterisco", "almohadilla").
    /// </summary>
    public static List<string> BuildTakes(IEnumerable<string?> inputs, out bool droppedPreamble)
    {
        var split = MultiImagePrompt.Split(inputs);

        var raw = split.Segments.Count > 0 ? split.Segments : [split.Common];
        droppedPreamble = split.Segments.Count > 0 && !string.IsNullOrWhiteSpace(split.Common);

        return raw
            .Select(t => InputAdapter.SanitizePlainText(t ?? "").Trim())
            .Where(t => t.Length > 0)
            .ToList();
    }
}
