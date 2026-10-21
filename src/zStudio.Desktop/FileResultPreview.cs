using System.Text.Json.Serialization;
using zStudio.Core;

namespace zStudio.Desktop;

/// <summary>Bound publication previews before retained operation JSON is constructed; internal save identities stay complete.</summary>
internal static class FileResultPreview
{
    internal const int MaximumPaths = 64, MaximumCharacters = 512, MaximumErrors = 32;
    internal sealed record Strings(string[] Values, int Count, bool Truncated);
    internal sealed record SaveResult(string[] SavedPaths, int SavedPathCount, bool SavedPathsTruncated,
        string[] Errors, int ErrorCount, bool ErrorsTruncated,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? RemainingPaths = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? RemainingPathCount = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? RemainingPathsTruncated = null);
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

    internal static SaveResult Saved(IReadOnlyList<string> paths, IReadOnlyList<string> errors, IReadOnlyList<string>? remaining = null)
    {
        var saved = Paths(paths, paths.Count);
        var failures = Paths(errors, errors.Count, MaximumErrors);
        var pending = remaining == null ? null : Paths(remaining, remaining.Count);
        return new(saved.Values, saved.Count, saved.Truncated, failures.Values, failures.Count, failures.Truncated,
            pending?.Values, pending?.Count, pending?.Truncated);
    }

    private static string Clip(string value, int maximum = MaximumCharacters)
        => value.Length <= maximum ? value : value[..(maximum - 1)] + "…";
}
