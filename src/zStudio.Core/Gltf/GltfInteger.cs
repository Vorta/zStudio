using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;

namespace Recoil.Zbd.Core.Gltf;

/// <summary>JSON integer properties may use decimals/exponents, but must be exactly integral, without floating rounding.</summary>
internal static class GltfInteger
{
    internal static int? OptionalInt32(JsonNode? node, string what, NumericWork? work = null) => node == null ? null : Int32(node, what, work);
    internal static long? OptionalInt64(JsonNode? node, string what, NumericWork? work = null) => node == null ? null : Int64(node, what, work);
    internal static int Int32(JsonNode? node, string what = "index", NumericWork? work = null) => TryInt64(node, out long value, work) && value is >= int.MinValue and <= int.MaxValue
        ? (int)value : throw new InvalidDataException($"The glTF {what} is not a whole number in the supported range.");
    internal static long Int64(JsonNode? node, string what, NumericWork? work = null) => TryInt64(node, out long value, work) ? value
        : throw new InvalidDataException($"The glTF {what} is not a whole number in the supported range.");

    internal static bool TryInt64(JsonNode? node, out long value, NumericWork? work = null)
    {
        Admit(node, work);
        value = 0;
        if (node is not JsonValue v) return false;
        if (v.TryGetValue(out JsonElement element))
            return element.ValueKind == JsonValueKind.Number && Parse(JsonMarshal.GetRawUtf8Value(element), out value);
        if (v.TryGetValue(out long integer)) { value = integer; return true; }
        // Values constructed in memory have no JSON token. Serialization is bounded by their scalar numeric type.
        if (!(v.TryGetValue<int>(out _) || v.TryGetValue<uint>(out _) || v.TryGetValue<ulong>(out _) ||
              v.TryGetValue<float>(out _) || v.TryGetValue<double>(out _) || v.TryGetValue<decimal>(out _))) return false;
        try { return Parse(Encoding.UTF8.GetBytes(v.ToJsonString()), out value); }
        catch (ArgumentException) { return false; }
    }

    // Only interpreted numbers are admitted here. Unknown numeric metadata remains lossless in the source DOM.
    internal const int MaximumNumericBytes = 4096;
    internal static void Admit(JsonNode? node, NumericWork? work = null)
    {
        int length = node is JsonValue value && value.TryGetValue(out JsonElement element) && element.ValueKind == JsonValueKind.Number
            ? JsonMarshal.GetRawUtf8Value(element).Length : 32; // Upper bound for the supported CLR numeric scalar spellings.
        work?.CheckCancellation();
        if (length > MaximumNumericBytes)
            throw new InvalidDataException("An interpreted glTF number exceeds the 4,096-byte numeric token limit. Shorten its decimal or exponent spelling without changing its value.");
        work?.Reserve(length);
    }

    internal sealed class NumericWork(long maximum = NumericWork.DefaultMaximum, CancellationToken token = default)
    {
        internal const long DefaultMaximum = 64L * 1024 * 1024;
        internal long Used { get; private set; }
        private bool exhausted;
        internal void CheckCancellation() => token.ThrowIfCancellationRequested();
        internal void Reserve(int bytes)
        {
            token.ThrowIfCancellationRequested();
            // Parse scans the raw token at most four times; charge a fixed invocation unit as well.
            long charge = 4L * bytes + 1;
            if (exhausted || charge > maximum - Used)
            {
                exhausted = true;
                throw new InvalidDataException("The glTF numeric interpretation work budget was exceeded. Simplify repeated numeric spellings or split the model.");
            }
            Used += charge;
        }
    }

    private static bool Parse(ReadOnlySpan<byte> text, out long value)
    {
        value = 0;
        bool negative = text[0] == '-';
        int begin = negative ? 1 : 0, end = text.IndexOfAny((byte)'e', (byte)'E');
        if (end < 0) end = text.Length;
        int digits = 0, fraction = 0, first = -1, last = -1; bool dot = false;
        for (int i = begin; i < end; i++)
        {
            if (text[i] == '.') { dot = true; continue; }
            if (text[i] != '0') { if (first < 0) first = digits; last = digits; }
            digits++; if (dot) fraction++;
        }
        if (first < 0) return true;
        long exponent = 0; bool minus = false;
        for (int i = end + 1; i < text.Length; i++)
        {
            if (text[i] is (byte)'+' or (byte)'-') { minus = text[i] == '-'; continue; }
            exponent = Math.Min(4_000_000_000L, exponent * 10 + text[i] - '0');
        }
        long whole = digits - (long)fraction + (minus ? -exponent : exponent);
        if (whole <= last || whole - first > 19) return false;
        ulong limit = negative ? 9223372036854775808UL : long.MaxValue, result = 0;
        int position = 0;
        for (int i = begin; i < end && position < whole; i++)
        {
            if (text[i] == '.') continue;
            int digit = text[i] - '0'; position++;
            if (result > (limit - (uint)digit) / 10) return false;
            result = result * 10 + (uint)digit;
        }
        for (long i = digits; i < whole; i++) { if (result > limit / 10) return false; result *= 10; }
        value = negative ? result == 9223372036854775808UL ? long.MinValue : -(long)result : (long)result;
        return true;
    }
}
