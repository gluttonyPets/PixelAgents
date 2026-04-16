using System.Text.Json;
using Server.Models;

namespace Server.Services.Ai.Handlers;

/// <summary>
/// VideoEdit: video composition from script + media scenes OR template variables.
/// Supports two modes:
/// - Scene mode: input_script + input_scene_N_media -> video
/// - Template mode: input_tpl_* variables -> template video
/// </summary>
public class VideoEditModuleHandler : IModuleHandler
{
    private readonly IAiProviderRegistry _registry;
    public string ModuleType => "VideoEdit";

    public VideoEditModuleHandler(IAiProviderRegistry registry) => _registry = registry;

    public async Task<ModuleResult> ExecuteAsync(ModuleExecutionContext ctx)
    {
        var module = ctx.Node.AiModule;
        var provider = _registry.GetProvider(module.ProviderType);
        if (provider is null)
            return ModuleResult.Failed($"Proveedor '{module.ProviderType}' no disponible");

        var apiKey = module.ApiKey?.EncryptedKey;
        if (string.IsNullOrEmpty(apiKey))
            return ModuleResult.Failed("API Key no configurada");

        var isTemplateMode = ctx.GetConfigBool("useTemplate", false);
        string input;

        if (isTemplateMode)
        {
            input = BuildTemplateInput(ctx);
        }
        else
        {
            input = BuildSceneInput(ctx);
        }

        if (string.IsNullOrWhiteSpace(input))
            return ModuleResult.Failed("VideoEdit: sin datos de entrada");

        var aiContext = new AiExecutionContext
        {
            ModuleType = module.ModuleType,
            ModelName = module.ModelName,
            ApiKey = apiKey,
            Input = input,
            ProjectContext = ctx.Project.Context,
            Configuration = ctx.Config,
        };

        var result = await provider.ExecuteAsync(aiContext);
        if (!result.Success)
            return ModuleResult.Failed(result.Error ?? "Error en edicion de video");

        var producedFiles = new List<ProducedFile>();
        var outputFiles = new List<OutputFile>();

        if (result.FileOutput is { Length: > 0 })
        {
            var fileName = "output.mp4";
            producedFiles.Add(new ProducedFile
            {
                Data = result.FileOutput,
                FileName = fileName,
                ContentType = result.ContentType ?? "video/mp4",
            });
            outputFiles.Add(new OutputFile
            {
                FileName = fileName,
                ContentType = result.ContentType ?? "video/mp4",
                FileSize = result.FileOutput.Length,
            });
        }

        var output = OutputSchemaHelper.BuildVideoOutput(outputFiles, module.ModelName, result.Metadata);
        return ModuleResult.Completed(output, result.EstimatedCost, producedFiles);
    }

    private static string BuildTemplateInput(ModuleExecutionContext ctx)
    {
        var templateVars = new Dictionary<string, object>();

        foreach (var (portId, dataList) in ctx.InputsByPort)
        {
            if (!portId.StartsWith("input_tpl_")) continue;
            var varName = portId["input_tpl_".Length..];

            if (dataList.Count == 1)
            {
                var data = dataList[0];
                if (data.DataType == "scene" && !string.IsNullOrWhiteSpace(data.TextContent))
                {
                    // Single scene -> parse as object
                    try
                    {
                        var sceneObj = JsonSerializer.Deserialize<Dictionary<string, object>>(data.TextContent);
                        if (sceneObj is not null) { templateVars[varName] = sceneObj; continue; }
                    }
                    catch { /* fall through */ }
                }
                templateVars[varName] = ResolveTemplateValue(ctx, data);
            }
            else if (dataList.Count > 1)
            {
                // Multiple connections (scene array)
                var items = new List<object>();
                foreach (var data in dataList)
                {
                    if (data.DataType == "scene" && !string.IsNullOrWhiteSpace(data.TextContent))
                    {
                        try
                        {
                            var sceneObj = JsonSerializer.Deserialize<Dictionary<string, object>>(data.TextContent);
                            if (sceneObj is not null) { items.Add(sceneObj); continue; }
                        }
                        catch { /* fall through */ }
                    }
                    var value = ResolveTemplateValue(ctx, data);
                    if (!string.IsNullOrWhiteSpace(value))
                        items.Add(value);
                }
                templateVars[varName] = items;
            }
        }

        // Static fv_ values as fallbacks
        foreach (var (key, val) in ctx.Config)
        {
            if (!key.StartsWith("fv_")) continue;
            var fvName = key["fv_".Length..];
            if (templateVars.ContainsKey(fvName)) continue;
            string strVal;
            if (val is JsonElement je)
                strVal = je.ValueKind == JsonValueKind.String ? (je.GetString() ?? "") : je.GetRawText();
            else
                strVal = val?.ToString() ?? "";
            if (!string.IsNullOrEmpty(strVal))
                templateVars[fvName] = strVal;
        }

        var templateId = ctx.GetConfig("templateId", "");

        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["template"] = templateId,
            ["variables"] = templateVars,
        });
    }

    private static string ResolveTemplateValue(ModuleExecutionContext ctx, PortData data)
    {
        if (data.Files is { Count: > 0 })
        {
            var file = data.Files[0];
            return ctx.GetPublicFileUrl(file) ?? file.FileName;
        }

        return data.TextContent ?? "";
    }

    private static string BuildSceneInput(ModuleExecutionContext ctx)
    {
        // Script from input_script or any text input
        var script = ctx.GetInputText("input_script");
        if (!string.IsNullOrWhiteSpace(script))
            return script;

        // Fallback: collect all text inputs
        foreach (var (portId, dataList) in ctx.InputsByPort)
        {
            foreach (var data in dataList)
            {
                if (!string.IsNullOrWhiteSpace(data.TextContent))
                    return data.TextContent;
            }
        }

        return "";
    }
}
