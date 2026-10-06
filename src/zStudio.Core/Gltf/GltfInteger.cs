using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;

namespace Recoil.Zbd.Core.Gltf;

/// <summary>JSON integer properties may use decimals/exponents, but must be exactly integral, without floating rounding.</summary>
internal static class GltfInteger
{
    internal static int? OptionalInt32(JsonNode? node, string what) => node == null ? null : Int32(node, what);
    internal static long? OptionalInt64(JsonNode? node, string what) => node == null ? null : Int64(node, what);
    internal static int Int32(JsonNode? node, string what = "index") => TryInt64(node, out long value) && value is >= int.MinValue and <= int.MaxValue
        ? (int)value : throw new InvalidDataException($"The glTF {what} is not a whole number in the supported range.");
    internal static long Int64(JsonNode? node, string what) => TryInt64(node, out long value) ? value
        : throw new InvalidDataException($"The glTF {what} is not a whole number in the supported range.");

    internal static bool TryInt64(JsonNode? node, out long value)
    {
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
