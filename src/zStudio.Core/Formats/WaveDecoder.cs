using System.Text;

namespace Recoil.Zbd.Core.Formats;

public static class WaveDecoder
{
    public static WaveInfo Read(ReadOnlyMemory<byte> bytes, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        BinaryCursor c = new(bytes); if (c.String(4) != "RIFF") throw new InvalidDataException("Missing RIFF marker.");
        uint riffSize = c.U32(); if (c.String(4) != "WAVE") throw new InvalidDataException("Missing WAVE marker.");
        BinaryCursor.CheckRange(bytes.Length, 8, riffSize); int end = checked((int)riffSize + 8);
        ushort encoding = 0, channels = 0, bits = 0, align = 0; uint rate = 0; int dataOffset = -1, dataLength = 0; List<WaveCue> cues = [];
        while (c.Position + 8 <= end)
        {
            token.ThrowIfCancellationRequested();
            string type = Encoding.ASCII.GetString(c.Take(4).Span); uint size = c.U32(); BinaryCursor.CheckRange(end, c.Position, size);
            int start = c.Position; BinaryCursor chunk = new(c.Take((int)size), start);
            if (type == "fmt ") { encoding = chunk.U16(); channels = chunk.U16(); rate = chunk.U32(); chunk.Skip(4); align = chunk.U16(); bits = chunk.U16(); }
            else if (type == "data") { dataOffset = start; dataLength = (int)size; }
            else if (type == "cue ")
            {
                int count = chunk.Count(chunk.U32(), 24);
                for (int i = 0; i < count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    uint id = chunk.U32(); chunk.Skip(16); cues.Add(new(id, chunk.U32()));
                }
            }
            if ((size & 1) != 0 && c.Position < end) c.Skip(1);
        }
        if (dataOffset < 0 || rate == 0 || channels == 0 || align == 0) throw new InvalidDataException("Missing or invalid WAVE format/data chunk.");
        if (encoding == 1 && (bits is not (8 or 16 or 24 or 32) || align != channels * (bits / 8) || dataLength % align != 0))
            throw new InvalidDataException("Invalid PCM sample layout.");
        return new(encoding, channels, rate, bits, align, dataOffset, dataLength, cues);
    }
    public static float[] Peaks(ReadOnlyMemory<byte> bytes, WaveInfo info, int buckets = 1000)
    {
        if (info.Encoding != 1 || info.BitsPerSample is not (8 or 16)) return [];
        int frames = info.DataLength / info.BlockAlign; float[] result = new float[Math.Min(frames, buckets)];
        if (result.Length == 0) return result;
        for (int frame = 0; frame < frames; frame++)
        {
            int start = info.DataOffset + frame * info.BlockAlign; float peak = 0;
            for (int ch = 0; ch < info.Channels; ch++)
            {
                int p = start + ch * (info.BitsPerSample / 8);
                float value = info.BitsPerSample == 8 ? (bytes.Span[p] - 128) / 128f : System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(bytes.Span[p..]) / 32768f;
                peak = Math.Max(peak, Math.Abs(value));
            }
            int bucket = (int)((long)frame * result.Length / frames); result[bucket] = Math.Max(result[bucket], peak);
        }
        return result;
    }
}
