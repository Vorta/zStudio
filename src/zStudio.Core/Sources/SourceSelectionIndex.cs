using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using zStudio.Core.Gltf;
using zStudio.Core.Worlds;

namespace zStudio.Core.Sources;

/// <summary>An editor selection boundary. Negative identities are synthetic source occurrences, never compiled node slots.</summary>
public sealed record SourceSelectionBoundary(int Id, string Key, string Name, string? Source, bool Synthetic, string? ReadOnlyReason);

/// <summary>One authored source row. Its row identity is never a glTF ordinal or an invented compiled slot.</summary>
public sealed record SourceSelectionNode(int Id, string Key, string Name, string File, int SourceNode, int? CompiledNode, bool Group);

/// <summary>Prepared ownership of source-map geometry by physical external-file occurrence, independent of names and shared models.</summary>
public sealed class SourceSelectionIndex
{
    public IReadOnlyDictionary<int, int> Owners { get; }
    public IReadOnlyDictionary<int, SourceSelectionBoundary> Boundaries { get; }
    public IReadOnlyDictionary<int, string> Identities { get; }
    public IReadOnlyDictionary<int, SourceSelectionNode> AuthoredNodes { get; }
    public IReadOnlyDictionary<int, IReadOnlyList<int>> AuthoredChildren { get; }
    public IReadOnlySet<(int Parent, int Child)> FlattenedEdges { get; }
    private SourceSelectionIndex(Dictionary<int, int> owners, Dictionary<int, SourceSelectionBoundary> boundaries, Dictionary<int, string> identities,
        Dictionary<int, SourceSelectionNode> authoredNodes, Dictionary<int, IReadOnlyList<int>> authoredChildren, HashSet<(int Parent, int Child)> flattenedEdges)
    { Owners = owners; Boundaries = boundaries; Identities = identities; AuthoredNodes = authoredNodes; AuthoredChildren = authoredChildren; FlattenedEdges = flattenedEdges; }

    public static SourceSelectionIndex Build(IReadOnlyDictionary<int, WorldNodeProvenance> origins, CancellationToken token = default,
        IReadOnlyDictionary<int, WorldNode>? topology = null, IReadOnlyDictionary<string, string>? readHashes = null,
        SourceAuthoredHierarchy? authored = null)
    {
        if (origins.Count > 200_000) throw new InvalidDataException("The source world has too many nodes to prepare selection.");
        // Ordinals and script lines identify a node only while their authoritative
        // files are unchanged. A Blender reorder or reused script line must not
        // inherit another object's hidden-part state on the next build.
        Dictionary<string, string> content = new(StringComparer.OrdinalIgnoreCase);
        string unverified = Guid.NewGuid().ToString("N"), scripts = "", terrainInputs = unverified;
        if (readHashes != null)
        {
            if (readHashes.Count > 200_000) throw new InvalidDataException("Source identity inputs exceed the map presentation limit.");
            long text = 0;
            foreach (var (path, digest) in readHashes)
            {
                token.ThrowIfCancellationRequested();
                if (path.Length > 8192 || digest.Length != 64 || digest.Any(c => !char.IsAsciiHexDigit(c)) ||
                    (text += path.Length + digest.Length) > 32_000_000) throw new InvalidDataException("Source identity inputs exceed the metadata limit.");
                content.Add(path.Replace('\\', '/'), digest.ToUpperInvariant());
            }
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var (path, digest) in content.Where(p => p.Key.EndsWith(".gs", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                hash.AppendData(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
                hash.AppendData(Convert.FromHexString(digest));
            }
            scripts = Convert.ToHexString(hash.GetHashAndReset());
            // A generated piece depends on its recipe, referenced surface geometry/buffers and world grid.
            // The sealed build input snapshot supplies that dependency conservatively without another recipe parser.
            foreach (var (path, digest) in content.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                hash.AppendData(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
                hash.AppendData(Convert.FromHexString(digest));
            }
            terrainInputs = Convert.ToHexString(hash.GetHashAndReset());
        }
        string Content(string? path) => path == null || readHashes == null ? "" : content.GetValueOrDefault(path.Replace('\\', '/'), unverified);
        Dictionary<WorldNodeProvenance, int> slots = new(ReferenceEqualityComparer.Instance);
        foreach (var (slot, origin) in origins)
        {
            token.ThrowIfCancellationRequested();
            BoundMetadata(origin);
            if (!slots.TryAdd(origin, slot)) throw new InvalidDataException("Several compiled nodes share one source occurrence; source selection is ambiguous.");
        }
        Dictionary<WorldNodeProvenance, (string Key, int Depth)> keys = new(ReferenceEqualityComparer.Instance);
        HashSet<WorldNodeProvenance> visiting = new(ReferenceEqualityComparer.Instance);
        // One physical line may execute repeatedly with different macros. Created and Load retain the exact
        // instruction object for each execution; script/line equality alone cannot associate their objects.
        Dictionary<SourceInstruction, List<WorldNodeProvenance>> loads = new(ReferenceEqualityComparer.Instance);
        HashSet<SourceInstruction> databaseLoads = new(ReferenceEqualityComparer.Instance);
        Dictionary<SourceInstruction, string> instructionKeys = new(ReferenceEqualityComparer.Instance);
        Dictionary<string, int> instructionUses = new(StringComparer.Ordinal);
        HashSet<WorldNodeProvenance> lineage = new(ReferenceEqualityComparer.Instance);
        long instructionText = 0;
        if (authored?.Nodes.Count > 200_000) throw new InvalidDataException("The authored hierarchy exceeds its presentation limit.");
        foreach (var origin in origins.Values.Concat(authored?.Nodes.Select(n => n.Origin) ?? []))
            for (var at = origin; at != null && lineage.Add(at); at = at.ReferencedBy)
            {
                token.ThrowIfCancellationRequested();
                if (lineage.Count > 400_000) throw new InvalidDataException("The source selection identity count exceeds its metadata limit.");
                BoundMetadata(at);
                // Terrain markers are admitted only in this database load, but generated-piece provenance
                // intentionally carries recipe fields instead of NodeImported's Database/ModelFile fields.
                if ((at.Database || at.Terrain != null) && at.Load is { } databaseLoad) databaseLoads.Add(databaseLoad);
                CaptureInstruction(at.Created); CaptureInstruction(at.Load);
            }
        // Identical repeated executions have no proven persistent identity. Keep them distinct in this snapshot,
        // but deliberately refuse cross-build restoration rather than inventing an ordinal or matching names.
        foreach (var instruction in instructionKeys.Keys.ToArray())
            if (instructionUses[instructionKeys[instruction]] > 1) instructionKeys[instruction] += ":" + Guid.NewGuid().ToString("N");
        foreach (var origin in origins.Values)
            if (origin.LoadedFile != null && origin.Created is { } made)
            {
                if (!loads.TryGetValue(made, out var list)) loads[made] = list = [];
                list.Add(origin);
            }
        Dictionary<SourceInstruction, WorldNodeProvenance> missingLoads = new(ReferenceEqualityComparer.Instance);
        Dictionary<WorldNodeProvenance, int> boundaryIds = new(ReferenceEqualityComparer.Instance);
        Dictionary<WorldNode, int> topologySlots = new(ReferenceEqualityComparer.Instance);
        if (topology != null) foreach (var (slot, node) in topology) { token.ThrowIfCancellationRequested(); topologySlots.Add(node, slot); }
        Dictionary<WorldNodeProvenance, (WorldNodeProvenance Root, int[] Members)> promoted = new(ReferenceEqualityComparer.Instance);
        Dictionary<int, int> wrapperOwners = [];
        Dictionary<int, SourceSelectionBoundary> boundaries = [];
        Dictionary<int, int> owners = [];
        Dictionary<int, string> identities = [];
        int synthetic = -2; // -1 remains the conventional missing-node sentinel used by mission remapping.
        long work = 0;
        foreach (var (slot, origin) in origins)
        {
            token.ThrowIfCancellationRequested();
            identities[slot] = Key(origin, 0).Key;
            // Generated recipe cells are independently inspectable terrain, not imported model internals.
            // Their null ModelFile/ReferencedBy fields cannot establish a missing external-object root.
            if (origin.Terrain != null) { owners[slot] = slot; continue; }
            WorldNodeProvenance? boundary = null;
            string? boundaryFile = null;
            int depth = 0;
            for (var child = origin; child.ReferencedBy is { } parent; child = parent)
            {
                if (++depth > GltfDocument.MaximumDepth || ++work > 8_000_000) throw new InvalidDataException("The source reference hierarchy exceeds the selection work limit.");
                // Inferred loader sections remain in the owning physical file. They are not external objects.
                if (!string.Equals(child.ModelFile, parent.ModelFile, StringComparison.OrdinalIgnoreCase)) { boundary = parent; boundaryFile = child.ModelFile; }
            }
            // Script loads are object occurrences too. The mission database's temporary load root is deliberately absent.
            if (!origin.Database && origin.Load is { } load && !databaseLoads.Contains(load))
            {
                if (loads.TryGetValue(load, out var candidates))
                {
                    if (candidates.Count != 1) throw new InvalidDataException("A script load has ambiguous selection ownership.");
                    boundary = candidates[0];
                }
                else
                {
                    // A deleted load root may leave shared children alive. Preserve their atomic occurrence as
                    // read-only, never borrowing a different execution's surviving root or inventing a live slot.
                    if (!missingLoads.TryGetValue(load, out boundary)) missingLoads[load] = boundary = new() { Created = load };
                }
                boundaryFile = boundary.LoadedFile;
            }
            if (boundary == null) { owners[slot] = slot; continue; }
            var placement = Placement(boundary);
            boundary = placement.Root;
            if (!boundaryIds.TryGetValue(boundary, out int owner))
            {
                bool missing = !slots.TryGetValue(boundary, out owner);
                if (missing) owner = synthetic--;
                boundaryIds.Add(boundary, owner);
                string name = topology?.GetValueOrDefault(owner)?.Name is { Length: > 0 } shown ? shown
                    : boundary.ModelNodeName is { Length: > 0 } named ? named : Path.GetFileNameWithoutExtension(boundaryFile ?? "Referenced object");
                boundaries.Add(owner, new(owner, Key(boundary, 0).Key, name, boundaryFile, missing,
                    missing ? "This source reference group is removed when the game world is built. Its complete occurrence can be inspected" + (boundary.Database && boundary.ModelFile != null
                        ? " and it can be renamed (right-click in the tree), but moving or otherwise editing this group is unavailable." : ", but moving or editing this group is unavailable.") : null));
                identities[owner] = boundaries[owner].Key;
            }
            owners[slot] = owner;
            foreach (int member in placement.Members) wrapperOwners[member] = owner;
        }
        // A holder may be visited before any of its imported children. Its selection still resolves to its own boundary.
        foreach (var (origin, owner) in boundaryIds)
            if (slots.TryGetValue(origin, out int slot) && owners.GetValueOrDefault(slot, slot) == slot) owners[slot] = owner;
        foreach (var (member, owner) in wrapperOwners)
            if (owners.GetValueOrDefault(member, member) == member) owners[member] = owner;
        // Repeating the same recipe marker in one load has no recorded marker occurrence. Its equal piece
        // ordinals must not restore selection to another compilation; ordinary cell/attribute pieces stay stable.
        foreach (var ambiguous in origins.Where(p => p.Value.Terrain != null).GroupBy(p => identities[p.Key]).Where(g => g.Skip(1).Any()))
            foreach (var entry in ambiguous) identities[entry.Key] += ":" + Guid.NewGuid().ToString("N");
        Dictionary<int, SourceSelectionNode> authoredNodes = [];
        Dictionary<int, IReadOnlyList<int>> authoredChildren = [];
        if (authored is { Nodes.Count: > 0 })
        {
            Dictionary<int, int> projected = [];
            foreach (var node in authored.Nodes)
            {
                token.ThrowIfCancellationRequested();
                int row;
                if (node.CompiledNode is int compiled && owners.TryGetValue(compiled, out int owner)) row = owner;
                else if (boundaryIds.TryGetValue(node.Origin, out int boundary)) row = boundary;
                else if (node.Group) row = synthetic--;
                else continue; // A script deleted this object; it has no preview identity.
                projected[node.Id] = row;
                // A reference's placement wrapper is the displayed source identity, not one of its model parts.
                if (node.CompiledNode != row && row >= 0 || authoredNodes.ContainsKey(row)) continue;
                string key = identities.GetValueOrDefault(row) ?? Key(node.Origin, 0).Key;
                identities[row] = key;
                authoredNodes[row] = new(row, key, node.Name, node.File, node.SourceNode, node.CompiledNode, node.Group);
            }
            foreach (var node in authored.Nodes)
            {
                token.ThrowIfCancellationRequested();
                if (!projected.TryGetValue(node.Id, out int row) || boundaries.ContainsKey(row)
                    || authoredNodes.GetValueOrDefault(row)?.SourceNode != node.SourceNode
                    || authoredNodes[row].File != node.File) continue;
                authoredChildren[row] = Children(node.Children, row);
            }
            foreach (var root in authored.Roots)
            {
                token.ThrowIfCancellationRequested();
                var children = Children(root.Children, root.World);
                authoredChildren[root.World] = Array.AsReadOnly(authoredChildren.GetValueOrDefault(root.World, []).Concat(children).ToArray());
            }
            IReadOnlyList<int> Children(IReadOnlyList<int> nodes, int parent)
            {
                List<int> result = [];
                HashSet<int> seen = [];
                foreach (int id in nodes)
                {
                    token.ThrowIfCancellationRequested();
                    if (++work > 8_000_000) throw new InvalidDataException("The authored hierarchy exceeds its selection work limit.");
                    if (projected.TryGetValue(id, out int child) && child != parent && seen.Add(child)) result.Add(child);
                }
                return result.AsReadOnly();
            }
        }
        HashSet<(int Parent, int Child)> flattenedEdges = [];
        foreach (var edge in authored?.FlattenedEdges ?? [])
        {
            token.ThrowIfCancellationRequested();
            if (++work > 8_000_000) throw new InvalidDataException("The authored hierarchy exceeds its selection work limit.");
            flattenedEdges.Add((owners.GetValueOrDefault(edge.Parent, edge.Parent), owners.GetValueOrDefault(edge.Child, edge.Child)));
        }
        // Different physical edges can become the same whole-object edge after wrapper/part ownership.
        // Any explicit attachment of that owner must survive automatic flattening suppression.
        foreach (var edge in authored?.ScriptEdges ?? [])
        {
            token.ThrowIfCancellationRequested();
            if (++work > 8_000_000) throw new InvalidDataException("The authored hierarchy exceeds its selection work limit.");
            flattenedEdges.Remove((owners.GetValueOrDefault(edge.Parent, edge.Parent), owners.GetValueOrDefault(edge.Child, edge.Child)));
        }
        return new(owners, boundaries, identities, authoredNodes, authoredChildren, flattenedEdges);

        (WorldNodeProvenance Root, int[] Members) Placement(WorldNodeProvenance reference)
        {
            if (promoted.TryGetValue(reference, out var cached)) return cached;
            // Script-created parents are independent editable groups, not authored wrappers of a physical glTF reference.
            // Their null ModelFile/Load fields do not establish shared source ownership with a LoadGameGen root.
            if (reference.ModelFile == null || topology == null || !slots.TryGetValue(reference, out int slot) || !topology.TryGetValue(slot, out var node))
                return promoted[reference] = (reference, []);
            List<int> members = [slot];
            HashSet<WorldNode> seen = new(ReferenceEqualityComparer.Instance) { node };
            var root = reference;
            // A single-child, geometry-free authored object is the placement wrapper of this reference.
            // Source groups are removed by the compiler; multi-object groups and shared attachments remain separate.
            while (node.Parents.Count == 1)
            {
                token.ThrowIfCancellationRequested();
                if (++work > 8_000_000 || members.Count > GltfDocument.MaximumDepth) throw new InvalidDataException("The source placement hierarchy exceeds the selection work limit.");
                var parent = node.Parents[0];
                if (!seen.Add(parent)) throw new InvalidDataException("The source placement hierarchy is cyclic.");
                if (parent.Class != WorldNodeClass.Object3D || parent.Model != null || parent.Children.Count != 1 || !ReferenceEquals(parent.Children[0], node)
                    || !topologySlots.TryGetValue(parent, out int parentSlot) || !origins.TryGetValue(parentSlot, out var parentOrigin)
                    || !string.Equals(parentOrigin.ModelFile, reference.ModelFile, StringComparison.OrdinalIgnoreCase)
                    || !ReferenceEquals(parentOrigin.ReferencedBy, reference.ReferencedBy)
                    || parentOrigin.Database != reference.Database || parentOrigin.Part != reference.Part
                    || !ReferenceEquals(parentOrigin.Load, reference.Load)) break;
                root = parentOrigin; node = parent; members.Add(parentSlot);
            }
            return promoted[reference] = (root, members.ToArray());
        }

        (string Key, int Depth) Key(WorldNodeProvenance origin, int depth)
        {
            token.ThrowIfCancellationRequested();
            BoundMetadata(origin);
            if (depth > GltfDocument.MaximumDepth) throw new InvalidDataException("The source reference hierarchy exceeds the selection depth limit.");
            if (keys.TryGetValue(origin, out var cached))
            {
                if (depth + cached.Depth > GltfDocument.MaximumDepth) throw new InvalidDataException("The source reference hierarchy exceeds the selection depth limit.");
                return cached;
            }
            if (!visiting.Add(origin)) throw new InvalidDataException("The source reference hierarchy is cyclic.");
            if (keys.Count + visiting.Count > 400_000) throw new InvalidDataException("The source selection identity count exceeds its metadata limit.");
            try
            {
                var parent = origin.ReferencedBy == null ? (Key: "", Depth: 0) : Key(origin.ReferencedBy, depth + 1);
                string[] fields = [origin.ModelFile?.ToUpperInvariant() ?? "", origin.ModelNode.ToString(CultureInfo.InvariantCulture),
                    origin.LoadedFile?.ToUpperInvariant() ?? "", origin.Load?.Script.ToUpperInvariant() ?? origin.Created?.Script.ToUpperInvariant() ?? "",
                    (origin.Load?.Line ?? origin.Created?.Line ?? -1).ToString(CultureInfo.InvariantCulture), origin.Instance?.ToString() ?? "", parent.Key,
                    Content(origin.ModelFile), Content(origin.LoadedFile), Content(origin.Load?.Script), Content(origin.Created?.Script), scripts,
                    origin.Load is { } loaded ? instructionKeys[loaded] : "", origin.Created is { } created ? instructionKeys[created] : "",
                    origin.Terrain?.ToUpperInvariant() ?? "", origin.TerrainSurface ?? "",
                    origin.TerrainCell.Column.ToString(CultureInfo.InvariantCulture), origin.TerrainCell.Row.ToString(CultureInfo.InvariantCulture),
                    origin.TerrainPiece.ToString(CultureInfo.InvariantCulture),
                    origin.Terrain != null ? terrainInputs : "",
                    origin.ModelFile == null && origin.Created == null && origin.Terrain == null ? Guid.NewGuid().ToString("N") : ""];
                if (fields.Sum(s => (long)s.Length) > 32_768) throw new InvalidDataException("The source identity exceeds the selection metadata limit.");
                string text = string.Concat(fields.Select(s => s.Length.ToString(CultureInfo.InvariantCulture) + ":" + s));
                return keys[origin] = (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))), parent.Depth + 1);
            }
            finally { visiting.Remove(origin); }
        }
        void CaptureInstruction(SourceInstruction? instruction)
        {
            if (instruction == null || instructionKeys.ContainsKey(instruction)) return;
            if (instructionKeys.Count >= 400_000 || instruction.Script.Length > 8192 || instruction.Command.Length > 8192 || instruction.Args.Count > 1024)
                throw new InvalidDataException("The source instruction exceeds the selection metadata limit.");
            long length = instruction.Script.Length + instruction.Command.Length;
            foreach (var arg in instruction.Args)
            {
                if (arg.Length > 8192 || (length += arg.Length) > 32_768) throw new InvalidDataException("The source instruction exceeds the selection metadata limit.");
            }
            if ((instructionText += length) > 32_000_000) throw new InvalidDataException("Source instructions exceed the selection metadata limit.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var field in new[] { instruction.Script.ToUpperInvariant(), instruction.Line.ToString(CultureInfo.InvariantCulture), instruction.Command }.Concat(instruction.Args))
                hash.AppendData(SHA256.HashData(Encoding.UTF8.GetBytes(field)));
            string key = Convert.ToHexString(hash.GetHashAndReset());
            instructionKeys.Add(instruction, key);
            instructionUses[key] = instructionUses.GetValueOrDefault(key) + 1;
        }
        static void BoundMetadata(WorldNodeProvenance origin)
        {
            string?[] values = [origin.ModelFile, origin.LoadedFile, origin.ModelNodeName, origin.Load?.Script, origin.Created?.Script, origin.Terrain, origin.TerrainSurface];
            if (values.Any(s => s?.Length > 8192) || values.Sum(s => (long)(s?.Length ?? 0)) > 24_576)
                throw new InvalidDataException("The source identity exceeds the selection metadata limit.");
        }
    }
}
