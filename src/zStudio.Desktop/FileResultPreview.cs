using System.Text.Json.Serialization;
using Recoil.Zbd.Core;

namespace Recoil.Zbd.Desktop;

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
    internal sealed record FileRow(string Path, string destination, bool PathTruncated, bool destinationTruncated,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DiagnosticRow[]? diagnostics = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? diagnosticCount = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? diagnosticsTruncated = null);
    internal sealed record DiagnosticRow(string Severity, string Message, int? AssetIndex, long? Offset, bool MessageTruncated);
    internal sealed record Files(FileRow[] Values, int Count, bool Truncated);

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

    internal static Files Content(ContentEditSession edits, bool diagnostics = false, bool includeDetails = true)
    {
        List<FileRow> rows = []; int count = 0; bool truncated = false;
        foreach (var document in edits.Documents)
        {
            count++;
            if (!includeDetails || rows.Count == MaximumPaths) { truncated = true; continue; }
            string path = document.Path, target = edits.TargetPath(path);
            bool pathTruncated = path.Length > MaximumCharacters, targetTruncated = target.Length > MaximumCharacters;
            truncated |= pathTruncated || targetTruncated;
            DiagnosticRow[]? notes = diagnostics ? document.Diagnostics.Take(4).Select(d =>
                new DiagnosticRow(Clip(d.Severity, 32), Clip(d.Message, 256), d.AssetIndex, d.Offset, d.Message.Length > 256)).ToArray() : null;
            rows.Add(new(Clip(path), Clip(target), pathTruncated, targetTruncated, notes,
                diagnostics ? document.Diagnostics.Count : null,
                diagnostics ? document.Diagnostics.Count > 4 || notes!.Any(d => d.MessageTruncated) : null));
        }
        return new([.. rows], count, truncated);
    }

    private static string Clip(string value, int maximum = MaximumCharacters)
        => value.Length <= maximum ? value : value[..(maximum - 1)] + "…";
}
