using System.Text.Json.Serialization;

namespace Client.Models;

// ── Port data types (determines color and connection compatibility) ──
public static class PortDataType
{
    public const string Text = "text";
    public const string Image = "image";
    public const string Video = "video";
    public const string VideoList = "video[]";
    public const string Audio = "audio";
    public const string File = "file";
    public const string Config = "config";
    public const string Any = "any";

    public static string GetColor(string type) => type switch
    {
        Text => "#6c63ff",
        Image => "#4caf50",
        Video => "#ff9800",
        VideoList => "#ff9800",
        Audio => "#e91e63",
        File => "#888",
        Config => "#ffc107",
        Any => "#e0e0e0",
        _ => "#e0e0e0"
    };

    public static string GetLabel(string type) => type switch
    {
        Text => "Texto",
        Image => "Imagen",
        Video => "Video",
        VideoList => "Videos",
        Audio => "Audio",
        File => "Archivo",
        Config => "Config",
        Any => "Cualquiera",
        _ => type
    };

    public static bool AreCompatible(string output, string input)
    {
        if (input == Any || output == Any) return true;
        if (input == output) return true;
        if (input == Video && output == VideoList) return true;
        if (input == VideoList && output == Video) return true;
        if (input == File) return true; // file accepts anything
        return false;
    }
}

// ── Port definition ──
public class PortDefinition
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string DataType { get; set; } = PortDataType.Any;
    public bool IsInput { get; set; }
    public bool IsRequired { get; set; }
    /// <summary>When true, this input port accepts multiple connections (data is aggregated).
    /// Defaults to true for all input ports — every module can receive multiple inputs.</summary>
    public bool AllowMultiple { get; set; } = true;

    public PortDefinition() { }
    public PortDefinition(string id, string label, string dataType, bool isInput, bool isRequired = false, bool allowMultiple = true)
    {
        Id = id; Label = label; DataType = dataType; IsInput = isInput; IsRequired = isRequired; AllowMultiple = allowMultiple;
    }
}

// ── Graph persistence ──
public class PipelineGraph
{
    [JsonPropertyName("nodes")]
    public List<PipelineNodeState> Nodes { get; set; } = [];

    [JsonPropertyName("connections")]
    public List<PipelineConnection> Connections { get; set; } = [];
}

public class PipelineNodeState
{
    [JsonPropertyName("moduleId")]
    public Guid ModuleId { get; set; }

    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    /// <summary>Extra config like sceneCount for Json2Video</summary>
    [JsonPropertyName("nodeConfig")]
    public Dictionary<string, object>? NodeConfig { get; set; }
}

public class PipelineConnection
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    [JsonPropertyName("fromModuleId")]
    public Guid FromModuleId { get; set; }

    [JsonPropertyName("fromPort")]
    public string FromPort { get; set; } = "";

    [JsonPropertyName("toModuleId")]
    public Guid ToModuleId { get; set; }

    [JsonPropertyName("toPort")]
    public string ToPort { get; set; } = "";
}

// ── Module port registry ──
public static class ModulePortRegistry
{
    public static List<PortDefinition> GetPorts(string moduleType, string providerType = "", int sceneCount = 0, string? inputMapping = null, List<OrchestratorOutputResponse>? orchestratorOutputs = null)
    {
        var ports = new List<PortDefinition>();

        switch (moduleType)
        {
            case "Text":
                ports.Add(new("input_prompt", "Prompt", PortDataType.Text, isInput: true));
                ports.Add(new("output_text", "Texto", PortDataType.Text, isInput: false));
                break;

            case "Image":
                ports.Add(new("input_prompt", "Prompt", PortDataType.Text, isInput: true));
                var imgCount = Math.Max(sceneCount, 1);
                if (imgCount == 1)
                {
                    ports.Add(new("output_image", "Imagen", PortDataType.Image, isInput: false));
                }
                else
                {
                    for (int i = 1; i <= imgCount; i++)
                        ports.Add(new($"output_image_{i}", $"Imagen {i}", PortDataType.Image, isInput: false));
                }
                break;

            case "Video":
                ports.Add(new("input_prompt", "Prompt", PortDataType.Text, isInput: true, isRequired: true));
                ports.Add(new("input_image", "Imagen ref.", PortDataType.Image, isInput: true));
                ports.Add(new("output_video", "Video", PortDataType.Video, isInput: false));
                break;

            case "VideoSearch":
                ports.Add(new("input_query", "Busqueda", PortDataType.Text, isInput: true, isRequired: true));
                ports.Add(new("output_videos", "Videos", PortDataType.VideoList, isInput: false));
                break;

            case "VideoEdit":
                ports.Add(new("input_script", "Guion", PortDataType.Text, isInput: true));
                // Dynamic media inputs (one per scene) — accept video, image, or any file
                // AllowMultiple: each scene port can receive multiple files (e.g. image + text overlay)
                var scenes = Math.Max(sceneCount, 1);
                for (int i = 1; i <= scenes; i++)
                {
                    ports.Add(new($"input_scene_{i}_media", $"Escena {i}", PortDataType.Any, isInput: true, isRequired: true, allowMultiple: true));
                }
                ports.Add(new("output_video", "Video final", PortDataType.Video, isInput: false));
                break;

            case "Audio":
                ports.Add(new("input_text", "Texto", PortDataType.Text, isInput: true, isRequired: true));
                ports.Add(new("output_audio", "Audio", PortDataType.Audio, isInput: false));
                break;

            case "Transcription":
                ports.Add(new("input_audio", "Audio", PortDataType.Audio, isInput: true, isRequired: true));
                ports.Add(new("output_text", "Texto", PortDataType.Text, isInput: false));
                break;

            case "Orchestrator":
                ports.Add(new("input_prompt", "Instrucciones", PortDataType.Text, isInput: true, isRequired: true));
                if (orchestratorOutputs?.Count > 0)
                {
                    foreach (var o in orchestratorOutputs)
                        ports.Add(new(o.OutputKey, o.Label, o.DataType, isInput: false));
                }
                else
                {
                    ports.Add(new("output_plan", "Plan", PortDataType.Text, isInput: false));
                }
                break;

            case "Design":
                ports.Add(new("input_prompt", "Instrucciones", PortDataType.Text, isInput: true, isRequired: true));
                ports.Add(new("output_file", "Diseno", PortDataType.File, isInput: false));
                break;

            case "Interaction":
                ports.Add(new("input_message", "Mensaje", PortDataType.Text, isInput: true));
                ports.Add(new("output_response", "Respuesta", PortDataType.Text, isInput: false));
                break;

            case "Checkpoint":
                var checkpointPorts = Math.Max(sceneCount, 1);
                for (int i = 1; i <= checkpointPorts; i++)
                {
                    ports.Add(new($"input_{i}", $"Entrada {i}", PortDataType.Any, isInput: true));
                    ports.Add(new($"output_{i}", $"Salida {i}", PortDataType.Any, isInput: false));
                }
                break;

            case "Coordinator":
                var coordInputs = Math.Max(sceneCount, 1);
                for (int i = 1; i <= coordInputs; i++)
                    ports.Add(new($"input_{i}", $"Entrada {i}", PortDataType.Any, isInput: true, allowMultiple: true));
                ports.Add(new("output_result", "Resultado", PortDataType.Text, isInput: false));
                break;

            case "Publish":
                ports.Add(new("input_content", "Contenido", PortDataType.Any, isInput: true, isRequired: true));
                ports.Add(new("output_result", "Resultado", PortDataType.Text, isInput: false));
                break;

            case "Embeddings":
                ports.Add(new("input_text", "Texto", PortDataType.Text, isInput: true, isRequired: true));
                ports.Add(new("output_embedding", "Embedding", PortDataType.File, isInput: false));
                break;

            default:
                ports.Add(new("input_data", "Entrada", PortDataType.Any, isInput: true));
                ports.Add(new("output_data", "Salida", PortDataType.Any, isInput: false));
                break;
        }

        // If inputMapping requests file input, widen the primary input port to accept any type
        if (inputMapping is not null && inputMapping.Contains("\"file\""))
        {
            var primaryInput = ports.FirstOrDefault(p => p.IsInput);
            if (primaryInput is not null)
            {
                primaryInput.DataType = PortDataType.Any;
            }
        }

        return ports;
    }

    public static string GetModuleIcon(string moduleType) => moduleType switch
    {
        "Text" => "bi-chat-left-text",
        "Image" => "bi-image",
        "Video" => "bi-camera-video",
        "VideoSearch" => "bi-search",
        "VideoEdit" => "bi-scissors",
        "Audio" => "bi-volume-up",
        "Transcription" => "bi-mic",
        "Orchestrator" => "bi-diagram-3",
        "Design" => "bi-palette",
        "Interaction" => "bi-chat-dots",
        "Publish" => "bi-send",
        "Embeddings" => "bi-grid-3x3",
        "Checkpoint" => "bi-check-circle",
        _ => "bi-gear"
    };

    public static string GetModuleColor(string moduleType) => moduleType switch
    {
        "Text" => "#6c63ff",
        "Image" => "#4caf50",
        "Video" => "#ff9800",
        "VideoSearch" => "#ff6f00",
        "VideoEdit" => "#e65100",
        "Audio" => "#e91e63",
        "Transcription" => "#ad1457",
        "Orchestrator" => "#7c4dff",
        "Design" => "#00bcd4",
        "Interaction" => "#26a69a",
        "Publish" => "#ab47bc",
        "Embeddings" => "#78909c",
        "Checkpoint" => "#f44336",
        _ => "#888"
    };
}
