using Server.Services.Scheduler;
using Xunit;

namespace Server.Tests.NoRepetirTemas;

/// <summary>
/// Tests para el metodo estatico ComputeNextRun del SchedulerBackgroundService.
/// Verifica que el scheduler calcule correctamente la proxima ejecucion
/// con distintas expresiones cron y zonas horarias.
/// </summary>
public class SchedulerComputeNextRunTests
{
    [Fact]
    public void ComputeNextRun_CronDiarioMediodia_DevuelveProximoMediodia()
    {
        // Arrange: 2026-05-03 08:00 UTC, cron "0 12 * * *" = mediodia UTC
        var utcNow = new DateTime(2026, 5, 3, 8, 0, 0, DateTimeKind.Utc);
        var cron = "0 12 * * *";
        var tz = "UTC";

        // Act
        var result = SchedulerBackgroundService.ComputeNextRun(cron, tz, utcNow);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(new DateTime(2026, 5, 3, 12, 0, 0, DateTimeKind.Utc), result!.Value);
    }

    [Fact]
    public void ComputeNextRun_CronDiarioCon_TZMadrid_DevuelveCorrectoUTC()
    {
        // Arrange: zona Europe/Madrid en mayo es UTC+2
        // cron "0 9 * * *" = 09:00 Madrid = 07:00 UTC
        // utcNow = 2026-05-03 06:00 UTC => proximo 09:00 Madrid es hoy a las 07:00 UTC
        var utcNow = new DateTime(2026, 5, 3, 6, 0, 0, DateTimeKind.Utc);
        var cron = "0 9 * * *";
        var tz = "Europe/Madrid";

        // Act
        var result = SchedulerBackgroundService.ComputeNextRun(cron, tz, utcNow);

        // Assert
        Assert.NotNull(result);
        // En UTC+2, 09:00 Madrid = 07:00 UTC
        Assert.Equal(new DateTime(2026, 5, 3, 7, 0, 0, DateTimeKind.Utc), result!.Value);
    }

    [Fact]
    public void ComputeNextRun_CronYaPasado_DevuelveSiguienteDia()
    {
        // Arrange: utcNow ya paso las 12:00 UTC de hoy
        var utcNow = new DateTime(2026, 5, 3, 14, 0, 0, DateTimeKind.Utc);
        var cron = "0 12 * * *";
        var tz = "UTC";

        // Act
        var result = SchedulerBackgroundService.ComputeNextRun(cron, tz, utcNow);

        // Assert
        Assert.NotNull(result);
        // Debe ser manana a las 12:00
        Assert.Equal(new DateTime(2026, 5, 4, 12, 0, 0, DateTimeKind.Utc), result!.Value);
    }

    [Fact]
    public void ComputeNextRun_CronInvalido_DevuelveNull()
    {
        // Arrange
        var utcNow = DateTime.UtcNow;
        var cronInvalido = "no-es-un-cron";

        // Act
        var result = SchedulerBackgroundService.ComputeNextRun(cronInvalido, "UTC", utcNow);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ComputeNextRun_TimeZoneInvalida_DevuelveNull()
    {
        // Arrange
        var utcNow = DateTime.UtcNow;

        // Act
        var result = SchedulerBackgroundService.ComputeNextRun("0 12 * * *", "Zona/Inexistente", utcNow);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ComputeNextRun_CronCadaHora_DevuelveProximaHora()
    {
        // Arrange: utcNow = 2026-05-03 10:30 UTC
        // cron "0 * * * *" = al inicio de cada hora
        var utcNow = new DateTime(2026, 5, 3, 10, 30, 0, DateTimeKind.Utc);
        var cron = "0 * * * *";
        var tz = "UTC";

        // Act
        var result = SchedulerBackgroundService.ComputeNextRun(cron, tz, utcNow);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(new DateTime(2026, 5, 3, 11, 0, 0, DateTimeKind.Utc), result!.Value);
    }
}
