using System.Runtime.CompilerServices;
using zStudio.Automation;
using zStudio.Core;
using zStudio.Rendering;

namespace zStudio.Desktop;

internal sealed record SourceMapPartRow(int Node, int? Parent, string Name, string Kind,
    bool DefaultVisible, bool Visible, bool EffectiveVisible, bool Editable);

public partial class MainWindow
{
    private sealed class SourceMapPartState { internal IReadOnlyDictionary<string, bool> Overrides = new Dictionary<string, bool>(StringComparer.Ordinal); }
    private sealed record SourceMapPartSnapshot(SourceMapSelection Selection, IReadOnlyDictionary<string, bool> Overrides,
        IReadOnlyDictionary<int, bool> Nodes);
    private readonly ConditionalWeakTable<SourceWorldSession, SourceMapPartState> sourceMapPartStates = new();
    private readonly ConditionalWeakTable<SceneViewport, SourceMapPartSnapshot> sourceMapPartSnapshots = new();
    private bool sourceMapPartsBusy;

    private void ConfigureSourceMapPresentation(SceneViewport viewport, DocumentModel doc, MissionSceneContext? mission,
        IReadOnlyDictionary<string, bool>? requested = null)
    {
        viewport.SelectionOwner = null; viewport.NodeVisibility = null;
        sourceMapPartSnapshots.Remove(viewport);
        if (doc.SourceWorld is not { } session || mission == null) return;
        var projection = SourceSelection(doc, mission.Scene) ?? throw new StudioCommandException("not_ready", "Source-map selection is not prepared.");
        var values = requested ?? sourceMapPartStates.GetOrCreateValue(session).Overrides;
        Dictionary<string, bool> retained = new(StringComparer.Ordinal);
        Dictionary<int, bool> nodes = [];
        foreach (var (node, key) in projection.PartIdentities)
            if (projection.IsBoundary(projection.Owner(node)) && node != projection.Owner(node) && values.TryGetValue(key, out bool visible))
            { retained[key] = visible; nodes[node] = visible; }
        sourceMapPartSnapshots.Add(viewport, new(projection, retained, nodes));
        viewport.SelectionOwner = projection.Owner;
        viewport.NodeVisibility = node => nodes.TryGetValue(node, out bool visible) ? visible : null;
    }

    private void PublishSourceMapParts(SceneViewport viewport, DocumentModel doc)
    {
        if (doc.SourceWorld is { } session && sourceMapPartSnapshots.TryGetValue(viewport, out var snapshot))
            sourceMapPartStates.GetOrCreateValue(session).Overrides = snapshot.Overrides;
    }

    private void ClearSourceMapParts(DocumentModel doc)
    {
        if (doc.SourceWorld is { } session) sourceMapPartStates.Remove(session);
    }

    private IReadOnlyList<SourceMapPartRow> DescribeSourceMapParts(int owner)
    {
        if (scene == null || shownDocument?.SourceWorld == null || !sourceMapPartSnapshots.TryGetValue(scene, out var snapshot) ||
            !snapshot.Selection.IsBoundary(owner) || !snapshot.Selection.MemberSlots.TryGetValue(owner, out var members)) return [];
        var data = snapshot.Selection.Scene;
        var memberSet = members.ToHashSet();
        List<SourceMapPartRow> rows = [];
        foreach (int node in members)
        {
            if (node == owner) continue;
            var part = data.Nodes[node];
            bool baseline = part.Class is not ("object3d" or "lod") || (part.Metadata.UInt("flags") & 4) != 0;
            int[] parents = part.Parents.Where(p => p != owner && p != node && memberSet.Contains(p)).Distinct().Take(2).ToArray();
            rows.Add(new(node, parents.Length == 1 ? parents[0] : null, JsonData.ShownText(part.Name, 256), part.Class,
                baseline, snapshot.Nodes.GetValueOrDefault(node, baseline), scene.VisibleSceneNodes.Contains(node),
                snapshot.Selection.PartIdentities.ContainsKey(node)));
        }
        return rows;
    }

    private async Task SetSourceMapPartVisibilityAsync(int owner, int? part, bool? visible, CancellationToken token = default)
    {
        if (sourceMapPartsBusy || staticRefresh != null || sourceWorkspaceBusy || shownDocument?.SourceWorld?.IsRebuilding == true)
            throw new StudioCommandException("busy", "Wait for the current map update before changing its parts.");
        if (scene == null || shownDocument is not { SourceWorld: not null } doc || shownAsset == null ||
            !sourceMapPartSnapshots.TryGetValue(scene, out var snapshot) || !snapshot.Selection.IsBoundary(owner) ||
            !snapshot.Selection.MemberSlots.TryGetValue(owner, out var members))
            throw new StudioCommandException("stale_record", "The referenced object is no longer available.");
        if (scene.IsFlyActive || scene.IsPickupDragging) throw new StudioCommandException("busy", "Finish the drag or exit Fly before changing parts.");
        RequireNoDrafts(doc);
        if (part != null && (visible == null || part == owner || !members.Contains(part.Value) || !snapshot.Selection.PartIdentities.ContainsKey(part.Value)))
            throw new StudioCommandException("stale_record", "Choose a uniquely identified part of this object and its visibility.");
        Dictionary<string, bool> next = new(snapshot.Overrides, StringComparer.Ordinal);
        if (part is int changed)
        {
            string key = snapshot.Selection.PartIdentities[changed];
            var row = DescribeSourceMapParts(owner).First(r => r.Node == changed);
            if (visible == row.DefaultVisible) next.Remove(key); else next[key] = visible!.Value;
        }
        else foreach (int member in members)
            if (snapshot.Selection.PartIdentities.TryGetValue(member, out string? key)) next.Remove(key);
        sourceMapPartsBusy = true;
        try
        {
            using var scope = PreviewOperation.Begin(token);
            if (await RefreshStaticSceneAsync(doc, shownAsset, next) == null)
                throw new StudioCommandException("context_changed", "The part update did not publish; the previous presentation was retained.");
        }
        finally { sourceMapPartsBusy = false; }
    }

    private void RegisterSourceMapPartCommands(StudioCommands commands)
    {
        Register(commands, "source_map_parts", "Read a referenced map object's editor-only part hierarchy. Children are paged. Names are bounded to 256 characters. Visible is the part's own gate; effectiveVisible also includes ancestors, LOD and mission placement. No source flags are changed.", false,
            [PreviewParameter, P("owner", "integer", "Selection owner from scene_tree; negative IDs denote source-only reference groups.", true),
                P("parent", "integer", "Part whose direct children to list; omit for roots."), .. PageParameters], a =>
        {
            TargetViewport(a); int owner = Int(a, "owner");
            if (scene == null || !sourceMapPartSnapshots.TryGetValue(scene, out var snapshot) || !snapshot.Selection.IsBoundary(owner))
                throw new StudioCommandException("stale_record", "The referenced map object is unavailable.");
            var rows = DescribeSourceMapParts(owner);
            int? parent = a["parent"] == null ? null : Int(a, "parent");
            if (parent != null && !rows.Any(r => r.Node == parent)) throw new StudioCommandException("stale_record", "Part parent unavailable.");
            var page = Page(rows.Where(r => r.Parent == parent), a, r => r.Name);
            return Result(new { preview = previewId, owner, document = shownDocument?.SessionId, revision = shownDocument?.Revision, children = page.Data });
        });
        RegisterJob(commands, "source_map_parts_visibility", "Set one referenced-object part's preview visibility or reset that object's part overrides. Rebuilds the preview asynchronously while retaining the current scene on failure. Editor-only: no source edit, dirty state or undo entry.",
            [PreviewParameter, DocumentParameter, RevisionParameter, P("owner", "integer", "Selection owner from scene_tree.", true),
                P("action", "string", "Visibility action.", true, "set", "reset"), P("part", "integer", "Physical part ID from source_map_parts; required for set."),
                P("visible", "boolean", "Part gate; required for set.")], false, async (a, token) =>
        {
            TargetViewport(a); var doc = TargetDocument(a, true);
            if (doc != shownDocument) throw new StudioCommandException("stale_document", "The requested map is not the displayed map.");
            string action = Text(a, "action");
            if (action == "set" && (a["part"] == null || a["visible"] == null) || action == "reset" && (a["part"] != null || a["visible"] != null))
                throw new StudioCommandException("invalid_argument", "Set requires part and visible; reset takes neither.");
            await SetSourceMapPartVisibilityAsync(Int(a, "owner"), action == "set" ? Int(a, "part") : null,
                action == "set" ? Flag(a, "visible") : null, token);
            return Result(new { preview = previewId, document = doc.SessionId, revision = doc.Revision });
        });
    }
}
