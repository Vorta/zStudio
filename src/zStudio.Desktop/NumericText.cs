using System.Globalization;
using Recoil.Zbd.Core.Animation;

namespace Recoil.Zbd.Desktop;

/// <summary>Plain decimal display without rounding away stored single-precision values.</summary>
internal static class NumericText
{
    public static string ToEditorText(this float value)
    {
        // Expand the shortest round-trip representation, rather than using a fixed
        // number of decimal places (which would lose tiny values or add float noise).
        string text = value.ToString("R", CultureInfo.InvariantCulture);
        int exponentIndex = text.IndexOf('E');
        if (exponentIndex < 0) return text;

        int exponent = int.Parse(text.AsSpan(exponentIndex + 1), CultureInfo.InvariantCulture);
        string sign = text[0] == '-' ? "-" : "";
        string mantissa = text[sign.Length..exponentIndex];
        int point = mantissa.IndexOf('.');
        int decimalPosition = (point < 0 ? mantissa.Length : point) + exponent;
        string digits = mantissa.Replace(".", "", StringComparison.Ordinal);
        return sign + (decimalPosition <= 0
            ? "0." + new string('0', -decimalPosition) + digits
            : decimalPosition >= digits.Length
                ? digits + new string('0', decimalPosition - digits.Length)
                : digits.Insert(decimalPosition, "."));
    }

    public static string FormatForEditor(this AnimationField field, AnimationRecord record) => field.Kind switch
    {
        AnimationFieldKind.Float => record.F32(field.Offset).ToEditorText(),
        AnimationFieldKind.Vector => string.Join(", ", Enumerable.Range(0, 3)
            .Select(axis => record.F32(field.Offset + axis * 4).ToEditorText())),
        _ => field.Format(record)
    };
}
