using Microsoft.AspNetCore.Mvc;
using PixelAgents.AgentSystem.Discovery;

namespace PixelAgents.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ModulesController : ControllerBase
{
    private readonly AgentModuleRegistry _registry;

    public ModulesController(AgentModuleRegistry registry)
    {
        _registry = registry;
    }

    [HttpGet]
    public IActionResult GetRegisteredModules()
    {
        var modules = _registry.GetAllModules().Select(m => new
        {
            m.ModuleKey,
            m.DisplayName,
            m.Description,
            m.Role,
            m.Personality,
            Skills = m.Skills.Select(s => new { s.Type, s.Name, s.Level, s.Description }),
            Appearance = new
            {
                m.DefaultAppearance.SpriteSheet,
                m.DefaultAppearance.OfficePositionX,
                m.DefaultAppearance.OfficePositionY,
                m.DefaultAppearance.DeskStyle
            }
        });

        return Ok(modules);
    }
}
