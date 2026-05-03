using Server.Models;
using Xunit;

namespace Server.Tests.NoRepetirTemas;

/// <summary>
/// Tests de los DTOs de Schedule.
/// Verifica que UseHistory se transmita correctamente en los records
/// de Create y Update.
/// </summary>
public class ScheduleDtoTests
{
    [Fact]
    public void CreateScheduleRequest_UseHistory_DefaultEsTrue()
    {
        // Arrange & Act
        var req = new CreateScheduleRequest("0 9 * * *", "UTC", null);

        // Assert: UseHistory debe ser true por defecto
        Assert.True(req.UseHistory);
    }

    [Fact]
    public void CreateScheduleRequest_UseHistory_PuedeSerFalse()
    {
        // Arrange & Act
        var req = new CreateScheduleRequest("0 9 * * *", "UTC", "input text", UseHistory: false);

        // Assert
        Assert.False(req.UseHistory);
    }

    [Fact]
    public void UpdateScheduleRequest_UseHistory_DefaultEsTrue()
    {
        // Arrange & Act
        var req = new UpdateScheduleRequest("0 9 * * *", "UTC", null, true);

        // Assert
        Assert.True(req.UseHistory);
    }

    [Fact]
    public void UpdateScheduleRequest_UseHistory_PuedeSerFalse()
    {
        // Arrange & Act
        var req = new UpdateScheduleRequest("0 9 * * *", "UTC", null, true, UseHistory: false);

        // Assert
        Assert.False(req.UseHistory);
    }

    [Fact]
    public void ExecuteProjectRequest_UseHistory_DefaultEsTrue()
    {
        // Arrange & Act
        var req = new ExecuteProjectRequest(null);

        // Assert: la ejecucion manual tambien debe tener UseHistory=true por defecto
        Assert.True(req.UseHistory);
    }

    [Fact]
    public void ExecuteProjectRequest_UseHistory_PuedeSerFalse()
    {
        // Arrange & Act
        var req = new ExecuteProjectRequest("mi input", UseHistory: false);

        // Assert
        Assert.False(req.UseHistory);
    }
}
