using System;
using System.Buffers.Binary;
using System.Text;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Tests;

internal static class ContentFixture
{
    internal static byte[] Scripts()
    {
        byte[] header = new byte[12]; BinaryPrimitives.WriteUInt32LittleEndian(header, 0x08971119); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 7);
        byte[] record = new byte[128]; Encoding.Latin1.GetBytes("same").CopyTo(record, 0); record[50] = 0xce;
        string[] tokens = ["strange command", "", "é\n\\\""]; byte[] strings = Encoding.Latin1.GetBytes(string.Join('\0', tokens) + "\0");
        byte[] raw = new byte[strings.Length + 11]; BinaryPrimitives.WriteInt32LittleEndian(raw, strings.Length + 3); BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(4), tokens.Length); strings.CopyTo(raw, 8); raw[^1] = 0x7f;
        var instruction = new ScriptInstruction(Guid.NewGuid(), tokens, raw, null);
        var entry = new PreparedScriptEntry(Guid.NewGuid(), 0, "same", 123, record, [instruction], new byte[] { 0xde, 0xad });
        return PreparedScriptWriter.Write(new(header, new byte[] { 9, 8, 7 }, [entry, entry with { Id = Guid.NewGuid(), SourceIndex = 1, FollowingBytes = ReadOnlyMemory<byte>.Empty }], new byte[] { 0xfa, 0xfb }));
    }
    internal static byte[] DamagedScripts(int index, string damage)
    {
        var package = FormatRegistry.Default.OpenBytes("interp.zbd", Scripts()).Scripts!;
        var entries = Enumerable.Range(0, 3).Select(i => package.Entries[0].Duplicate($"script-{i}.zrd")).ToArray();
        byte[] bytes = PreparedScriptWriter.Write(package with { Entries = entries });
        int directoryOffset = 12 + index * 128 + 124;
        int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(directoryOffset));
        switch (damage)
        {
            case "count": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 4), uint.MaxValue); break;
            case "size": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), uint.MaxValue); break;
            case "string": bytes.AsSpan(offset + 8, (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset))).Fill(0x41); break;
            case "terminator": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + entries[index].Instructions[0].Raw.Length), 1); break;
            case "index": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(directoryOffset), 1); break;
            case "outside": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(directoryOffset), (uint)bytes.Length + 1); break;
            default: throw new ArgumentException("Unknown fixture damage.", nameof(damage));
        }
        return bytes;
    }
    internal static byte[] Texture(int width, int height, bool shared)
    {
        const int table = 104; int offset = table + (shared ? 512 : 0), pixels = width * height;
        byte[] bytes = new byte[offset + 16 + pixels + (shared ? 0 : 4)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 1); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), shared ? 1 : 0); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), 2);
        for (int i = 0; i < 2; i++) { Encoding.ASCII.GetBytes(i == 0 ? "sample" : "other").CopyTo(bytes, 24 + i * 40); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(56 + i * 40), offset); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(60 + i * 40), shared ? 0 : -1); }
        bytes[offset] = shared ? (byte)0x11 : (byte)1;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 4), (ushort)width); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 6), (ushort)height); bytes[offset + 12] = 2;
        for (int i = 0; i < pixels; i++) bytes[offset + 16 + i] = (byte)(i % 2);
        int palette = shared ? table : offset + 16 + pixels;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(palette), 0xf800); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(palette + 2), 0x07e0); return bytes;
    }
}
