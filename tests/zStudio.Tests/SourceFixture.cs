using System.Buffers.Binary;
using System.IO;
using System.Text;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Tests;

/// <summary>A small shipped data folder: one resource archive, two sound banks, prepared scripts and an unknown file.</summary>
internal sealed class SourceFixture : IDisposable
{
    private static ZrdNode A(params ZrdNode[] children) => new(Guid.NewGuid(), ZrdKind.Array, 0, "", children);
    private static ZrdNode S(string text) => new(Guid.NewGuid(), ZrdKind.String, 0, text, []);
    private static ZrdNode F(float value) => new(Guid.NewGuid(), ZrdKind.Float, BitConverter.SingleToUInt32Bits(value), "", []);
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "zstudio-source-" + Guid.NewGuid().ToString("N"));
    public string Corpus => Path.Combine(Root, "game");
    public string Project => Path.Combine(Root, "project");
    public byte[] Blob { get; } = [9, 8, 7, 6, 5];
    public byte[] WaveA { get; } = Wave(1);
    public byte[] WaveB { get; } = Wave(2);
    public byte[] WaveBLow { get; } = Wave(3);
    public SourceFixture()
    {
        Directory.CreateDirectory(Path.Combine(Corpus, "m1"));
        var ai = ZrdWriter.Write(A(A(S("GRAVITY"), A(F(-9.8f)))));
        var gate = ZrdWriter.Write(A(A(S("MODEL"), A(S("frcgate")))));
        File.WriteAllBytes(Path.Combine(Corpus, "m1", "zrdr.zbd"), Archive(
            ("ai.zrd", ai, Field("D:\\battlesportdev\\data\\m1\\zrdr\\ai.E3A5.TMP")),
            ("frcgate.zrd", gate, Field("D:\\battlesportdev\\data\\m1\\zrdr\\envmodels\\frcE3B0.TMP")),
            ("blob.bin", Blob, Field("somewhere\\else.TMP"))));
        byte[] garbage = Enumerable.Range(0, 64).Select(i => (byte)(0xF7 ^ i)).ToArray();
        File.WriteAllBytes(Path.Combine(Corpus, "soundsh.zbd"), Archive(("a.wav", WaveA, garbage), ("b.wav", WaveB, garbage), ("a.wav", WaveA, garbage)));
        File.WriteAllBytes(Path.Combine(Corpus, "soundsm.zbd"), Archive(("a.wav", WaveA, garbage), ("b.wav", WaveBLow, garbage), ("a.wav", WaveA, garbage)));
        File.WriteAllBytes(Path.Combine(Corpus, "interp.zbd"), Scripts());
        File.WriteAllBytes(Path.Combine(Corpus, "other.bin"), [1, 2, 3]);
    }
    private static byte[] Wave(byte fill)
    {
        byte[] b = new byte[64]; "RIFF"u8.CopyTo(b); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 56); "WAVE"u8.CopyTo(b.AsSpan(8)); b.AsSpan(12).Fill(fill); return b;
    }
    private static byte[] Field(string text) { byte[] b = new byte[64]; Encoding.Latin1.GetBytes(text).CopyTo(b, 0); return b; }
    private static byte[] Archive(params (string Name, byte[] Data, byte[] Source)[] members)
    {
        using MemoryStream s = new(); using BinaryWriter w = new(s);
        foreach (var m in members) w.Write(m.Data);
        uint offset = 0;
        foreach (var m in members)
        {
            w.Write(offset); w.Write(m.Data.Length); offset += (uint)m.Data.Length;
            byte[] name = new byte[64]; Encoding.Latin1.GetBytes(m.Name).CopyTo(name, 0); w.Write(name);
            w.Write(0x7ddfu); w.Write(m.Source); w.Write(0x01be0c9a_5d8f1e00ul);
        }
        w.Write(1); w.Write(members.Length); return s.ToArray();
    }
    private static byte[] Scripts()
    {
        byte[] header = new byte[12]; BinaryPrimitives.WriteUInt32LittleEndian(header, 0x08971119); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 7);
        PreparedScriptEntry Entry(string name, uint time, params string[][] lines)
        {
            byte[] record = new byte[128]; Encoding.Latin1.GetBytes(name).CopyTo(record, 0); record[125] = 0xEE;
            var instructions = lines.Select((t, n) => new ScriptInstruction(Guid.NewGuid(), t, ReadOnlyMemory<byte>.Empty, null) { Padding = n == 0 ? new byte[] { 0, 0 } : ReadOnlyMemory<byte>.Empty }).ToArray();
            return new(Guid.NewGuid(), null, name, time, record, instructions, ReadOnlyMemory<byte>.Empty);
        }
        var package = new PreparedScriptPackage(header, ReadOnlyMemory<byte>.Empty,
            [Entry("support\\common.gw", 912_000_000, ["set", "ZBD_DIR", "zbd"], ["mkdir", "%ZBD_DIR%", ""]), Entry("m1.gs", 912_000_100, ["source", "support\\common.gw"])], ReadOnlyMemory<byte>.Empty);
        return PreparedScriptWriter.Write(package);
    }
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
}
