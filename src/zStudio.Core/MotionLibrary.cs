using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core;

/// <summary>Resolves model libraries by their decoded structure in the current dependency scope.</summary>
public static class MotionLibrary
{
    /// <param name="skipped">Receives bounded per-file failures; one unreadable archive does not hide the library.</param>
    public static async Task<ZbdDocument> LoadAsync(string context, AssetResolver resolver, CancellationToken token = default, ICollection<string>? skipped = null)
    {
        List<ZbdDocument> candidates = []; int failures = 0;
        foreach (string path in resolver.ResourceDirectories(context).SelectMany(d => Directory.EnumerateFiles(d, "*.zbd")).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (FormatRegistry.Probe(path).Family != FormatFamily.Archive) continue;
                var doc = await resolver.OpenCachedAsync(path, token).ConfigureAwait(false);
                if (doc.Scene != null && doc.Assets.Any(a => a.Content is MechAssembly)) candidates.Add(doc);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            { if (++failures <= 32) skipped?.Add($"{Path.GetFileName(path)}: {(ex.Message.Length > 512 ? ex.Message[..512] + "…" : ex.Message)}"); }
        }
        if (candidates.Count == 1) return candidates[0];
        string unread = failures == 0 ? "" : $" {failures} archive(s) could not be read.";
        throw new InvalidDataException((candidates.Count == 0 ? "No decoded mech library is available in this workspace." : "Multiple mech libraries are available; use a root with one unambiguous library.") + unread);
    }
    public static int? SuggestedMember(string motionName, ZbdDocument library)
    {
        var candidates = library.Assets.Where(a => a.Content is MechAssembly assembly &&
            library.Scene!.Nodes[assembly.RootNode].Children.Any(i =>
                a.Name == "mech_" + library.Scene.Nodes[i].Name + ".flt" && motionName.StartsWith(library.Scene.Nodes[i].Name + "_", StringComparison.Ordinal))).ToArray();
        return candidates.Length == 1 ? candidates[0].Index : null;
    }
}
