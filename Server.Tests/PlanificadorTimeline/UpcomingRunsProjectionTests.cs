using Server.Services.Scheduler;
using Xunit;

namespace Server.Tests.PlanificadorTimeline;

/// <summary>
/// Tests de la proyeccion de ejecuciones futuras que alimenta el timeline del
/// planificador (GET /api/projects/{id}/schedule/upcoming).
///
/// El endpoint encadena <see cref="SchedulerBackgroundService.ComputeNextRun"/>
/// pasandole su propio resultado como origen. Eso solo funciona si el calculo es
/// exclusivo: si devolviera la misma fecha que recibe, el bucle repetiria el primer
/// disparo N veces (y el timeline mostraria N ejecuciones identicas).
/// </summary>
public class UpcomingRunsProjectionTests
{
    /// <summary>Misma proyeccion que hace el endpoint.</summary>
    private static List<DateTime> Project(string cron, string tz, DateTime fromUtc, int count)
    {
        var runs = new List<DateTime>();
        var cursor = fromUtc;
        for (var i = 0; i < count; i++)
        {
            var next = SchedulerBackgroundService.ComputeNextRun(cron, tz, cursor);
            if (next is null) break;
            cursor = next.Value;
            runs.Add(next.Value);
        }
        return runs;
    }

    [Fact]
    public void Encadenar_avanza_y_no_repite_la_misma_fecha()
    {
        var from = new DateTime(2026, 3, 10, 8, 0, 0, DateTimeKind.Utc);

        var runs = Project("0 9 * * *", "UTC", from, 5);

        Assert.Equal(5, runs.Count);
        Assert.Equal(runs.Count, runs.Distinct().Count());
        Assert.True(runs.Zip(runs.Skip(1)).All(p => p.Second > p.First),
            "cada ejecucion proyectada debe ser posterior a la anterior");
    }

    [Fact]
    public void Un_cron_diario_proyecta_un_disparo_por_dia()
    {
        var from = new DateTime(2026, 3, 10, 8, 0, 0, DateTimeKind.Utc);

        var runs = Project("0 9 * * *", "UTC", from, 3);

        Assert.Equal(new DateTime(2026, 3, 10, 9, 0, 0, DateTimeKind.Utc), runs[0]);
        Assert.Equal(new DateTime(2026, 3, 11, 9, 0, 0, DateTimeKind.Utc), runs[1]);
        Assert.Equal(new DateTime(2026, 3, 12, 9, 0, 0, DateTimeKind.Utc), runs[2]);
    }

    [Fact]
    public void Un_cron_invalido_no_proyecta_nada_en_vez_de_reventar()
    {
        var runs = Project("esto no es un cron", "UTC", DateTime.UtcNow, 5);

        Assert.Empty(runs);
    }

    [Fact]
    public void Una_zona_horaria_invalida_no_proyecta_nada()
    {
        var runs = Project("0 9 * * *", "Marte/Olympus", DateTime.UtcNow, 5);

        Assert.Empty(runs);
    }
}
