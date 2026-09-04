using Server.Models;
using Server.Services.Ai;
using Xunit;

namespace Server.Tests.ImagenMultiple;

/// <summary>
/// Reparto de las imagenes producidas entre los puertos de salida del modulo.
/// El puerto i entrega la imagen i; si esa imagen no existe no entrega nada.
/// Antes caia en "todas las imagenes", asi que un modulo que devolvia una sola
/// la mandaba por los dos puertos y parecia haber generado dos.
/// </summary>
public class PuertosDeImagenTests
{
    [Fact]
    public void CadaPuertoEntregaSuImagen()
    {
        var node = CreateNode(imageFiles: 2, ports: 2);

        PortDataResolver.ResolveOutputPorts(node);

        Assert.Equal("image_1.png", Single(node, "output_image_1").FileName);
        Assert.Equal("image_2.png", Single(node, "output_image_2").FileName);
    }

    [Fact]
    public void PuertoSinImagen_NoPropagaNada()
    {
        var node = CreateNode(imageFiles: 1, ports: 2);

        PortDataResolver.ResolveOutputPorts(node);

        Assert.Equal("image_1.png", Single(node, "output_image_1").FileName);
        Assert.Null(Port(node, "output_image_2").Data!.Files);
    }

    private static OutputFile Single(ModuleNode node, string portId) =>
        Assert.Single(Port(node, portId).Data!.Files!);

    private static OutputPort Port(ModuleNode node, string portId) =>
        node.OutputPorts.First(p => p.PortId == portId);

    private static ModuleNode CreateNode(int imageFiles, int ports)
    {
        var aiModule = new AiModule
        {
            Id = Guid.NewGuid(),
            Name = "Generar imagen",
            ModuleType = "Image",
            ProviderType = "OpenAI",
            ModelName = "gpt-image-2",
        };
        var node = new ModuleNode(new ProjectModule { Id = Guid.NewGuid(), IsActive = true, AiModule = aiModule });

        for (var i = 1; i <= ports; i++)
            node.OutputPorts.Add(new OutputPort { PortId = $"output_image_{i}", DataType = "image" });

        node.Output = new StepOutput
        {
            Type = "image",
            Files = Enumerable.Range(1, imageFiles)
                .Select(i => new OutputFile
                {
                    FileName = $"image_{i}.png",
                    ContentType = "image/png",
                    FileSize = 3,
                })
                .ToList(),
        };

        return node;
    }
}
