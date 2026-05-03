using System.Reflection;
using Xunit;

namespace Server.Tests.NoRepetirTemas;

/// <summary>
/// Tests de analisis estructural del codigo.
/// Verifican que las clases, interfaces y metodos criticos del feature
/// "no-repetir temas" existan y tengan la firma correcta.
/// </summary>
public class CodeAnalysisTests
{
    [Fact]
    public void IPipelineExecutor_ExecuteAsync_TieneParametroUseHistory()
    {
        // Arrange
        var interfaceType = typeof(Server.Services.Ai.IPipelineExecutor);

        // Act
        var method = interfaceType.GetMethod("ExecuteAsync");

        // Assert
        Assert.NotNull(method);
        var parameters = method!.GetParameters();
        var useHistoryParam = parameters.FirstOrDefault(p => p.Name == "useHistory");
        Assert.NotNull(useHistoryParam);
        Assert.Equal(typeof(bool), useHistoryParam!.ParameterType);

        // Verificar que el valor por defecto es true
        Assert.True(useHistoryParam.HasDefaultValue);
        Assert.Equal(true, useHistoryParam.DefaultValue);
    }

    [Fact]
    public void ProjectSchedule_TienePropiedad_UseHistory()
    {
        // Arrange
        var type = typeof(Server.Models.ProjectSchedule);

        // Act
        var prop = type.GetProperty("UseHistory");

        // Assert
        Assert.NotNull(prop);
        Assert.Equal(typeof(bool), prop!.PropertyType);
    }

    [Fact]
    public void ProjectExecution_TienePropiedad_ExecutionSummary()
    {
        // Arrange: ExecutionSummary es el campo que alimenta el contexto historico
        var type = typeof(Server.Models.ProjectExecution);

        // Act
        var prop = type.GetProperty("ExecutionSummary");

        // Assert
        Assert.NotNull(prop);
        // Debe ser nullable string
        Assert.Equal(typeof(string), prop!.PropertyType);
    }

    [Fact]
    public void CreateScheduleRequest_TienePropiedad_UseHistory()
    {
        // Arrange
        var type = typeof(Server.Models.CreateScheduleRequest);

        // Act
        var prop = type.GetProperty("UseHistory");

        // Assert
        Assert.NotNull(prop);
        Assert.Equal(typeof(bool), prop!.PropertyType);
    }

    [Fact]
    public void UpdateScheduleRequest_TienePropiedad_UseHistory()
    {
        // Arrange
        var type = typeof(Server.Models.UpdateScheduleRequest);

        // Act
        var prop = type.GetProperty("UseHistory");

        // Assert
        Assert.NotNull(prop);
        Assert.Equal(typeof(bool), prop!.PropertyType);
    }

    [Fact]
    public void ExecuteProjectRequest_TienePropiedad_UseHistory()
    {
        // Arrange: la ejecucion manual tambien soporta UseHistory
        var type = typeof(Server.Models.ExecuteProjectRequest);

        // Act
        var prop = type.GetProperty("UseHistory");

        // Assert
        Assert.NotNull(prop);
        Assert.Equal(typeof(bool), prop!.PropertyType);
    }

    [Fact]
    public void ScheduleResponse_TienePropiedad_UseHistory()
    {
        // Arrange: la respuesta del GET /schedule debe incluir UseHistory
        var type = typeof(Server.Models.ScheduleResponse);

        // Act
        var prop = type.GetProperty("UseHistory");

        // Assert
        Assert.NotNull(prop);
        Assert.Equal(typeof(bool), prop!.PropertyType);
    }

    [Fact]
    public void SchedulerBackgroundService_ComputeNextRun_EsPublicoYEstatico()
    {
        // Arrange: ComputeNextRun debe ser publico y estatico para poder testearse
        var type = typeof(Server.Services.Scheduler.SchedulerBackgroundService);

        // Act
        var method = type.GetMethod("ComputeNextRun",
            BindingFlags.Public | BindingFlags.Static);

        // Assert
        Assert.NotNull(method);
        Assert.True(method!.IsStatic);
        Assert.True(method.IsPublic);
    }
}
