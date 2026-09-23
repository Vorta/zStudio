using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Recoil.Zbd.Core;

public sealed class BinaryCursor(ReadOnlyMemory<byte> data, long origin = 0)
{
    public ReadOnlyMemory<byte> Data { get; } = data;
    public int Position { get; private set; }
    public int Remaining => Data.Length - Position;
    public long AbsolutePosition => origin + Position;
    public static void CheckRange(long total, long offset, long length)
    {
        if (offset < 0 || length < 0 || offset > total || length > total - offset)
            throw new InvalidDataException($"Range 0x{offset:X}+0x{length:X} exceeds 0x{total:X} bytes.");
    }
    public void Require(long length) => CheckRange(Data.Length, Position, length);
    public int Count(uint count, int stride = 1)
    {
        if (count > int.MaxValue || stride < 1 || (long)count * stride > Remaining)
            throw new InvalidDataException($"Invalid count {count} × {stride} at 0x{AbsolutePosition:X}.");
        return (int)count;
    }
    public void Seek(int offset) { CheckRange(Data.Length, offset, 0); Position = offset; }
    public ReadOnlyMemory<byte> Take(int length) { Require(length); var result = Data.Slice(Position, length); Position += length; return result; }
    public void Skip(int length) => _ = Take(length);
    public byte U8() => Take(1).Span[0];
    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2).Span);
    public short I16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2).Span);
    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4).Span);
    public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4).Span);
    public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8).Span);
    public float F32() => BitConverter.UInt32BitsToSingle(U32());
    public string String(int length) => FixedString(Take(length).Span);
    public static string FixedString(ReadOnlySpan<byte> bytes)
    { int nul = bytes.IndexOf((byte)0); return Encoding.Latin1.GetString(nul < 0 ? bytes : bytes[..nul]); }
    public int[] Indices(int count)
    { Count(checked((uint)count), 4); int[] a = new int[count]; for (int i = 0; i < count; i++) a[i] = I32(); return a; }
    public Vector3[] Vectors(int count)
    { Count(checked((uint)count), 12); Vector3[] a = new Vector3[count]; for (int i = 0; i < count; i++) a[i] = new(F32(), F32(), F32()); return a; }
    public Vector2[] Uvs(int count)
    { Count(checked((uint)count), 8); Vector2[] a = new Vector2[count]; for (int i = 0; i < count; i++) a[i] = new(F32(), F32()); return a; }
}
