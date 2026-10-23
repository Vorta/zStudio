using System.Numerics;
using zStudio.Core.Worlds;

namespace zStudio.Core.Sources;

public static partial class SourceWorlds
{
    /// <summary>
    /// Prepares a source-only transform update without running GameGen, decoding geometry or rebuilding resources.
    /// A null result requires the normal source evaluator; no accepted data is mutated in either case.
    /// The caller still owns workspace dependency, external-change and revision checks before publication.
    /// </summary>
    public static SourceSceneSnapshot? TryUpdateTransforms(SourceSceneSnapshot accepted, IReadOnlyDictionary<string, byte[]> overlay,
        out string reason, CancellationToken token = default)
    {
        if (accepted.WorldChildOrder.Count == 0) { reason = "The accepted scene lacks retained source transform data."; return null; }
        if (ReadChangedModels(accepted, overlay, token, out reason) is not { } read) return null;
        reason = "The edit changes more than an unambiguous imported transform.";
        List<(string Path, WorldGltf.NodeTransformChange Change)> changes = [];
        foreach (var (path, before, bytes) in read.Files)
        {
            token.ThrowIfCancellationRequested();
            var changed = WorldGltf.TransformChanges(before, bytes, token);
            if (changed == null) { reason = "A changed model contains non-transform edits or changes reflection parity."; return null; }
            if (changed.Count > GameZWorld.MaximumNodeCapacity - changes.Count) return null;
            foreach (var change in changed) changes.Add((path, change));
        }
        if (changes.Count == 0) { reason = "The edit has no changed imported transforms."; return null; }
        var slots = GameZWriter.NodeSlots(accepted.World, token);
        var bySlot = slots.ToDictionary(p => p.Value, p => p.Key);
        var bySource = accepted.Provenance.Where(p => p.Value.ModelFile != null)
            .GroupBy(p => (File: p.Value.ModelFile!.ToLowerInvariant(), p.Value.ModelNode))
            .ToDictionary(g => g.Key, g => g.ToArray());
        Dictionary<int, Matrix4x4> transforms = [];
        foreach (var (path, change) in changes)
        {
            token.ThrowIfCancellationRequested();
            if (!bySource.TryGetValue((path.ToLowerInvariant(), change.Index), out var matches) || matches.Length != 1) { reason = "A changed source node has no unique live occurrence."; return null; }
            var (slot, origin) = matches[0]; var node = bySlot[slot];
            if (node.Class != WorldNodeClass.Object3D || origin.SharedPlacement || node.Parents.Count != 1
                || origin.Writers.Keys.Any(command => command is "Object3DTranslate" or "Object3DRotate" or "Object3DScale")
                || (WorldUpdate.LocalMatrix(node) ?? Matrix4x4.Identity) != change.Before) { reason = "A changed source node has a shared, scripted or non-imported transform."; return null; }
            transforms.Add(slot, change.After);
        }
        var world = CopyNodes(accepted.World, token);
        var updatedSlots = GameZWriter.NodeSlots(world, token).ToDictionary(p => p.Value, p => p.Key);
        foreach (var (slot, matrix) in transforms) WorldGltf.SetImportedTransform(updatedSlots[slot], matrix, accepted.Mission);
        // Existing model boxes are immutable and already measured by the complete source evaluator.
        Dictionary<WorldModel, WorldBox> bounds = [];
        foreach (var node in accepted.World.Nodes) if (node.Model != null) bounds.TryAdd(node.Model, node.PrimaryBounds);
        WorldUpdate.RebuildBoundsCore(world, token, bounds);
        foreach (var (slot, children) in accepted.WorldChildOrder)
        {
            token.ThrowIfCancellationRequested(); var node = updatedSlots[slot];
            // Clear old cell height caches, including a formerly occupied cell that is now empty.
            float toleranceX = node.PayloadFloat(0x70), toleranceZ = node.PayloadFloat(0x74);
            if (node.Areas.Count > 0) WorldUpdate.SetPartition(node, node.PayloadFloat(0x54), node.PayloadFloat(0x58));
            node.SetPayloadFloat(0x70, toleranceX); node.SetPayloadFloat(0x74, toleranceZ);
            WorldUpdate.Partition(node, children.Select(index => updatedSlots[index]), parentsAttached: true);
        }
        WorldUpdate.SingleParentFlags(world);
        var scene = WorldScene.Create(world, accepted.Scene, token);
        reason = "Imported transforms updated from the accepted source snapshot.";
        return Derive(accepted, read, world, scene, provenance => { foreach (var (slot, matrix) in transforms) provenance[slot].ModelTransformAuthored = !matrix.IsIdentity; }, token);
    }

    /// <summary>The model sources an edit changed against an accepted snapshot, with the snapshot's read records updated.</summary>
    private sealed record ChangedModels(Dictionary<string, byte[]> Sources, Dictionary<string, string> Hashes, Dictionary<string, string> DiskHashes,
        Dictionary<string, FileStamp> Stamps, long RetainedBytes, SourceBuilder.Snapshot Reads, IReadOnlyList<(string Path, byte[] Before, byte[] After)> Files);

    /// <summary>
    /// Null (with the reason) unless the edit changes only model glTF files the accepted snapshot read: never a resource,
    /// script, geometry buffer, a file it did not read or a model that also supplies baked terrain geometry.
    /// </summary>
    private static ChangedModels? ReadChangedModels(SourceSceneSnapshot accepted, IReadOnlyDictionary<string, byte[]> overlay, CancellationToken token, out string reason)
    {
        reason = "The edit changes no model source.";
        if (accepted.Scene == null || accepted.ModelSources.Count == 0) { reason = "The accepted scene lacks retained source data."; return null; }
        // An unknown pending source can change a previously absent dependency or a resource inventory.
        if (overlay.Keys.Any(path => !accepted.ReadHashes.ContainsKey(path))) { reason = "The edit contains a source outside the accepted dependency snapshot."; return null; }
        Dictionary<string, byte[]> sources = new(accepted.ModelSources, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> hashes = new(accepted.ReadHashes, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> diskHashes = new(accepted.InputHashes, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, FileStamp> stamps = new(accepted.Inputs, StringComparer.OrdinalIgnoreCase);
        // Match complete evaluation: pending sources are guarded by their workspace baselines, not by
        // an obsolete disk stamp. A successful Save may replace those files without retiring this scene.
        foreach (string path in overlay.Keys) { diskHashes.Remove(path); stamps.Remove(path); }
        long retainedBytes = accepted.RetainedBytes;
        List<(string Path, byte[] Before, byte[] After)> files = [];
        SourceBuilder.Snapshot reads = new(accepted.Root, overlay, inventoryToken: token);
        foreach (var (path, hash) in accepted.ReadHashes)
        {
            token.ThrowIfCancellationRequested();
            byte[]? bytes = null;
            if (overlay.TryGetValue(path, out var pending))
            { if (SourceProject.Sha256(pending) != hash) bytes = pending; }
            else if (!accepted.InputHashes.TryGetValue(path, out string? disk) || disk != hash)
            {
                // Undo may remove a newly created, unsaved source from the overlay. Inventory/resource
                // changes require full evaluation; do not try to open their nonexistent disk counterpart.
                if (!sources.ContainsKey(path)) { reason = "A pending resource, script or geometry buffer changed or left the overlay."; return null; }
                if (!reads.Exists(path)) { reason = "A pending model left the overlay without a disk counterpart."; return null; }
                // Exists distinguishes genuine absence from I/O/access failures; existing unreadable or
                // concurrently changed sources still propagate their normal failure rather than being ignored.
                bytes = reads.Read(path, token, ProjectReadLimits.Model());
            }
            if (bytes == null || SourceProject.Sha256(bytes) == hash) continue;
            if (accepted.TerrainModelInputs.Contains(path)) { reason = "A changed model also supplies baked terrain geometry."; return null; }
            if (!sources.TryGetValue(path, out byte[]? before)) { reason = "The edit changes a resource, script or geometry buffer."; return null; }
            retainedBytes += bytes.LongLength - before.LongLength;
            if (retainedBytes > SourceExtractor.MaximumRetainedBytes) { reason = "The changed models exceed the retained source allowance."; return null; }
            files.Add((path, before, bytes));
            sources[path] = bytes; hashes[path] = SourceProject.Sha256(bytes);
        }
        if (files.Count == 0) return null;
        return new(sources, hashes, diskHashes, stamps, retainedBytes, reads, files);
    }

    /// <summary>
    /// A snapshot of the accepted one with an updated world and scene and the changed sources; everything else (resources,
    /// textures, animations, lookups) is the accepted snapshot's. <paramref name="adjust"/> may change the copied provenance.
    /// </summary>
    private static SourceSceneSnapshot Derive(SourceSceneSnapshot accepted, ChangedModels read, GameZWorld world, GameScene scene,
        Action<Dictionary<int, WorldNodeProvenance>>? adjust, CancellationToken token)
    {
        Dictionary<WorldNodeProvenance, WorldNodeProvenance> origins = [];
        WorldNodeProvenance Origin(WorldNodeProvenance before)
        {
            if (origins.TryGetValue(before, out var found)) return found;
            var copy = before.Copy(); origins.Add(before, copy);
            copy.ReferencedBy = before.ReferencedBy == null ? null : Origin(before.ReferencedBy);
            copy.ImportedZoneSupplier = before.ImportedZoneSupplier == null ? null : Origin(before.ImportedZoneSupplier);
            return copy;
        }
        var provenance = accepted.Provenance.ToDictionary(p => p.Key, p => Origin(p.Value));
        adjust?.Invoke(provenance);
        foreach (var (path, hash) in read.Reads.Hashes()) read.DiskHashes[path] = hash;
        foreach (var (path, stamp) in read.Reads.Stamps()) read.Stamps[path] = stamp;
        read.Reads.CheckUnchanged(token);
        SourceSceneSnapshot result = new(accepted.Root, accepted.Mission)
        {
            World = world, Scene = scene, ModelSources = read.Sources, WorldChildOrder = accepted.WorldChildOrder, RetainedBytes = read.RetainedBytes, TerrainModelInputs = accepted.TerrainModelInputs,
            Resources = accepted.Resources, TextureFiles = accepted.TextureFiles, Textures = accepted.Textures, Sounds = accepted.Sounds,
            TextureScripts = accepted.TextureScripts, Animations = accepted.Animations, AnimationDefinitions = accepted.AnimationDefinitions,
            AnimationProvenance = accepted.AnimationProvenance, Provenance = provenance,
            Freed = accepted.Freed.Select(Origin).ToArray(), AuthoredHierarchy = accepted.AuthoredHierarchy with
                { Nodes = accepted.AuthoredHierarchy.Nodes.Select(node => node with { Origin = Origin(node.Origin) }).ToArray() },
            Executions = accepted.Executions, WriteInstruction = accepted.WriteInstruction, Inputs = read.Stamps, InputHashes = read.DiskHashes,
            ReadHashes = read.Hashes, Dependencies = accepted.Dependencies, MissingInputs = accepted.MissingInputs, MissingFolders = accepted.MissingFolders,
            ResourceInputs = accepted.ResourceInputs, TextureInputs = accepted.TextureInputs, SoundInputs = accepted.SoundInputs,
            ScriptInputs = accepted.ScriptInputs, Notes = accepted.Notes, Lookups = accepted.Lookups
        };
        result.Assets.Add(new() { Id = new(result.Path, AssetKind.World, 0), Name = "Whole world", Content = scene });
        foreach (var model in scene.Models) result.Assets.Add(new() { Id = new(result.Path, AssetKind.Model, model.Index), Name = $"Model {model.Index}", Content = model, Metadata = model.Metadata });
        foreach (var node in scene.Nodes) result.Assets.Add(new() { Id = new(result.Path, AssetKind.Node, node.Index), Name = node.Name, Content = node, Metadata = node.Metadata });
        return result;
    }

    private static GameZWorld CopyNodes(GameZWorld source, CancellationToken token)
    {
        GameZWorld result = new() { SourceVersion = source.SourceVersion, FreeHead = source.FreeHead, NodeCapacity = source.NodeCapacity,
            ModelCapacity = source.ModelCapacity, MaterialCapacity = source.MaterialCapacity };
        result.Textures.AddRange(source.Textures); result.Materials.AddRange(source.Materials); result.Models.AddRange(source.Models);
        foreach (var (slot, bytes) in source.FreedSlots) result.FreedSlots.Add(slot, bytes);
        Dictionary<WorldNode, WorldNode> nodes = [];
        foreach (var node in source.Nodes)
        {
            token.ThrowIfCancellationRequested();
            WorldNode copy = new(node.Name, node.Class) { NameField = (byte[])node.NameField.Clone(), Flags = node.Flags,
                AuxFlags = node.AuxFlags, BoundsFlags = node.BoundsFlags, Zone = node.Zone, Model = node.Model, Priority = node.Priority,
                GridColumn = node.GridColumn, GridRow = node.GridRow, SphereCache = (byte[])node.SphereCache.Clone(),
                CachedBounds = node.CachedBounds, PrimaryBounds = node.PrimaryBounds, SecondaryBounds = node.SecondaryBounds,
                Payload = (byte[])node.Payload.Clone() };
            nodes.Add(node, copy); result.Nodes.Add(copy);
        }
        WorldNode? Node(WorldNode? node) => node == null ? null : nodes[node];
        foreach (var node in source.Nodes)
        {
            token.ThrowIfCancellationRequested(); var copy = nodes[node];
            copy.Parents.AddRange(node.Parents.Select(n => nodes[n])); copy.Children.AddRange(node.Children.Select(n => nodes[n]));
            copy.WorldLights.AddRange(node.WorldLights.Select(n => nodes[n])); copy.WorldSounds.AddRange(node.WorldSounds.Select(n => nodes[n]));
            copy.AttachedWorlds.AddRange(node.AttachedWorlds.Select(n => nodes[n]));
            copy.CameraWorld = Node(node.CameraWorld); copy.CameraWindow = Node(node.CameraWindow);
            copy.CameraHorizon = Node(node.CameraHorizon); copy.CameraHorizonXZ = Node(node.CameraHorizonXZ);
            foreach (var area in node.Areas)
            { WorldArea cloned = new() { Record = (byte[])area.Record.Clone() }; cloned.Nodes.AddRange(area.Nodes.Select(n => nodes[n])); copy.Areas.Add(cloned); }
        }
        return result;
    }
}
