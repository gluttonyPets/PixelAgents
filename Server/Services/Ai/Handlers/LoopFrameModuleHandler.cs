using Server.Models;

namespace Server.Services.Ai.Handlers;

/// <summary>
/// Rectangulo de loop ("Bucle"). Cuando le llega el turno, el cuerpo ya se ha
/// ejecutado una vez por parte (ver <see cref="LoopFrames"/>): lo unico que hace
/// es entregar por su salida lo que produjeron los ultimos modulos del cuerpo,
/// los ficheros en orden de parte (clip 1..N) y el texto con sus tomas.
///
/// Es un modulo de sistema: no llama a ningun proveedor ni tiene coste.
/// </summary>
public class LoopFrameModuleHandler : IModuleHandler
{
    public string ModuleType => LoopFrames.ModuleType;

    public async Task<ModuleResult> ExecuteAsync(ModuleExecutionContext ctx)
    {
        var info = ctx.Node.OwnedLoopFrame;
        if (info is null || info.Members.Count == 0)
            return ModuleResult.Failed(
                "El rectangulo de loop no tiene modulos dentro. Agrandalo o mueve dentro los modulos que se repiten.");

        if (info.Driver is null)
            await ctx.LogWarningAsync(
                "Ningun orquestador en modo loop alimenta a los modulos del rectangulo: se han ejecutado una sola vez. "
                + "Conecta una salida de un orquestador loop a un modulo de dentro.");

        var received = ctx.InputsByPort.GetValueOrDefault(LoopFrames.BodyPort) ?? [];

        var files = received
            .Where(d => d.Files is not null)
            .SelectMany(d => d.Files!)
            .ToList();

        var texts = received
            .Select(d => d.TextContent)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Cast<string>()
            .ToList();

        var parts = info.Driver is null ? 1 : LoopFrames.PartsFor(info);
        await ctx.LogInfoAsync(
            $"Loop terminado: {parts} parte(s) de {info.Members.Count} modulo(s); salen {files.Count} fichero(s)");

        var output = new StepOutput
        {
            Type = "loop",
            Title = $"Loop de {parts} parte(s)",
            Content = string.Join("\n\n", texts),
            Files = files,
            Metadata = new Dictionary<string, object>
            {
                ["parts"] = parts,
                ["members"] = info.Members.Select(m => m.ProjectModule.StepName ?? m.AiModule.Name).ToList(),
            },
        };

        return ModuleResult.Completed(output);
    }
}
