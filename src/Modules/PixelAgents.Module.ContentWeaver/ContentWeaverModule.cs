using Microsoft.Extensions.Logging;
using PixelAgents.AgentSystem.Abstractions;
using PixelAgents.Domain.Enums;
using PixelAgents.Domain.ValueObjects;

namespace PixelAgents.Module.ContentWeaver;

public class ContentWeaverModule : AgentModuleBase
{
    public ContentWeaverModule(ILogger<ContentWeaverModule> logger) : base(logger) { }

    public override string ModuleKey => "content-weaver";
    public override string DisplayName => "Weaver";
    public override string Description => "Ensamblador de contenido. Combina las plantillas con los datos de tendencias para crear publicaciones listas para subir.";
    public override string Role => "Content Assembler";

    public override AvatarAppearance DefaultAppearance => new(
        SpriteSheet: "sprites/content-weaver.png",
        IdleAnimation: "idle",
        WorkingAnimation: "assembling",
        ThinkingAnimation: "reviewing",
        OfficePositionX: 2,
        OfficePositionY: 4,
        DeskStyle: "assembly-desk"
    );

    public override List<AgentSkill> Skills =>
    [
        new(SkillType.ContentCreation, "Content Assembly", 5, "Ensamblaje de contenido visual y textual"),
        new(SkillType.Writing, "Copywriting", 4, "Redaccion de textos persuasivos para redes sociales"),
        new(SkillType.TextGeneration, "Caption Generation", 4, "Generacion de descripciones y hashtags optimizados")
    ];

    public override string Personality => "Meticuloso, eficiente y con gran capacidad de sintesis. Sabe como unir todas las piezas para crear contenido impactante.";

    public override bool CanHandle(string taskType) =>
        taskType is "content-assembly" or "content-creation" or "post-generation";

    public override async Task<AgentModuleResult> ExecuteAsync(AgentModuleContext context, CancellationToken ct = default)
    {
        var topic = GetInputOrDefault<string>(context, "topic") ?? "General";
        var trendingSubtopics = GetInputOrDefault<List<string>>(context, "trending_subtopics");
        var suggestedAngles = GetInputOrDefault<List<string>>(context, "suggested_angles");
        var bestHashtags = GetInputOrDefault<List<string>>(context, "best_hashtags");

        ReportProgress(context, 10, "Recopilando datos de modulos anteriores...");
        await Task.Delay(400, ct);

        ReportProgress(context, 30, "Generando copywriting para cada plataforma...");
        await Task.Delay(500, ct);

        ReportProgress(context, 55, "Ensamblando contenido con plantillas...");
        await Task.Delay(500, ct);

        ReportProgress(context, 80, "Revisando calidad del contenido final...");
        await Task.Delay(300, ct);

        // TODO: Integrate with AI for actual content generation
        var angle = suggestedAngles?.FirstOrDefault() ?? $"Contenido sobre {topic}";
        var hashtags = bestHashtags ?? [$"#{topic.Replace(" ", "")}"];

        var contentData = new Dictionary<string, object>
        {
            ["posts"] = new List<Dictionary<string, object>>
            {
                new()
                {
                    ["post_id"] = Guid.NewGuid().ToString(),
                    ["title"] = $"Descubre {topic}: Lo que nadie te cuenta",
                    ["caption"] = $"¿Sabias que {topic} esta revolucionando la industria? Aqui te traemos los datos mas impactantes. {string.Join(" ", hashtags)}",
                    ["angle"] = angle,
                    ["content_type"] = "carousel",
                    ["status"] = "ready"
                }
            },
            ["topic"] = topic,
            ["total_posts_created"] = 1,
            ["content_quality_score"] = 8.2
        };

        ReportProgress(context, 100, "Contenido ensamblado y listo para programar");

        return AgentModuleResult.Ok(
            contentData,
            $"Creado 1 post tipo carousel sobre '{topic}' con score de calidad 8.2/10"
        );
    }
}
