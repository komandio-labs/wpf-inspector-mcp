using System.IO;
using System.Reflection;

namespace KomandioLabs.WpfInspector.Mcp;

/// <summary>Resolves the inspection payloads beside the server or extracts them for a single-file publish.</summary>
internal static class RuntimeAssets
{
    private const string AgentFileName = "KomandioLabs.WpfInspector.Agent.dll";
    private const string NativeInjectorFileName = "KomandioLabs.WpfInspector.NativeInjector.x64.dll";
    private static readonly Lazy<string> AgentPath = new(() => Resolve(AgentFileName));
    private static readonly Lazy<string> NativeInjectorPath = new(() => Resolve(NativeInjectorFileName));
    private static readonly Lazy<string> ExtractedDirectory = new(ExtractEmbeddedAssets);

    internal static string Agent => AgentPath.Value;
    internal static string NativeInjector => NativeInjectorPath.Value;

    private static string Resolve(string fileName)
    {
        var besideServer = Path.Combine(AppContext.BaseDirectory, fileName);
        return File.Exists(besideServer) ? besideServer : Path.Combine(ExtractedDirectory.Value, fileName);
    }

    private static string ExtractEmbeddedAssets()
    {
        var directory = Directory.CreateTempSubdirectory("wpfinspectmcp-").FullName;
        Extract("WpfInspectorMcp.Agent.dll", Path.Combine(directory, AgentFileName));
        Extract("WpfInspectorMcp.NativeInjector.x64.dll", Path.Combine(directory, NativeInjectorFileName));
        return directory;
    }

    private static void Extract(string resourceName, string destinationPath)
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"The embedded inspection asset '{resourceName}' is missing.");
        using var destination = File.Create(destinationPath);
        resource.CopyTo(destination);
    }
}
