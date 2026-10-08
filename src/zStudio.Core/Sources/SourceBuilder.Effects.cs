using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Core.Sources;

public static partial class SourceBuilder
{
    /// <summary>One export's effects inputs, cached identities and repeated discovery work, across all selected missions.</summary>
    internal sealed record EffectLimits(long InputBytes = AnimationDefinitionSet.MaximumSourceBytes,
        long RetainedBytes = AnimationDefinitionSet.MaximumSourceBytes, long Work = AnimationDefinitionSet.MaximumPathWork);

    private sealed class EffectDiscovery
    {
        private readonly Snapshot snapshot;
        private readonly string root;
        private readonly EffectLimits limits;
        private readonly Dictionary<string, IReadOnlyCollection<string>> files = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IReadOnlyList<string>> inventories = new(StringComparer.OrdinalIgnoreCase);
        private long inputBytes, retained, work;
        private bool exhausted;
        internal long InputBytes => inputBytes;
        internal int FilesDecoded => files.Count;

        internal EffectDiscovery(Snapshot snapshot, string root, EffectLimits limits)
        {
            if (limits.InputBytes is < 0 or > AnimationDefinitionSet.MaximumSourceBytes
                || limits.RetainedBytes is < 0 or > AnimationDefinitionSet.MaximumSourceBytes
                || limits.Work is < 0 or > AnimationDefinitionSet.MaximumPathWork)
                throw new ArgumentOutOfRangeException(nameof(limits));
            this.snapshot = snapshot; this.root = root; this.limits = limits;
        }

        // No tree or payload is cached: successful names belong to this snapshot, whose final unchanged check
        // still verifies every source digest. Case-sensitive template identity differs from file identity.
        internal HashSet<string>? ForMission(string mission, CancellationToken token)
        {
            SpendWork(1, token);
            HashSet<string>? names = null;
            var common = Inventory("data/common", token);
            var local = Inventory($"data/{mission}/zrdr", token);
            foreach (string source in common.Concat(local))
            {
                SpendWork(128L + 4L * source.Length, token); // Before splitting, hashing or keeping the path.
                SourceProject.RequireSource(source);
                names ??= new(StringComparer.Ordinal);
                if (!files.TryGetValue(source, out var found))
                {
                    long remaining = limits.InputBytes - inputBytes;
                    byte[] bytes;
                    try { bytes = snapshot.Files().Read(source, token, ProjectReadLimits.Resource(Math.Min(SourceProject.MaximumSourceTextBytes, remaining))); }
                    catch (InvalidDataException ex) when (remaining < SourceProject.MaximumSourceTextBytes)
                    { throw Refused("input bytes", ex); }
                    inputBytes += bytes.LongLength; // Failed parses also spend their read/decode allowance.
                    var tree = ZrdText.LooksLikeText(bytes) ? ZrdText.Parse(bytes, token) : ZrdDecoder.Read(bytes, token);
                    // Bound the shared semantic reader's scans and temporary item/scalar arrays before invoking it.
                    Inspect(tree, token);
                    SpendRetained(128L + 2L * source.Length, token);
                    HashSet<string> distinct = new(StringComparer.Ordinal);
                    foreach (string name in AnimationCompiler.EffectNames(tree))
                    {
                        SpendWork(128L + 4L * name.Length, token);
                        if (distinct.Contains(name)) continue;
                        SpendRetained(128L + 2L * name.Length, token);
                        distinct.Add(name);
                    }
                    files.Add(source, found = distinct);
                }
                foreach (string name in found)
                {
                    // Include the mission set and the compiler's subsequent copy/hash pass. Reusing a cached
                    // common file saves decoding but never makes repeated unions uncharged.
                    SpendWork(256L + 8L * name.Length, token);
                    names.Add(name);
                }
            }
            return names;
        }

        private IReadOnlyList<string> Inventory(string folder, CancellationToken token)
        {
            SpendWork(128L + 4L * folder.Length, token);
            if (inventories.TryGetValue(folder, out var known)) return known;
            SpendRetained(128L + 2L * folder.Length, token);
            var found = Scan(folder, token, retaining: true);
            inventories.Add(folder, found);
            return found;
        }

        private IReadOnlyList<string> Scan(string folder, CancellationToken token, bool retaining)
        {
            var scanned = SourceProject.Files(root, folder, name => name.Equals("effects.zrd", StringComparison.OrdinalIgnoreCase), snapshot.Added, token,
                snapshot.Inventory, retaining ? length => SpendRetained(64L + 2L * length, token) : null);
            snapshot.Inventory.Rows(scanned.Count, token);
            List<string> found = [];
            foreach (string path in scanned)
            {
                SpendWork(128L + 4L * path.Length, token);
                if (folder == "data/common" && !path.AsSpan().Contains("/zrdr/", StringComparison.OrdinalIgnoreCase)) continue;
                found.Add(path);
            }
            return found;
        }

        internal void CheckUnchanged(CancellationToken token)
        {
            // Cache common enumeration as well as decoded names: repeatedly scanning a large irrelevant common
            // tree per mission is expensive too. Verify the inventory, including initial absence, before publishing.
            foreach (var (folder, before) in inventories)
                if (!before.SequenceEqual(Scan(folder, token, retaining: false), StringComparer.OrdinalIgnoreCase))
                    throw snapshot.Changed(folder, "The project's effects.zrd inventory changed while exporting; export again.");
        }

        private void Inspect(ZrdNode node, CancellationToken token)
        {
            SpendWork(128L + 2L * node.Text.Length + 16L * node.Children.Count, token);
            // Shared ZRD readers already enforce depth128; no new parser or unbounded pending child array.
            foreach (var child in node.Children) Inspect(child, token);
        }
        private void SpendWork(long amount, CancellationToken token) => Spend(ref work, amount, limits.Work, "discovery work", token);
        private void SpendRetained(long amount, CancellationToken token) => Spend(ref retained, amount, limits.RetainedBytes, "retained names", token);
        private void Spend(ref long used, long amount, long maximum, string kind, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (exhausted || amount < 0 || amount > maximum - used) throw Refused(kind);
            used += amount;
        }
        private InvalidDataException Refused(string kind, Exception? inner = null)
        {
            exhausted = true;
            return new($"The export's effects exceed the aggregate {kind} limit; simplify effects.zrd resources or export fewer missions together.", inner);
        }
    }
}
