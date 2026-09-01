using Microsoft.EntityFrameworkCore;
using Server.Data;
using Server.Models;

namespace Server.Services.Ai;

/// <summary>
/// Expone un nodo Directorio de archivos en una URL publica: resuelve su indice
/// y localiza en disco el fichero que corresponde a una ruta del indice.
///
/// El truco del modulo es que el directorio sea alcanzable sin autenticacion:
/// el indice que sale del nodo lleva URLs absolutas, y quien las recibe (otro
/// modulo, un servicio externo o el propio modelo) tiene que poder descargarlas
/// sin credenciales. Por eso estos endpoints son publicos, y por eso el acceso
/// se limita a lo que el indice declara: una ruta que no esta en el indice no
/// se sirve, aunque el fichero exista en el nodo.
/// </summary>
public static class FileDirectoryPublisher
{
    /// <summary>Nodo Directorio cargado con lo necesario para resolver su indice.</summary>
    public sealed record DirectoryNode(ProjectModule Node, List<ModuleFile> Files);

    /// <summary>
    /// Carga el nodo si de verdad es un Directorio de archivos. Devuelve null
    /// cuando no existe o cuando es un nodo de otro tipo, para que el endpoint
    /// responda 404 sin distinguir entre ambos casos.
    /// </summary>
    public static async Task<DirectoryNode?> LoadAsync(
        UserDbContext db,
        Guid moduleId,
        CancellationToken ct = default)
    {
        var node = await db.ProjectModules
            .Include(pm => pm.AiModule)
            .FirstOrDefaultAsync(pm => pm.Id == moduleId, ct);

        if (node is null || !string.Equals(
                node.AiModule?.ModuleType,
                FileDirectoryIndex.ModuleType,
                StringComparison.OrdinalIgnoreCase))
            return null;

        var files = await db.ModuleFiles
            .Where(f => f.ProjectModuleId == moduleId)
            .ToListAsync(ct);

        return new DirectoryNode(node, files);
    }

    /// <summary>Resuelve el indice del nodo con las URLs publicas ya absolutas.</summary>
    public static FileDirectoryIndex.ParseResult Resolve(
        DirectoryNode directory,
        string tenant,
        string? publicBaseUrl)
    {
        var moduleConfig = directory.Node.AiModule?.Configuration;
        var nodeConfig = directory.Node.Configuration;

        var indexJson = FileDirectoryIndex.ReadConfig(moduleConfig, nodeConfig, FileDirectoryIndex.IndexConfigKey);
        var baseUrl = FileDirectoryIndex.ReadConfig(moduleConfig, nodeConfig, FileDirectoryIndex.BaseUrlConfigKey);

        return FileDirectoryIndex.Resolve(
            indexJson,
            baseUrl,
            directory.Files.Select(f => new FileDirectoryIndex.HostedFile(f.Id, f.FileName)),
            path => FileDirectoryIndex.Absolutize(
                publicBaseUrl,
                FileDirectoryIndex.BuildPublicPath(tenant, directory.Node.Id, path)));
    }

    /// <summary>
    /// Localiza el fichero de disco que respalda una ruta del indice.
    ///
    /// Solo se sirven rutas alojadas por nosotros y declaradas en el indice: si
    /// la entrada apunta a un repositorio externo no hay nada que servir aqui, y
    /// si la ruta no aparece en el indice tampoco, aunque el nodo tenga subido un
    /// fichero con ese nombre.
    /// </summary>
    public static ModuleFile? FindHostedFile(
        DirectoryNode directory,
        FileDirectoryIndex.ParseResult index,
        string requestedPath)
    {
        var path = FileDirectoryIndex.NormalizePath(requestedPath);
        if (path is null || FileDirectoryIndex.HasTraversal(path)) return null;

        var entry = index.Entries.FirstOrDefault(e =>
            string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));

        if (entry is null || entry.Source != FileDirectoryIndex.Sources.Hosted) return null;

        // El indice ya resolvio cual es el fichero: aqui solo se recupera la fila.
        return directory.Files.FirstOrDefault(f => f.Id == entry.SourceFileId);
    }

    /// <summary>Ruta en disco de un fichero del directorio, o null si ya no esta.</summary>
    public static string? ResolveDiskPath(string mediaRoot, ModuleFile file)
    {
        var fullPath = Path.GetFullPath(Path.Combine(mediaRoot, file.FilePath));

        // El FilePath viene de la base de datos; se comprueba igualmente que no
        // se escape del almacen antes de leer nada. La barra final importa: sin
        // ella, un hermano tipo "GeneratedMedia2" pasaria el filtro.
        var root = Path.GetFullPath(mediaRoot);
        if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.Ordinal)) return null;

        return File.Exists(fullPath) ? fullPath : null;
    }
}
