namespace Server.Services.Ai;

/// <summary>
/// Resolves a completed module's StepOutput into PortData for each of its output ports.
/// This replaces all the manual input resolution logic (sceneInputs, templateInputs, etc.)
/// </summary>
public static class PortDataResolver
{
    /// <summary>
    /// After a node completes, extract PortData for each output port based on StepOutput.
    /// </summary>
    public static void ResolveOutputPorts(ModuleNode node)
    {
        var output = node.Output;
        if (output is null) return;

        foreach (var port in node.OutputPorts)
        {
            port.Data = ResolvePort(node, port, output);
        }
    }

    private static PortData ResolvePort(ModuleNode node, OutputPort port, StepOutput output)
    {
        // Orchestrator indexed outputs: output_1 -> Items[0], output_2 -> Items[1], etc.
        if (node.ModuleType == "Orchestrator" && port.PortId.StartsWith("output_")
            && int.TryParse(port.PortId.AsSpan("output_".Length), out var orchIdx))
        {
            var idx = orchIdx - 1;
            var text = idx >= 0 && idx < output.Items.Count
                ? output.Items[idx].Content
                : output.Content;
            return new PortData
            {
                DataType = "text",
                TextContent = text,
                FullOutput = output,
                SourcePortId = port.PortId,
            };
        }

        // Checkpoint pass-through: output_N copies input_N data
        if (node.ModuleType == "Checkpoint" && port.PortId.StartsWith("output_"))
        {
            var inputPortId = "input_" + port.PortId["output_".Length..];
            var inputPort = node.InputPorts.FirstOrDefault(p => p.PortId == inputPortId);
            if (inputPort?.ReceivedData.Count > 0)
                return inputPort.ReceivedData[0];

            return new PortData { DataType = "any", SourcePortId = port.PortId };
        }

        // Indexed image output: output_image_1, output_image_2, etc.
        if (port.PortId.StartsWith("output_image_")
            && int.TryParse(port.PortId.AsSpan("output_image_".Length), out var imgIdx))
        {
            var idx = imgIdx - 1;
            var files = output.Files;
            var file = idx >= 0 && idx < files.Count ? [files[idx]] : files;
            return new PortData
            {
                DataType = "image",
                Files = file,
                FullOutput = output,
                SourcePortId = port.PortId,
            };
        }

        // Standard port resolution by port ID pattern
        return port.PortId switch
        {
            "output_text" or "output_prompt" or "output_result" or "output_response"
                => new PortData
                {
                    DataType = "text",
                    TextContent = BuildFullText(output),
                    Files = output.Files.Count > 0 ? output.Files : null,
                    FullOutput = output,
                    SourcePortId = port.PortId,
                },

            "output_image"
                => new PortData
                {
                    DataType = "image",
                    Files = output.Files,
                    FullOutput = output,
                    SourcePortId = port.PortId,
                },

            "output_video" or "output_videos"
                => new PortData
                {
                    DataType = "video",
                    Files = output.Files,
                    FullOutput = output,
                    SourcePortId = port.PortId,
                },

            "output_audio"
                => new PortData
                {
                    DataType = "audio",
                    Files = output.Files,
                    FullOutput = output,
                    SourcePortId = port.PortId,
                },

            "output_scene"
                => new PortData
                {
                    DataType = "scene",
                    TextContent = output.Content,
                    FullOutput = output,
                    SourcePortId = port.PortId,
                },

            "output_file"
                => new PortData
                {
                    DataType = node.ModuleType == "FileUpload" ? "any" : "file",
                    Files = output.Files,
                    TextContent = output.Content,
                    FullOutput = output,
                    SourcePortId = port.PortId,
                },

            "output_embedding"
                => new PortData
                {
                    DataType = "file",
                    TextContent = output.Content,
                    FullOutput = output,
                    SourcePortId = port.PortId,
                },

            // Default: expose content and files as-is
            _ => new PortData
            {
                DataType = "any",
                TextContent = BuildFullText(output),
                Files = output.Files.Count > 0 ? output.Files : null,
                FullOutput = output,
                SourcePortId = port.PortId,
            }
        };
    }

    /// <summary>
    /// Flatten a StepOutput to plain text for downstream text ports: top-level
    /// Content followed by every item as "Label: Content". AI providers force
    /// the JSON schema (title/content/summary/items/metadata) so returning just
    /// Content would silently drop the slide-by-slide (or prompt-by-prompt)
    /// detail the model actually produced.
    /// </summary>
    private static string BuildFullText(StepOutput output)
    {
        var hasContent = !string.IsNullOrWhiteSpace(output.Content);

        if (output.Items.Count == 0)
            return output.Content ?? string.Empty;

        var itemsText = string.Join("\n\n", output.Items
            .Where(i => !string.IsNullOrWhiteSpace(i.Content))
            .Select(i => string.IsNullOrWhiteSpace(i.Label)
                ? i.Content
                : $"{i.Label}: {i.Content}"));

        if (string.IsNullOrWhiteSpace(itemsText))
            return output.Content ?? string.Empty;

        return hasContent ? $"{output.Content}\n\n{itemsText}" : itemsText;
    }
}
