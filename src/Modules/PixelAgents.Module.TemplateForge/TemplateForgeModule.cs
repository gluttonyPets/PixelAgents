using Microsoft.Extensions.Logging;
using PixelAgents.AgentSystem.Abstractions;
using PixelAgents.Domain.Enums;
using PixelAgents.Domain.ValueObjects;

namespace PixelAgents.Module.TemplateForge;

public class TemplateForgeModule : AgentModuleBase
{
    public TemplateForgeModule(ILogger<TemplateForgeModule> logger) : base(logger) { }

    public override string ModuleKey => "template-forge";
    public override string DisplayName => "Forja";
    public override string Description => "Disenador de plantillas. Crea y gestiona plantillas de Photoshop optimizadas para cada red social.";
    public override string Role => "Template Designer";

    public override AvatarAppearance DefaultAppearance => new(
        SpriteSheet: "sprites/template-forge.png",
        IdleAnimation: "idle",
        WorkingAnimation: "drawing",
        ThinkingAnimation: "sketching",
        OfficePositionX: 5,
        OfficePositionY: 1,
        DeskStyle: "design-desk"
    );

    public override List<AgentSkill> Skills =>
    [
        new(SkillType.TemplateDesign, "Template Design", 5, "Creacion de plantillas visuales profesionales"),
        new(SkillType.ImageGeneration, "Image Composition", 4, "Composicion y manipulacion de imagenes"),
        new(SkillType.Design, "Brand Consistency", 3, "Mantiene coherencia visual entre publicaciones")
    ];

    public override string Personality => "Creativo, perfeccionista y con un ojo increible para el diseno. Siempre busca la combinacion perfecta de colores y tipografia.";

    public override bool CanHandle(string taskType) =>
        taskType is "template-creation" or "template-design" or "visual-design";

    public override async Task<AgentModuleResult> ExecuteAsync(AgentModuleContext context, CancellationToken ct = default)
    {
        var platforms = GetInputOrDefault<List<string>>(context, "platforms") ?? ["Instagram"];
        var topic = GetInputOrDefault<string>(context, "topic") ?? "General";
        var suggestedAngles = GetInputOrDefault<List<string>>(context, "suggested_angles");

        ReportProgress(context, 10, "Analizando requerimientos de diseno...");
        await Task.Delay(500, ct);

        ReportProgress(context, 30, "Seleccionando paleta de colores y tipografia...");
        await Task.Delay(500, ct);

        ReportProgress(context, 60, "Generando plantillas para cada plataforma...");
        await Task.Delay(500, ct);

        ReportProgress(context, 80, "Optimizando dimensiones por plataforma...");
        await Task.Delay(300, ct);

        // TODO: Integrate with Photoshop API / image generation AI
        var templateData = new Dictionary<string, object>
        {
            ["templates"] = platforms.Select(platform => new Dictionary<string, object>
            {
                ["platform"] = platform,
                ["template_id"] = Guid.NewGuid().ToString(),
                ["dimensions"] = platform.ToLower() switch
                {
                    "instagram" => "1080x1080",
                    "twitter" => "1200x675",
                    "facebook" => "1200x630",
                    "tiktok" => "1080x1920",
                    "linkedin" => "1200x627",
                    _ => "1080x1080"
                },
                ["style"] = "modern-gradient",
                ["color_palette"] = new List<string> { "#FF6B6B", "#4ECDC4", "#45B7D1", "#FFFFFF" },
                ["font_primary"] = "Montserrat Bold",
                ["font_secondary"] = "Open Sans",
                ["template_url"] = $"/templates/{platform.ToLower()}-{topic.Replace(" ", "-")}.psd"
            }).ToList(),
            ["topic"] = topic,
            ["design_notes"] = $"Plantillas creadas con estilo moderno para '{topic}'. Incluye espacios para titulo, subtitulo y CTA."
        };

        ReportProgress(context, 100, "Plantillas generadas exitosamente");

        return AgentModuleResult.Ok(
            templateData,
            $"Creadas {platforms.Count} plantilla(s) optimizada(s) para: {string.Join(", ", platforms)}"
        );
    }
}
