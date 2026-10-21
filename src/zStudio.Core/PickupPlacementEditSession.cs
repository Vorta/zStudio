using System.Numerics;
using zStudio.Core.Formats;

namespace zStudio.Core;

public sealed record PickupPlacementResource(MissionDifficulty Difficulty, AssetDocument Archive, AssetRecord Asset);
public sealed record PickupPlacementRecord(MissionPickupSource Source, string Type, Vector3 OriginalPosition,
    Vector3 Rotation, IReadOnlyList<MissionDifficulty> Difficulties);
public sealed record PickupPlacementScope(IReadOnlyList<MissionPickupSource> Sources, string Description);

/// <summary>
/// A mission's authored placements (pickups, AIV vehicles, AI nodes): their records, linked difficulty counterparts and the
/// transforms an edit of a source map would store. It reads compiled archives for inspection and source resources for edits;
/// a source edit becomes scalar replacements in the source files (<see cref="SourceChanges"/>), never a write of its own.
/// </summary>
public sealed partial class PickupPlacementEditSession
{
    private sealed class ArchiveState(AssetDocument document)
    {
        public AssetDocument Original { get; } = document;
    }
    private sealed record Entry(PickupPlacementRecord Record, int[] Offsets, int[] RotationOffsets);
    // Pickup behavior remains in this session; AI/vehicle records use typed adapters.
    private readonly Dictionary<MissionPickupSource, MissionCoordinateRecord> otherCoordinates;
    private readonly Dictionary<string, ArchiveState> archives = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<MissionPickupSource, Entry> entries;
    private readonly Dictionary<MissionPickupSource, Vector3> positions;
    private readonly Dictionary<MissionPickupSource, Vector3> rotations;
    private readonly PreviewNotes diagnostics = new();
    private readonly Dictionary<AssetDocument, HashSet<AssetRecord>> overlappingMembers = [];
    public IReadOnlyList<string> Diagnostics => diagnostics;
    public int DiagnosticCount => diagnostics.TotalCount;
    public IReadOnlyList<PickupPlacementRecord> Records => IndexedRecords();
    public IReadOnlyList<MissionCoordinateRecord> OtherCoordinates => otherCoordinates.Values.ToArray();

    public static Task<PickupPlacementEditSession> LoadAsync(string worldPath, AssetResolver resolver, CancellationToken token = default)
        => LoadAsync(worldPath, resolver, new AssetReadBudget(), token);
    internal static Task<PickupPlacementEditSession> LoadAsync(string worldPath, AssetResolver resolver, AssetReadBudget reads, CancellationToken token)
        => LoadWithResourcesAsync(worldPath, resolver, new PreviewResourceBudget(reads), token);
    internal static async Task<PickupPlacementEditSession> LoadWithResourcesAsync(string worldPath, AssetResolver resolver, PreviewResourceBudget resourcesBudget, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        resourcesBudget.Check(token);
        Dictionary<string, (AssetDocument Archive, AssetRecord Asset)> found = new(StringComparer.OrdinalIgnoreCase);
        List<(AssetDocument Archive, AssetRecord Asset)> coordinateResources = [];
        PreviewNotes notes = new();
        var probe = FormatRegistry.Probe(worldPath);
        bool mw3 = probe is { Family: FormatFamily.GameZ, Version: 27 };
        // Load the coordinate store once for every reader in this map. Preview selection filters by archive identity.
        var files = MissionSceneLoader.ResourceFiles(worldPath, resolver, token);
        foreach (string file in files)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (FormatRegistry.Probe(file).Family != FormatFamily.Archive) continue;
                var archive = await resourcesBudget.OpenAsync(file, resolver, token).ConfigureAwait(false);
                coordinateResources.AddRange(archive.Assets.Where(a => IsCoordinateResource(a.Name)).Select(a => (archive, a)));
                foreach (var asset in archive.Assets.Where(a => IsPickupResource(a.Name) || IsCoordinateResource(a.Name))) found.TryAdd(asset.Name, (archive, asset));
            }
            // A missing/damaged file remains a diagnostic. Capacity exhaustion cannot publish a
            // partial store: it could hide a difficulty counterpart or an independently owned actor.
            catch (Exception ex) when (!resourcesBudget.Exhausted && (ex is IOException or UnauthorizedAccessException or InvalidDataException))
            { notes.Add($"Pickup editing: {Path.GetFileName(file)}: {ex.Message}"); }
        }
        List<PickupPlacementResource> resources = [];
        foreach (var difficulty in mw3 ? Array.Empty<MissionDifficulty>() : Enum.GetValues<MissionDifficulty>())
        {
            string requested = MissionLayoutSelection.For(difficulty).PickupResource;
            string effective = found.ContainsKey(requested) ? requested : "puppies.zrd";
            if (found.TryGetValue(effective, out var resource)) resources.Add(new(difficulty, resource.Archive, resource.Asset));
            else notes.Add($"{difficulty}: pickup resource unavailable.");
        }
        var loaded = await Task.Run(() =>
        {
            var result = Create(resources, notes, token);
            result.AddCoordinates(coordinateResources, token, mw3);
            return result;
        }, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return loaded;
    }
    private static bool IsPickupResource(string name) => name.ToLowerInvariant() is "puppies.zrd" or "puppies_easy.zrd" or "puppies_hard.zrd";

    public static PickupPlacementEditSession Create(IEnumerable<PickupPlacementResource> resources, IEnumerable<string>? notes = null, CancellationToken token = default)
    {
        var result = new PickupPlacementEditSession();
        if (notes != null) result.diagnostics.AddRange(notes);
        foreach (var group in resources.GroupBy(r => (Path.GetFullPath(r.Archive.Path).ToUpperInvariant(), r.Asset.Index)))
        {
            var resource = group.First(); var doc = resource.Archive; var asset = resource.Asset;
            try
            {
                if (doc is not Sources.SourceResourceDocument && doc.Probe.Family != FormatFamily.Archive || doc.Diagnostics.Any(d => d.Severity == "Error") || !IsPickupResource(asset.Name))
                    throw new InvalidDataException("An intact ZAR pickup resource is required.");
                if (result.Overlaps(doc, asset, token))
                    throw new InvalidDataException("Pickup member overlaps another archive member.");
                var tree = ZrdDecoder.ReadAsset(doc, asset, token);
                if (tree.Kind != ZrdKind.Array || tree.Children is not { Count: 1 } root || root[0].Kind != ZrdKind.Array)
                    throw new InvalidDataException("Expected an ordered pickup placement list.");
                var rows = root[0].Children;
                if (!result.archives.ContainsKey(group.Key.Item1)) result.archives.Add(group.Key.Item1, new(doc));
                string? memberIdentity = null;
                for (int index = 0; index < rows.Count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if (rows[index].Kind != ZrdKind.Array || rows[index].Children is not { Count: 5 } row || row[0].Kind != ZrdKind.String || row[1].Kind != ZrdKind.Int)
                            throw new InvalidDataException("Invalid pickup record shape.");
                        string type = row[0].Text;
                        if (!MissionPickupType.Catalog.Any(t => t.Name == type)) throw new InvalidDataException("Unknown pickup type.");
                        var (position, offsets) = ReadVector(row[2], doc, asset);
                        var (rotation, rotationOffsets) = ReadVector(row[3], doc, asset);
                        _ = ReadNumber(row[4]);
                        var source = new MissionPickupSource(group.Key.Item1, asset.Index, memberIdentity ??= asset.Name.ToUpperInvariant(), index);
                        var record = new PickupPlacementRecord(source, type, position, rotation, group.Select(r => r.Difficulty).Distinct().Order().ToArray());
                        result.sourceComparer.Register(source);
                        result.entries.Add(source, new(record, offsets, rotationOffsets)); result.positions.Add(source, position);
                        result.rotations.Add(source, rotation);
                    }
                    catch (Exception ex) when (ex is InvalidDataException or OverflowException or FormatException)
                    { result.diagnostics.Add($"{asset.Name} #{index}: {ex.Message}"); }
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or OverflowException or FormatException)
            { result.diagnostics.Add($"{asset.Name} in {doc.Path}: {ex.Message}"); }
        }
        return result;
    }
    private static (Vector3 Vector, int[] Offsets) ReadVector(ZrdNode node, AssetDocument doc, AssetRecord asset)
    {
        if (node.Kind != ZrdKind.Array || node.Children is not { Count: 3 } components) throw new InvalidDataException("Expected three position/rotation components.");
        float[] values = new float[3]; int[] offsets = new int[3];
        for (int i = 0; i < 3; i++)
        {
            values[i] = ReadNumber(components[i]);
            offsets[i] = ReadScalarOffset(components[i], doc, asset);
        }
        return (new(values[0], values[1], values[2]), offsets);
    }
    private static int ReadScalarOffset(ZrdNode node, AssetDocument doc, AssetRecord asset)
    {
        if (doc is Sources.SourceResourceDocument source) return source.ScalarKey(node);
        long offset = node.SourceOffset;
        if (offset < 0 || offset > asset.Length - 8) throw new InvalidDataException("Invalid transform source range.");
        int absolute = checked((int)(asset.Offset + offset)); _ = doc.Slice(absolute, 8); return absolute;
    }
    private static float ReadNumber(ZrdNode value)
    {
        float number = value.Kind switch { ZrdKind.Int => unchecked((int)value.Bits), ZrdKind.Float => BitConverter.UInt32BitsToSingle(value.Bits), _ => float.NaN };
        if (!float.IsFinite(number)) throw new InvalidDataException("Expected a finite numeric coordinate.");
        return number;
    }
    private bool Overlaps(AssetDocument doc, AssetRecord asset, CancellationToken token)
    {
        if (!overlappingMembers.TryGetValue(doc, out var overlaps))
        {
            overlaps = []; AssetRecord? furthest = null;
            foreach (var member in doc.Assets.OrderBy(a => a.Offset).ThenBy(a => a.Length))
            {
                token.ThrowIfCancellationRequested();
                if (furthest != null && furthest.Offset + furthest.Length > member.Offset && furthest.Offset < member.Offset + member.Length)
                { overlaps.Add(furthest); overlaps.Add(member); }
                if (furthest == null || member.Offset + member.Length > furthest.Offset + furthest.Length) furthest = member;
            }
            overlappingMembers.Add(doc, overlaps);
        }
        return overlaps.Contains(asset);
    }
    public PickupPlacementRecord? Find(MissionPickupSource source) => otherCoordinates.ContainsKey(source) ? null : entries.GetValueOrDefault(source)?.Record;
    public Vector3 Position(MissionPickupSource source) => positions[source];
    public Vector3 Rotation(MissionPickupSource source) => rotations[source];
    public PlacementTransform Transform(MissionPickupSource source) => new(positions[source], rotations[source]);
    public PlacementRotationKind RotationKind(MissionPickupSource source) => entries[source].RotationOffsets.Length switch
    { 3 => PlacementRotationKind.EulerRadians, 1 => PlacementRotationKind.HeadingDegrees, _ => PlacementRotationKind.None };
    public PickupPlacementScope Scope(MissionPickupSource source)
        => IndexedScope(source);
    /// <summary>
    /// <paramref name="value"/> as an edit of <paramref name="source"/> stores it (<see cref="PlacementPrecision"/>): changed
    /// positions and AIV headings at one decimal, changed pickup rotations at four; unchanged components as they are.
    /// </summary>
    public PlacementTransform Stored(MissionPickupSource source, PlacementTransform value)
    {
        var current = Transform(source);
        var rotation = RotationKind(source) switch
        {
            PlacementRotationKind.EulerRadians => PlacementPrecision.Changed(value.Rotation, current.Rotation, PlacementPrecision.RadianDecimals),
            PlacementRotationKind.HeadingDegrees => PlacementPrecision.Changed(value.Rotation, current.Rotation, PlacementPrecision.HeadingDecimals),
            _ => value.Rotation
        };
        return new(PlacementPrecision.Position(value.Position, current.Position), rotation);
    }
    /// <summary>Validate a transform and derive its linked counterparts' transforms, as an edit would store them.</summary>
    public IReadOnlyDictionary<MissionPickupSource, PlacementTransform> PreviewTransform(MissionPickupSource source, PlacementTransform transform)
    {
        RequireFinite(transform.Position); RequireFinite(transform.Rotation);
        transform = Stored(source, transform);
        var position = transform.Position;
        var kind = RotationKind(source);
        if (kind == PlacementRotationKind.None && transform.Rotation != rotations[source] ||
            kind == PlacementRotationKind.HeadingDegrees && (transform.Rotation.X != 0 || transform.Rotation.Z != 0))
            throw new InvalidDataException("This placement does not support the requested rotation axes.");
        if (otherCoordinates.TryGetValue(source, out var vehicle) && vehicle.Kind == "tank" &&
            (!vehicle.MissionSpecific && (vehicle.TemplateSourceNode == null || vehicle.Difficulties.Count == 0)))
            throw new InvalidDataException("The tank placement has no unambiguous active template binding.");
        if (otherCoordinates.TryGetValue(source, out var record) && (record.Kind == "ai" || record.MissionSpecific) &&
            (Math.Abs(position.X) > 1e12 || Math.Abs(position.Y) > 1e12 || Math.Abs(position.Z) > 1e12))
            throw new InvalidDataException("Mission coordinates must stay within the supported ±1e12 preview range.");
        Vector3 delta = position - positions[source]; RequireFinite(delta);
        Vector3 rotationDelta = transform.Rotation - rotations[source]; RequireFinite(rotationDelta);
        // A counterpart where the source is takes the requested values exactly: arithmetic on the delta would round them
        // apart, and the records would no longer match as counterparts.
        var after = Scope(source).Sources.ToDictionary(s => s, s => sourceComparer.Equals(s, source) || positions[s] == positions[source] && rotations[s] == rotations[source] ? transform
            : Stored(s, new PlacementTransform(positions[s] + delta, rotations[s] + rotationDelta)), sourceComparer);
        foreach (var value in after.Values) { RequireFinite(value.Position); RequireFinite(value.Rotation); }
        return after;
    }
    private static void RequireFinite(Vector3 value)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z)) throw new InvalidDataException("Transform components must be finite game floats.");
    }
    /// <summary>
    /// The scalar writes <paramref name="values"/> need in the source resources: for each component that differs from the
    /// stored value, the resource, the key of its node and the new value (see <see cref="Sources.SourceResourceEdits"/>).
    /// </summary>
    public IReadOnlyList<(string ArchivePath, int Offset, float Value)> ScalarWrites(IReadOnlyDictionary<MissionPickupSource, PlacementTransform> values)
    {
        List<(string, int, float)> writes = [];
        foreach (var (source, transform) in values)
        {
            var entry = entries[source];
            for (int axis = 0; axis < 3; axis++)
                if (transform.Position[axis] != entry.Record.OriginalPosition[axis]) writes.Add((source.ArchivePath, entry.Offsets[axis], transform.Position[axis]));
            for (int i = 0; i < entry.RotationOffsets.Length; i++)
            {
                int axis = entry.RotationOffsets.Length == 1 ? 1 : i;
                if (transform.Rotation[axis] != entry.Record.Rotation[axis]) writes.Add((source.ArchivePath, entry.RotationOffsets[i], transform.Rotation[axis]));
            }
        }
        return writes;
    }
    /// <summary>The authoritative project source recorded by a loaded resource member; no disk read or inference by name.</summary>
    public string? AuthoredResourcePath(MissionPickupSource source)
    {
        if (!archives.TryGetValue(source.ArchivePath, out var archive)) return null;
        if (archive.Original is Sources.SourceResourceDocument document) return document.Source.Path;
        var asset = archive.Original.Assets.FirstOrDefault(a => a.Index == source.AssetIndex && a.Name.Equals(source.ResourceName, StringComparison.OrdinalIgnoreCase));
        string? field = asset?.Metadata.Text("source_path");
        if (field is not { Length: > 0 and <= Sources.ArchiveSources.MaximumSourceField }) return null;
        string relative = field.Replace('\\', '/');
        if (!relative.StartsWith("data/", StringComparison.OrdinalIgnoreCase)) return null;
        try { _ = Sources.SourceProject.Resolve(Path.GetTempPath(), relative); }
        catch (InvalidDataException) { return null; }
        return relative;
    }
    public MissionPickupSource? MatchIn(MissionPickupSource source, MissionSceneContext mission)
    {
        if (!entries.ContainsKey(source)) return null;
        var identities = Scope(source).Sources.ToHashSet(sourceComparer);
        var found = mission.Actors.Where(a => a.Pickup is { } p && identities.Contains(p.Source)).ToArray();
        return found.Length == 1 ? found[0].Pickup!.Source : null;
    }
}
