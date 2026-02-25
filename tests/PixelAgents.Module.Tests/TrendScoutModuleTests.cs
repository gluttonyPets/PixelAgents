using Microsoft.Extensions.Logging.Abstractions;
using PixelAgents.AgentSystem.Abstractions;
using PixelAgents.Module.TrendScout;

namespace PixelAgents.Module.Tests;

public class TrendScoutModuleTests
{
    private readonly TrendScoutModule _module;

    public TrendScoutModuleTests()
    {
        _module = new TrendScoutModule(NullLogger<TrendScoutModule>.Instance);
    }

    [Fact]
    public void ModuleKey_ShouldBeTrendScout()
    {
        Assert.Equal("trend-scout", _module.ModuleKey);
    }

    [Fact]
    public void CanHandle_TrendResearch_ShouldReturnTrue()
    {
        Assert.True(_module.CanHandle("trend-research"));
    }

    [Fact]
    public void CanHandle_Unknown_ShouldReturnFalse()
    {
        Assert.False(_module.CanHandle("unknown-task"));
    }

    [Fact]
    public async Task ExecuteAsync_WithValidTopic_ShouldReturnSuccess()
    {
        var context = new AgentModuleContext
        {
            TaskId = Guid.NewGuid(),
            PipelineId = Guid.NewGuid(),
            AgentId = Guid.NewGuid(),
            InputParameters = new Dictionary<string, object>
            {
                ["topic"] = "AI Technology"
            }
        };

        var result = await _module.ExecuteAsync(context);

        Assert.True(result.Success);
        Assert.Contains("topic", result.OutputData.Keys);
        Assert.Contains("trending_subtopics", result.OutputData.Keys);
    }

    [Fact]
    public void Skills_ShouldContainExpectedSkills()
    {
        Assert.Equal(3, _module.Skills.Count);
        Assert.Contains(_module.Skills, s => s.Name == "Trend Analysis");
    }
}
