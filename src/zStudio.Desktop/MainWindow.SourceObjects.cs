using System.IO;
using System.Numerics;
using System.Text.Json.Nodes;
using zStudio.Automation;
using zStudio.Core.Sources;
using zStudio.Core.Worlds;

namespace zStudio.Desktop;

/// <summary>
/// World objects of source worlds: each node of the built world knows the source that made it (see
/// <see cref="WorldNodeProvenance"/>), and moving it or changing its flags edits that source — the mission database's glTF
/// node, the model file's node or the script instruction that placed it — as one change of the project's workspace.
/// </summary>
public partial class MainWindow
{
    /// <summary>The built world read once per document, for current transforms and flags.</summary>
    private sealed record SourceWorldModelEntry(GameZWorld World, IReadOnlyDictionary<int, WorldNode> Slots,
        IReadOnlyDictionary<WorldNode, WorldNodeProvenance> Provenance, SourceObjectIndex Sources, SourceSelectionIndex Selection,
        IReadOnlyDictionary<string, string> IdentityHashes);

    // Full identities belong to one prepared world. UI scans compare group/copy ids;
    // path hashing and lineage discovery happen off-thread, without joined/lowercased strings.
    private readonly record struct SourceIdentity(byte Kind, string? Path, int Record)
    {
        public static SourceIdentity Of(int node, WorldNodeProvenance origin) => origin.ModelFile is { } file
            ? new(1, file, origin.ModelNode) : origin.Created is { } created
                ? new(2, created.Script, created.Line) : new(0, null, node);
    }
    private sealed class SourceIdentityComparer : IEqualityComparer<SourceIdentity>
    {
        public bool Equals(SourceIdentity x, SourceIdentity y) => x.Kind == y.Kind && x.Record == y.Record && StringComparer.OrdinalIgnoreCase.Equals(x.Path, y.Path);
        public int GetHashCode(SourceIdentity value) => HashCode.Combine(value.Kind, value.Record, value.Path == null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(value.Path));
    }
    private readonly record struct CopyLink(int Parent, string? Model, int Node, string? Script, int? Line)
    {
        public static CopyLink Of(int parent, WorldNodeProvenance origin) => new(parent, origin.ModelFile, origin.ModelNode, origin.Load?.Script, origin.Load?.Line);
    }
    private sealed class CopyLinkComparer : IEqualityComparer<CopyLink>
    {
        public bool Equals(CopyLink x, CopyLink y) => x.Parent == y.Parent && x.Node == y.Node && x.Line == y.Line
            && StringComparer.OrdinalIgnoreCase.Equals(x.Model, y.Model) && StringComparer.OrdinalIgnoreCase.Equals(x.Script, y.Script);
        public int GetHashCode(CopyLink value) => HashCode.Combine(value.Parent, value.Node, value.Line,
            value.Model == null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(value.Model),
            value.Script == null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(value.Script));
    }
    private sealed class SourceObjectIndex
    {
        internal sealed class Group { public List<int> Slots { get; } = []; }
        public Dictionary<WorldNode, Group> Groups { get; } = new(ReferenceEqualityComparer.Instance);
        public Dictionary<WorldNode, int> Copies { get; } = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<SourceIdentity, Group> groups = new(new SourceIdentityComparer());
        private readonly Dictionary<CopyLink, int> links = new(new CopyLinkComparer());

        public SourceObjectIndex(IReadOnlyDictionary<int, WorldNode> slots, IReadOnlyDictionary<WorldNode, WorldNodeProvenance> provenance, CancellationToken token)
        {
            Dictionary<WorldNodeProvenance, (int Id, int Depth)> known = new(ReferenceEqualityComparer.Instance);
            HashSet<WorldNodeProvenance> visiting = new(ReferenceEqualityComparer.Instance);
            foreach (var (slot, node) in slots)
            {
                token.ThrowIfCancellationRequested();
                if (!provenance.TryGetValue(node, out var origin)) continue;
                var key = SourceIdentity.Of(slot, origin);
                if (!groups.TryGetValue(key, out var group)) groups.Add(key, group = new());
                group.Slots.Add(slot); Groups.Add(node, group);
                Copies.Add(node, Lineage(origin.ReferencedBy, 0).Id);
            }
            foreach (var group in groups.Values) { token.ThrowIfCancellationRequested(); group.Slots.Sort(); }

            (int Id, int Depth) Lineage(WorldNodeProvenance? origin, int depth)
            {
                token.ThrowIfCancellationRequested();
                if (origin == null) return (0, 0);
                if (depth >= zStudio.Core.Gltf.GltfDocument.MaximumDepth) throw BadLineage();
                if (known.TryGetValue(origin, out var cached))
                {
                    if (depth + cached.Depth > zStudio.Core.Gltf.GltfDocument.MaximumDepth) throw BadLineage();
                    return cached;
                }
                if (!visiting.Add(origin)) throw BadLineage();
                try
                {
                    var parent = Lineage(origin.ReferencedBy, depth + 1);
                    var key = CopyLink.Of(parent.Id, origin);
                    if (!links.TryGetValue(key, out int id)) links.Add(key, id = links.Count + 1);
                    return known[origin] = (id, parent.Depth + 1);
                }
                finally { visiting.Remove(origin); }
            }
        }
        public List<int> Matching(SourceIdentity identity) => groups.TryGetValue(identity, out var group) ? group.Slots : [];

        // Resolve an origin from the preceding world against this world's links once;
        // snapshot-local ids must never be compared across rebuilds.
        public int? CopyOf(WorldNodeProvenance origin)
        {
            List<WorldNodeProvenance> chain = [];
            HashSet<WorldNodeProvenance> seen = new(ReferenceEqualityComparer.Instance);
            for (var by = origin.ReferencedBy; by != null; by = by.ReferencedBy)
            {
                if (!seen.Add(by) || seen.Count > zStudio.Core.Gltf.GltfDocument.MaximumDepth) throw BadLineage();
                chain.Add(by);
            }
            int id = 0;
            for (int i = chain.Count - 1; i >= 0; i--)
                if (!links.TryGetValue(CopyLink.Of(id, chain[i]), out id)) return null;
            return id;
        }
        private static InvalidDataException BadLineage() => new($"The source reference lineage is cyclic or exceeds {zStudio.Core.Gltf.GltfDocument.MaximumDepth} levels; rebuild the world before selecting a copy.");
    }
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<DocumentModel, SourceWorldModelEntry> sourceWorldModels = new();
    // The callback is an instance-local deterministic preparation boundary, never a UI callback.
    internal Action<CancellationToken>? PreparingSourceWorldModel { get; set; }
    private static SourceWorldModelEntry PrepareSourceWorldModel(zStudio.Core.AssetDocument document, SourceSceneState build, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var world = document is SourceSceneSnapshot source ? source.World : throw new InvalidDataException("Expected an evaluated source scene.");
        Dictionary<int, WorldNode> slots = [];
        Dictionary<WorldNode, WorldNodeProvenance> provenance = new(ReferenceEqualityComparer.Instance);
        foreach (var (node, slot) in GameZWriter.NodeSlots(world, token))
        {
            token.ThrowIfCancellationRequested();
            slots.Add(slot, node);
            if (build.Provenance.TryGetValue(slot, out var origin)) provenance.Add(node, origin);
        }
        token.ThrowIfCancellationRequested();
        return new(world, slots, provenance, new(slots, provenance, token), SourceSelectionIndex.Build(build.Provenance, token, slots, build.ReadHashes, build.AuthoredHierarchy), build.ReadHashes);
    }
    // Called only after the complete scene comparison proves unchanged geometry/topology/attributes apart from
    // transforms. Retain the previous presentation hashes only if every source occurrence and authored edge still maps.
    // Reloads and structural edits keep the newly evaluated content hashes.
    private static SourceWorldModelEntry? RetainSourceTransformIdentity(SourceWorldModelEntry previous, SourceWorldModelEntry candidate,
        SourceSceneState state, CancellationToken token)
    {
        var selection = SourceSelectionIndex.Build(state.Provenance, token, candidate.Slots, previous.IdentityHashes, state.AuthoredHierarchy);
        var before = previous.Selection;
        if (!SameKeys(before.Identities, selection.Identities) || before.Owners.Count != selection.Owners.Count
            || before.Owners.Any(pair => !selection.Owners.TryGetValue(pair.Key, out int owner) || owner != pair.Value)
            || before.AuthoredChildren.Count != selection.AuthoredChildren.Count
            || before.AuthoredChildren.Any(pair => !selection.AuthoredChildren.TryGetValue(pair.Key, out var children) || !pair.Value.SequenceEqual(children))
            || before.AuthoredNodes.Count != selection.AuthoredNodes.Count
            || before.AuthoredNodes.Any(pair => !selection.AuthoredNodes.TryGetValue(pair.Key, out var node) || node != pair.Value)) return null;
        return candidate with { Selection = selection, IdentityHashes = previous.IdentityHashes };
    }

    private SourceWorldModelEntry SourceWorldModel(DocumentModel doc)
    {
        if (sourceWorldModels.TryGetValue(doc, out var cached)) return cached;
        throw new StudioCommandException("context_changed", "The source world's prepared object model is unavailable. Reopen the world.");
    }

    /// <summary>The world-file node a shown scene node is, or null for a runtime copy (a placed vehicle or pickup instance).</summary>
    private int? SourceObjectNode(int node)
    {
        if (scene?.Mission is not { } mission) return node;
        int source = node >= 0 && node < mission.SourceNodes.Count ? mission.SourceNodes[node] : node;
        return source == node ? node : null;
    }
    /// <summary>What a node of a source world is and where it came from.</summary>
    private SourceObjectState DescribeSourceObject(DocumentModel doc, int node)
    {
        node = EditableSourceSelectionNode(doc, node);
        if (doc.SourceWorld == null || doc.SourceBuild is not { } build) throw new StudioCommandException("unsupported", "This document is not a source world.");
        if (!build.Provenance.TryGetValue(node, out var origin)) throw new StudioCommandException("read_only", $"Scene node {node} has no recorded source.");
        WorldNode? built;
        try { built = SourceWorldModel(doc).Slots.GetValueOrDefault(node); }
        catch (InvalidDataException ex) { throw new StudioCommandException("build_failed", ex.Message); }
        if (built == null) throw new StudioCommandException("stale_record", $"Scene node {node} is not in the built world.");
        // An object whose identity flag is set has the identity transform (LocalMatrix is null for it).
        ObjectTransform? transform = built.Class == WorldNodeClass.Object3D ? ObjectTransform.Of(built) : null;
        List<string> notes = [];
        string source;
        if (origin.ModelFile != null)
        {
            source = $"{Bounded(origin.ModelFile, 256)} node {origin.ModelNode}" + (origin.Load is { } load ? $", loaded by {Bounded(load.Script, 256)} line {load.Line}" : "");
            if (!origin.Database) notes.Add($"A node of the model file {Bounded(origin.ModelFile, 256)}: transform and flag edits apply wherever that file is loaded.");
            else if (origin.Part) notes.Add($"A node of {Bounded(origin.ModelFile, 256)}, a part of the mission database: edits apply to every copy of it the database references.");
        }
        else source = origin.Created is { } created ? $"{Bounded(created.Script, 256)} line {created.Line} ({Bounded(created.Command, 128)})" : "the scripts";
        foreach (var (command, writer) in origin.Writers.OrderBy(w => w.Value.Script).ThenBy(w => w.Value.Line).Take(16))
        {
            int runs = doc.SourceBuild.Executions.GetValueOrDefault((writer.Script, writer.Line));
            notes.Add($"{Bounded(command, 128)}: {Bounded(writer.Script, 256)} line {writer.Line}" + (runs > 1 ? $" (runs {runs} times; edit the script directly)" : ""));
        }
        var whole = SourceObjectEdits.ObjectOf(built, SourceWorldProvenance(doc));
        return new(node, built.Name, built.Class.ToString(), transform, built.Flags, origin, source, notes)
        {
            Parent = built.Parents.FirstOrDefault()?.Name, Object = ReferenceEquals(whole, built) ? null : whole.Name, Zone = built.Zone, ParentZone = built.Parents.FirstOrDefault()?.Zone,
            Model = origin.Database ? ReferencedModel(built, origin, SourceWorldProvenance(doc)) : null,
        };
    }
    /// <summary>
    /// The model file a mission database object references: read by its own reference, or by its one referencing child
    /// (an OpenFlight placement such as tur_101's pturret.flt), whose loaded roots record that holder. Null when it holds
    /// several references or references a part of the mission database, which Model does not replace.
    /// </summary>
    private static string? ReferencedModel(WorldNode built, WorldNodeProvenance origin, IReadOnlyDictionary<WorldNode, WorldNodeProvenance> provenance)
    {
        List<WorldNodeProvenance> models = [];
        foreach (var holder in built.Children.Prepend(built))
        {
            if (!provenance.TryGetValue(holder, out var held) || !string.Equals(held.ModelFile, origin.ModelFile, StringComparison.OrdinalIgnoreCase)) continue;
            if (holder.Children.Select(root => provenance.GetValueOrDefault(root)).FirstOrDefault(loaded => ReferenceEquals(loaded?.ReferencedBy, held) && loaded!.ModelFile != null) is { } model)
                models.Add(model);
        }
        return models is [{ Database: false } only] ? only.ModelFile : null;
    }
    /// <summary>The provenance of the shown world's nodes, by node.</summary>
    private IReadOnlyDictionary<WorldNode, WorldNodeProvenance> SourceWorldProvenance(DocumentModel doc)
        => SourceWorldModel(doc).Provenance;
    /// <summary>The object a structural edit of a shown node applies to, with the world and provenance it is checked against.</summary>
    private SourceObjectTarget SourceObjectTargetFor(DocumentModel doc, int node, SourceWorkspace workspace)
    {
        node = EditableSourceSelectionNode(doc, node);
        if (doc.SourceWorld is not { } session || doc.SourceBuild is not { } build) throw new StudioCommandException("unsupported", "This document is not a source world.");
        var model = SourceWorldModel(doc);
        var built = model.Slots.GetValueOrDefault(node) ?? throw new StudioCommandException("stale_record", $"Scene node {node} is not in the built world.");
        var provenance = SourceWorldProvenance(doc);
        return new(workspace, session.Mission, model.World, built, provenance, build.Executions) { Write = build.WriteInstruction };
    }
    /// <summary>
    /// A node of the shown world by name, refused when no node or several nodes have it, unless they are the copies of one
    /// node of a part of the mission database: an edit of the part's file is the same through any of them. Of those, the one
    /// in the same copy of the part as scene node <paramref name="near"/> (the node an edit moves) is taken, as a parent's
    /// node index would choose it, so the checks against its place agree.
    /// </summary>
    private WorldNode SourceWorldNodeNamedNear(DocumentModel doc, string name, int? near)
    {
        var model = SourceWorldModel(doc);
        var matches = model.World.Nodes.Where(n => n.Name == name).ToList();
        if (matches.Count > 1)
        {
            var provenance = SourceWorldProvenance(doc);
            var first = model.Sources.Groups.GetValueOrDefault(matches[0]);
            if (first != null && matches.All(n => provenance.TryGetValue(n, out var p) && p.Part && p.ModelFile != null
                && ReferenceEquals(model.Sources.Groups.GetValueOrDefault(n), first)))
            {
                int? copy = near is int index && model.Slots.GetValueOrDefault(index) is { } moved
                    && model.Sources.Copies.TryGetValue(SourceObjectEdits.ObjectOf(moved, provenance), out int found) ? found : null;
                return matches.FirstOrDefault(n => copy != null && model.Sources.Copies.GetValueOrDefault(n, -1) == copy) ?? matches[0];
            }
        }
        return matches.Count switch
        {
            0 => throw new StudioCommandException("invalid_argument", $"The world has no node named {Bounded(name, 64)}."),
            1 => matches[0],
            _ => throw SeveralNamed(doc, name, matches),
        };
    }
    /// <summary>
    /// The refusal of a name several nodes share, listing them as Properties can name each: its scene node index (the number
    /// Document scene and the inspection card show, source_world_object_edit's parent), where it stands and where it came from.
    /// Only a refusal formats candidates, at most eight.
    /// </summary>
    private StudioCommandException SeveralNamed(DocumentModel doc, string name, List<WorldNode> matches)
    {
        var slots = GameZWriter.NodeSlots(SourceWorldModel(doc).World);
        var provenance = SourceWorldProvenance(doc);
        string Candidate(WorldNode node)
        {
            List<string> chain = [];
            for (var parent = node.Parents.FirstOrDefault(); parent != null && chain.Count < 4; parent = parent.Parents.FirstOrDefault())
                chain.Add(Bounded(parent.Name, 32));
            chain.Reverse();
            string source = !provenance.TryGetValue(node, out var origin) ? "no recorded source"
                : origin.ModelFile != null ? $"{Bounded(origin.ModelFile, 96)} node {origin.ModelNode}"
                : origin.Created is { } created ? $"{Bounded(created.Script, 96)} line {created.Line}" : "the scripts";
            return $"{(slots.TryGetValue(node, out int slot) ? $"#{slot}" : "a node without a slot")} under {(chain.Count == 0 ? "nothing" : string.Join(" / ", chain))} ({source})";
        }
        return new("invalid_argument", $"Several nodes are named {Bounded(name, 64)}: {string.Join("; ", matches.Take(8).Select(Candidate))}"
            + (matches.Count > 8 ? $"; and {matches.Count - 8} more" : "") + ". Enter the parent as #index (or name #index), the scene node number Document scene shows; zstudio_source_world_object_edit takes it as parent.");
    }
    /// <summary>
    /// The scene node index of the parent Properties names: a node's name (<see cref="SourceWorldNodeNamedNear"/>), or
    /// <c>#index</c>, optionally after the node's name (<c>name #index</c>), for the scene node with that number, as
    /// source_world_object_edit's parent index chooses it; this tells apart nodes that share a name.
    /// </summary>
    private int SourceParentNode(DocumentModel doc, string text, int near)
    {
        int mark = text.LastIndexOf('#');
        var digits = mark < 0 ? default : text.AsSpan(mark + 1);
        string named = mark < 0 ? text : text[..mark];
        // "#index" alone, or after the name and a space: a name such as "a#1" stays a name.
        if (mark < 0 || digits.Length is 0 or > 10 || digits.ContainsAnyExceptInRange('0', '9') || named.Length > 0 && !char.IsWhiteSpace(named[^1]))
            return GameZWriter.NodeSlots(SourceWorldModel(doc).World)[SourceWorldNodeNamedNear(doc, text, near)];
        if (!int.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int index))
            throw new StudioCommandException("invalid_argument", $"#{Bounded(digits.ToString(), 16)} is not a scene node number.");
        var node = SourceWorldModel(doc).Slots.GetValueOrDefault(index) ?? throw new StudioCommandException("stale_record", $"Scene node {index} is not in the built world.");
        named = named.Trim();
        if (named.Length > 0 && named != node.Name)
            throw new StudioCommandException("invalid_argument", $"Scene node #{index} is named {Bounded(node.Name, 64)}, not {Bounded(named, 64)}.");
        return index;
    }
    /// <summary>Plans a delete, copy or re-parenting of the object a node belongs to and rebuilds the world with it.</summary>
    /// <param name="keepsNodes">Whether the plan keeps every node of the files it changes (a copy or a move, not a deletion):
    /// then a change of glTF files alone is checked to leave every script instruction on the same nodes. A change made in
    /// the scripts is always checked, through how it renumbered their lines.</param>
    private Task<DocumentModel> EditSourceStructureAsync(DocumentModel doc, int node, Func<SourceObjectTarget, CancellationToken, SourceEditPlan> plan, CancellationToken token, bool keepsNodes = false)
    {
        node = EditableSourceSelectionNode(doc, node);
        var state = DescribeSourceObject(doc, node);
        List<SourceModelAddition> additions = [];
        List<string> notes = [];
        ScriptLineChanges? verify = null;
        return PrepareSourceWorldEditAsync(doc, $"Editing {state.Object ?? state.Name}", (workspace, ct) =>
        {
            var planned = plan(SourceObjectTargetFor(doc, node, workspace), ct);
            additions.AddRange(planned.Additions); notes.AddRange(planned.Notes);
            bool gltfOnly = planned.Changes.Any(c => c.Relative.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase)) &&
                planned.Changes.All(c => c.Relative.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase) || c.Relative.EndsWith(".glb", StringComparison.OrdinalIgnoreCase));
            verify = planned.Lines ?? (keepsNodes && gltfOnly ? ScriptLineChanges.None : null);
            return workspace.Apply(planned.Label, planned.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), ct);
        }, token, additions, verifyTargets: () => verify, notes: notes);
    }
    /// <summary>
    /// Rounds values a matrix decomposition leaves a hair off (89.99999 → 90), relative to their size: a small authored
    /// scale (0.00105, 0.0199) stays as it is.
    /// </summary>
    private static Vector3 Rounded(Vector3 v)
    {
        static float R(float x) => MathF.Round(x, 3) is var r && MathF.Abs(x) >= 1e-3f && MathF.Abs(x - r) <= 1e-6f * MathF.Max(1, MathF.Abs(x)) ? r : x;
        return new(R(v.X), R(v.Y), R(v.Z));
    }
    private Task<DocumentModel> RenameSourceObjectAsync(DocumentModel doc, int node, string name, CancellationToken token) =>
        EditSourceStructureAsync(doc, node, (target, ct) => SourceObjectEdits.PlanRename(target, name, ct), token, keepsNodes: true);
    /// <summary>
    /// Makes the object reference another model (a project path). The previous model's nodes leave the world, which the plan
    /// refuses while a script acts on one; the new model's names are checked like a copy's, so no instruction finds them instead.
    /// </summary>
    private Task<DocumentModel> ReplaceSourceModelAsync(DocumentModel doc, int node, string model, CancellationToken token) =>
        EditSourceStructureAsync(doc, node, (target, ct) => SourceObjectEdits.PlanReplaceModel(target, model, ct), token, keepsNodes: true);
    private Task<DocumentModel> DeleteSourceObjectAsync(DocumentModel doc, int node, CancellationToken token) =>
        EditSourceStructureAsync(doc, node, (target, ct) => SourceObjectEdits.PlanDelete(target, ct), token);
    private Task<DocumentModel> DuplicateSourceObjectAsync(DocumentModel doc, int node, string name, ObjectTransform? transform, CancellationToken token, bool keepBasis = false) =>
        EditSourceStructureAsync(doc, node, (target, ct) => SourceObjectEdits.PlanDuplicate(target, name, transform, ct, keepBasis), token, keepsNodes: true);
    /// <summary>
    /// Where Copy places a copy: beside the object, its local X plus the object's width along its parent's X (the extent of
    /// its shown meshes, in the parent's coordinates its position is given in). The copy keeps its rotation and scale (only
    /// the position is taken). Null, a copy in place, when the object has no transform or shows nothing in the view.
    /// </summary>
    private ObjectTransform? BesideTransform(DocumentModel doc, int node)
    {
        if (scene == null || shownDocument != doc) return null;
        var whole = SourceObjectTargetFor(doc, node, SourceWorldOf(doc).Workspace).Node;
        int wholeNode = SourceWorldModel(doc).Slots.First(p => ReferenceEquals(p.Value, whole)).Key;
        if (DescribeSourceObject(doc, wholeNode).Transform is not { } accepted || scene.BoundsInParentFrame(wholeNode) is not { } bounds) return null;
        float width = bounds.Max.X - bounds.Min.X;
        if (!float.IsFinite(width) || width <= 0) return null;
        // Only X changes: Y and Z stay exactly as authored.
        float x = ObjectTransform.SnapPosition(accepted.Position + new Vector3(width, 0, 0)).X;
        return float.IsFinite(x) ? accepted with { Position = accepted.Position with { X = x } } : null;
    }
    /// <summary>Moves the object under <paramref name="parent"/> (a scene node index; null for the world).</summary>
    private Task<DocumentModel> ReparentSourceObjectAsync(DocumentModel doc, int node, int? parent, CancellationToken token)
    {
        node = EditableSourceSelectionNode(doc, node);
        if (parent is int selectedParent) parent = EditableSourceSelectionNode(doc, selectedParent);
        WorldNode? into = null;
        if (parent is int p)
        {
            into = SourceWorldModel(doc).Slots.GetValueOrDefault(p) ?? throw new StudioCommandException("stale_record", $"Scene node {p} is not in the built world.");
            if (into.Class == WorldNodeClass.World) into = null;
        }
        return EditSourceStructureAsync(doc, node, (target, ct) => SourceObjectEdits.PlanReparent(target, into, ct), token, keepsNodes: true);
    }
    /// <summary>
    /// The scene node indices of the nodes named <paramref name="name"/> in a rebuilt world, for following a copy: one per copy
    /// of a part the mission database references several times, the one in the copy <paramref name="copyOrigin"/> names (the
    /// original's, <see cref="SourceObjectEdits.CopyKey"/>) first, then the newest.
    /// </summary>
    private List<int> SourceCopiesNamed(DocumentModel doc, string name, WorldNodeProvenance? copyOrigin)
    {
        if (doc.SourceBuild == null) return [];
        var model = SourceWorldModel(doc);
        int? preferred = copyOrigin == null ? null : model.Sources.CopyOf(copyOrigin);
        List<int> copies = [.. model.Slots.Where(p => p.Value.Name == name).Select(p => p.Key).OrderDescending()];
        int same = copies.FindIndex(c => preferred != null && model.Sources.Copies.GetValueOrDefault(model.Slots[c], -1) == preferred);
        if (same > 0) { int first = copies[same]; copies.RemoveAt(same); copies.Insert(0, first); }
        return copies;
    }
    /// <summary>Which copy of its file the object scene node <paramref name="node"/> belongs to is in (<see cref="SourceObjectEdits.CopyKey"/>), or null when unknown.</summary>
    private WorldNodeProvenance? SourceCopyOrigin(DocumentModel doc, int node)
    {
        try
        {
            var provenance = SourceWorldProvenance(doc);
            return SourceWorldModel(doc).Slots.GetValueOrDefault(node) is { } built && provenance.TryGetValue(built, out var origin) ? origin : null;
        }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>Plans one edit of a source world's object against the workspace and rebuilds the world with it.</summary>
    private Task<DocumentModel> EditSourceObjectAsync(DocumentModel doc, int node, Func<SourceWorkspace, SourceObjectState, IReadOnlyDictionary<(string Script, int Line), int>, CancellationToken, SourceEditPlan> plan, CancellationToken token)
    {
        node = EditableSourceSelectionNode(doc, node);
        var state = DescribeSourceObject(doc, node);
        var executions = doc.SourceBuild!.Executions;
        string label = $"Editing {state.Name}";
        List<string> notes = [];
        return PrepareSourceWorldEditAsync(doc, label, (workspace, ct) =>
        {
            var planned = plan(workspace, state, executions, ct);
            notes.AddRange(planned.Notes);
            return workspace.Apply(planned.Label, planned.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), ct);
        }, token, notes: notes);
    }
    /// <summary>
    /// Moves, turns or scales an object. The shown scene draws the object at its new transform at once (see
    /// <see cref="SceneViewport.PreviewObjectTransform"/>) while the sources change and the world rebuilds; an edit that is
    /// refused or taken back puts it back.
    /// </summary>
    private async Task<DocumentModel> MoveSourceObjectAsync(DocumentModel doc, int node, ObjectTransform transform, CancellationToken token)
    {
        var shown = shownDocument == doc ? scene : null;
        long preview = 0;
        try
        {
            // Only an edit that can start now previews (a refused one would replace another edit's preview), and only an
            // object drawn once in this world: copies of a part or of a model file all change, each in its own frame.
            var session = SourceWorldOf(doc);
            RequireSourceWorldIdle(session); RequireNoDrafts(doc, committing: true);
            int edited = EditableSourceSelectionNode(doc, node);
            var state = DescribeSourceObject(doc, edited);
            if (shown != null && state.Transform is { } before && before != transform && SourceObjectEdits.CopiesOf(state.Origin, SourceWorldProvenance(doc).Values).Take(2).Count() == 1)
            {
                StopWorldAnimationForEdit();
                preview = shown.PreviewObjectTransform(edited, before.Matrix(), transform.Matrix(),
                    positionOnly: before.RotationDegrees == transform.RotationDegrees && before.Scale == transform.Scale);
            }
        }
        catch (StudioCommandException) { } // The edit reports the same refusal.
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            // The preview is only presentation: a failure there must neither block the edit nor leave rows moved.
            shown?.ClearObjectTransformPreview(); preview = 0;
        }
        try
        {
            var next = await EditSourceObjectAsync(doc, node, (w, s, e, ct) => SourceObjectEdits.PlanTransform(w, s.Name, s.Origin, e, transform, ct, doc.SourceWorld?.Mission, s.Transform,
                SourceObjectEdits.CopiesOf(s.Origin, SourceWorldProvenance(doc).Values), SourceWorldModel(doc).World, doc.SourceBuild?.WriteInstruction), token);
            if (preview != 0 && ReferenceEquals(next, doc)) shown!.ClearObjectTransformPreview(preview); // Retained publication clears its preview; a no-op still needs restoration.
            return next;
        }
        catch
        {
            if (preview != 0) shown!.ClearObjectTransformPreview(preview);
            throw;
        }
    }
    private Task<DocumentModel> FlagSourceObjectAsync(DocumentModel doc, int node, uint bit, bool on, CancellationToken token) =>
        EditSourceObjectAsync(doc, node, (w, s, e, ct) => SourceObjectEdits.PlanFlag(w, SourceWorldModel(doc).Slots[s.Node], s.Origin, e, bit, on, ct, doc.SourceWorld?.Mission,
            SourceObjectEdits.CopiesOf(s.Origin, SourceWorldProvenance(doc).Values), SourceWorldModel(doc).World, doc.SourceBuild?.WriteInstruction), token);
    private Task<DocumentModel> CommandSourceObjectAsync(DocumentModel doc, int node, string command, IReadOnlyList<string> args, CancellationToken token)
    {
        if (!SourceObjectEdits.PropertyCommands.ContainsKey(command)) throw new StudioCommandException("invalid_argument", $"{command} is not a property command (see zstudio_source_world_object).");
        if (args.Count is 0 or > 8 || args.Any(a => a.Length is 0 or > 64)) throw new StudioCommandException("invalid_argument", "Give 1–8 arguments of up to 64 characters.");
        // The interpreter applies World… commands to worlds, Light… to lights and so on; others would do nothing.
        if (!SourceObjectFields.CommandFits(DescribeSourceObject(doc, node).Class, command)) throw new StudioCommandException("invalid_argument", $"{command} does not apply to a {DescribeSourceObject(doc, node).Class} node.");
        return EditSourceObjectAsync(doc, node, (w, s, e, ct) => SourceObjectEdits.PlanCommand(w, s.Name, s.Origin, e, command, args, ct, doc.SourceWorld?.Mission,
            SourceWorldModel(doc).World, doc.SourceBuild?.WriteInstruction), token);
    }

    /// <summary>Opens Properties for a source world's object; after an edit it follows the object into the rebuilt world.</summary>
    private async Task<bool> ShowSourceObjectPropertiesAsync(DocumentModel doc, int node, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        node = EditableSourceSelectionNode(doc, node);
        var state = DescribeSourceObject(doc, node);
        // A terrain piece is compiled from its recipe; Properties edits the recipe.
        if (state.Origin.Terrain is { } recipe)
            return await ShowTerrainPropertiesAsync(doc, recipe, state.Origin.TerrainSurface, $"Piece {state.Name}: surface {state.Origin.TerrainSurface}, cell {state.Origin.TerrainCell.Column}, {state.Origin.TerrainCell.Row}", null, token);
        return FocusSourceObjectCard(doc, node, focus: true);
    }

    /// <summary>
    /// Whether Properties, which showed <paramref name="window"/>'s content before an edit rebuilt the world, reopens on
    /// <paramref name="next"/>: not when the user closed it meanwhile or another Properties opened.
    /// </summary>
    private bool FollowsProperties(PropertiesWindow? window, DocumentModel next) =>
        window is { ClosedByUser: false } && (propertiesWindow == null || propertiesWindow == window && (window.Document == null || window.Document == next));
    private void RegisterSourceObjectCommands(StudioCommands r)
    {
        var scopeParameter = P("scope", "string", "object (default) targets the whole picked object; node targets the exact source node, as a child selected in the World tree. Unsupported part operations are refused.", false, "object", "node");
        Register(r, "source_world_object", "Describe a world object of a source world: its name, class, the glTF model a mission database object references (model), local transform (position, rotation in degrees about Y then X then Z, scale), node flags, its built zone (an ID or any) with altitude_zone_check, zone_inherited when its source node inherits the zone, and zone_source (the node, reference placement, ancestor or load root that supplied it), and where it came from — the glTF file and node it was imported from (origin.part marks a part of the mission database, a file it references and copies wherever it does), or the script instruction that created it — with the instruction that last set each property and how often it ran. Node indices are those of zstudio_scene_nodes for the shown world. Inspection previews bound script/model paths to 256 characters and tokens to 128, with truncation flags; edits still target document and node identities.", false,
            [DocumentParameter, new("node", "integer", "Scene node index.", true, Minimum: 0, Maximum: int.MaxValue), scopeParameter], a =>
        {
            var d = TargetDocument(a); var state = DescribeSourceObject(d, CommandSourceNode(d, a));
            var origin = state.Origin;
            return Result(new
            {
                document = d.SessionId, revision = d.Revision, @object = state.Json(),
                origin = new
                {
                    modelFile = origin.ModelFile == null ? null : Bounded(origin.ModelFile, 256), modelFileTruncated = origin.ModelFile?.Length > 256, modelNode = origin.ModelFile == null ? (int?)null : origin.ModelNode, database = origin.Database, part = origin.Part,
                    load = Instruction(origin.Load), created = Instruction(origin.Created), attached = Instruction(origin.Attached),
                    writers = origin.Writers.Take(32).ToDictionary(w => w.Key, w => Instruction(w.Value))
                },
                // Those its source can set: only an object's, and a script has no command for every flag (ClipTo).
                editableFlags = SourceObjectFields.EditableFlags.Where(f => SourceObjectEdits.FlagSettable(state.NodeClass, origin, f.Bit)).Select(f => new { bit = $"0x{f.Bit:X}", label = f.Label, on = (state.Flags & f.Bit) != 0 }).ToArray(),
                applied = origin.Applied.Take(32).Select(Instruction).ToArray(), appliedCount = origin.Applied.Count,
                propertyCommands = SourceObjectEdits.PropertyCommands.Select(p => new { command = p.Key, arguments = p.Value, set = origin.Writers.TryGetValue(p.Key, out var w) ? SourceInstructionTokens(w, 1) : null, truncated = w != null && (w.Tokens.Count > 17 || w.Tokens.Skip(1).Take(16).Any(t => t.Length > 128)) }).Where(p => p.set != null || SourceObjectFields.CommandFits(state.Class, p.command)).ToArray()
            });
            object? Instruction(SourceInstruction? i) => SourceInstructionProjection(i, i == null ? 0 : d.SourceBuild!.Executions.GetValueOrDefault((i.Script, i.Line)));
        });
        RegisterJob(r, "source_world_command", "Set a property a script command makes on a node of a source world (fog of the world, a light's color, ranges or orientation, a camera's clip or field of view) as one undoable change of the project's workspace: the instruction that last set it changes, or a new one follows the instruction that created the node. zstudio_source_world_object lists the commands that fit the node and the arguments each set one has. The scene is evaluated in memory and the result is the accepted document/revision. Save writes the owning sources; no archives are created.",
            [DocumentParameter, RevisionParameter, new("node", "integer", "Scene node index.", true, Minimum: 0, Maximum: int.MaxValue), scopeParameter,
             P("command", "string", "Property command.", true, [.. SourceObjectEdits.PropertyCommands.Keys]),
             new("arguments", "array", "The command's arguments as script tokens (numbers as text).", true, Items: new("", "string", "One argument."), MinItems: 1, MaxItems: 8)], true,
            async (a, token) =>
            {
                var d = TargetDocument(a, true);
                var args = (a["arguments"] as JsonArray)!.Select(v => v!.GetValue<string>()).ToArray();
                return Result(new { document = DocumentState(await CommandSourceObjectAsync(d, CommandSourceNode(d, a), Text(a, "command"), args, token)) });
            });
        RegisterJob(r, "source_world_object_edit", "Edit a world object of a source world as one undoable change of the project's workspace: move, rotate or scale it, set or clear one of its node flags, copy it, delete it, move it under another parent, rename a mission database object (rename; a group or reference holder the build removes, shown only in the source tree, is renamed by sourceFile and gltfNode instead of node; refused while a script finds it by name, or while one of the mission's animations is bound to its name, such as destroy_turret, whose NAME list binds to the first listed name the world has, until the new name is added to that list; also while the mission's animations, compiled with the new name, would be rejected), or make one reference another glTF model (model: the reference it or its one referencing child holds, keeping name, place, flags and zone; the placement's zone overrides for the previous model are removed). The edit changes the source that placed the object: the transform or extras of its glTF node (the mission database or one of its parts, which every copy of the part uses, or a model file, which every load of it uses), or the script instruction that set the value (refused when it ran more than once or takes the value from a macro); a value nothing set yet is added as an instruction after the one that created the object. With default scope object, copying, deleting and re-parenting apply to the whole object: a node of a model a script loaded stands for that load (zstudio_source_world_object names it). With scope node, the exact child is targeted and unsupported part operations are refused. A mission database object is copied, deleted or moved in its glTF file; a script's object is deleted by turning its instructions into comments, copied by loading it again before the world is written, and moved by attaching it there. The scene is evaluated in memory and the result is the accepted document/revision (with the copy's node index); a change that cannot be evaluated is refused. Save writes the owning sources; no archives are created.",
            [DocumentParameter, RevisionParameter, new("node", "integer", "Scene node index. Omit it to rename a source-only node (sourceFile with gltfNode).", Minimum: 0, Maximum: int.MaxValue), scopeParameter,
             P("sourceFile", "string", "rename without node: the project path of the glTF file holding a source-only node, as scene_tree's sourceFile gives it (a group or reference holder the build removes, so the game never uses its name)."),
             new("gltfNode", "integer", "rename without node: that node's glTF index (scene_tree's gltfNode).", Minimum: 0, Maximum: int.MaxValue),
             P("action", "string", "What to do; inferred from the other arguments when omitted (a transform or a flag).", false, ["transform", "flag", "duplicate", "delete", "parent", "rename", "model"]),
             new("position", "object", "New local position (or the copy's).", Properties: [P("x", "number", "X.", true), P("y", "number", "Y.", true), P("z", "number", "Z.", true)]),
             new("rotationDegrees", "object", "New local rotation in degrees (about Y, then X, then Z, as Object3DRotate).", Properties: [P("x", "number", "X.", true), P("y", "number", "Y.", true), P("z", "number", "Z.", true)]),
             new("scale", "object", "New local scale.", Properties: [P("x", "number", "X.", true), P("y", "number", "Y.", true), P("z", "number", "Z.", true)]),
             P("flag", "string", "Node flag to set or clear. Enabling CanModify (0x10000) or ClipTo (0x20000) clears the other mode atomically; a source that cannot express that change is refused.", false, SourceObjectFields.EditableFlags.Select(f => $"0x{f.Bit:X}").ToArray()),
             P("on", "boolean", "Whether the flag is set; required with flag. Clearing the selected crater mode leaves neither mode. For a glTF-owned mode this removes declient; a script-owned setting changes its instruction."),
             P("beside", "boolean", "duplicate: place the copy beside the original, as the card's Copy does (its local X plus the object's width along its parent's X, rotation and scale kept); not with position, rotationDegrees or scale."),
             new("name", "string", "The copy's node name (duplicate) or the object's new name (rename): 1–32 printable characters without spaces, commas, quotes or # % ;, used by no node of the world."),
             new("model", "string", "Project path of a .gltf model under data/ the object should reference (model), as zstudio_source_world_models lists it, for example data/common/models/sonictur.gltf."),
             new("parent", "integer", "The new parent's scene node index (parent); -1 or the world's node makes it a root of the world (a node of a part of the mission database: a root of the part).", Minimum: -1, Maximum: int.MaxValue)], true,
            async (a, token) =>
            {
                var d = TargetDocument(a, true);
                if (a["node"] == null)
                {
                    if (a["action"]?.GetValue<string>() != "rename" || a["sourceFile"] == null || a["gltfNode"] == null || a["name"] == null)
                        throw new StudioCommandException("invalid_argument", "Give node, or for a source-only node action rename with sourceFile, gltfNode and name.");
                    return Result(new { document = DocumentState(await RenameRemovedSourceNodeAsync(d, Text(a, "sourceFile"), Int(a, "gltfNode"), null, Text(a, "name"), token)), node = (int?)null });
                }
                if (a["sourceFile"] != null || a["gltfNode"] != null) throw new StudioCommandException("invalid_argument", "Give node or sourceFile with gltfNode, not both.");
                bool moves = a["position"] != null || a["rotationDegrees"] != null || a["scale"] != null;
                string action = a["action"] is null ? (moves ? "transform" : "flag") : Text(a, "action");
                int node = CommandSourceNode(d, a, action is "duplicate" or "delete" or "parent" or "rename" or "model");
                DocumentModel next;
                if (action == "duplicate")
                {
                    if (a["name"] is null) throw new StudioCommandException("invalid_argument", "Give the copy's name.");
                    string name = Text(a, "name");
                    ObjectTransform? placed = null;
                    bool beside = a["beside"] != null && Flag(a, "beside");
                    if (beside && moves) throw new StudioCommandException("invalid_argument", "Give beside or a transform for the copy, not both.");
                    if (beside) placed = BesideTransform(d, node) ?? throw new StudioCommandException("unsupported", "This object shows nothing in the view to place a copy beside; give its position.");
                    else if (moves)
                    {
                        // Omitted values come from the object copied (a part stands for the object that loaded it).
                        var whole = SourceObjectTargetFor(d, node, SourceWorldOf(d).Workspace).Node;
                        int wholeNode = SourceWorldModel(d).Slots.First(p => ReferenceEquals(p.Value, whole)).Key;
                        var current = DescribeSourceObject(d, wholeNode).Transform ?? throw new StudioCommandException("unsupported", "Only object nodes have a transform.");
                        placed = new(Vector(a, "position") ?? current.Position, Vector(a, "rotationDegrees") ?? Rounded(current.RotationDegrees), Vector(a, "scale") ?? Rounded(current.Scale));
                    }
                    // Only a position: a copy in the database keeps the original's exact basis (and any mirroring).
                    bool keepBasis = beside || moves && a["rotationDegrees"] is null && a["scale"] is null;
                    var copyOrigin = SourceCopyOrigin(d, node);
                    next = await DuplicateSourceObjectAsync(d, node, name, placed, token, keepBasis);
                    // A part's copy is made in every copy of the part: copy is the one beside the object edited, copies all.
                    var copies = SourceCopiesNamed(next, name, copyOrigin);
                    return Result(new { document = DocumentState(next), copy = copies.Count > 0 ? copies[0] : (int?)null, copies = copies.Take(256).ToArray() });
                }
                if (action == "delete") return Result(new { document = DocumentState(await DeleteSourceObjectAsync(d, node, token)) });
                if (action == "rename")
                {
                    if (a["name"] is null) throw new StudioCommandException("invalid_argument", "Give the object's new name.");
                    string name = Text(a, "name");
                    var origin = SourceCopyOrigin(d, node);
                    next = await RenameSourceObjectAsync(d, node, name, token);
                    return Result(new { document = DocumentState(next), node = SourceCopiesNamed(next, name, origin).Cast<int?>().FirstOrDefault() });
                }
                if (action == "model")
                {
                    if (a["model"] is null) throw new StudioCommandException("invalid_argument", "Give the model's project path.");
                    var origin = SourceCopyOrigin(d, node);
                    string name = DescribeSourceObject(d, node).Name;
                    next = await ReplaceSourceModelAsync(d, node, Text(a, "model"), token);
                    return Result(new { document = DocumentState(next), node = SourceCopiesNamed(next, name, origin).Cast<int?>().FirstOrDefault() });
                }
                if (action == "parent")
                {
                    if (a["parent"] is null) throw new StudioCommandException("invalid_argument", "Give the new parent's node index, or -1 for the world.");
                    int parent = Int(a, "parent");
                    if (parent >= 0 && Text(a, "scope", "object") == "object") parent = SourceSelectionOwner(d, parent);
                    return Result(new { document = DocumentState(await ReparentSourceObjectAsync(d, node, parent < 0 ? null : parent, token)) });
                }
                if ((action == "transform") != moves || (action == "flag") != (a["flag"] != null)) throw new StudioCommandException("invalid_argument", "Give a transform (position, rotationDegrees, scale) or one flag.");
                long contentBefore = SourceWorldOf(d).Workspace.ContentRevision;
                if (moves)
                {
                    var current = DescribeSourceObject(d, node).Transform ?? throw new StudioCommandException("unsupported", "Only object nodes have a transform.");
                    ObjectTransform requested = new(Vector(a, "position") ?? current.Position, Vector(a, "rotationDegrees") ?? current.RotationDegrees, Vector(a, "scale") ?? current.Scale);
                    next = await MoveSourceObjectAsync(d, node, requested, token);
                }
                else
                {
                    if (a["on"] == null) throw new StudioCommandException("invalid_argument", "Give on with flag.");
                    uint bit = Convert.ToUInt32(Text(a, "flag")[2..], 16);
                    next = await FlagSourceObjectAsync(d, node, bit, Flag(a, "on"), token);
                }
                // Retained transforms keep the document even when they accept a source transaction.
                return Result(new { document = DocumentState(next), unchanged = SourceWorldOf(next).Workspace.ContentRevision == contentBefore });
            });
    }
    private int CommandSourceNode(DocumentModel doc, JsonObject args, bool structural = false)
    {
        int node = Int(args, "node");
        if (Text(args, "scope", "object") == "node") return EditableSourceSelectionNode(doc, node);
        node = EditableSourceSelectionNode(doc, SourceSelectionOwner(doc, node));
        if (!structural) return node;
        var model = SourceWorldModel(doc);
        if (!model.Slots.TryGetValue(node, out var built)) throw new StudioCommandException("stale_record", "The source object is no longer available.");
        var whole = SourceObjectEdits.ObjectOf(built, model.Provenance);
        return model.Slots.First(p => ReferenceEquals(p.Value, whole)).Key;
    }
    internal static string[] SourceInstructionTokens(SourceInstruction instruction, int skip = 0) => instruction.Tokens.Skip(skip).Take(16).Select(t => Bounded(t, 128)).ToArray();
    internal static object? SourceInstructionProjection(SourceInstruction? i, int runs) => i == null ? null : new
    {
        script = Bounded(i.Script, 256), line = i.Line, command = Bounded(i.Command, 128), tokens = SourceInstructionTokens(i), runs,
        truncated = i.Script.Length > 256 || i.Command.Length > 128 || i.Tokens.Count > 16 || i.Tokens.Take(16).Any(t => t.Length > 128)
    };
    private static Vector3? Vector(JsonObject a, string name) => a[name] is JsonObject v ? new Vector3(Coordinate(v, "x"), Coordinate(v, "y"), Coordinate(v, "z")) : null;
}
