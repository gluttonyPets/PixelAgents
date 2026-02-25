using Microsoft.Extensions.Logging;
using PixelAgents.AgentSystem.Abstractions;
using PixelAgents.Domain.Enums;
using PixelAgents.Domain.ValueObjects;

namespace PixelAgents.Module.TrendScout;

public class TrendScoutModule : AgentModuleBase
{
    public TrendScoutModule(ILogger<TrendScoutModule> logger) : base(logger) { }

    public override string ModuleKey => "trend-scout";
    public override string DisplayName => "Scout";
    public override string Description => "Investigador de tendencias. Analiza redes sociales y motores de busqueda para encontrar los temas mas relevantes del momento.";
    public override string Role => "Trend Researcher";

    public override AvatarAppearance DefaultAppearance => new(
        SpriteSheet: "sprites/trend-scout.png",
        IdleAnimation: "idle",
        WorkingAnimation: "typing",
        ThinkingAnimation: "thinking",
        OfficePositionX: 2,
        OfficePositionY: 1,
        DeskStyle: "research-desk"
    );

    public override List<AgentSkill> Skills =>
    [
        new(SkillType.TrendAnalysis, "Trend Analysis", 5, "Identifica tendencias emergentes en redes sociales"),
        new(SkillType.Research, "Deep Research", 4, "Investigacion profunda de temas y nichos"),
        new(SkillType.DataAnalysis, "Data Mining", 3, "Extraccion y analisis de datos de multiples fuentes")
    ];

    public override string Personality => "Curioso, analitico y siempre al tanto de las ultimas tendencias. Le encanta descubrir patrones ocultos en los datos.";

    public override bool CanHandle(string taskType) =>
        taskType is "trend-research" or "topic-discovery" or "audience-analysis";

    public override async Task<AgentModuleResult> ExecuteAsync(AgentModuleContext context, CancellationToken ct = default)
    {
        var topic = GetInput<string>(context, "topic");
        var platforms = GetInputOrDefault<List<string>>(context, "platforms") ?? ["Instagram", "Twitter"];

        ReportProgress(context, 10, $"Iniciando investigacion sobre: {topic}");
        await Task.Delay(500, ct); // Simulated AI processing

        ReportProgress(context, 30, "Analizando tendencias en redes sociales...");
        await Task.Delay(500, ct);

        ReportProgress(context, 60, "Recopilando datos de engagement...");
        await Task.Delay(500, ct);

        // TODO: Integrate with real AI service (Semantic Kernel / OpenAI)
        var trendData = new Dictionary<string, object>
        {
            ["topic"] = topic,
            ["trending_subtopics"] = new List<string>
            {
                $"{topic} - Tendencia 1",
                $"{topic} - Tendencia 2",
                $"{topic} - Tendencia 3"
            },
            ["suggested_angles"] = new List<string>
            {
                $"Angulo educativo sobre {topic}",
                $"Infografia comparativa de {topic}",
                $"Top 5 datos curiosos sobre {topic}"
            },
            ["target_audience"] = "18-35, interesados en tecnologia y tendencias",
            ["engagement_score"] = 8.5,
            ["best_hashtags"] = new List<string> { $"#{topic.Replace(" ", "")}", "#trending", "#viral" },
            ["platforms"] = platforms
        };

        ReportProgress(context, 100, "Investigacion completada");

        return AgentModuleResult.Ok(
            trendData,
            $"Encontrados 3 subtemas relevantes para '{topic}' con score de engagement de 8.5/10"
        );
    }
}
