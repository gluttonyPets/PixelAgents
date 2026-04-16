using System.Text.Json;

namespace Server.Services.Ai.Handlers;

/// <summary>
/// Builds a JSON scene object from static field values + connected inputs.
/// Output is a JSON string representing one scene/slide.
/// </summary>
public class SceneModuleHandler : IModuleHandler
{
    public string ModuleType => "Scene";

    public Task<ModuleResult> ExecuteAsync(ModuleExecutionContext ctx)
    {
        var sceneObj = new Dictionary<string, object>();

        // 1. Load static field values from config (keys prefixed with "fv_")
        foreach (var (key, val) in ctx.Config)
        {
            if (!key.StartsWith("fv_")) continue;
            var fieldName = key["fv_".Length..];
            string strVal;
            if (val is JsonElement je)
                strVal = je.ValueKind == JsonValueKind.String ? (je.GetString() ?? "") : je.GetRawText();
            else
                strVal = val?.ToString() ?? "";

            if (!string.IsNullOrEmpty(strVal))
                SetAutoTyped(sceneObj, fieldName, strVal);
        }

        // 2. Override with connected input values (port data takes priority)
        foreach (var (portId, dataList) in ctx.InputsByPort)
        {
            if (!portId.StartsWith("input_field_")) continue;
            var fieldName = portId["input_field_".Length..];

            foreach (var data in dataList)
            {
                if (!string.IsNullOrWhiteSpace(data.TextContent))
                {
                    SetAutoTyped(sceneObj, fieldName, data.TextContent);
                }
                else if (data.Files is { Count: > 0 })
                {
                    // Use first file's info as value (URL would be resolved by executor)
                    sceneObj[fieldName] = data.Files[0].FileName;
                }
            }
        }

        var sceneJson = JsonSerializer.Serialize(sceneObj);

        var output = new StepOutput
        {
            Type = "scene",
            Content = sceneJson,
        };

        return Task.FromResult(ModuleResult.Completed(output));
    }

    private static void SetAutoTyped(Dictionary<string, object> dict, string key, string value)
    {
        if (int.TryParse(value, out var intVal))
            dict[key] = intVal;
        else if (double.TryParse(value, System.Globalization.NumberStyles.Any,
                     System.Globalization.CultureInfo.InvariantCulture, out var dblVal)
                 && value.Contains('.'))
            dict[key] = dblVal;
        else
            dict[key] = value;
    }
}
