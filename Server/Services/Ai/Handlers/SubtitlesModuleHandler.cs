using Server.Models;

namespace Server.Services.Ai.Handlers;

/// <summary>
/// Subtitulos palabra a palabra sobre un video, estilo TikTok.
///
/// La IA (ElevenLabs) solo dice cuando suena cada palabra (<see cref="ElevenLabsTimings"/>).
/// Las letras, su estilo y el resaltado de la palabra activa los escribe
/// <see cref="SubtitleBuilder"/> en un fichero ASS, y ffmpeg los pinta en el video
/// (<see cref="SubtitleBurner"/>).
///
/// Si al puerto de texto llega la locucion (lo que escribio el modulo de voz en
/// off), se alinea ese texto con el audio: subtitulos con el texto exacto. Si no,
/// se transcribe el audio del video con Scribe.
/// </summary>
public class SubtitlesModuleHandler : IModuleHandler
{
    public const string Type = "Subtitles";
    public const string VideoPort = "input_video";
    public const string TextPort = "input_text";
    public const string OutputFileName = "video_subtitulado.mp4";
    public const string SrtFileName = "subtitulos.srt";

    private readonly IHttpClientFactory _httpFactory;

    public SubtitlesModuleHandler(IHttpClientFactory httpFactory) => _httpFactory = httpFactory;

    public string ModuleType => Type;

    public async Task<ModuleResult> ExecuteAsync(ModuleExecutionContext ctx)
    {
        var video = FindVideo(ctx);
        if (video is null)
            return ModuleResult.Failed($"Sin video que subtitular. Conecta un video al puerto '{VideoPort}'.");

        var module = ctx.Node.AiModule;
        var apiKey = module.ApiKey?.EncryptedKey;
        if (string.IsNullOrEmpty(apiKey))
            return ModuleResult.Failed("API Key de ElevenLabs no configurada");

        if (!await VideoAssembler.IsAvailableAsync(ctx.CancellationToken))
            return ModuleResult.Failed("ffmpeg no esta instalado en el servidor: hace falta para poner los subtitulos.");

        var videoPath = ctx.ResolveOutputFilePath(video);
        var workDir = Path.Combine(Path.GetTempPath(), $"pixelagents_subs_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        try
        {
            var (audio, audioError) = await SubtitleBurner.ExtractAudioAsync(videoPath, workDir, ctx.CancellationToken);
            if (audio is null) return ModuleResult.Failed(audioError!);

            // El texto se limpia igual que para locutarlo: sin marcas de toma ni el
            // contexto previo, que no se dicen y descuadrarian la alineacion.
            var spoken = AudioModuleHandler.BuildTakes(ctx.GetAllInputTexts(TextPort), out _);
            var text = string.Join("\n", spoken);

            var (words, timingError) = text.Length > 0
                ? await AlignAsync(ctx, apiKey, audio, text)
                : await TranscribeAsync(ctx, apiKey, audio, module.ModelName);
            if (words is null) return ModuleResult.Failed(timingError!);
            if (words.Count == 0) return ModuleResult.Failed("ElevenLabs no encontro palabras en el audio del video.");

            var style = SubtitleBuilder.Style.FromConfig(ctx.Config);
            var groups = SubtitleBuilder.Group(words, style.WordsPerGroup);
            var (width, height) = await VideoAssembler.ProbeDimensionsAsync(videoPath, ctx.CancellationToken);
            await ctx.LogInfoAsync($"{words.Count} palabras en {groups.Count} grupos de hasta {style.WordsPerGroup}; video de {width}x{height}");

            var output = Path.Combine(workDir, OutputFileName);
            var burnError = await SubtitleBurner.BurnAsync(
                videoPath, SubtitleBuilder.BuildAss(groups, style, width, height), workDir, output, ctx.CancellationToken);
            if (burnError is not null) return ModuleResult.Failed(burnError);

            var bytes = await File.ReadAllBytesAsync(output, ctx.CancellationToken);
            var srt = System.Text.Encoding.UTF8.GetBytes(SubtitleBuilder.BuildSrt(groups));

            // Se cobra por hora de audio; el audio es el del video entero.
            var seconds = await VideoAssembler.ProbeDurationAsync(videoPath, ctx.CancellationToken) ?? words[^1].End;
            var cost = PricingCatalog.GetAuxiliaryRate(module.ModelName) is { } rate ? rate.Amount * (decimal)seconds / 60m : 0m;

            return Deliver(bytes, srt, module.ModelName, words.Count, groups.Count, text.Length > 0, cost);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); }
            catch { /* el temporal se lo lleva el sistema */ }
        }
    }

    private async Task<(List<ElevenLabsTimings.Word>? Words, string? Error)> AlignAsync(
        ModuleExecutionContext ctx, string apiKey, byte[] audio, string text)
    {
        await ctx.LogInfoAsync($"Alineando el texto de la locucion ({text.Length} caracteres) con el audio del video");
        using var form = ElevenLabsTimings.BuildAlignmentForm(audio, "audio.mp3", text);
        return await PostAsync(apiKey, ElevenLabsTimings.AlignmentUrl, form, ctx.CancellationToken);
    }

    private async Task<(List<ElevenLabsTimings.Word>? Words, string? Error)> TranscribeAsync(
        ModuleExecutionContext ctx, string apiKey, byte[] audio, string model)
    {
        var language = ctx.GetConfig(SubtitleBuilder.LanguageKey, "es");
        await ctx.LogInfoAsync($"Sin texto de la locucion: transcribiendo el audio con {model} ({language})");
        using var form = ElevenLabsTimings.BuildTranscriptionForm(audio, "audio.mp3", model, language);
        return await PostAsync(apiKey, ElevenLabsTimings.TranscriptionUrl, form, ctx.CancellationToken);
    }

    private async Task<(List<ElevenLabsTimings.Word>? Words, string? Error)> PostAsync(
        string apiKey, string url, MultipartFormDataContent form, CancellationToken ct)
    {
        using var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(3);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        request.Headers.Add("xi-api-key", apiKey);

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        // 5xx y 429 se relanzan como transitorios para que el executor reintente.
        if ((int)response.StatusCode >= 500 || (int)response.StatusCode == 429)
        {
            var message = $"ElevenLabs {(int)response.StatusCode}: {body}";
            throw new TransientProviderException(message, new HttpRequestException(message));
        }
        if (!response.IsSuccessStatusCode)
            return (null, $"ElevenLabs ({(int)response.StatusCode}): {body}");

        return (ElevenLabsTimings.ParseWords(body), null);
    }

    private static ModuleResult Deliver(byte[] video, byte[] srt, string model, int words, int groups, bool aligned, decimal cost)
    {
        var produced = new List<ProducedFile>
        {
            new() { Data = video, FileName = OutputFileName, ContentType = "video/mp4" },
            new() { Data = srt, FileName = SrtFileName, ContentType = "application/x-subrip" },
        };

        var outputFiles = new List<OutputFile>
        {
            new() { FileName = OutputFileName, ContentType = "video/mp4", FileSize = video.Length },
            new() { FileName = SrtFileName, ContentType = "application/x-subrip", FileSize = srt.Length },
        };

        var output = OutputSchemaHelper.BuildVideoOutput(outputFiles, model, new Dictionary<string, object>
        {
            ["words"] = words,
            ["groups"] = groups,
            ["timing"] = aligned ? "alineacion con el texto" : "transcripcion",
        });

        return ModuleResult.Completed(output, cost, produced);
    }

    /// <summary>
    /// El video a subtitular: el primero que llegue por su puerto o, si esta vacio,
    /// por cualquier otro (como hace el montaje con sus clips).
    /// </summary>
    private static OutputFile? FindVideo(ModuleExecutionContext ctx)
    {
        var files = ctx.GetInputFiles(VideoPort);
        if (files.Count == 0)
            files = ctx.InputsByPort.SelectMany(kv => kv.Value)
                .Where(d => d.Files is not null)
                .SelectMany(d => d.Files!)
                .ToList();

        return files.FirstOrDefault(f => f.ContentType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true);
    }
}
