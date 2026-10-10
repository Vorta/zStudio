using System.Globalization;
using System.Text;

namespace Recoil.Zbd.Core.Animation;

/// <summary>Measures script output before allocating its builder and final string, sharing reconstruction reservations.</summary>
internal sealed class AnimationScriptText(int maximumCharacters, CancellationToken token, StringBuilder? text, Action<long>? reserve)
{
    private int length;

    internal static string? Build(Func<AnimationScriptText, bool> write, CancellationToken token,
        int maximumCharacters = Sources.SourceProject.MaximumSourceTextBytes, Action<long>? reserve = null)
    {
        if (maximumCharacters is < 0 or > Sources.SourceProject.MaximumSourceTextBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        token.ThrowIfCancellationRequested();
        reserve?.Invoke(64); // Builder/result overhead, before retaining either output representation.
        AnimationScriptText measure = new(maximumCharacters, token, null, reserve);
        if (!write(measure)) return null;
        token.ThrowIfCancellationRequested();
        StringBuilder builder = new(measure.length, Math.Max(1, measure.length));
        AnimationScriptText output = new(measure.length, token, builder, null);
        if (!write(output)) return null;
        token.ThrowIfCancellationRequested();
        if (output.length != measure.length) throw new InvalidDataException("The animation script changed while its text was being reconstructed.");
        return builder.ToString();
    }

    internal AnimationScriptText Append(ReadOnlySpan<char> value)
    {
        token.ThrowIfCancellationRequested();
        if (value.Length > maximumCharacters - length)
            throw new InvalidDataException($"The animation script would be larger than {maximumCharacters:N0} bytes, the source limit.");
        // The first pass owns both the exact builder capacity and the eventual immutable UTF-16 string.
        // A shared budget can refuse while measuring, before either allocation or a later worker's publication.
        reserve?.Invoke(4L * value.Length);
        length += value.Length;
        text?.Append(value);
        return this;
    }

    internal AnimationScriptText Append(string value) => Append(value.AsSpan());
    internal AnimationScriptText Append(char value) => Append(new ReadOnlySpan<char>(in value));
    internal AnimationScriptText Append(int value) => Append((long)value);
    internal AnimationScriptText Append(long value)
    {
        Span<char> buffer = stackalloc char[32];
        if (!value.TryFormat(buffer, out int written, provider: CultureInfo.InvariantCulture)) throw new InvalidOperationException("An integer did not fit its script representation.");
        return Append(buffer[..written]);
    }
    internal AnimationScriptText Float(float value)
    {
        Span<char> buffer = stackalloc char[32];
        if (!value.TryFormat(buffer, out int written, "R", CultureInfo.InvariantCulture)) throw new InvalidOperationException("A number did not fit its script representation.");
        return Append(buffer[..written]);
    }
}
