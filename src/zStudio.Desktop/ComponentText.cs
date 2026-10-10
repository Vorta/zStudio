using System.Globalization;
using System.IO;

namespace Recoil.Zbd.Desktop;

/// <summary>Bound compound-field presentation and numeric parsing before splitting authored text.</summary>
internal static class ComponentText
{
    internal const int MaximumNumericCharacters = 4096;
    internal static void CheckNumeric(string value)
    {
        if (value.Length > MaximumNumericCharacters)
            throw new InvalidDataException($"A numeric field accepts at most {MaximumNumericCharacters:N0} characters. Enter only its scalar or component values.");
    }

    /// <summary>Validate an authored list completely before allocating any of its token strings.</summary>
    internal static string[] BoundedTokens(ReadOnlySpan<char> value, ReadOnlySpan<char> separators, int minimum, int maximum,
        int maximumTokenCharacters, string error)
    {
        Range[] ranges = new Range[maximum]; int count = 0;
        foreach (var range in value.SplitAny(separators))
        {
            int length = range.End.Value - range.Start.Value;
            if (length == 0) continue;
            if (count == maximum || length > maximumTokenCharacters) throw new FormatException(error);
            ranges[count++] = range;
        }
        if (count < minimum) throw new FormatException(error);
        string[] result = new string[count];
        for (int i = 0; i < count; i++) result[i] = value[ranges[i]].ToString();
        return result;
    }

    // A nonnumeric pipe-delimited component may contain an arbitrarily long authored text. Limit the number of
    // materialized components, never clip their identities. Invalid/oversized numeric drafts stay as one full string.
    internal static bool TrySplit(string value, string separator, int maximum, out string[] parts)
    {
        parts = [];
        if (separator != "|" && value.Length > MaximumNumericCharacters) return false;
        Range[] ranges = new Range[maximum]; int count = 0;
        foreach (var range in value.AsSpan().SplitAny(separator == "|" ? "|" : ", \t"))
        {
            if (separator != "|" && range.Start.Value == range.End.Value) continue;
            if (count == maximum) return false;
            ranges[count++] = range;
        }
        parts = new string[count];
        for (int i = 0; i < count; i++) parts[i] = value.AsSpan()[ranges[i]].ToString();
        return true;
    }

    internal static string[] Display(string value, string separator, int maximum) => TrySplit(value, separator, maximum, out var parts) ? parts : [value];

    internal static string[] Require(string value, string separator, int count)
    {
        if (!TrySplit(value, separator, count, out var parts) || parts.Length != count)
            throw new InvalidDataException($"Enter all {count} components.");
        return parts;
    }

    internal static float[] ParseFinite(string value, int count, bool commaOnly = false)
    {
        CheckNumeric(value);
        float[] numbers = new float[count]; int at = 0;
        foreach (var range in value.AsSpan().SplitAny(commaOnly ? "," : ", "))
        {
            var text = value.AsSpan()[range];
            if (commaOnly) text = text.Trim();
            if (!commaOnly && text.IsEmpty) continue;
            if (at == count || !float.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out float number) || !float.IsFinite(number))
                throw new InvalidDataException($"Enter {count} finite components.");
            numbers[at++] = number;
        }
        if (at != count) throw new InvalidDataException($"Enter {count} finite components.");
        return numbers;
    }
}
