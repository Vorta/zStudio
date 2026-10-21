using System.Numerics;
using zStudio.Core.Formats;
using zStudio.Core.Worlds;

namespace zStudio.Core.Sources;

public sealed record SourcePickupAddition(string Type, Vector3 Position, string Difficulty,
    Vector3 RotationDegrees = default, int? Amount = null, float RespawnDelay = 0);
public sealed record SourceAddedPickup(string File, int RecordIndex, MissionDifficulty Difficulty);

/// <summary>Append source placements without changing existing records or sharing a difficulty edit accidentally.</summary>
public static class SourcePickupEdits
{
    public static MissionPickupType Validate(SourcePickupAddition addition)
    {
        var type = MissionPickupType.Catalog.FirstOrDefault(t => t.Name == addition.Type)
            ?? throw new InvalidDataException("Choose a type from the pickup catalog.");
        if (addition.Difficulty is not ("All" or "Easy" or "Medium" or "Hard"))
            throw new InvalidDataException("Choose All, Easy, Medium or Hard.");
        if (!Finite(addition.Position) || !Finite(addition.RotationDegrees))
            throw new InvalidDataException($"Pickup position and rotation must be finite numbers within ±{Bound}.");
        if (addition.Amount is < 0)
            throw new InvalidDataException("Amount must be zero or greater.");
        if (!float.IsFinite(addition.RespawnDelay) || addition.RespawnDelay < 0 || addition.RespawnDelay > SourceWorlds.MaximumCoordinate)
            throw new InvalidDataException($"Respawn delay must be a finite number from 0 to {Bound}.");
        return type;
    }
    private static readonly string Bound = SourceWorlds.MaximumCoordinate.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    public static IReadOnlyList<SourceAddedPickup> Add(SourceWorkspace workspace, SourceSceneSnapshot snapshot,
        SourcePickupAddition addition, CancellationToken token = default)
    {
        var type = Validate(addition);
        List<(string Relative, byte[]? Content)> changes = [];
        List<SourceAddedPickup> added = [];
        Dictionary<string, (SourceSceneResource Resource, byte[] Bytes)> read = new(StringComparer.OrdinalIgnoreCase);
        bool all = addition.Difficulty == "All";
        var selected = Enum.GetValues<MissionDifficulty>().Where(d => all || d.ToString() == addition.Difficulty).ToArray();
        var targets = new Dictionary<string, List<MissionDifficulty>>(StringComparer.OrdinalIgnoreCase);
        foreach (var difficulty in selected)
        {
            var resource = Effective(difficulty);
            string path = resource.Path;
            // A missing variant must become independent before it receives an exclusive edit.
            if (!all && difficulty != MissionDifficulty.Medium && !snapshot.Resources.ContainsKey(MissionLayoutSelection.For(difficulty).PickupResource))
                path = VariantPath(resource, difficulty);
            if (!targets.TryGetValue(path, out var difficulties)) targets[path] = difficulties = [];
            difficulties.Add(difficulty);
        }
        // Medium owns puppies.zrd. Preserve any unselected layouts that currently fall back to it.
        if (!all && selected[0] == MissionDifficulty.Medium)
            foreach (var difficulty in new[] { MissionDifficulty.Easy, MissionDifficulty.Hard })
                if (!snapshot.Resources.ContainsKey(MissionLayoutSelection.For(difficulty).PickupResource))
                {
                    var fallback = Effective(difficulty);
                    changes.Add((VariantPath(fallback, difficulty), Read(fallback)));
                }
        foreach (var (path, difficulties) in targets)
        {
            token.ThrowIfCancellationRequested();
            var resource = Effective(difficulties[0]);
            var bytes = Read(resource);
            var syntax = resource.Syntax;
            var root = resource.Root ?? throw new InvalidDataException($"{resource.Path} is not a valid pickup resource.");
            if (root.Children is not [{ Kind: ZrdKind.Array } list])
                throw new InvalidDataException($"{resource.Path} must contain one ordered pickup list.");
            var rotation = StoredRadians(addition.RotationDegrees);
            ZrdNode record = ZrdNode.Create(ZrdKind.Array) with
            {
                Children = [new(Guid.NewGuid(), ZrdKind.String, 0, type.Name, []),
                    new(Guid.NewGuid(), ZrdKind.Int, checked((uint)(addition.Amount ?? type.DefaultAmount)), "", []),
                    Vector(PlacementPrecision.Position(addition.Position)), Vector(rotation), SourceResourceEdits.Float(addition.RespawnDelay)]
            };
            var edited = root with { Children = [list with { Children = [.. list.Children, record] }] };
            byte[] content;
            if (syntax != null)
            {
                var rewritten = syntax.Rewrite(edited, token);
                if (!rewritten.Lossless) throw new InvalidDataException($"Adding a pickup could not preserve the source layout of {resource.Path}.");
                content = ZrdTextSyntax.Encode(rewritten.Text);
            }
            else
            {
                content = ZrdWriter.Write(edited, token);
                if (!ZrdTextSyntax.StructurallyEqual(edited, ZrdDecoder.Read(content, token)))
                    throw new InvalidDataException("The added pickup did not read back correctly.");
            }
            changes.Add((path, content));
            added.AddRange(difficulties.Select(d => new SourceAddedPickup(path, list.Children.Count, d)));
        }
        workspace.Apply($"Add {type.Name} ({addition.Difficulty})", changes, token);
        return added;

        SourceSceneResource Effective(MissionDifficulty difficulty)
        {
            string requested = MissionLayoutSelection.For(difficulty).PickupResource;
            if (!snapshot.Resources.TryGetValue(requested, out var resource)) snapshot.Resources.TryGetValue("puppies.zrd", out resource);
            if (resource == null || resource.Root == null)
                throw new InvalidDataException($"The {difficulty} pickup resource is missing or malformed.");
            if (!resource.Path.StartsWith($"data/{snapshot.Mission}/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Pickup creation requires mission-owned resources; a common resource cannot be changed for just this map.");
            return resource;
        }
        byte[] Read(SourceSceneResource resource)
        {
            if (read.TryGetValue(resource.Path, out var existing)) return existing.Bytes;
            byte[] bytes = workspace.Read(resource.Path, token, ProjectReadLimits.Resource())
                ?? throw new InvalidDataException($"The resource {resource.Path} is missing.");
            if (!bytes.AsSpan().SequenceEqual(resource.Bytes))
                throw new SourceFileChangedException($"{resource.Path} changed; reload the map.", [resource.Path]);
            read.Add(resource.Path, (resource, bytes));
            return bytes;
        }
        string VariantPath(SourceSceneResource resource, MissionDifficulty difficulty)
        {
            string path = Path.GetDirectoryName(resource.Path)!.Replace('\\', '/') + "/" + MissionLayoutSelection.For(difficulty).PickupResource;
            if (workspace.Exists(path, token))
                throw new InvalidDataException($"{path} exists but is not the effective resource; reload and resolve the resource conflict.");
            return path;
        }
    }

    /// <summary>Verify actual clone/placement behavior, including bvol copying, before accepting the prepared transaction.</summary>
    public static void Verify(SourceSceneSnapshot snapshot, IReadOnlyList<SourceAddedPickup> added, SourcePickupAddition addition, CancellationToken token)
    {
        foreach (var group in added.GroupBy(a => a.Difficulty))
        {
            token.ThrowIfCancellationRequested();
            var mission = snapshot.MissionScene(group.Key, token);
            foreach (var item in group)
                if (!mission.Actors.Any(a => a.Pickup is { } p && p.Source.RecordIndex == item.RecordIndex &&
                    string.Equals(Path.GetFullPath(p.Source.ArchivePath), Path.GetFullPath(Path.Combine(snapshot.Root, item.File)), StringComparison.OrdinalIgnoreCase) &&
                    p.LogicalName == addition.Type && p.Position == PlacementPrecision.Position(addition.Position)))
                    throw new InvalidDataException(Cause(mission, item) is { } cause ? $"The {group.Key} pickup cannot be placed: {cause}"
                        : $"The {group.Key} pickup cannot be placed. Check its template and copyable bvol collision child.");
        }

        // The placement's own note names the template or bvol problem, when the bounded notes kept it.
        static string? Cause(MissionSceneContext mission, SourceAddedPickup item)
        {
            string prefix = $"Mission pickup {Path.GetFileName(item.File)} #{item.RecordIndex} in ";
            string? note = mission.Diagnostics.FirstOrDefault(d => d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            int colon = note?.IndexOf(": ", prefix.Length, StringComparison.Ordinal) ?? -1;
            return colon < 0 ? null : note![(colon + 2)..];
        }
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) &&
        Math.Abs(v.X) <= SourceWorlds.MaximumCoordinate && Math.Abs(v.Y) <= SourceWorlds.MaximumCoordinate && Math.Abs(v.Z) <= SourceWorlds.MaximumCoordinate;
    /// <summary>A new pickup's rotation as stored: radians at <see cref="PlacementPrecision.RadianDecimals"/> decimals.</summary>
    private static Vector3 StoredRadians(Vector3 degrees)
    {
        var radians = degrees * (MathF.PI / 180);
        return new(PlacementPrecision.Round(radians.X, PlacementPrecision.RadianDecimals), PlacementPrecision.Round(radians.Y, PlacementPrecision.RadianDecimals),
            PlacementPrecision.Round(radians.Z, PlacementPrecision.RadianDecimals));
    }
    private static ZrdNode Vector(Vector3 v) => ZrdNode.Create(ZrdKind.Array) with
    { Children = [SourceResourceEdits.Float(v.X), SourceResourceEdits.Float(v.Y), SourceResourceEdits.Float(v.Z)] };
}
