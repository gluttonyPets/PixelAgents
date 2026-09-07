using Microsoft.EntityFrameworkCore;
using Server.Data;
using Server.Models;
using Xunit;

namespace Server.Tests.ProyectosAgrupados;

/// <summary>
/// Tests de los proyectos de alto nivel (<see cref="ProjectGroup"/>): la agrupacion
/// organizativa que muestra /projects y dentro de la cual viven los pipelines
/// (<see cref="Project"/>). Cubren los valores por defecto de los DTOs, el criterio
/// de listado por secciones y que la agrupacion nunca arrastra pipelines al borrarse.
/// </summary>
public class ProyectosAgrupadosTests
{
    // ── Valores por defecto: agrupar es opcional ──

    [Fact]
    public void Project_SinAgrupar_PorDefecto()
    {
        var project = new Project { Name = "Pipeline suelto" };

        Assert.Null(project.ProjectGroupId);
    }

    [Fact]
    public void ProjectResponse_ProjectGroupId_DefaultEsNull()
    {
        var resp = new ProjectResponse(Guid.NewGuid(), "P", null, null,
            DateTime.UtcNow, DateTime.UtcNow);

        Assert.Null(resp.ProjectGroupId);
    }

    [Fact]
    public void CreateProjectRequest_ProjectGroupId_DefaultEsNull()
    {
        // Crear un pipeline sin indicar proyecto lo deja sin agrupar.
        var req = new CreateProjectRequest("P", null, null);

        Assert.Null(req.ProjectGroupId);
        Assert.False(req.IsTestProject);
    }

    [Fact]
    public void ProjectGroupResponse_PipelineCount_DefaultEsCero()
    {
        var resp = new ProjectGroupResponse(Guid.NewGuid(), "Marca", null, 0,
            DateTime.UtcNow, DateTime.UtcNow);

        Assert.Equal(0, resp.PipelineCount);
    }

    [Fact]
    public void SetProjectGroupRequest_NullSacaDelProyecto()
    {
        // Mover a "sin proyecto" no es un caso especial: es el mismo endpoint con null.
        var req = new SetProjectGroupRequest(null);

        Assert.Null(req.ProjectGroupId);
    }

    // ── Listado: cada proyecto agrupa sus pipelines, el resto va al final ──

    [Fact]
    public void Listado_CadaPipelineVaEnSuProyecto()
    {
        var marca = Guid.NewGuid();
        var cliente = Guid.NewGuid();
        var projects = new[]
        {
            Pipeline("marca-1", grupo: marca),
            Pipeline("suelto"),
            Pipeline("cliente-1", grupo: cliente),
            Pipeline("marca-2", grupo: marca),
        };

        Assert.Equal(new[] { "marca-1", "marca-2" },
            DeProyecto(projects, marca).Select(p => p.Name).OrderBy(n => n));
        Assert.Equal(new[] { "cliente-1" },
            DeProyecto(projects, cliente).Select(p => p.Name));
        Assert.Equal(new[] { "suelto" },
            SinProyecto(projects).Select(p => p.Name));
    }

    [Fact]
    public void Listado_DentroDelProyecto_MismoOrdenQueElListadoGeneral()
    {
        var grupo = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var projects = new[]
        {
            Pipeline("prueba",   grupo: grupo, esPrueba: true,  creado: now),
            Pipeline("reciente", grupo: grupo, creado: now.AddDays(-1)),
            Pipeline("fijado",   grupo: grupo, fijado: true,    creado: now.AddDays(-9)),
        };

        var ordenados = DeProyecto(projects, grupo);

        // Igual que fuera de un proyecto: fijados primero, pruebas al final.
        Assert.Equal(new[] { "fijado", "reciente", "prueba" }, ordenados.Select(p => p.Name));
    }

    [Fact]
    public void Listado_PipelineDePruebaAgrupado_SeQuedaEnSuProyecto()
    {
        // La seccion "Pipelines de prueba" solo recoge los que no estan agrupados:
        // un pipeline de prueba dentro de un proyecto se ve junto a sus companeros.
        var grupo = Guid.NewGuid();
        var projects = new[]
        {
            Pipeline("prueba agrupada", grupo: grupo, esPrueba: true),
            Pipeline("prueba suelta", esPrueba: true),
        };

        Assert.Equal(new[] { "prueba agrupada" }, DeProyecto(projects, grupo).Select(p => p.Name));
        Assert.Equal(new[] { "prueba suelta" },
            SinProyecto(projects).Where(p => p.IsTestProject).Select(p => p.Name));
    }

    // ── Borrar el proyecto es organizativo, no destructivo ──

    [Fact]
    public async Task BorrarProyecto_DejaLosPipelinesSinAgrupar()
    {
        await using var db = NewDb();

        var group = new ProjectGroup
        {
            Id = Guid.NewGuid(),
            Name = "Marca",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.ProjectGroups.Add(group);
        db.Projects.Add(new Project { Id = Guid.NewGuid(), Name = "p1", ProjectGroupId = group.Id });
        db.Projects.Add(new Project { Id = Guid.NewGuid(), Name = "p2", ProjectGroupId = group.Id });
        db.Projects.Add(new Project { Id = Guid.NewGuid(), Name = "suelto" });
        await db.SaveChangesAsync();

        // Misma logica que DELETE /api/project-groups/{id}.
        var agrupados = await db.Projects.Where(p => p.ProjectGroupId == group.Id).ToListAsync();
        foreach (var p in agrupados) p.ProjectGroupId = null;
        db.ProjectGroups.Remove(group);
        await db.SaveChangesAsync();

        Assert.Empty(await db.ProjectGroups.ToListAsync());
        Assert.Equal(3, await db.Projects.CountAsync());
        Assert.All(await db.Projects.ToListAsync(), p => Assert.Null(p.ProjectGroupId));
    }

    [Fact]
    public async Task AnadirPipelines_LosSacaDelProyectoAnterior()
    {
        await using var db = NewDb();

        var origen = new ProjectGroup { Id = Guid.NewGuid(), Name = "Origen" };
        var destino = new ProjectGroup { Id = Guid.NewGuid(), Name = "Destino" };
        db.ProjectGroups.AddRange(origen, destino);

        var movido = new Project { Id = Guid.NewGuid(), Name = "movido", ProjectGroupId = origen.Id };
        var quieto = new Project { Id = Guid.NewGuid(), Name = "quieto", ProjectGroupId = origen.Id };
        db.Projects.AddRange(movido, quieto);
        await db.SaveChangesAsync();

        // Misma logica que POST /api/project-groups/{id}/projects.
        var seleccionados = await db.Projects.Where(p => p.Id == movido.Id).ToListAsync();
        foreach (var p in seleccionados) p.ProjectGroupId = destino.Id;
        await db.SaveChangesAsync();

        Assert.Equal(destino.Id, (await db.Projects.FindAsync(movido.Id))!.ProjectGroupId);
        Assert.Equal(origen.Id, (await db.Projects.FindAsync(quieto.Id))!.ProjectGroupId);
        Assert.Equal(1, await db.Projects.CountAsync(p => p.ProjectGroupId == origen.Id));
    }

    [Fact]
    public async Task Listado_CuentaLosPipelinesDeCadaProyecto()
    {
        await using var db = NewDb();

        var conPipelines = new ProjectGroup { Id = Guid.NewGuid(), Name = "Con pipelines", SortOrder = 0 };
        var vacio = new ProjectGroup { Id = Guid.NewGuid(), Name = "Vacio", SortOrder = 1 };
        db.ProjectGroups.AddRange(conPipelines, vacio);
        db.Projects.Add(new Project { Id = Guid.NewGuid(), Name = "p1", ProjectGroupId = conPipelines.Id });
        db.Projects.Add(new Project { Id = Guid.NewGuid(), Name = "p2", ProjectGroupId = conPipelines.Id });
        db.Projects.Add(new Project { Id = Guid.NewGuid(), Name = "suelto" });
        await db.SaveChangesAsync();

        // Misma proyeccion que GET /api/project-groups.
        var listado = await db.ProjectGroups
            .OrderBy(g => g.SortOrder).ThenBy(g => g.CreatedAt)
            .Select(g => new ProjectGroupResponse(
                g.Id, g.Name, g.Description, g.SortOrder, g.CreatedAt, g.UpdatedAt,
                db.Projects.Count(p => p.ProjectGroupId == g.Id)))
            .ToListAsync();

        Assert.Equal(new[] { "Con pipelines", "Vacio" }, listado.Select(g => g.Name));
        Assert.Equal(2, listado[0].PipelineCount);
        // Un proyecto vacio sigue en el listado: es donde el usuario anade sus pipelines.
        Assert.Equal(0, listado[1].PipelineCount);
    }

    // ── Helpers ──

    private static UserDbContext NewDb() =>
        new(new DbContextOptionsBuilder<UserDbContext>()
            .UseInMemoryDatabase($"grupos-{Guid.NewGuid()}")
            .Options);

    private static Project Pipeline(string name, Guid? grupo = null, bool esPrueba = false,
        bool fijado = false, DateTime? creado = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            ProjectGroupId = grupo,
            IsTestProject = esPrueba,
            IsPinned = fijado,
            CreatedAt = creado ?? DateTime.UtcNow,
        };

    /// <summary>Mismo criterio que el listado del cliente para un proyecto.</summary>
    private static List<Project> DeProyecto(IEnumerable<Project> projects, Guid grupo) =>
        Ordenar(projects.Where(p => p.ProjectGroupId == grupo));

    private static List<Project> SinProyecto(IEnumerable<Project> projects) =>
        Ordenar(projects.Where(p => p.ProjectGroupId is null));

    private static List<Project> Ordenar(IEnumerable<Project> projects) => projects
        .OrderBy(p => p.IsTestProject)
        .ThenByDescending(p => p.IsPinned)
        .ThenByDescending(p => p.CreatedAt)
        .ToList();
}
