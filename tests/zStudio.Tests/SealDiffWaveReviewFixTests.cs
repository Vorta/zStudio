using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>
/// Work on large valid inputs is bounded by what it keeps: a sealed Blender export is held once while it is checked for
/// changes, a diff of a large source text indexes only the lines that differ (and summarizes too many), and a WAV is
/// converted a block at a time into its result, with the bytes the whole-sound conversion produced.
/// </summary>
// Measures allocations of large inputs; kept apart from tests that run in parallel.
[Collection("Allocation-sensitive")]
public sealed class SealDiffWaveReviewFixTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Model = "data/m1/models/m1.gltf";

    private static long Allocated(Action action)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    // ---- Sealing a Blender export ----

    [Fact]
    public void AFileIsComparedWithItsCopyInBlocks()
    {
        string folder = Path.Combine(Path.GetTempPath(), "zstudio-file-equals-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            byte[] bytes = new byte[24 << 20]; new Random(7).NextBytes(bytes);
            string path = Path.Combine(folder, "big.bin"); File.WriteAllBytes(path, bytes);
            bool same = false;
            long allocated = Allocated(() => same = SourceProject.FileEquals(path, bytes, Token));
            Assert.True(same);
            Assert.True(allocated < 256 * 1024, $"Comparing a {bytes.Length:N0}-byte file allocated {allocated:N0} bytes.");
            // A change anywhere, a longer and a shorter file differ.
            byte[] changed = (byte[])bytes.Clone(); changed[^1] ^= 1;
            Assert.False(SourceProject.FileEquals(path, changed, Token));
            changed = (byte[])bytes.Clone(); changed[70_000] ^= 1;
            Assert.False(SourceProject.FileEquals(path, changed, Token));
            Assert.False(SourceProject.FileEquals(path, bytes.AsSpan(0, bytes.Length - 1), Token));
            File.AppendAllText(path, "x");
            Assert.False(SourceProject.FileEquals(path, bytes, Token));
            string empty = Path.Combine(folder, "empty.bin"); File.WriteAllBytes(empty, []);
            Assert.True(SourceProject.FileEquals(empty, [], Token));
            Assert.False(SourceProject.FileEquals(empty, [0], Token));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void AnExportIsSealedHoldingEachFileOnce()
    {
        using SourceWorldFixture fixture = new();
        SourceWorkspace workspace = new(fixture.Project);
        var checkout = SourceBlender.Checkout(workspace, Model, Token);
        // Blender exported the model with a second, large buffer.
        const int size = 48 << 20;
        string input = Path.GetDirectoryName(checkout.Input)!, outbox = Path.Combine(checkout.Outbox, "edit");
        Directory.CreateDirectory(Path.Combine(outbox, "textures"));
        var json = JsonNode.Parse(File.ReadAllText(checkout.Input))!.AsObject();
        json["buffers"]!.AsArray().Add(new JsonObject { ["uri"] = "big.bin", ["byteLength"] = size });
        File.WriteAllText(Path.Combine(outbox, "m1.gltf"), json.ToJsonString());
        File.Copy(Path.Combine(input, "m1.bin"), Path.Combine(outbox, "m1.bin"));
        File.Copy(Path.Combine(input, "textures", "rock.png"), Path.Combine(outbox, "textures", "rock.png"));
        byte[] big = new byte[size]; new Random(3).NextBytes(big);
        File.WriteAllBytes(Path.Combine(outbox, "big.bin"), big);

        BlenderUpdatePlan? plan = null;
        long allocated = Allocated(() => plan = SourceBlender.PlanUpdate(workspace, checkout, "edit/m1.gltf", token: Token));
        // The buffer is read once and held once: the second read that checks it was not still being written compares blocks.
        var buffer = plan!.Changes.Single(c => c.Relative == "data/m1/models/m1.1.bin").Content;
        Assert.Equal(big, buffer);
        Assert.True(allocated < size + size / 4, $"Sealing a {size:N0}-byte buffer allocated {allocated:N0} bytes.");
        Assert.Equal(big, File.ReadAllBytes(Path.Combine(plan.Sealed, "edit", "big.bin")));
    }

    // ---- Source diffs ----

    /// <summary>A text of as many one-letter lines (a to z, repeated) as the source limit allows: millions of lines.</summary>
    private static byte[] ManyLines()
    {
        byte[] text = new byte[SourceProject.MaximumSourceTextBytes];
        for (int i = 0; i < text.Length; i += 2) { text[i] = (byte)Letter(i / 2 + 1); text[i + 1] = (byte)'\n'; }
        return text;
    }
    private static char Letter(int line) => (char)('a' + (line - 1) % 26);

    [Fact]
    public void AnUnchangedLargeTextIsDescribedWithoutSplittingIt()
    {
        byte[] disk = ManyLines(), working = (byte[])disk.Clone();
        SourceDiffReport? report = null;
        long allocated = Allocated(() => report = SourceDiff.Describe("data/m1/big.zrd", disk, working, 2000, Token));
        Assert.Equal(0, report!.ChangedLines); Assert.Empty(report.Lines); Assert.False(report.Truncated);
        Assert.True(allocated < 64 * 1024, $"Describing an unchanged {disk.Length:N0}-byte text allocated {allocated:N0} bytes.");
    }

    [Fact]
    public void AChangeInsideALargeTextIsListedFromTheLinesAroundIt()
    {
        byte[] disk = ManyLines(), working = (byte[])disk.Clone();
        int line = 3_000_001; working[(line - 1) * 2] = (byte)'#';
        SourceDiffReport? report = null;
        long allocated = Allocated(() => report = SourceDiff.Describe("data/m1/big.zrd", disk, working, 2000, Token));
        Assert.Equal(2, report!.ChangedLines); Assert.False(report.Truncated);
        (char, int, int, string) Kept(int at) => (' ', at, at, Letter(at).ToString());
        Assert.Equal([Kept(line - 3), Kept(line - 2), Kept(line - 1), ('-', line, 0, Letter(line).ToString()), ('+', 0, line, "#"), Kept(line + 1), Kept(line + 2), Kept(line + 3)],
            report.Lines.Select(l => (l.Kind, l.DiskLine, l.WorkingLine, l.Text)));
        Assert.True(allocated < 256 * 1024, $"Describing one changed line of {disk.Length:N0} bytes allocated {allocated:N0} bytes.");
    }

    [Fact]
    public void TooManyLinesBetweenTheSharedStartAndEndAreSummarizedBeforeTheyAreIndexed()
    {
        // The first and last lines differ, so every line lies between what both versions share.
        byte[] disk = ManyLines(), working = (byte[])disk.Clone();
        working[0] = (byte)'#'; working[^2] = (byte)'#';
        SourceDiffReport? report = null;
        long allocated = Allocated(() => report = SourceDiff.Describe("data/m1/big.zrd", disk, working, 2000, Token));
        Assert.Equal(-1, report!.ChangedLines); Assert.Empty(report.Lines); Assert.True(report.Truncated);
        Assert.True(allocated < 64 * 1024, $"Summarizing {disk.Length / 2:N0} lines allocated {allocated:N0} bytes.");
        // The work observes cancellation.
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => SourceDiff.Describe("data/m1/big.zrd", disk, working, 2000, canceled.Token));
    }

    /// <summary>Lines as sources split them: Latin-1, CR LF read as LF, n line feeds making n + 1 lines; an empty file has none.</summary>
    private static string[] Split(byte[]? bytes) => bytes == null || bytes.Length == 0 ? [] : Encoding.Latin1.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    /// <summary>The fewest lines to delete and insert to turn <paramref name="a"/> into <paramref name="b"/> (n + m − 2 LCS).</summary>
    private static int Distance(string[] a, string[] b)
    {
        int[,] lcs = new int[a.Length + 1, b.Length + 1];
        for (int i = a.Length - 1; i >= 0; i--)
            for (int j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        return a.Length + b.Length - 2 * lcs[0, 0];
    }

    [Fact]
    public void DiffsAreMinimalAndConsistentWithTheLinesOfBothVersions()
    {
        Random random = new(11);
        string[] pieces = ["a", "b", "c", "", "a\r", "\r"];
        string Text(int lines, bool crlf, bool final)
        {
            StringBuilder text = new();
            for (int i = 0; i < lines; i++) { if (i > 0) text.Append(crlf && random.Next(2) == 0 ? "\r\n" : "\n"); text.Append(pieces[random.Next(pieces.Length)]); }
            if (final) text.Append('\n');
            return text.ToString();
        }
        for (int round = 0; round < 3000; round++)
        {
            byte[]? disk = random.Next(20) == 0 ? null : Encoding.Latin1.GetBytes(Text(random.Next(0, 14), random.Next(2) == 0, random.Next(2) == 0));
            byte[]? working;
            if (random.Next(20) == 0) working = null;
            else if (disk != null && random.Next(2) == 0)
            {
                // A few edits of the disk version: shared beginnings and ends.
                var lines = Split(disk).ToList();
                for (int e = random.Next(1, 4); e > 0; e--)
                {
                    int at = random.Next(lines.Count + 1);
                    switch (random.Next(3))
                    {
                        case 0: lines.Insert(at, pieces[random.Next(pieces.Length)]); break;
                        case 1: if (at < lines.Count) lines.RemoveAt(at); break;
                        default: if (at < lines.Count) lines[at] = pieces[random.Next(pieces.Length)]; break;
                    }
                }
                working = Encoding.Latin1.GetBytes(string.Join(random.Next(2) == 0 ? "\n" : "\r\n", lines));
            }
            else working = Encoding.Latin1.GetBytes(Text(random.Next(0, 14), random.Next(2) == 0, random.Next(2) == 0));
            if (disk == null && working == null) continue;
            var report = SourceDiff.Describe("f", disk, working, 1000, Token);
            string[] a = Split(disk), b = Split(working);
            string context = $"disk {Convert.ToHexString(disk ?? [])}, working {Convert.ToHexString(working ?? [])}";
            Assert.True(Distance(a, b) == report.ChangedLines, context);
            Assert.False(report.Truncated, context);
            // Every listed line is the line of its version, changes are all listed, and removing the deleted lines from the
            // disk version and the inserted ones from the working version leaves the same lines.
            Assert.Equal(report.ChangedLines, report.Lines.Count(l => l.Kind != ' '));
            foreach (var l in report.Lines)
            {
                if (l.Kind != '+') Assert.True(a[l.DiskLine - 1] == l.Text, context);
                if (l.Kind != '-') Assert.True(b[l.WorkingLine - 1] == l.Text, context);
            }
            Assert.True(report.Lines.Where(l => l.Kind != '+').Select(l => l.DiskLine).SequenceEqual(report.Lines.Where(l => l.Kind != '+').Select(l => l.DiskLine).Order()), context);
            Assert.True(report.Lines.Where(l => l.Kind != '-').Select(l => l.WorkingLine).SequenceEqual(report.Lines.Where(l => l.Kind != '-').Select(l => l.WorkingLine).Order()), context);
            HashSet<int> deleted = [.. report.Lines.Where(l => l.Kind == '-').Select(l => l.DiskLine)], inserted = [.. report.Lines.Where(l => l.Kind == '+').Select(l => l.WorkingLine)];
            Assert.True(a.Where((_, i) => !deleted.Contains(i + 1)).SequenceEqual(b.Where((_, i) => !inserted.Contains(i + 1))), context);
        }
    }

    [Fact]
    public void ShownLinesAreBoundedAndLongLinesClipped()
    {
        string Lines(Func<int, string> line, int count) => string.Join("\n", Enumerable.Range(0, count).Select(line));
        byte[] disk = Encoding.Latin1.GetBytes(Lines(i => $"line {i}", 100)), working = Encoding.Latin1.GetBytes(Lines(i => i % 10 == 5 ? new string('é', 500) : $"line {i}", 100));
        var report = SourceDiff.Describe("f", disk, working, 12, Token);
        Assert.Equal(20, report.ChangedLines); Assert.True(report.Truncated); Assert.Equal(12, report.Lines.Count);
        var clipped = report.Lines.First(l => l.Kind == '+');
        Assert.Equal(new string('é', SourceDiff.MaximumLineCharacters) + "…", clipped.Text);
        Assert.Equal(6, clipped.WorkingLine);
        // Binary content is only summarized.
        var binary = SourceDiff.Describe("f", [1, 0, 2], [1, 0, 3], 12, Token);
        Assert.False(binary.Text); Assert.Empty(binary.Lines);
    }

    // ---- WAV conversion ----

    /// <summary>A PCM WAV of noise with full-scale extremes at the start and a cue for every tenth of it, written as the retail banks are.</summary>
    private static byte[] Noise(WaveFormat format, int frames, int seed)
    {
        Random random = new(seed);
        int step = format.Bits / 8; byte[] pcm = new byte[frames * step * format.Channels];
        random.NextBytes(pcm);
        byte[] extremes = step == 1 ? [0, 255, 128, 0] : [0x00, 0x80, 0xFF, 0x7F, 0, 0, 0x01, 0x80];
        extremes.AsSpan(0, Math.Min(extremes.Length, pcm.Length)).CopyTo(pcm);
        return ReferenceWave.Write(format, pcm, [.. Enumerable.Range(0, frames < 10 ? 1 : 10).Select(i => ((uint)i + 1, (uint)(frames * i / 10)))]);
    }

    public static TheoryData<int, int, int, int, int, int, int> Conversions() => new()
    {
        // Source rate, bits, channels, frames; declared rate, bits, channels.
        { 44100, 16, 2, 100_003, 22050, 8, 1 },
        { 44100, 16, 2, 70_001, 22050, 16, 2 },
        { 22050, 16, 1, 50_000, 11025, 16, 1 },
        { 22050, 8, 1, 65_537, 11025, 8, 1 },
        { 11025, 8, 2, 40_000, 22050, 8, 1 },
        { 48000, 16, 2, 96_001, 44100, 16, 2 },
        { 44100, 16, 1, 120_000, 8000, 8, 1 },
        { 22050, 16, 2, 33_333, 44100, 8, 2 },
        { 44100, 16, 1, 1, 22050, 16, 1 },
        { 44100, 8, 1, 3, 1000, 8, 1 },
        { 22050, 16, 1, 0, 11025, 16, 1 },
        { 192000, 16, 2, 200_000, 1000, 16, 2 },
        { 40_000_000, 8, 1, 5000, 1000, 8, 1 },
    };

    [Theory]
    [MemberData(nameof(Conversions))]
    public void ConversionInBlocksGivesTheBytesOfTheWholeSoundConversion(int rate, int bits, int channels, int frames, int toRate, int toBits, int toChannels)
    {
        byte[] wave = Noise(new(rate, bits, channels), frames, rate ^ frames);
        WaveFormat declared = new(toRate, toBits, toChannels);
        Assert.Equal(ReferenceWave.Convert(wave, declared), WaveConverter.Convert(wave, declared, Token));
    }

    [Fact]
    public void AnEmptySoundOfTheSameRateBecomesOneSilentFrame()
    {
        // The whole-sound conversion failed on it (index out of range); a block conversion gives what a rate change gives.
        byte[] wave = Noise(new(22050, 16, 2), 0, 1);
        var info = WaveDecoder.Read(WaveConverter.Convert(wave, new(22050, 8, 1), Token), Token);
        Assert.Equal(1, info.DataLength);
        Assert.Equal(128, WaveConverter.Convert(wave, new(22050, 8, 1), Token)[info.DataOffset]);
        Assert.Throws<IndexOutOfRangeException>(() => ReferenceWave.Convert(wave, new(22050, 8, 1)));
    }

    [Fact]
    public void WritingGivesTheBytesOfTheRetailLayout()
    {
        byte[] pcm = [1, 2, 3, 4, 5];
        Assert.Equal(ReferenceWave.Write(new(22050, 8, 1), pcm, []), WaveConverter.Write(new(22050, 8, 1), pcm, []));
        Assert.Equal(ReferenceWave.Write(new(11025, 16, 2), pcm, [(1, 2), (7, 0xFFFF_FFFF)]), WaveConverter.Write(new(11025, 16, 2), pcm, [(1, 2), (7, 0xFFFF_FFFF)]));
        Assert.Equal(ReferenceWave.Write(new(44100, 16, 1), [], [(3, 0)]), WaveConverter.Write(new(44100, 16, 1), [], [(3, 0)]));
    }

    [Theory]
    [InlineData(44100, 8, 1, 22050, 8, 1)]
    [InlineData(44100, 16, 2, 44100, 8, 1)]
    [InlineData(22050, 16, 2, 11025, 16, 2)]
    public void ALongSoundIsConvertedHoldingOnlyItsSourceAndResult(int rate, int bits, int channels, int toRate, int toBits, int toChannels)
    {
        byte[] wave = Noise(new(rate, bits, channels), (8 << 20) / (bits / 8 * channels), 5);
        byte[]? result = null;
        long allocated = Allocated(() => result = WaveConverter.Convert(wave, new(toRate, toBits, toChannels), Token));
        Assert.True(allocated < result!.Length + (1 << 20), $"Converting {wave.Length:N0} bytes to {result.Length:N0} allocated {allocated:N0} bytes.");
    }

    [Fact]
    public void ARateDropBeyondTheBlockBudgetIsRefused()
    {
        // From 40 MHz to 1 kHz each output frame reads 1.3 million source frames: a sound longer than that is refused
        // rather than held whole as floating-point samples.
        byte[] wave = Noise(new(40_000_000, 8, 1), WaveConverter.MaximumBlockFrames + 1000, 9);
        var refused = Assert.Throws<InvalidDataException>(() => WaveConverter.Convert(wave, new(1000, 8, 1), Token));
        Assert.Contains($"{40_000_000:N0} Hz", refused.Message);
    }

    [Fact]
    public void RetailSoundsConvertToTheBytesOfTheWholeSoundConversion()
    {
        string? corpus = Environment.GetEnvironmentVariable("ZSTUDIO_CORPUS");
        if (string.IsNullOrEmpty(corpus) || !File.Exists(Path.Combine(corpus, "soundsh.zbd")) || !File.Exists(Path.Combine(corpus, "zrdr.zbd"))) return;
        var definitions = ArchiveSources.Read(File.ReadAllBytes(Path.Combine(corpus, "zrdr.zbd"))).Single(m => m.Name.Equals("sounds.zrd", StringComparison.OrdinalIgnoreCase));
        var declared = SourceBuilder.DeclaredFormats(definitions.Payload.ToArray(), Token);
        int converted = 0, sounds = 0;
        // Each best-quality sound (the project's source) at the medium and low formats its export converts it to.
        foreach (var member in ArchiveSources.Read(File.ReadAllBytes(Path.Combine(corpus, "soundsh.zbd"))))
        {
            if (!declared.TryGetValue(member.Name, out var formats)) continue;
            var source = WaveConverter.Format(member.Payload); sounds++;
            foreach (var format in formats)
            {
                if (WaveConverter.Target(source, format) == source) continue;
                Assert.True(ReferenceWave.Convert(member.Payload, format).AsSpan().SequenceEqual(WaveConverter.Convert(member.Payload, format, Token)), $"{member.Name} at {format}");
                converted++;
            }
        }
        Assert.True(converted > 100, $"Only {converted} retail conversions were compared.");
        TestContext.Current.TestOutputHelper?.WriteLine($"{converted} conversions of {sounds} retail sounds match.");
    }

    /// <summary>The whole-sound conversion zStudio used before converting in blocks, kept as the reference its bytes must match.</summary>
    private static class ReferenceWave
    {
        public static byte[] Convert(ReadOnlyMemory<byte> wave, WaveFormat declared)
        {
            var info = WaveDecoder.Read(wave);
            WaveFormat source = new((int)info.SampleRate, info.BitsPerSample, info.Channels), target = WaveConverter.Target(source, declared);
            if (target == source) return wave.ToArray();
            int frames = info.DataLength / info.BlockAlign, channels = info.Channels, step = info.BitsPerSample / 8;
            var data = wave.Span.Slice(info.DataOffset, info.DataLength);
            float[][] input = new float[target.Channels][];
            for (int c = 0; c < target.Channels; c++) input[c] = new float[frames];
            for (int f = 0; f < frames; f++)
            {
                float mix = 0;
                for (int c = 0; c < channels; c++)
                {
                    int p = f * info.BlockAlign + c * step;
                    float value = step == 1 ? (data[p] - 128) / 128f : BinaryPrimitives.ReadInt16LittleEndian(data[p..]) / 32768f;
                    if (target.Channels == channels) input[c][f] = value; else mix += value;
                }
                if (target.Channels != channels) for (int c = 0; c < target.Channels; c++) input[c][f] = mix / channels;
            }
            double ratio = (double)target.Rate / info.SampleRate;
            int outFrames = Math.Max(1, (int)Math.Round(frames * ratio));
            float[][] output = target.Rate == info.SampleRate ? input : input.Select(channel => Resample(channel, ratio, outFrames)).ToArray();
            int outStep = target.Bits / 8, align = outStep * target.Channels;
            byte[] pcm = new byte[checked(outFrames * align)];
            for (int f = 0; f < outFrames; f++)
                for (int c = 0; c < target.Channels; c++)
                {
                    float value = Math.Clamp(output[c][f], -1f, 1f); int p = f * align + c * outStep;
                    if (outStep == 1) pcm[p] = (byte)Math.Clamp((int)MathF.Round(value * 127f + 128f), 0, 255);
                    else BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(p), (short)Math.Clamp((int)MathF.Round(value * 32767f), short.MinValue, short.MaxValue));
                }
            var cues = info.Cues.Select(c => (c.Id, Position: (uint)Math.Min(outFrames, Math.Round(c.SampleOffset * ratio)))).ToArray();
            return Write(target, pcm, cues);
        }

        private static float[] Resample(float[] input, double ratio, int count)
        {
            const int taps = 16; double scale = Math.Min(1, ratio);
            float[] result = new float[count];
            for (int i = 0; i < count; i++)
            {
                double center = i / ratio, sum = 0, weights = 0; int first = (int)Math.Floor(center - taps / scale), last = (int)Math.Ceiling(center + taps / scale);
                for (int j = Math.Max(0, first); j <= Math.Min(input.Length - 1, last); j++)
                {
                    double x = (j - center) * scale, w = x == 0 ? 1 : Math.Abs(x) >= taps ? 0 : Sinc(x) * Sinc(x / taps);
                    sum += input[j] * w; weights += w;
                }
                result[i] = weights == 0 ? 0 : (float)(sum / weights);
            }
            return result;
            static double Sinc(double x) => Math.Sin(Math.PI * x) / (Math.PI * x);
        }

        public static byte[] Write(WaveFormat format, ReadOnlySpan<byte> pcm, IReadOnlyList<(uint Id, uint Position)> cues)
        {
            int cueBytes = cues.Count == 0 ? 0 : 8 + 4 + cues.Count * 24, pad = pcm.Length & 1;
            using MemoryStream stream = new(); using BinaryWriter w = new(stream);
            w.Write("RIFF"u8); w.Write(4 + 24 + cueBytes + 8 + pcm.Length + pad); w.Write("WAVE"u8);
            int align = format.Bits / 8 * format.Channels;
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)format.Channels); w.Write(format.Rate); w.Write(format.Rate * align); w.Write((short)align); w.Write((short)format.Bits);
            if (cues.Count > 0)
            {
                w.Write("cue "u8); w.Write(4 + cues.Count * 24); w.Write(cues.Count);
                foreach (var (id, position) in cues) { w.Write(id); w.Write(position); w.Write("data"u8); w.Write(0); w.Write(0); w.Write(position); }
            }
            w.Write("data"u8); w.Write(pcm.Length); w.Write(pcm); if (pad != 0) w.Write((byte)0);
            return stream.ToArray();
        }
    }
}
