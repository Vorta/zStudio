using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Worlds;
using System.Text.Json.Nodes;

namespace Recoil.Zbd.Core.Sources;

/// <summary>Selected model content cannot be read or decoded to prepare a source-world build.</summary>
public sealed class SourceModelBuildException(string message, Exception innerException) : IOException(message, innerException) { }

public static partial class SourceWorlds
{
    // A model brought from another map carries its reference bindings with it. Copy the dependency
    // closure into the receiving map's source transaction, never make builds consult another map.
    private static SourceMapZones? AdditionMap(SourceWorkspace workspace, string? mission, string model,
        CancellationToken token, out bool changed)
    {
        changed = false;
        model = SourceWorkspace.Normalize(model);
        byte[]? existing = mission == null ? null : workspace.Read(SourceMapZones.PathForMission(mission), token, SourceMapZones.MaximumBytes);
        SourceMapZones own = existing == null ? new([]) : SourceMapZones.Parse(existing, token);
        if (own.TryGetAsset(model, out _)) return own;

        long remaining = (128L << 20) - (existing?.Length ?? 0);
        long work = 128L << 20;
        void Reserve(long amount)
        {
            token.ThrowIfCancellationRequested();
            if (amount < 0 || amount > work) throw new InvalidDataException("Adding this model exceeds the dependency inspection work allowance.");
            work -= amount;
        }
        static T ReadBuildContent<T>(string geometry, Func<T> read)
        {
            try { return read(); }
            catch (SourceFileChangedException) { throw; }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                throw new SourceModelBuildException($"The model {JsonData.ShownText(geometry)} cannot be built: {ex.Message}", ex);
            }
        }
        string[] components = model.Split('/');
        // Mission-local geometry has an explicit owner. A common file may have several map profiles;
        // accepting an arbitrary one would silently choose that map's object references and zones.
        bool local = components.Length > 2 && MissionName().IsMatch(components[1]);
        IEnumerable<string> candidates = local
            ? [components[1].ToLowerInvariant()]
            : SourceProject.MissionFolders(workspace.Root, token).Select(m => m.ToLowerInvariant());
        List<(string Path, SourceMapZoneAsset? Binding)>? selected = null;
        foreach (string candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
            string path = SourceMapZones.PathForMission(candidate);
            byte[]? bytes = workspace.Read(path, token, Math.Min(remaining, SourceMapZones.MaximumBytes));
            if (bytes != null) { remaining -= bytes.Length; Reserve(bytes.Length); }
            var map = bytes == null ? new SourceMapZones([]) : SourceMapZones.Parse(bytes, token);
            var closure = Closure(map, requireComplete: local);
            if (closure == null) continue; // This common model cannot be loaded in this mission.
            if (selected != null && !SameClosure(selected, closure))
                throw new InvalidDataException("This shared model has different bindings in several maps. Choose a mission-local model with an explicit zone profile before adding it.");
            selected = closure;
        }
        // Projects without mission contexts still have ordinary inline models. Missing dependencies refuse.
        selected ??= Closure(new SourceMapZones([]), requireComplete: true)!;
        List<SourceMapZoneAsset> assets = [.. own.Assets];
        foreach (var (path, asset) in selected)
        {
            token.ThrowIfCancellationRequested();
            if (own.TryGetAsset(path, out var present))
            {
                if (asset == null)
                    throw new InvalidDataException($"The donor uses inline data for {JsonData.ShownText(path)}, but the receiving map overrides it. Give the donor a distinct logical dependency before adding it.");
                if (!SameBinding(present, asset, token))
                    throw new InvalidDataException($"The model's dependency {JsonData.ShownText(asset.LogicalPath)} has different geometry, zones or references in the receiving map. Give the donor a distinct logical dependency before adding it.");
                continue;
            }
            if (asset == null) continue;
            if (assets.Count >= SourceMapZones.MaximumAssets)
                throw new InvalidDataException("Adding this model exceeds the map's zone asset limit.");
            assets.Add(asset);
        }
        changed = assets.Count != own.Assets.Count;
        return changed ? new SourceMapZones(assets, own.Labels) : own;

        bool SameClosure(List<(string Path, SourceMapZoneAsset? Binding)> first, List<(string Path, SourceMapZoneAsset? Binding)> second)
        {
            if (first.Count != second.Count) return false;
            for (int i = 0; i < first.Count; i++)
            {
                var a = first[i]; var b = second[i];
                Reserve(a.Path.Length + b.Path.Length + 1L);
                if (!a.Path.Equals(b.Path, StringComparison.OrdinalIgnoreCase)) return false;
                if (a.Binding == null ? b.Binding != null : b.Binding == null || !SameBinding(a.Binding, b.Binding, token)) return false;
            }
            return true;
        }

        List<(string Path, SourceMapZoneAsset? Binding)>? Closure(SourceMapZones source, bool requireComplete)
        {
            var bindings = new SourceMissionModels(source, token);
            List<(string Path, SourceMapZoneAsset? Binding)> result = [];
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase) { model };
            Queue<string> pending = new(); pending.Enqueue(model);
            while (pending.TryDequeue(out string? path))
            {
                token.ThrowIfCancellationRequested();
                Reserve(path.Length + 1L);
                result.Add((path, source.TryGetAsset(path, out var asset) ? asset : null));
                string geometry = bindings.Geometry(path);
                if (!bindings.Exists(path, p => workspace.Exists(p, token)))
                {
                    if (!requireComplete) return null;
                    throw new InvalidDataException($"The model dependency {JsonData.ShownText(path)} is missing.");
                }
                byte[]? bytes = ReadBuildContent(geometry, () => workspace.ReadModel(geometry, token, remaining, GltfDocument.MaximumJsonBytes));
                if (bytes == null)
                {
                    if (!requireComplete) return null;
                    throw new InvalidDataException($"The model dependency {JsonData.ShownText(geometry)} is missing.");
                }
                remaining -= bytes.Length; Reserve(bytes.Length);
                var hierarchy = ReadBuildContent(geometry, () => GltfDocument.ReadHierarchy(bytes, token));
                bool needsBinding = false;
                foreach (var node in hierarchy.AllNodes())
                {
                    Reserve(1);
                    var engine = node.Extras?[WorldGltf.Key] as JsonObject;
                    if (engine?[WorldGltf.ZoneReference] is not { } marker) continue;
                    bool enabled = marker is JsonValue value && value.TryGetValue(out bool flag)
                        ? flag : GltfInteger.Int32(marker, WorldGltf.ZoneReference) == 1;
                    if (!enabled || engine["ref"] != null)
                        throw new InvalidDataException($"{JsonData.ShownText(path)} has conflicting or invalid neutral reference fields.");
                    needsBinding = true;
                }
                // A physical common model can be neutral in only some missions. Absence of its entire
                // binding makes this context unusable; a present but incomplete/damaged binding still refuses.
                if (!requireComplete && asset == null && needsBinding) return null;
                foreach (string reference in bindings.Dependencies(path, hierarchy, Reserve))
                {
                    token.ThrowIfCancellationRequested();
                    if (seen.Contains(reference)) continue;
                    if (seen.Count >= SourceMapZones.MaximumAssets) throw new InvalidDataException("The model's zone dependency closure exceeds the map asset limit.");
                    seen.Add(reference); pending.Enqueue(reference);
                }
            }
            result.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
            return result;
        }
    }
    // Compare values, including exact URI spelling: ./ is a distinct original-loader cache identity.
    private static bool SameBinding(SourceMapZoneAsset a, SourceMapZoneAsset b, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!a.LogicalPath.Equals(b.LogicalPath, StringComparison.OrdinalIgnoreCase) ||
            !a.GeometryPath.Equals(b.GeometryPath, StringComparison.OrdinalIgnoreCase) ||
            a.Profile.Fingerprint != b.Profile.Fingerprint || a.Profile.LoadRoot != b.Profile.LoadRoot ||
            a.Profile.Nodes.Count != b.Profile.Nodes.Count || a.Profile.MeshPolygons.Count != b.Profile.MeshPolygons.Count ||
            a.References.Count != b.References.Count) return false;
        for (int i = 0; i < a.Profile.Nodes.Count; i++)
        { token.ThrowIfCancellationRequested(); if (a.Profile.Nodes[i] != b.Profile.Nodes[i]) return false; }
        for (int i = 0; i < a.Profile.MeshPolygons.Count; i++)
        {
            var x = a.Profile.MeshPolygons[i]; var y = b.Profile.MeshPolygons[i];
            if (x.Count != y.Count) return false;
            for (int j = 0; j < x.Count; j++)
            { if ((j & 1023) == 0) token.ThrowIfCancellationRequested(); if (x[j] != y[j]) return false; }
        }
        // File order is not identity; the unique source-node ordinal is.
        var other = b.References.ToDictionary(r => r.Node);
        foreach (var r in a.References)
        {
            token.ThrowIfCancellationRequested();
            if (!other.TryGetValue(r.Node, out var s) || !r.Asset.Equals(s.Asset, StringComparison.OrdinalIgnoreCase) || r.Spelling != s.Spelling) return false;
        }
        return true;
    }
}
