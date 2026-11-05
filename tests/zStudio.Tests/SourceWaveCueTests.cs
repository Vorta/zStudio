using System.Buffers.Binary;
using System.Text;
using zStudio.Core.Formats;
using zStudio.Core.Sources;
using Xunit;

namespace zStudio.Tests;

public sealed class SourceWaveCueTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    internal const int Frames = 32;

    // Protects the reproduced high-bank data-integrity defect, including either invalid field and the inclusive end.
    [Fact]
    public void UnchangedFormatPatchesOnlyInvalidCueFields()
    {
        byte[] source = Wave(); source[Record(1) + 12] = 0xb7; // A valid cue's unrelated metadata must survive too.
        byte[] original = source.ToArray(), expected = source.ToArray();
        foreach (int index in new[] { 0, 3, 4 })
        {
            BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(Record(index) + 4), Frames);
            BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(Record(index) + 20), Frames);
        }
        Assert.Equal(expected, WaveConverter.Convert(source, SourceFixture.High, Token));
        Assert.Equal(original, source);
    }

    [Theory]
    [InlineData(22050)]
    [InlineData(11025)]
    public void ConversionKeepsSamplesAndValidCuesAndMovesInvalidCuesToOutputEnd(int rate)
    {
        byte[] source = Wave(), control = source.ToArray();
        // The control's invalid cues already name the end; conversion must produce exactly these same bytes.
        foreach (int index in new[] { 0, 3, 4 })
        {
            BinaryPrimitives.WriteUInt32LittleEndian(control.AsSpan(Record(index) + 4), Frames);
            BinaryPrimitives.WriteUInt32LittleEndian(control.AsSpan(Record(index) + 20), Frames);
        }
        var target = new WaveFormat(rate, 8, 1);
        byte[] result = WaveConverter.Convert(source, target, Token);
        Assert.Equal(WaveConverter.Convert(control, target, Token), result);
        int end = rate == 22050 ? Frames : Frames / 2;
        var cues = WaveDecoder.Read(result, Token).Cues;
        Assert.Equal(new uint[] { (uint)end, (uint)Math.Round(31d * rate / 22050), (uint)end, (uint)end, (uint)end }, cues.Select(c => c.SampleOffset));
        if (rate == 22050)
            foreach (int index in new[] { 1, 2 })
                Assert.True(source.AsSpan(Record(index), 24).SequenceEqual(result.AsSpan(cues[index].RecordOffset, 24)));
    }

    [Fact]
    public async Task CheckAndExportWarnOncePerSourceCueIncludingLowerBankOnlyAndUndeclaredSounds()
    {
        using var fixture = new SourceFixture();
        await SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, token: Token);
        string input = "data/common/sounds/a.wav", path = SourceProject.Resolve(fixture.Project, input);
        byte[] source = Wave(); File.WriteAllBytes(path, source);
        var check = await SourceBuilder.CheckAsync(fixture.Project, SourceBuilder.Banks, token: Token);
        AssertWarnings(check, input);
        var lower = await SourceBuilder.CheckAsync(fixture.Project, ["soundsm.zbd"], token: Token);
        AssertWarnings(lower, input);
        string destination = Path.Combine(fixture.Root, "export");
        AssertWarnings(await SourceBuilder.ExportAsync(fixture.Project, destination, SourceBuilder.Banks, token: Token), input);
        Assert.Equal(source, File.ReadAllBytes(path));
        // Undeclared WAVs retain their format, but must not bypass cue correction.
        File.Move(path, SourceProject.Resolve(fixture.Project, "data/common/sounds/extra.wav"));
        var undeclared = await SourceBuilder.ExportAsync(fixture.Project, Path.Combine(fixture.Root, "undeclared"), ["soundsl.zbd"], token: Token);
        AssertWarnings(undeclared, "data/common/sounds/extra.wav");
        var member = ArchiveSources.Read(File.ReadAllBytes(Path.Combine(fixture.Root, "undeclared/soundsl.zbd"))).Single(m => m.Name == "extra.wav");
        Assert.Equal(WaveConverter.Convert(source, SourceFixture.High, Token), member.Payload.ToArray());
    }

    private static void AssertWarnings(SourceExportReport report, string input)
    {
        Assert.Equal(0, report.Failed);
        var warnings = report.Outputs.SelectMany(o => o.Warnings).ToArray();
        Assert.Equal(3, warnings.Length);
        foreach (int id in new[] { 1, 4, 5 })
        {
            string warning = Assert.Single(warnings, w => w.Contains($"cue {id} ", StringComparison.Ordinal));
            Assert.Contains(input, warning); Assert.Contains("4294967295", warning); Assert.Contains("32 frames", warning);
            Assert.Contains("end of the sound", warning);
        }
    }

    // Independent RIFF fixture with odd unknown-chunk padding before the cues and sentinel sample bytes.
    internal static byte[] Wave()
    {
        using MemoryStream stream = new(); using BinaryWriter writer = new(stream);
        writer.Write("RIFF"u8); writer.Write(0); writer.Write("WAVE"u8);
        writer.Write("fmt "u8); writer.Write(16); writer.Write((ushort)1); writer.Write((ushort)1);
        writer.Write(22050); writer.Write(44100); writer.Write((ushort)2); writer.Write((ushort)16);
        writer.Write("JUNK"u8); writer.Write(3); writer.Write(new byte[] { 9, 8, 7, 0xa5 });
        writer.Write("cue "u8); writer.Write(4 + 5 * 24); writer.Write(5);
        uint[] positions = [uint.MaxValue, 31, 32, uint.MaxValue, 8], offsets = [uint.MaxValue, 31, 32, 7, uint.MaxValue];
        for (int i = 0; i < 5; i++)
        {
            writer.Write((uint)i + 1); writer.Write(positions[i]); writer.Write("data"u8); writer.Write(0); writer.Write(0); writer.Write(offsets[i]);
        }
        writer.Write("data"u8); writer.Write(Frames * 2);
        writer.Write(Enumerable.Range(0, Frames * 2).Select(i => (byte)(i * 37)).ToArray());
        byte[] result = stream.ToArray(); BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), result.Length - 8); return result;
    }
    private static int Record(int index) => 60 + index * 24;
}
