using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core;

/// <summary>Resolves model libraries by their decoded structure in the current dependency scope.</summary>
public static class MotionLibrary
{
    public static async Task<ZbdDocument> LoadAsync(string context, AssetResolver resolver, CancellationToken token = default)
    {
        List<ZbdDocument> candidates = [];
        foreach (string path in resolver.ResourceDirectories(context).SelectMany(d => Directory.EnumerateFiles(d, "*.zbd")).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            if (FormatRegistry.Probe(path).Family != FormatFamily.Archive) continue;
            var doc = await resolver.OpenCachedAsync(path, token).ConfigureAwait(false);
            if (doc.Scene != null && doc.Assets.Any(a => a.Content is MechAssembly)) candidates.Add(doc);
        }
        return candidates.Count == 1 ? candidates[0] : throw new InvalidDataException(candidates.Count == 0 ? "No decoded mech library is available in this workspace." : "Multiple mech libraries are available; use a root with one unambiguous library.");
    }
    public static int? SuggestedMember(string motionName, ZbdDocument library)
    {
        var candidates = library.Assets.Where(a => a.Content is MechAssembly assembly &&
            library.Scene!.Nodes[assembly.RootNode].Children.Any(i =>
                a.Name == "mech_" + library.Scene.Nodes[i].Name + ".flt" && motionName.StartsWith(library.Scene.Nodes[i].Name + "_", StringComparison.Ordinal))).ToArray();
        return candidates.Length == 1 ? candidates[0].Index : null;
    }
}
