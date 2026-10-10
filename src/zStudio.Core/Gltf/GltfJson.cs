using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Recoil.Zbd.Core.Gltf;

/// <summary>Writes edited source JSON within the same byte limit its reader accepts, without an oversized intermediate string.</summary>
internal static class GltfJson
{
    internal static byte[] Write(JsonNode root, bool indented, CancellationToken token = default, int maximumBytes = GltfDocument.MaximumJsonBytes)
    {
        if (maximumBytes is < 1 or > GltfDocument.MaximumJsonBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        Buffer buffer = new(maximumBytes, token);
        using (Utf8JsonWriter writer = new(buffer, new() { Indented = indented })) root.WriteTo(writer);
        byte[] bytes = buffer.ToArray();
        GltfDocument.ValidateJsonText(bytes, token);
        return bytes;
    }

    // Utf8JsonWriter requests space before writing, including escaping and indentation. Refuse the reservation before
    // allocation, even for a single enormous scalar, rather than serialize an unbounded DOM to a string and check later.
    private sealed class Buffer(int maximum, CancellationToken token) : IBufferWriter<byte>
    {
        private byte[] bytes = [];
        private int count;
        public void Advance(int length)
        {
            token.ThrowIfCancellationRequested();
            if (length < 0 || length > bytes.Length - count) throw new InvalidOperationException("Invalid JSON buffer advance.");
            count += length;
        }
        public Memory<byte> GetMemory(int sizeHint = 0) { Reserve(sizeHint); return bytes.AsMemory(count); }
        public Span<byte> GetSpan(int sizeHint = 0) { Reserve(sizeHint); return bytes.AsSpan(count); }
        private void Reserve(int sizeHint)
        {
            token.ThrowIfCancellationRequested();
            sizeHint = Math.Max(1, sizeHint);
            if (sizeHint > maximum - count)
                throw new InvalidDataException("The edited glTF JSON exceeds the supported 32 MiB serialization budget. Remove unused metadata or split the model before editing it.");
            if (sizeHint <= bytes.Length - count) return;
            int capacity = (int)Math.Min(maximum, Math.Max((long)count + sizeHint, Math.Max(4096L, 2L * bytes.Length)));
            Array.Resize(ref bytes, capacity);
        }
        internal byte[] ToArray() => bytes.AsSpan(0, count).ToArray();
    }
}
