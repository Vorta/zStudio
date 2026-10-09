using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core;

/// <summary>Resolves model libraries by their decoded structure in the current dependency scope.</summary>
public static class MotionLibrary
{
    /// <param name="skipped">Receives bounded per-file failures; one unreadable archive does not hide the library.</param>
    public static Task<ZbdDocument> LoadAsync(string context, AssetResolver resolver, CancellationToken token = default, ICollection<string>? skipped = null)
        => LoadAsync(context, resolver, new AssetReadBudget(), new CompiledInventory(token), token, skipped);
    internal static async Task<ZbdDocument> LoadAsync(string context, AssetResolver resolver, AssetReadBudget reads,
        CompiledInventory inventory, CancellationToken token, ICollection<string>? skipped = null)
    {
        reads.Check(); token.ThrowIfCancellationRequested();
        ZbdDocument? candidate = null; int failures = 0;
        List<string> paths = []; HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in resolver.ResourceDirectories(context, inventory))
            foreach (string path in inventory.Files(directory, sort: false))
            {
                inventory.Path(path.Length);
                if (seen.Add(path)) { inventory.Rows(1); paths.Add(path); }
            }
        inventory.Sort(paths, StringComparer.OrdinalIgnoreCase.Compare, p => p);
        foreach (string path in paths)
        {
            token.ThrowIfCancellationRequested();
            ZbdDocument doc;
            try
            {
                if (FormatRegistry.Probe(path).Family != FormatFamily.Archive) continue;
                doc = await resolver.OpenCachedAsync(path, FormatRegistry.MaximumDocumentBytes, token, reads).ConfigureAwait(false);
            }
            catch (Exception ex) when (!reads.Exhausted && (ex is InvalidDataException or IOException or UnauthorizedAccessException))
            { if (++failures <= 32) skipped?.Add($"{Path.GetFileName(path)}: {(ex.Message.Length > 512 ? ex.Message[..512] + "…" : ex.Message)}"); continue; }
            if (doc.Scene == null || !doc.Assets.Any(a => a.Content is MechAssembly)) continue;
            // A second supported library proves ambiguity; later archives cannot make it unique.
            // Keep this refusal outside the per-file catch and never retain an unbounded candidate list.
            if (candidate != null) throw new InvalidDataException("Multiple mech libraries are available; use a root with one unambiguous library.");
            candidate = doc;
        }
        token.ThrowIfCancellationRequested();
        if (candidate != null) return candidate;
        string unread = failures == 0 ? "" : $" {failures} archive(s) could not be read.";
        throw new InvalidDataException("No decoded mech library is available in this workspace." + unread);
    }
    public static int? SuggestedMember(string motionName, ZbdDocument library)
    {
        var candidates = library.Assets.Where(a => a.Content is MechAssembly assembly &&
            library.Scene!.Nodes[assembly.RootNode].Children.Any(i =>
                a.Name == "mech_" + library.Scene.Nodes[i].Name + ".flt" && motionName.StartsWith(library.Scene.Nodes[i].Name + "_", StringComparison.Ordinal))).ToArray();
        return candidates.Length == 1 ? candidates[0].Index : null;
    }
}
