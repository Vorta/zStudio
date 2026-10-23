namespace zStudio.Desktop;

/// <summary>Bound publication previews before retained operation JSON is constructed; internal save identities stay complete.</summary>
internal static class FileResultPreview
{
    internal const int MaximumPaths = 64, MaximumCharacters = 512;
    internal sealed record Strings(string[] Values, int Count, bool Truncated);
    internal static Strings Paths(IEnumerable<string> paths, int count, int maximum = MaximumPaths)
    {
        List<string> values = []; bool truncated = count > maximum;
        foreach (string path in paths.Take(maximum))
        {
            truncated |= path.Length > MaximumCharacters;
            values.Add(Clip(path));
        }
        return new([.. values], count, truncated);
    }

    private static string Clip(string value, int maximum = MaximumCharacters)
        => value.Length <= maximum ? value : value[..(maximum - 1)] + "…";
}
