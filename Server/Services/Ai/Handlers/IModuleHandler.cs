using Server.Data;
using Server.Models;

namespace Server.Services.Ai.Handlers;

public interface IModuleHandler
{
    string ModuleType { get; }
    Task<ModuleResult> ExecuteAsync(ModuleExecutionContext ctx);
}

public enum ModuleResultStatus { Completed, Failed, Paused }

public class ModuleResult
{
    public ModuleResultStatus Status { get; set; }
    public StepOutput? Output { get; set; }
    public string? Error { get; set; }
    public decimal Cost { get; set; }
    public string? PauseReason { get; set; }

    /// <summary>Files produced by this module (images, videos, etc.) to be persisted by the executor.</summary>
    public List<ProducedFile> ProducedFiles { get; set; } = [];

    public static ModuleResult Completed(StepOutput output, decimal cost = 0m, List<ProducedFile>? files = null) =>
        new() { Status = ModuleResultStatus.Completed, Output = output, Cost = cost, ProducedFiles = files ?? [] };

    public static ModuleResult Failed(string error) =>
        new() { Status = ModuleResultStatus.Failed, Error = error };

    public static ModuleResult Paused(string reason, StepOutput? output = null) =>
        new() { Status = ModuleResultStatus.Paused, PauseReason = reason, Output = output };
}

/// <summary>A file produced by a module handler, to be saved to the workspace by the executor.</summary>
public class ProducedFile
{
    public byte[] Data { get; set; } = [];
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
}

/// <summary>Everything a module handler needs to execute.</summary>
public class ModuleExecutionContext
{
    public required ModuleNode Node { get; init; }
    public required ExecutionGraph Graph { get; init; }
    public required ProjectExecution Execution { get; init; }
    public required Project Project { get; init; }
    public required string TenantDbName { get; init; }
    public required string WorkspacePath { get; init; }
    public string? PreviousSummaryContext { get; init; }
    /// <summary>Pre-joined mandatory rules from the tenant Rules table.</summary>
    public string? MandatoryRules { get; init; }
    public CancellationToken CancellationToken { get; init; }

    /// <summary>Pre-resolved input data grouped by port ID.</summary>
    public Dictionary<string, List<PortData>> InputsByPort { get; init; } = [];

    /// <summary>Merged configuration from AiModule + ProjectModule.</summary>
    public Dictionary<string, object> Config { get; init; } = [];

    /// <summary>Files attached to the AiModule (for FileUpload modules).</summary>
    public List<ModuleFileInfo> ModuleFiles { get; init; } = [];

    /// <summary>Base path for media storage.</summary>
    public required string MediaRoot { get; init; }

    /// <summary>Public base URL used to expose produced files to external services.</summary>
    public string PublicBaseUrl { get; init; } = "";

    /// <summary>Execution file id -> path relative to the execution workspace.</summary>
    public Dictionary<Guid, string> ExecutionFilePaths { get; init; } = [];

    /// <summary>Build the public URL for a file produced during this execution.</summary>
    public string? GetPublicFileUrl(OutputFile file)
    {
        if (file.FileId == Guid.Empty)
            return null;

        var path = $"/api/public/files/{Uri.EscapeDataString(TenantDbName)}/{Execution.Id}/{file.FileId}/{Uri.EscapeDataString(file.FileName)}";
        return string.IsNullOrWhiteSpace(PublicBaseUrl)
            ? path
            : $"{PublicBaseUrl.TrimEnd('/')}{path}";
    }

    /// <summary>
    /// Get text content from an input port. When several upstream modules connect
    /// to the same port (fan-in) every non-empty text is concatenated with a
    /// blank line separator so the downstream handler sees the full context.
    /// </summary>
    public string GetInputText(string portId, string fallback = "")
    {
        if (!InputsByPort.TryGetValue(portId, out var dataList) || dataList.Count == 0)
            return fallback;

        var texts = dataList
            .Select(d => d.TextContent)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Cast<string>()
            .ToList();

        return texts.Count switch
        {
            0 => fallback,
            1 => texts[0],
            _ => string.Join("\n\n", texts),
        };
    }

    /// <summary>Get all text content from a port (for multi-connection ports).</summary>
    public List<string> GetAllInputTexts(string portId)
    {
        if (!InputsByPort.TryGetValue(portId, out var dataList))
            return [];
        return dataList.Where(d => d.TextContent is not null).Select(d => d.TextContent!).ToList();
    }

    /// <summary>Get files from a specific input port.</summary>
    public List<OutputFile> GetInputFiles(string portId)
    {
        if (!InputsByPort.TryGetValue(portId, out var dataList))
            return [];
        return dataList.Where(d => d.Files is not null).SelectMany(d => d.Files!).ToList();
    }

    /// <summary>Resolve an output file produced by another module to a disk path.</summary>
    public string ResolveOutputFilePath(OutputFile file)
    {
        var candidates = new List<string>();

        if (file.FileId != Guid.Empty && ExecutionFilePaths.TryGetValue(file.FileId, out var relativePath))
            candidates.Add(Path.Combine(WorkspacePath, relativePath));

        if (Path.IsPathRooted(file.FileName))
        {
            candidates.Add(file.FileName);
        }
        else
        {
            candidates.Add(Path.Combine(WorkspacePath, file.FileName));
            candidates.Add(Path.Combine(MediaRoot, file.FileName));
        }

        return candidates.FirstOrDefault(File.Exists) ?? candidates.First();
    }

    /// <summary>Read a produced file if it is still available on disk.</summary>
    public async Task<byte[]?> ReadOutputFileBytesAsync(OutputFile file)
    {
        var path = ResolveOutputFilePath(file);
        return File.Exists(path)
            ? await File.ReadAllBytesAsync(path, CancellationToken)
            : null;
    }

    /// <summary>
    /// Collect the distinct non-empty `Format` contracts declared on every
    /// outgoing edge of this node. Upstream handlers use them to steer the
    /// AI response so the JSON matches what downstream modules expect.
    /// </summary>
    public List<string> GetOutgoingFormats()
    {
        return Node.OutputPorts
            .SelectMany(p => p.Connections)
            .Select(c => c.Format)
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f!.Trim())
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Read the edge `Format` declared on a given input port (first connection
    /// with a non-empty format). Downstream handlers use it to interpret the
    /// incoming payload as structured data.
    /// </summary>
    public string? GetInputFormat(string portId)
    {
        var port = Node.InputPorts.FirstOrDefault(p => p.PortId == portId);
        return port?.Connections
            .Select(c => c.Format)
            .FirstOrDefault(f => !string.IsNullOrWhiteSpace(f));
    }

    /// <summary>Get a config value as string.</summary>
    public string GetConfig(string key, string fallback = "")
    {
        if (!Config.TryGetValue(key, out var val)) return fallback;
        if (val is System.Text.Json.JsonElement je)
            return je.ValueKind == System.Text.Json.JsonValueKind.String ? je.GetString() ?? fallback : je.GetRawText();
        // bool.ToString() yields "True"/"False"; normalize to JSON lowercase so callers
        // that compare against "true"/"false" keep working.
        if (val is bool b) return b ? "true" : "false";
        return val?.ToString() ?? fallback;
    }

    /// <summary>Get a config value as bool.</summary>
    public bool GetConfigBool(string key, bool fallback = false)
    {
        if (!Config.TryGetValue(key, out var val)) return fallback;
        if (val is bool b) return b;
        if (val is System.Text.Json.JsonElement je)
            return je.ValueKind == System.Text.Json.JsonValueKind.True;
        return bool.TryParse(val?.ToString(), out var parsed) ? parsed : fallback;
    }
}

/// <summary>Info about a file attached to an AiModule (for FileUpload).</summary>
public class ModuleFileInfo
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public string FilePath { get; set; } = "";
    public long FileSize { get; set; }
}
