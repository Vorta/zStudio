using zStudio.Core.Animation;
using zStudio.Core.Worlds;

namespace zStudio.Core.Sources;

public static partial class SourceObjectEdits
{
    /// <summary>Placed script additions obey the same binding and initialization protections as hierarchy additions.</summary>
    public static void ValidatePlacedAddition(SourceWorkspace workspace, IReadOnlyDictionary<string, byte[]> before,
        SourceSceneSnapshot active, SourceModelAddition addition, CancellationToken token = default)
    {
        if (addition.Position == null) return;
        var old = SourceWorlds.LoadSceneAsync(workspace.Root, active.Mission, before, token, sourceMission: active.SourceMission).GetAwaiter().GetResult();
        ValidateAnimationRecipients(old, active, token);
        var roots = active.Scene!.Nodes.Where(n => n.Name == addition.Name).Select(n => n.Index).ToArray();
        if (roots.Length != 1) throw new InvalidDataException("The placed addition has no unique source root.");
        ValidateInitialization(workspace, before, active, old, roots, addition.Name, token);
    }

    private static void ValidateInitialization(SourceWorkspace workspace, IReadOnlyDictionary<string, byte[]> before,
        SourceSceneSnapshot built, SourceSceneSnapshot old, IReadOnlyList<int> roots, string name, CancellationToken token)
    {
        var subtree = CreatedSubtree(built, roots, token);
        if (built.Game == GameVariant.MechWarrior3)
        {
            workspace.TrackResourceInventory(built.Mission, token);
            workspace.TrackResourceInventory(null, token);
            if (built.BuildState.ResourceInputsChanged(workspace, token))
                throw new SourceFileChangedException("Mission resources changed before placement validation; try again.", []);
        }
        var contexts = built.Game == GameVariant.MechWarrior3
            ? SourceGameProfile.Missions(workspace, built.Mission, token).Select(c => (Mission: (string?)c.SourceMission, Difficulty: MissionDifficulty.Medium))
            : Enum.GetValues<MissionDifficulty>().Select(d => (Mission: (string?)null, Difficulty: d));
        if (!contexts.Any()) contexts = [(built.SourceMission, MissionDifficulty.Medium)];
        foreach (var context in contexts)
        {
            token.ThrowIfCancellationRequested();
            var initialized = built.Game == GameVariant.MechWarrior3 && context.Mission != built.SourceMission
                ? built.NativeMissionScene(context.Mission, workspace.Overlay(), workspace, token) : built.MissionScene(context.Difficulty, token);
            var previous = old.Game == GameVariant.MechWarrior3 && context.Mission != old.SourceMission
                ? old.NativeMissionScene(context.Mission, before, null, token) : old.MissionScene(context.Difficulty, token);
            ValidateActorRecipients(old, built, previous, initialized, token);
            if (initialized.Actors.Any(actor => subtree.Contains(actor.SourceRoot) || subtree.Contains(actor.Root))
                || roots.Any(root => initialized.InitializationTransforms?.Writes.Contains(root) == true
                || initialized.InitializationTransforms?.Reads.Contains(root) == true
                || initialized.Actors.Any(actor => actor.Root == root) || initialized.DormantRoots.Contains(root)
                || SceneBuilder.LocalTransform(initialized.Scene.Nodes[root]) != SceneBuilder.LocalTransform(built.Scene!.Nodes[root])))
                throw new InvalidDataException($"{built.Mission} ({context.Mission ?? context.Difficulty.ToString()}): mission initialization or vehicle/pickup resources control {name}'s placement. Choose another object name or update those definitions before placing it on the map.");
            foreach (var (file, hash) in built.ReadHashes)
                if (workspace.Read(file, token) is not { } bytes || SourceProject.Sha256(bytes) != hash)
                    throw new SourceFileChangedException($"{file} changed during placement validation.", [file]);
        }
    }

    private static void ValidateAnimationRecipients(SourceSceneSnapshot before, SourceSceneSnapshot after, CancellationToken token)
    {
        if (before.Animations == null || after.Animations == null) return;
        AnimationPreviewContext was = new() { Package = before.Animations, World = before };
        AnimationPreviewContext now = new() { Package = after.Animations, World = after };
        AnimationBindingOperation oldWork = new(was, token), newWork = new(now, token);
        static IEnumerable<((string Name, string Root, int Occurrence) Key, AnimationEntry Entry)> Entries(AnimationPackage package)
        {
            Dictionary<(string, string), int> occurrences = [];
            foreach (var entry in package.Entries)
            {
                var key = (entry.Name, entry.RootName);
                int occurrence = occurrences.GetValueOrDefault(key); occurrences[key] = occurrence + 1;
                if (!NameLookups.SkipsRoot(package.Entries, entry.Index)) yield return ((entry.Name, entry.RootName, occurrence), entry);
            }
        }
        var current = Entries(after.Animations).ToDictionary(p => p.Key, p => p.Entry);
        foreach (var (key, entry) in Entries(before.Animations))
        {
            token.ThrowIfCancellationRequested();
            int root = oldWork.Root(entry);
            if (root < 0) continue; // Newly bound dormant definitions are permitted.
            if (!current.TryGetValue(key, out var next)) throw Refuse(entry.Name);
            int nextRoot = newWork.Root(next);
            Check(root, nextRoot, entry.Name);
            var binding = was.Binding(entry, root, oldWork); var nextBinding = now.Binding(next, nextRoot, newWork);
            var names = entry.References[0].Skip(1).Concat(entry.References[1].Skip(1)).Select(r => r.Text(0, 36));
            if (entry.AttachName.Length > 0) names = names.Prepend(entry.AttachName);
            foreach (string name in names)
            {
                token.ThrowIfCancellationRequested();
                Check(was.ResolveTrackedNode(entry, name, root, binding, oldWork), now.ResolveTrackedNode(next, name, nextRoot, nextBinding, newWork), entry.Name);
            }
            int oldParent = -1, newParent = -1; bool first = true;
            foreach (var record in entry.References[6])
            {
                oldWork.Reserve(1); newWork.Reserve(1);
                if (record.Bytes.Length < 48 || record.Bytes[4] is not (2 or 3)) continue;
                string name = record.Text(12, 28);
                oldParent = Prerequisite(was, oldWork, oldParent, name, first);
                newParent = Prerequisite(now, newWork, newParent, name, first);
                Check(oldParent, newParent, entry.Name);
                first = record.Bytes[4] == 2;
                if (first) { oldParent = -1; newParent = -1; }
            }
        }
        void Check(int from, int to, string animation)
        {
            if (from < 0 && to < 0) return;
            if (!before.Provenance.TryGetValue(from, out var a) || !after.Provenance.TryGetValue(to, out var b) || !SameSource(a, b)) throw Refuse(animation);
        }
        InvalidDataException Refuse(string animation) => new($"{before.Mission}: the addition would change an existing binding of animation {JsonData.ShownText(animation, 64)} to another object. Rename the conflicting nodes in the selected model or update the definitions before adding it.");
        static int Prerequisite(AnimationPreviewContext context, AnimationBindingOperation work, int parent, string name, bool first)
        {
            if (first) return work.Named(name, context.LoadedLimit) is { Count: > 0 } named ? named[0] : -1;
            if (parent < 0) return -1;
            // The canonical last-child-first path resolver owns its own traversal allowance. Charge a conservative
            // full-scene pass here too, so repeated prerequisite paths share this comparison's operation budget.
            work.Reserve(context.Scene.Nodes.Count);
            work.Reserve(1L + context.Scene.Nodes.Sum(n => (long)n.Children.Length));
            return context.FindSubBelow(parent, name);
        }
    }

    private static HashSet<int> CreatedSubtree(SourceSceneSnapshot snapshot, IReadOnlyList<int> roots, CancellationToken token)
    {
        HashSet<int> result = []; Stack<int> pending = new(roots); LookupWorkBudget work = new(token: token);
        while (pending.TryPop(out int index))
        {
            work.Reserve(1);
            if (!result.Add(index)) continue;
            var node = snapshot.Scene!.Nodes[index]; work.Reserve(node.Children.Length);
            foreach (int child in node.Children) pending.Push(child);
        }
        return result;
    }

    private static void ValidateActorRecipients(SourceSceneSnapshot before, SourceSceneSnapshot after, MissionSceneContext was, MissionSceneContext now, CancellationToken token)
    {
        TargetIdentities identities = new();
        (bool Pickup, string Archive, string Resource, int Asset, int Record, int Source) Key(SourceSceneSnapshot snapshot, MissionActor actor)
        {
            var resource = actor.CoordinateSource ?? actor.Pickup?.Source;
            if (resource == null || resource.ResourceName == "GAMEZ")
                return (actor.Pickup != null, "", "", -1, -1, Source(snapshot, actor));
            return (actor.Pickup != null, resource.ArchivePath, resource.ResourceName, resource.AssetIndex, resource.RecordIndex, -1);
        }
        int Source(SourceSceneSnapshot snapshot, MissionActor actor) => snapshot.Provenance.TryGetValue(actor.SourceRoot, out var origin) ? identities.Identity(origin) : -1;
        var current = now.Actors.GroupBy(a => Key(after, a)).ToDictionary(g => g.Key, g => g.ToArray());
        foreach (var group in was.Actors.GroupBy(a => Key(before, a)))
        {
            token.ThrowIfCancellationRequested();
            var oldActors = group.ToArray();
            if (!current.TryGetValue(group.Key, out var next) || oldActors.Length != next.Length
                || oldActors.Where((actor, i) => (actor.TemplateIdentity != null ? actor.TemplateIdentity != next[i].TemplateIdentity : Source(before, actor) < 0 || Source(before, actor) != Source(after, next[i]))
                    || actor.PlacementPosition != next[i].PlacementPosition || actor.PlacementRotation != next[i].PlacementRotation).Any())
                throw new InvalidDataException($"{before.Mission} ({was.Layout.Difficulty}): the addition would change an existing vehicle or pickup placement. Rename the conflicting nodes in the selected model before adding it.");
        }
    }
}
