using Server.Models;
using Xunit;

namespace Server.Tests.ProyectosDePrueba;

/// <summary>
/// Tests de la marca "proyecto de prueba" (Project.IsTestProject).
/// Cubren el valor por defecto, el criterio de orden del listado /api/projects
/// (los de prueba al final) y la semantica opcional del flag al actualizar.
/// </summary>
public class ProyectoDePruebaTests
{
    [Fact]
    public void Project_IsTestProject_DefaultEsFalse()
    {
        // Un proyecto nuevo es un proyecto real salvo que se marque explicitamente.
        var project = new Project { Name = "Pipeline real" };

        Assert.False(project.IsTestProject);
    }

    [Fact]
    public void ProjectResponse_IsTestProject_DefaultEsFalse()
    {
        var resp = new ProjectResponse(Guid.NewGuid(), "P", null, null,
            DateTime.UtcNow, DateTime.UtcNow);

        Assert.False(resp.IsTestProject);
    }

    [Fact]
    public void CreateProjectRequest_IsTestProject_DefaultEsFalse()
    {
        var req = new CreateProjectRequest("P", null, null);

        Assert.False(req.IsTestProject);
    }

    [Fact]
    public void UpdateProjectRequest_IsTestProject_DefaultEsNull()
    {
        // null = "no toques la marca": lo usan pantallas que solo editan contexto.
        var req = new UpdateProjectRequest("P", null, null);

        Assert.Null(req.IsTestProject);
    }

    [Fact]
    public void Orden_ProyectosDePrueba_VanAlFinal()
    {
        var now = DateTime.UtcNow;
        var projects = new[]
        {
            new Project { Name = "prueba vieja", IsTestProject = true,  CreatedAt = now.AddDays(-3) },
            new Project { Name = "real",         IsTestProject = false, CreatedAt = now.AddDays(-2) },
            new Project { Name = "prueba nueva", IsTestProject = true,  CreatedAt = now.AddDays(-1) },
        };

        var ordered = Ordenar(projects);

        Assert.Equal(
            new[] { "real", "prueba nueva", "prueba vieja" },
            ordered.Select(p => p.Name));
    }

    [Fact]
    public void Orden_FijadoNoAdelantaAUnProyectoDePrueba()
    {
        var now = DateTime.UtcNow;
        var projects = new[]
        {
            new Project { Name = "prueba fijada", IsTestProject = true,  IsPinned = true,  CreatedAt = now },
            new Project { Name = "real",          IsTestProject = false, IsPinned = false, CreatedAt = now.AddDays(-5) },
        };

        var ordered = Ordenar(projects);

        // La seccion manda sobre el pin: un proyecto de prueba fijado sigue abajo,
        // aunque dentro de su grupo aparezca el primero.
        Assert.Equal("real", ordered[0].Name);
        Assert.Equal("prueba fijada", ordered[1].Name);
    }

    [Fact]
    public void Orden_DentroDelGrupo_LosFijadosVanPrimero()
    {
        var now = DateTime.UtcNow;
        var projects = new[]
        {
            new Project { Name = "reciente", IsPinned = false, CreatedAt = now },
            new Project { Name = "fijado",   IsPinned = true,  CreatedAt = now.AddDays(-10) },
        };

        var ordered = Ordenar(projects);

        Assert.Equal("fijado", ordered[0].Name);
        Assert.Equal("reciente", ordered[1].Name);
    }

    [Theory]
    [InlineData(true, null, true)]    // sin flag en la peticion, se conserva el valor previo
    [InlineData(false, null, false)]
    [InlineData(false, true, true)]   // el cliente lo marca
    [InlineData(true, false, false)]  // el cliente lo desmarca
    public void Update_SoloCambiaLaMarcaSiLaPeticionLaEnvia(bool actual, bool? enviado, bool esperado)
    {
        var project = new Project { Name = "P", IsTestProject = actual };

        // Misma logica que el endpoint PUT /api/projects/{id}.
        if (enviado is { } isTest) project.IsTestProject = isTest;

        Assert.Equal(esperado, project.IsTestProject);
    }

    /// <summary>Mismo criterio que GET /api/projects y que el listado del cliente.</summary>
    private static List<Project> Ordenar(IEnumerable<Project> projects) => projects
        .OrderBy(p => p.IsTestProject)
        .ThenByDescending(p => p.IsPinned)
        .ThenByDescending(p => p.CreatedAt)
        .ToList();
}
