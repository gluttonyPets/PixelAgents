using Microsoft.EntityFrameworkCore;
using Server.Data;
using Server.Models;
using Server.Services.Scheduler;
using Xunit;

namespace Server.Tests.PapeleraPipelines;

/// <summary>
/// Tests de la papelera de pipelines (<see cref="Project.DeletedAt"/>): borrar es un
/// borrado logico reversible, lo borrado no aparece en los listados ni se ejecuta, y
/// permanece en la papelera indefinidamente hasta que el usuario lo elimina.
/// </summary>
public class PapeleraTests
{
    [Fact]
    public void Project_Nuevo_NoEstaEnLaPapelera()
    {
        var project = new Project { Name = "Pipeline" };

        Assert.Null(project.DeletedAt);
    }

    [Fact]
    public async Task Borrar_EsLogico_ElPipelineSigueEnBaseDeDatos()
    {
        await using var db = NewDb();
        var project = Pipeline("a borrar");
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        // Misma logica que DELETE /api/projects/{id}.
        project.DeletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        Assert.Equal(1, await db.Projects.CountAsync());
        Assert.Empty(await Activos(db));
        Assert.Single(await Papelera(db));
    }

    [Fact]
    public async Task Listado_NoIncluyeLoQueEstaEnLaPapelera()
    {
        await using var db = NewDb();
        db.Projects.Add(Pipeline("activo"));
        db.Projects.Add(Pipeline("borrado", borrado: DateTime.UtcNow));
        await db.SaveChangesAsync();

        var listado = await Activos(db);

        Assert.Equal(new[] { "activo" }, listado.Select(p => p.Name));
    }

    [Fact]
    public async Task Restaurar_DevuelveElPipelineAlListadoConSuProyecto()
    {
        await using var db = NewDb();
        var group = new ProjectGroup { Id = Guid.NewGuid(), Name = "Marca" };
        db.ProjectGroups.Add(group);
        var project = Pipeline("borrado", borrado: DateTime.UtcNow);
        project.ProjectGroupId = group.Id;
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        // Misma logica que POST /api/projects/{id}/restore.
        project.DeletedAt = null;
        await db.SaveChangesAsync();

        var listado = await Activos(db);
        Assert.Single(listado);
        Assert.Equal(group.Id, listado[0].ProjectGroupId);
        Assert.Empty(await Papelera(db));
    }

    [Fact]
    public async Task Papelera_NoCaduca_LoBorradoHaceMesesSigueAhi()
    {
        await using var db = NewDb();
        db.Projects.Add(Pipeline("antiguo", borrado: DateTime.UtcNow.AddYears(-2)));
        await db.SaveChangesAsync();

        // No hay purga por antiguedad: solo el borrado expreso saca algo de la papelera.
        Assert.Single(await Papelera(db));
    }

    [Fact]
    public async Task Papelera_OrdenadaPorFechaDeBorradoDescendente()
    {
        await using var db = NewDb();
        var now = DateTime.UtcNow;
        db.Projects.Add(Pipeline("viejo", borrado: now.AddDays(-10)));
        db.Projects.Add(Pipeline("reciente", borrado: now.AddMinutes(-5)));
        db.Projects.Add(Pipeline("intermedio", borrado: now.AddDays(-1)));
        await db.SaveChangesAsync();

        var papelera = await Papelera(db);

        Assert.Equal(new[] { "reciente", "intermedio", "viejo" }, papelera.Select(p => p.Name));
    }

    [Fact]
    public async Task EliminarDefinitivamente_QuitaElPipelineDeLaBaseDeDatos()
    {
        await using var db = NewDb();
        var project = Pipeline("condenado", borrado: DateTime.UtcNow);
        db.Projects.Add(project);
        db.Projects.Add(Pipeline("otro", borrado: DateTime.UtcNow));
        await db.SaveChangesAsync();

        // Misma logica que DELETE /api/projects/{id}/permanent.
        db.Projects.Remove(project);
        await db.SaveChangesAsync();

        Assert.Equal(new[] { "otro" }, (await Papelera(db)).Select(p => p.Name));
    }

    [Fact]
    public void EliminarDefinitivamente_SoloDesdeLaPapelera()
    {
        // Un pipeline activo no se puede eliminar en un paso: primero va a la papelera.
        var activo = new Project { Name = "activo" };
        var borrado = new Project { Name = "borrado", DeletedAt = DateTime.UtcNow };

        Assert.False(SePuedeEliminarDefinitivamente(activo));
        Assert.True(SePuedeEliminarDefinitivamente(borrado));
    }

    [Fact]
    public async Task ContadorDelProyecto_NoCuentaPipelinesDeLaPapelera()
    {
        await using var db = NewDb();
        var group = new ProjectGroup { Id = Guid.NewGuid(), Name = "Marca" };
        db.ProjectGroups.Add(group);
        db.Projects.Add(Agrupado("activo", group.Id));
        var borrado = Agrupado("borrado", group.Id);
        borrado.DeletedAt = DateTime.UtcNow;
        db.Projects.Add(borrado);
        await db.SaveChangesAsync();

        // Misma proyeccion que GET /api/project-groups.
        var count = await db.Projects.CountAsync(p => p.ProjectGroupId == group.Id && p.DeletedAt == null);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task BorrarElProyecto_TambienDesagrupaLoQueEstaEnLaPapelera()
    {
        // Si no, al restaurar el pipeline apuntaria a un proyecto que ya no existe.
        await using var db = NewDb();
        var group = new ProjectGroup { Id = Guid.NewGuid(), Name = "Marca" };
        db.ProjectGroups.Add(group);
        var borrado = Agrupado("borrado", group.Id);
        borrado.DeletedAt = DateTime.UtcNow;
        db.Projects.Add(borrado);
        await db.SaveChangesAsync();

        // Misma logica que DELETE /api/project-groups/{id}: no filtra por DeletedAt.
        var agrupados = await db.Projects.Where(p => p.ProjectGroupId == group.Id).ToListAsync();
        foreach (var p in agrupados) p.ProjectGroupId = null;
        db.ProjectGroups.Remove(group);
        await db.SaveChangesAsync();

        var enPapelera = Assert.Single(await Papelera(db));
        Assert.Null(enPapelera.ProjectGroupId);
        Assert.NotNull(enPapelera.DeletedAt);
    }

    [Fact]
    public async Task Programacion_DeUnPipelineEnLaPapelera_NoSeConsideraVencida()
    {
        await using var db = NewDb();
        var now = DateTime.UtcNow;

        var activo = Pipeline("activo");
        var borrado = Pipeline("borrado", borrado: now);
        db.Projects.AddRange(activo, borrado);
        db.ProjectSchedules.Add(Programacion(activo.Id, now.AddMinutes(-1)));
        db.ProjectSchedules.Add(Programacion(borrado.Id, now.AddMinutes(-1)));
        await db.SaveChangesAsync();

        // Misma consulta que SchedulerBackgroundService.ProcessTenantAsync.
        var due = await db.ProjectSchedules
            .Where(s => s.IsEnabled && s.NextRunAt != null && s.NextRunAt <= now
                && s.Project.DeletedAt == null)
            .ToListAsync();

        Assert.Equal(new[] { activo.Id }, due.Select(s => s.ProjectId));
    }

    [Fact]
    public void Restaurar_RecalculaLaProximaEjecucionParaQueNoDispareAlInstante()
    {
        var now = DateTime.UtcNow;
        var schedule = new ProjectSchedule
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            IsEnabled = true,
            CronExpression = "0 9 * * *",
            TimeZone = "UTC",
            NextRunAt = now.AddDays(-30),   // vencida mientras estaba en la papelera
        };

        // Misma logica que POST /api/projects/{id}/restore.
        schedule.NextRunAt = SchedulerBackgroundService.ComputeNextRun(
            schedule.CronExpression, schedule.TimeZone, now);

        Assert.NotNull(schedule.NextRunAt);
        Assert.True(schedule.NextRunAt > now);
    }

    [Fact]
    public async Task Papelera_DevuelveProyectoDeOrigenYContadores()
    {
        await using var db = NewDb();
        var group = new ProjectGroup { Id = Guid.NewGuid(), Name = "Marca" };
        db.ProjectGroups.Add(group);

        var project = Agrupado("borrado", group.Id);
        project.DeletedAt = DateTime.UtcNow;
        db.Projects.Add(project);

        var aiModule = new AiModule
        {
            Id = Guid.NewGuid(),
            Name = "Texto",
            ProviderType = "OpenAI",
            ModuleType = "Text",
            ModelName = "gpt",
        };
        db.AiModules.Add(aiModule);
        db.ProjectModules.Add(new ProjectModule
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            AiModuleId = aiModule.Id,
        });
        db.ProjectExecutions.Add(new ProjectExecution
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Status = "Completed",
            WorkspacePath = "/tmp",
        });
        await db.SaveChangesAsync();

        // Misma proyeccion que GET /api/projects/trash.
        var papelera = await db.Projects
            .Where(p => p.DeletedAt != null)
            .OrderByDescending(p => p.DeletedAt)
            .Select(p => new TrashedProjectResponse(
                p.Id, p.Name, p.Description, p.IsTestProject,
                p.ProjectGroupId,
                p.ProjectGroup != null ? p.ProjectGroup.Name : null,
                p.CreatedAt, p.DeletedAt!.Value,
                db.ProjectModules.Count(pm => pm.ProjectId == p.Id),
                db.ProjectExecutions.Count(e => e.ProjectId == p.Id)))
            .ToListAsync();

        var fila = Assert.Single(papelera);
        Assert.Equal("borrado", fila.Name);
        // El nombre del proyecto de origen se muestra para saber de donde salio.
        Assert.Equal("Marca", fila.ProjectGroupName);
        Assert.Equal(1, fila.ModuleCount);
        Assert.Equal(1, fila.ExecutionCount);
    }

    [Fact]
    public void TrashedProjectResponse_LlevaLoNecesarioParaDecidir()
    {
        var deleted = DateTime.UtcNow;
        var resp = new TrashedProjectResponse(
            Guid.NewGuid(), "P", "desc", false, null, "Marca",
            deleted.AddDays(-5), deleted, ModuleCount: 4, ExecutionCount: 12);

        Assert.Equal("Marca", resp.ProjectGroupName);
        Assert.Equal(deleted, resp.DeletedAt);
        Assert.Equal(4, resp.ModuleCount);
        Assert.Equal(12, resp.ExecutionCount);
    }

    // ── Helpers ──

    private static UserDbContext NewDb() =>
        new(new DbContextOptionsBuilder<UserDbContext>()
            .UseInMemoryDatabase($"papelera-{Guid.NewGuid()}")
            .Options);

    /// <summary>Misma condicion que GET /api/projects.</summary>
    private static Task<List<Project>> Activos(UserDbContext db) =>
        db.Projects.Where(p => p.DeletedAt == null).ToListAsync();

    /// <summary>Misma consulta que GET /api/projects/trash.</summary>
    private static Task<List<Project>> Papelera(UserDbContext db) =>
        db.Projects.Where(p => p.DeletedAt != null)
            .OrderByDescending(p => p.DeletedAt)
            .ToListAsync();

    /// <summary>Misma guarda que DELETE /api/projects/{id}/permanent.</summary>
    private static bool SePuedeEliminarDefinitivamente(Project p) => p.DeletedAt is not null;

    private static Project Pipeline(string name, DateTime? borrado = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        DeletedAt = borrado,
        CreatedAt = DateTime.UtcNow,
    };

    private static Project Agrupado(string name, Guid grupo)
    {
        var p = Pipeline(name);
        p.ProjectGroupId = grupo;
        return p;
    }

    private static ProjectSchedule Programacion(Guid projectId, DateTime nextRunAt) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        IsEnabled = true,
        CronExpression = "0 9 * * *",
        TimeZone = "UTC",
        NextRunAt = nextRunAt,
    };
}
