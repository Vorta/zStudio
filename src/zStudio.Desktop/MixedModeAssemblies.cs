using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace zStudio.Desktop;

/// <summary>
/// .NET 11's WPF has two mixed-mode (C++/CLI) assemblies, DirectWriteForwarder and System.Printing. Such an assembly cannot
/// load from the single-file bundle and needs <c>ijwhost.dll</c> beside it on disk. The package therefore carries them inside
/// this assembly instead of in the bundle (zStudio.Desktop.csproj, StudioPackage); when WPF first asks for one it is written
/// beside the <c>ijwhost.dll</c> the bundle extracted and loaded from there. Other builds have no such resources and load
/// both from their own folder as usual.
/// </summary>
internal static class MixedModeAssemblies
{
    private const string Prefix = "zStudio.Mixed.";

    // The application's Main is generated and its first statement touches WPF: only a module initializer runs before it.
    [ModuleInitializer]
    internal static void Install()
    {
        var assembly = typeof(MixedModeAssemblies).Assembly;
        if (!assembly.GetManifestResourceNames().Any(n => n.StartsWith(Prefix, StringComparison.Ordinal))) return;
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            string file = name.Name + ".dll";
            using var stream = assembly.GetManifestResourceStream(Prefix + file);
            if (stream == null) return null;
            string folder = ExtractionFolder() ?? throw new FileNotFoundException("zStudio could not find the folder its native libraries were unpacked to.", "ijwhost.dll");
            byte[] bytes = new byte[stream.Length]; stream.ReadExactly(bytes);
            string path = Path.Combine(folder, file);
            Write(path, bytes);
            return context.LoadFromAssemblyPath(path);
        };
    }

    /// <summary>The bundle's extraction folder for this build (%TEMP%\.net\zStudio\&lt;bundle id&gt;, or under DOTNET_BUNDLE_EXTRACT_BASE_DIR): where its ijwhost.dll is.</summary>
    private static string? ExtractionFolder() => (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string)?
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(folder => File.Exists(Path.Combine(folder, "ijwhost.dll")));

    /// <summary>Writes the assembly unless the folder already holds exactly it (another zStudio of this build may have it loaded).</summary>
    private static void Write(string path, byte[] bytes)
    {
        try { if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return; }
        catch (IOException) { }
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, overwrite: true); }
        finally { try { File.Delete(temporary); } catch (IOException) { } }
    }
}
