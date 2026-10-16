using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using zStudio.Core;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

/// <summary>Presentation identity across source evaluations, never a compiled slot, text offset or temporary path.</summary>
internal static class SourceMapActorKeys
{
    internal static Func<MissionActor, string?> Prepare(string buildFolder, SourceSelectionIndex source,
        PickupPlacementEditSession? edits, CancellationToken token, IReadOnlyDictionary<string, string>? readHashes = null)
    {
        Dictionary<MissionActor, string?> actors = new(ReferenceEqualityComparer.Instance);
        Dictionary<MissionPickupSource, string?> records = new(edits?.SourceComparer ?? EqualityComparer<MissionPickupSource>.Default);
        Dictionary<IReadOnlyList<MissionPickupSource>, string?> scopes = new(ReferenceEqualityComparer.Instance);
        Dictionary<string, string?> archives = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<(string Archive, int Member, string Name), string?> content = [];
        return Key;

        string? Key(MissionActor actor)
        {
            token.ThrowIfCancellationRequested();
            if (actors.TryGetValue(actor, out string? cached)) return cached;
            if (!source.Identities.TryGetValue(actor.SourceRoot, out string? template)) return actors[actor] = null;
            if (actor.Name.Length > 8192) throw new InvalidDataException("Source placement name is too long.");
            var record = actor.Pickup?.Source ?? actor.CoordinateSource;
            string kind = actor.Pickup is { } pickup ? "pickup:" + pickup.TypeIndex.ToString(CultureInfo.InvariantCulture) : "actor:" + actor.Name;
            if (record == null || record.ResourceName == "GAMEZ") return actors[actor] = Hash(kind, template);
            IReadOnlyList<MissionPickupSource> scope = edits != null && (edits.Find(record) != null || edits.Coordinate(record) != null)
                ? edits.Scope(record).Sources : [record];
            if (!scopes.TryGetValue(scope, out string? scopeKey))
            {
                if (scope.Count > 200_000) throw new InvalidDataException("Source placement identity exceeds the map presentation limit.");
                List<string> members = [];
                foreach (var member in scope)
                {
                    token.ThrowIfCancellationRequested();
                    if (Record(member) is not { } key) return actors[actor] = null;
                    members.Add(key);
                }
                members.Sort(StringComparer.Ordinal);
                // Each member is a fixed-size digest. Bound aggregate work before
                // retaining data; the source placement store normally links three difficulties.
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                foreach (string member in members) hash.AppendData(Convert.FromHexString(member));
                scopes[scope] = scopeKey = Convert.ToHexString(hash.GetHashAndReset());
            }
            return actors[actor] = Hash(kind, template, scopeKey!);
        }

        string? Record(MissionPickupSource record)
        {
            if (records.TryGetValue(record, out var key)) return key;
            if (record.ResourceName.Length > 8192) throw new InvalidDataException("Source placement resource name is too long.");
            if (!archives.TryGetValue(record.ArchivePath, out string? relative))
            {
                if (record.ArchivePath.Length > 8192) throw new InvalidDataException("Source placement path is too long.");
                relative = Path.GetRelativePath(buildFolder, record.ArchivePath).Replace('\\', '/');
                if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal)) relative = null;
                else relative = relative.ToUpperInvariant();
                archives[record.ArchivePath] = relative;
            }
            // Source sessions precompute a semantic resource fingerprint with only editable coordinate scalars
            // masked, plus record order. Moving an earlier text token cannot change later records' identities.
            if (edits?.SourcePresentationKey(record) is { } sourceKey)
                return records[record] = relative == null ? null : Hash(relative, record.ResourceName.ToUpperInvariant(), sourceKey);
            string digest = "";
            if (readHashes != null)
            {
                var member = (record.ArchivePath, record.AssetIndex, record.ResourceName);
                if (!content.TryGetValue(member, out var captured))
                {
                    string? path = edits?.AuthoredResourcePath(record);
                    captured = path != null && readHashes.TryGetValue(path, out var found) ? found : null;
                    content.Add(member, captured);
                }
                if (captured == null) return records[record] = null;
                digest = captured;
            }
            return records[record] = relative == null ? null : Hash(relative, record.ResourceName.ToUpperInvariant(),
                record.AssetIndex.ToString(CultureInfo.InvariantCulture), record.RecordIndex.ToString(CultureInfo.InvariantCulture), digest);
        }
    }

    private static string Hash(params string[] fields)
    {
        if (fields.Sum(f => (long)f.Length) > 32_768) throw new InvalidDataException("Source placement identity is too large.");
        string framed = string.Concat(fields.Select(f => f.Length.ToString(CultureInfo.InvariantCulture) + ":" + f));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(framed)));
    }
}
