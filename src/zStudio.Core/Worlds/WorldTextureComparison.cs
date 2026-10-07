namespace Recoil.Zbd.Core.Worlds;

/// <summary>Directory multiplicity and links, pairing repeated names by their directory occurrence, never as one identity.</summary>
internal static class WorldTextureComparison
{
    private sealed class Directory
    {
        internal readonly Dictionary<string, List<WorldTexture>> Names = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<WorldTexture, (string Name, int Occurrence)> Identities = new(ReferenceEqualityComparer.Instance);
        internal Directory(IReadOnlyList<WorldTexture> textures, CancellationToken token)
        {
            foreach (var texture in textures)
            {
                token.ThrowIfCancellationRequested();
                if (!Names.TryGetValue(texture.Name, out var entries)) Names[texture.Name] = entries = [];
                entries.Add(texture); Identities.Add(texture, (texture.Name, entries.Count));
            }
        }
        internal (string Name, int Occurrence)? Target(WorldTexture? texture) => texture == null ? null
            : Identities.TryGetValue(texture, out var id) ? id : (texture.Name, -1);
    }
    internal static IEnumerable<(string Name, string Field, string Expected, string Actual)> Compare(
        IReadOnlyList<WorldTexture> expected, IReadOnlyList<WorldTexture> actual, CancellationToken token)
    {
        Directory a = new(expected, token), b = new(actual, token);
        foreach (string name in a.Names.Keys.Concat(b.Names.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var left = a.Names.GetValueOrDefault(name); var right = b.Names.GetValueOrDefault(name);
            int countA = left?.Count ?? 0, countB = right?.Count ?? 0;
            if (countA != countB) yield return ("textures/" + JsonData.ShownText(name), "count", $"{countA}", $"{countB}");
            for (int i = 0; i < Math.Min(countA, countB); i++)
            {
                token.ThrowIfCancellationRequested();
                var x = left![i]; var y = right![i];
                var nx = a.Target(x.NextVariant); var ny = b.Target(y.NextVariant);
                if (nx?.Occurrence != ny?.Occurrence || !string.Equals(nx?.Name, ny?.Name, StringComparison.OrdinalIgnoreCase))
                    yield return (Label(name, i + 1), "nextVariant", Show(nx), Show(ny));
                if (x.State != y.State) yield return (Label(name, i + 1), "state", $"{x.State}", $"{y.State}");
            }
        }
    }
    private static string Label(string name, int occurrence) => $"textures/{JsonData.ShownText(name)} [{occurrence}]";
    private static string Show((string Name, int Occurrence)? id) => id is { } value
        ? $"{JsonData.ShownText(value.Name)} [{(value.Occurrence < 0 ? "outside directory" : value.Occurrence)}]" : "none";
}
