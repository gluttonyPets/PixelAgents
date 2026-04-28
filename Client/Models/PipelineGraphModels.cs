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
    public const string Scene = "scene";
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
        Scene => "#ff7043",
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
        Scene => "Escena",
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
        if (input == Scene && output == Scene) return true;
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

// ── Template variable for Json2Video ──
public class TemplateVariable
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "text"; // "text", "file", or "scene"

    /// <summary>Sub-fields when Type is "scene". Each field defines a variable inside each scene instance.</summary>
    [JsonPropertyName("fields")]
    public List<SceneField>? Fields { get; set; }
}

public class SceneField
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "text"; // "text" or "file"
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
    public static List<PortDefinition> GetPorts(string moduleType, string providerType = "", int sceneCount = 0, List<OrchestratorOutputResponse>? orchestratorOutputs = null, List<TemplateVariable>? templateVars = null, string modelName = "")
    {
        var ports = new List<PortDefinition>();

        switch (moduleType)
        {
            case "Text":
                ports.Add(new("input_prompt", "Prompt", PortDataType.Text, isInput: true));
                ports.Add(new("output_text", "Texto", PortDataType.Text, isInput: false));
                break;

            case "Image":
                ports.Add(new("input_prompt", "Prompt", PortDataType.Any, isInput: true));
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
                if (templateVars is { Count: > 0 })
                {
                    // Template mode: each variable becomes an input port
                    foreach (var tv in templateVars)
                    {
                        if (tv.Type == "scene")
                        {
                            // Scene variables accept multiple Scene modules (one per slide)
                            ports.Add(new($"input_tpl_{tv.Name}", tv.Name, PortDataType.Scene, isInput: true, isRequired: false, allowMultiple: true));
                        }
                        else
                        {
                            var dataType = tv.Type == "file" ? PortDataType.File : PortDataType.Text;
                            ports.Add(new($"input_tpl_{tv.Name}", tv.Name, dataType, isInput: true, isRequired: false, allowMultiple: false));
                        }
                    }
                }
                else
                {
                    ports.Add(new("input_script", "Guion", PortDataType.Text, isInput: true));
                    // Dynamic media inputs (one per scene) — accept video, image, or any file
                    // AllowMultiple: each scene port can receive multiple files (e.g. image + text overlay)
                    var scenes = Math.Max(sceneCount, 1);
                    for (int i = 1; i <= scenes; i++)
                    {
                        ports.Add(new($"input_scene_{i}_media", $"Escena {i}", PortDataType.Any, isInput: true, isRequired: true, allowMultiple: true));
                    }
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

            case "FileUpload":
                ports.Add(new("output_file", "Archivo", PortDataType.Any, isInput: false));
                break;

            case "Scene":
                // Dynamic input ports from configured scene fields
                if (templateVars is { Count: > 0 })
                {
                    foreach (var field in templateVars)
                    {
                        var dataType = field.Type == "file" ? PortDataType.File : PortDataType.Text;
                        ports.Add(new($"input_field_{field.Name}", field.Name, dataType, isInput: true, isRequired: false, allowMultiple: false));
                    }
                }
                ports.Add(new("output_scene", "Escena", PortDataType.Scene, isInput: false));
                break;

            case "StaticText":
                ports.Add(new("output_text", "Texto", PortDataType.Text, isInput: false));
                break;

            case "Start":
                ports.Add(new("output_prompt", "Prompt", PortDataType.Text, isInput: false));
                break;

            default:
                ports.Add(new("input_data", "Entrada", PortDataType.Any, isInput: true));
                ports.Add(new("output_data", "Salida", PortDataType.Any, isInput: false));
                break;
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
        "FileUpload" => "bi-paperclip",
        "Scene" => "bi-layers",
        "StaticText" => "bi-fonts",
        "Start" => "bi-play-circle",
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
        "FileUpload" => "#607d8b",
        "Scene" => "#ff7043",
        "StaticText" => "#5c6bc0",
        "Start" => "#43a047",
        _ => "#888"
    };
}

// ── Active rules catalog ──

/// <summary>A rule entry shown in the inspector so the user knows what is
/// being injected into the prompt for a given module.</summary>
public class ActiveRule
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Full text of the rule as it is sent to the provider. Null when
    /// the rule body is dynamic or comes from the tenant DB.</summary>
    public string? Body { get; set; }
}

/// <summary>
/// Resolves which rules are active for a given module. Three sources:
///   - tenant rules from /api/rules (user-managed)
///   - built-in text formatting/brand/behavior rules (server-side
///     OutputSchemaHelper.GetTextContentRules, mirrored here as user-facing
///     summary)
///   - context-dependent rules (multi-image disaggregation, per-module
///     system prompt)
/// Keep the text summaries in sync with Server/Services/Ai/OutputSchema.cs.
/// </summary>
public static class ActiveRulesRegistry
{
    private static readonly HashSet<string> AiCallingModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "Text", "Coordinator", "Orchestrator", "Image", "Video", "VideoEdit",
        "VideoSearch", "Audio", "Transcription", "Design", "Embeddings",
    };

    private static readonly HashSet<string> TextPathModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "Text", "Coordinator", "Orchestrator",
    };

    /// <summary>Build the full list of rules that act on a module.</summary>
    public static List<ActiveRule> GetActiveRules(
        string moduleType,
        int sceneCount,
        string? systemPrompt,
        IReadOnlyList<RuleResponse>? tenantRules)
    {
        var rules = new List<ActiveRule>();

        var usesProvider = AiCallingModules.Contains(moduleType);
        var usesTextPath = TextPathModules.Contains(moduleType);

        // Tenant rules: injected as MandatoryRules on every provider call.
        if (usesProvider && tenantRules is { Count: > 0 })
        {
            foreach (var r in tenantRules.Where(r => r.IsActive).OrderBy(r => r.SortOrder))
            {
                rules.Add(new ActiveRule
                {
                    Id = $"tenant:{r.Id}",
                    Category = "Reglas del proyecto",
                    Title = r.Title,
                    Description = "Regla configurada en /rules aplicada a todos los modulos con llamada a IA.",
                    Body = r.Content,
                });
            }
        }

        // Per-module system prompt override.
        if (usesProvider && !string.IsNullOrWhiteSpace(systemPrompt))
        {
            rules.Add(new ActiveRule
            {
                Id = "systemPrompt",
                Category = "Prompt de sistema",
                Title = "Prompt de sistema personalizado",
                Description = "Directiva configurada en este modulo; tiene prioridad sobre las reglas del proyecto.",
                Body = systemPrompt,
            });
        }

        // Text formatting / brand / behavior rules (OpenAI, Gemini, Grok, Anthropic).
        if (usesTextPath)
        {
            rules.Add(new ActiveRule
            {
                Id = "text-behavior",
                Category = "Comportamiento",
                Title = "Respuesta directa",
                Description = "No hace preguntas; decide y responde sin pedir aclaraciones.",
                Body = "- NUNCA hagas preguntas al usuario. Si hay varias opciones posibles, elige la mejor opcion tu mismo y da directamente la respuesta final.\n- Se concreto y directo. No pidas aclaraciones, no ofrezcas alternativas, no preguntes preferencias. Decide y responde.",
            });
            rules.Add(new ActiveRule
            {
                Id = "text-format",
                Category = "Formato",
                Title = "ASCII plano, sin markdown ni emojis",
                Description = "Sin emojis, ni formato markdown (**, #, listas), ni caracteres decorativos.",
                Body = "- NO uses emojis ni emoticonos de ningun tipo (ni unicode ni shortcodes).\n- NO uses formato markdown: nada de **, *, #, ##, ```, >, -, ni listas con vinetas.\n- NO uses caracteres especiales decorativos: flechas, bullets, guiones largos, comillas tipograficas, simbolos como estrella, circulo, rombo, triangulo, flecha, etc.\n- Usa solo texto plano ASCII basico: letras, numeros, puntuacion normal (. , ; : ! ? ' \").",
            });
            rules.Add(new ActiveRule
            {
                Id = "text-brands",
                Category = "Marcas",
                Title = "Sin menciones de marcas",
                Description = "Nunca nombra marcas, empresas ni productos. Usa descripciones genericas.",
                Body = "- NUNCA menciones marcas, empresas, productos, servicios o nombres comerciales de ningun tipo. Esto incluye marcas de tecnologia, redes sociales, ropa, alimentacion, automocion, software, hardware, o cualquier otro sector.\n- Si necesitas referirte a un concepto asociado a una marca, usa una descripcion generica. Por ejemplo: en vez de \"Instagram\" di \"redes sociales\", en vez de \"iPhone\" di \"telefono movil\", en vez de \"Photoshop\" di \"editor de imagenes\".\n- No uses nombres de marcas ni siquiera como referencia, comparacion, ejemplo o metafora.",
            });
        }

        // Multi-image disaggregation rule (Image module with n>1).
        if (string.Equals(moduleType, "Image", StringComparison.OrdinalIgnoreCase) && sceneCount > 1)
        {
            rules.Add(new ActiveRule
            {
                Id = "multi-image",
                Category = "Imagen",
                Title = $"Desagregacion multi-imagen (n={sceneCount})",
                Description = "Cada imagen generada representa una parte del prompt, no repite todas las partes.",
                Body = $"IMPORTANTE: Vas a generar {sceneCount} imagenes a partir de este prompt. El prompt describe {sceneCount} partes, slides o secciones distintas. Genera UNA imagen por cada parte, en orden: la imagen 1 representa la primera parte del prompt, la imagen 2 la segunda, etc. NO dibujes todas las partes en cada imagen. Cada imagen debe ser visualmente independiente y autocontenida, mostrando solo su parte correspondiente.",
            });
        }

        return rules;
    }
}
