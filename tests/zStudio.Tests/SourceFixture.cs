using System.Buffers.Binary;
using System.IO;
using System.Text;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;

namespace Recoil.Zbd.Tests;

/// <summary>
/// A small shipped data folder: common and mission resource archives, three sound banks declared by sounds.zrd,
/// prepared scripts and a file whose family is not reconstructed.
/// </summary>
internal sealed class SourceFixture : IDisposable
{
    private static ZrdNode A(params ZrdNode[] children) => new(Guid.NewGuid(), ZrdKind.Array, 0, "", children);
    private static ZrdNode S(string text) => new(Guid.NewGuid(), ZrdKind.String, 0, text, []);
    private static ZrdNode I(int value) => new(Guid.NewGuid(), ZrdKind.Int, (uint)value, "", []);
    private static ZrdNode F(float value) => new(Guid.NewGuid(), ZrdKind.Float, BitConverter.SingleToUInt32Bits(value), "", []);
    private static ZrdNode Format(int rate, int bits, int channels) => A(I(rate), I(bits), I(channels));
    public static readonly WaveFormat High = new(22050, 16, 1), Medium = new(22050, 8, 1), Low = new(11025, 8, 1);
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "zstudio-source-" + Guid.NewGuid().ToString("N"));
    public string Corpus => Path.Combine(Root, "game");
    public string Project => Path.Combine(Root, "project");
    public byte[] Blob { get; } = [9, 8, 7, 6, 5];
    /// <summary>a.wav is a 16-bit tone with a cue marker; b.wav was recorded at the lowest declared format.</summary>
    public byte[] WaveA { get; } = Tone(High, 2205, 1000);
    public byte[] WaveB { get; } = Tone(Low, 400, null);
    public SourceFixture()
    {
        Directory.CreateDirectory(Path.Combine(Corpus, "m1"));
        var sounds = ZrdWriter.Write(A(
            A(I(1), S("a.wav"), I(0), S("HIGH"), Format(22050, 16, 1), S("MED"), Format(22050, 8, 1), S("LOW"), Format(11025, 8, 1)),
            A(I(2), S("b.wav"), I(0), S("HIGH"), Format(22050, 16, 1), S("MED"), Format(22050, 8, 1), S("LOW"), Format(11025, 8, 1))));
        File.WriteAllBytes(Path.Combine(Corpus, "zrdr.zbd"), Archive(("sounds.zrd", sounds, Field("D:\\battlesportdev\\data\\common\\zrdr\\souE001.TMP"))));
        var ai = ZrdWriter.Write(A(S("GRAVITY"), A(F(-9.8f))));
        var gate = ZrdWriter.Write(A(S("MODEL"), A(S("frcgate"))));
        File.WriteAllBytes(Path.Combine(Corpus, "m1", "zrdr.zbd"), Archive(
            ("ai.zrd", ai, Field("D:\\battlesportdev\\data\\m1\\zrdr\\ai.E3A5.TMP")),
            ("frcgate.zrd", gate, Field("D:\\battlesportdev\\data\\m1\\zrdr\\envmodels\\frcE3B0.TMP")),
            ("blob.bin", Blob, Field("somewhere\\else.TMP"))));
        File.WriteAllBytes(Path.Combine(Corpus, "soundsh.zbd"), Archive(("a.wav", WaveA, Field("a.wav")), ("b.wav", WaveB, Field("b.wav"))));
        File.WriteAllBytes(Path.Combine(Corpus, "soundsm.zbd"), Archive(("a.wav", WaveConverter.Convert(WaveA, Medium), Field("a.wav")), ("b.wav", WaveB, Field("b.wav"))));
        File.WriteAllBytes(Path.Combine(Corpus, "soundsl.zbd"), Archive(("a.wav", WaveConverter.Convert(WaveA, Low), Field("a.wav")), ("b.wav", WaveB, Field("b.wav"))));
        File.WriteAllBytes(Path.Combine(Corpus, "interp.zbd"), Scripts());
        File.WriteAllBytes(Path.Combine(Corpus, "other.bin"), [1, 2, 3]);
    }
    public static byte[] Tone(WaveFormat format, int frames, uint? cue)
    {
        int step = format.Bits / 8; byte[] pcm = new byte[frames * step * format.Channels];
        for (int f = 0; f < frames; f++)
            for (int c = 0; c < format.Channels; c++)
            {
                double value = 0.5 * Math.Sin(2 * Math.PI * 440 * f / format.Rate) * (c == 0 ? 1 : -1); int p = (f * format.Channels + c) * step;
                if (step == 1) pcm[p] = (byte)Math.Round(value * 127 + 128); else BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(p), (short)Math.Round(value * 32767));
            }
        return WaveConverter.Write(format, pcm, cue is { } position ? [(1u, position)] : []);
    }
    private static byte[] Field(string text) { byte[] b = new byte[64]; Encoding.Latin1.GetBytes(text).CopyTo(b, 0); return b; }
    public static byte[] Archive(params (string Name, byte[] Data, byte[] Source)[] members)
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
            byte[] record = new byte[128]; Encoding.Latin1.GetBytes(name).CopyTo(record, 0);
            var instructions = lines.Select(t => new ScriptInstruction(Guid.NewGuid(), t, ReadOnlyMemory<byte>.Empty, null)).ToArray();
            return new(Guid.NewGuid(), null, name, time, record, instructions, ReadOnlyMemory<byte>.Empty);
        }
        var package = new PreparedScriptPackage(header, ReadOnlyMemory<byte>.Empty,
            [Entry("support\\common.gw", 912_000_000, ["set", "ZBD_DIR", "zbd"], ["mkdir", "%ZBD_DIR%", ""]), Entry("m1.gs", 912_000_100, ["source", "support\\common.gw"])], ReadOnlyMemory<byte>.Empty);
        return PreparedScriptWriter.Write(package);
    }
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
}
