using Server.Models;
using Xunit;

namespace Server.Tests.NoRepetirTemas;

/// <summary>
/// Tests del modelo ProjectSchedule.
/// Verifica que UseHistory tenga el valor por defecto correcto
/// y que el campo se asigne correctamente.
/// </summary>
public class ProjectScheduleModelTests
{
    [Fact]
    public void ProjectSchedule_UseHistory_DefaultEsTrue()
    {
        // Arrange & Act
        var schedule = new ProjectSchedule();

        // Assert: el valor por defecto debe ser true (activado)
        Assert.True(schedule.UseHistory);
    }

    [Fact]
    public void ProjectSchedule_UseHistory_PuedeSerFalse()
    {
        // Arrange & Act
        var schedule = new ProjectSchedule { UseHistory = false };

        // Assert
        Assert.False(schedule.UseHistory);
    }

    [Fact]
    public void ProjectSchedule_IsEnabled_DefaultEsTrue()
    {
        // Arrange & Act
        var schedule = new ProjectSchedule();

        // Assert: el scheduler debe estar habilitado por defecto
        Assert.True(schedule.IsEnabled);
    }

    [Fact]
    public void ProjectSchedule_TimeZone_DefaultEsUTC()
    {
        // Arrange & Act
        var schedule = new ProjectSchedule();

        // Assert
        Assert.Equal("UTC", schedule.TimeZone);
    }

    [Fact]
    public void ProjectSchedule_PropiedadesCompletas_AsignacionCorrecta()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        // Act
        var schedule = new ProjectSchedule
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            IsEnabled = true,
            CronExpression = "0 9 * * *",
            TimeZone = "Europe/Madrid",
            UserInput = "Genera contenido de redes sociales",
            UseHistory = true,
            LastRunAt = now.AddDays(-1),
            NextRunAt = now.AddHours(1),
            CreatedAt = now,
            UpdatedAt = now,
        };

        // Assert
        Assert.Equal(projectId, schedule.ProjectId);
        Assert.Equal("0 9 * * *", schedule.CronExpression);
        Assert.Equal("Europe/Madrid", schedule.TimeZone);
        Assert.Equal("Genera contenido de redes sociales", schedule.UserInput);
        Assert.True(schedule.UseHistory);
    }
}
