using System.Numerics;
using System.Text.Json.Nodes;
using zStudio.Core.Gltf;
using zStudio.Core.Worlds;

namespace zStudio.Core.Sources;

/// <summary>Only explicitly changed fields of one source object. Null vectors retain their authored values.</summary>
public sealed record SourceObjectPatch
{
    public string? Name { get; init; }
    public string? Model { get; init; }
    public bool Reparent { get; init; }
    public WorldNode? Parent { get; init; }
    public Vector3? Position { get; init; }
    public Vector3? RotationDegrees { get; init; }
    public Vector3? Scale { get; init; }
    public IReadOnlyDictionary<uint, bool> Flags { get; init; } = new Dictionary<uint, bool>();
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Commands { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
    /// <summary>The selected node's zone and altitude zone check, written where its occurrence keeps them (see <see cref="SourceZoneEdits"/>).</summary>
    public SourceObjectZoneEdit? Zone { get; init; }
}

/// <summary>A zone change of one exact scene node of <paramref name="Build"/>; <paramref name="SharedScope"/> consents to changing every use of a shared assignment.</summary>
public sealed record SourceObjectZoneEdit(SourceSceneState Build, int SceneNode, SourceZoneAssignment Assignment, bool SharedScope);

public static partial class SourceObjectEdits
{
    /// <summary>Compose edits against one build, preserving script anchors and merging changes within each source file.</summary>
    public static SourceEditPlan PlanPatch(SourceObjectTarget target, SourceObjectPatch patch, CancellationToken token = default)
    {
        if (patch.Flags.Count > 16 || patch.Commands.Count > PropertyCommands.Count) throw new InvalidDataException("Too many object fields.");
        if (patch.Flags.GetValueOrDefault(0x10000u) && patch.Flags.GetValueOrDefault(0x20000u)) throw new InvalidDataException("Choose one crater mode.");
        var workspace = target.Workspace; var node = target.Node; var origin = target.Origin;
        Dictionary<string, byte[]> files = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ScriptEdit> scripts = new(StringComparer.OrdinalIgnoreCase);
        List<string> notes = []; List<SourceModelAddition> additions = [];
        ObjectTransform original = ObjectTransform.Of(node);
        Matrix4x4? reparented = null;
        ObjectTransform? reparentedStored = null;
        if (patch.Reparent)
        {
            var reparent = PlanReparent(target, patch.Parent, token);
            Add(reparent); reparentedStored = reparent.ReparentedTransform;
            if (origin.ModelFile is { } file && files.TryGetValue(file, out var bytes))
            {
                var root = JsonNode.Parse(bytes)!;
                reparented = GltfNodeEdits.Local((JsonObject)root["nodes"]![origin.ModelNode]!);
            }
            else
            {
                var parent = patch.Parent ?? target.World.Nodes.Single(n => n.Class == WorldNodeClass.World);
                if (!Matrix4x4.Invert(WorldMatrix(parent, token), out var inverse)) throw new InvalidDataException("The parent transform is singular.");
                reparented = FiniteTransform(WorldMatrix(node, token) * inverse);
            }
        }
        var retained = reparentedStored ?? (reparented is { } matrix ? ObjectTransform.FromMatrix(matrix) : original);
        Vector3 Changed(Vector3? requested, Vector3 before, Vector3 after)
        {
            if (requested is not { } value) return after;
            return new(value.X != before.X ? value.X : after.X, value.Y != before.Y ? value.Y : after.Y, value.Z != before.Z ? value.Z : after.Z);
        }
        ObjectTransform requested = new(Changed(patch.Position, original.Position, retained.Position),
            Changed(patch.RotationDegrees, original.RotationDegrees, retained.RotationDegrees), Changed(patch.Scale, original.Scale, retained.Scale));
        if (patch.Position != null || patch.RotationDegrees != null || patch.Scale != null)
        {
            var checkedTransform = reparented != null && origin.ModelFile != null
                ? original with { Position = patch.Position != null ? requested.Position : original.Position, RotationDegrees = patch.RotationDegrees != null ? requested.RotationDegrees : original.RotationDegrees, Scale = patch.Scale != null ? requested.Scale : original.Scale }
                : requested;
            Add(PlanTransform(workspace, node.Name, origin, target.Executions, checkedTransform, token, target.Mission, original,
                CopiesOf(origin, target.Provenance.Values), target.World, target.Write));
            if (reparented is { } local && origin.ModelFile is { } file)
            {
                if ((patch.RotationDegrees != null || patch.Scale != null) && HasShear(local)) throw new InvalidDataException("The new parent requires a sheared transform; change rotation and scale in Blender.");
                var root = (JsonObject)JsonNode.Parse(files[file])!;
                var hierarchy = new GltfNodeEdits.Hierarchy(root, token);
                foreach (int copy in GltfNodeEdits.InstanceCopies(hierarchy, origin.ModelNode))
                {
                    var result = patch.RotationDegrees != null || patch.Scale != null ? requested.Matrix() : local;
                    result.Translation = requested.Position;
                    GltfNodeEdits.SetLocal((JsonObject)root["nodes"]![copy]!, result);
                }
                files[file] = GltfJson.Write(root, true, token);
            }
        }
        // Clearing first lets the final selected crater mode win, while all provenance checks still run.
        foreach (var (bit, on) in patch.Flags.OrderBy(p => p.Value).ThenBy(p => p.Key))
            Add(PlanFlag(workspace, node, origin, target.Executions, bit, on, token, target.Mission, CopiesOf(origin, target.Provenance.Values), target.World, target.Write));
        foreach (var (command, args) in patch.Commands)
        {
            string prefix = node.Class switch { WorldNodeClass.World => "World", WorldNodeClass.Light => "Light", WorldNodeClass.Camera => "Camera", WorldNodeClass.Display => "Display", _ => "\0" };
            if (!PropertyCommands.ContainsKey(command) || !command.StartsWith(prefix, StringComparison.Ordinal) || args.Count is 0 or > 8 || args.Any(a => a.Length is 0 or > 64))
                throw new InvalidDataException("Unsupported object property command or arguments.");
            Add(PlanCommand(workspace, node.Name, origin, target.Executions, command, args, token, target.Mission, target.World, target.Write));
        }
        if (patch.Zone is { } zone)
        {
            // A model change replaces the nodes a zone is written on; apply the two separately.
            if (patch.Model != null || patch.Reparent) throw new InvalidDataException("Apply the zone change separately from a model or parent change.");
            var zones = SourceZoneEdits.PlanAssignments(workspace, zone.Build, [new(zone.SceneNode)], zone.Assignment, zone.SharedScope, token);
            Add(new SourceEditPlan(zones.Label, zones.Changes, node.Name, []));
        }
        if (patch.Model is { } model) Add(PlanReplaceModel(target, model, token));
        // A rename tells the rebuild comparison the node's old name (see ScriptLineChanges.Renamed).
        ScriptLineChanges? renamed = null;
        if (patch.Name is { } name) { var plan = PlanRename(target, name, token); renamed = plan.Lines; Add(plan); }
        foreach (var edit in scripts.Values)
            foreach (var (file, bytes) in edit.Changes()) files[file] = bytes;
        foreach (var (file, bytes) in files.Where(p => p.Key.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase)))
        {
            var parsed = WorldAssembler.ReadModel(bytes, file, (path, remaining) => files.GetValueOrDefault(path) ?? workspace.Read(path, token, ProjectReadLimits.Bytes(remaining))
                ?? throw new InvalidDataException($"Missing buffer {path}."), token);
            WorldGltf.ValidateSupported(parsed, file);
        }
        return new($"Edit {node.Name}", files.Select(p => (p.Key, p.Value)).ToArray(), node.Name, notes.Distinct().ToArray())
        { Lines = LinesOf(scripts.Values).WithRename(renamed), Scripts = scripts.Values.ToArray(), Additions = additions };

        void Add(SourceEditPlan plan)
        {
            token.ThrowIfCancellationRequested(); notes.AddRange(plan.Notes); additions.AddRange(plan.Additions);
            foreach (var edit in plan.Scripts)
                if (scripts.TryGetValue(edit.Script, out var current)) current.Merge(edit); else scripts.Add(edit.Script, edit);
            foreach (var (file, bytes) in plan.Changes)
            {
                if (plan.Scripts.Any(e => e.Script.Equals(file, StringComparison.OrdinalIgnoreCase))) continue;
                if (!files.TryGetValue(file, out var current)) { files.Add(file, bytes); continue; }
                if (!file.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Cannot combine changes to {file}.");
                var baseline = workspace.Read(file, token) ?? throw new InvalidDataException($"Missing source {file}.");
                var combined = MergeJson(JsonNode.Parse(baseline), JsonNode.Parse(bytes), JsonNode.Parse(current), token);
                files[file] = GltfJson.Write(combined!, baseline.AsSpan(0, Math.Min(4096, baseline.Length)).Contains((byte)'\n'), token);
            }
        }
    }

    // Plans use the same immutable baseline. Only changed members are applied; independent fields never overwrite one another.
    private static JsonNode? MergeJson(JsonNode? before, JsonNode? after, JsonNode? current, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (JsonNode.DeepEquals(before, after)) return current;
        if ((before == null || before is JsonObject) && after is JsonObject b && current is JsonObject c)
        {
            var a = before as JsonObject ?? new JsonObject();
            foreach (string key in a.Select(p => p.Key).Union(b.Select(p => p.Key)).ToArray())
            {
                if (!b.ContainsKey(key)) { c.Remove(key); continue; }
                var merged = MergeJson(a[key], b[key], c[key], token);
                if (!ReferenceEquals(merged, c[key])) c[key] = merged;
            }
            return c;
        }
        if (before is JsonArray x && after is JsonArray y && current is JsonArray z && x.Count == y.Count && x.Count == z.Count)
        {
            for (int i = 0; i < x.Count; i++) { var merged = MergeJson(x[i], y[i], z[i], token); if (!ReferenceEquals(merged, z[i])) z[i] = merged; }
            return z;
        }
        return after?.DeepClone();
    }
}
