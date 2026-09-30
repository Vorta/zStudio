using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class SourceProjectTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly string[] AllOutputs = ["zrdr.zbd", "interp.zbd", "soundsh.zbd", "soundsm.zbd", "soundsl.zbd", "m1/zrdr.zbd"];
    private static ZbdDocument Open(string path) => FormatRegistry.Default.OpenBytes(path, File.ReadAllBytes(path), token: Token);
    private static byte[] Member(ZbdDocument doc, string name) { var asset = doc.Assets.Single(a => a.Name == name); return doc.Slice(asset.Offset, asset.Length).ToArray(); }
    private static WaveFormat Format(byte[] wave) => WaveConverter.Format(wave);

    [Fact]
    public async Task ReconstructionWritesTheOriginalLayoutWithoutMetadata()
    {
        using var fixture = new SourceFixture();
        var report = await SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, token: Token);
        Assert.Equal(2, report.Families["resources"]); Assert.Equal(1, report.Families["scripts"]); Assert.Equal(3, report.Families["sounds"]);
        Assert.Equal(["other.bin"], report.NotReconstructed);
        Assert.Contains(report.Notes, n => n.Contains("blob.bin") && n.Contains("leave it out"));
        Assert.True(SourceProject.IsProject(fixture.Project));
        Assert.Equal(["data", "gamegen"], Directory.GetFileSystemEntries(fixture.Project).Select(Path.GetFileName).Order().ToArray());
        string P(string relative) => SourceProject.Resolve(fixture.Project, relative);
        // Resources return to the folders recorded by the compiler, as editable text.
        Assert.Equal("GRAVITY ( -9.8 )", File.ReadAllText(P("data/m1/zrdr/ai.zrd")).Trim());
        Assert.True(File.Exists(P("data/m1/zrdr/envmodels/frcgate.zrd")));
        Assert.True(File.Exists(P("data/common/zrdr/sounds.zrd")));
        Assert.Equal(fixture.Blob, File.ReadAllBytes(P("data/m1/zrdr/blob.bin")));
        // Sounds keep only their best-quality version.
        Assert.Equal(fixture.WaveA, File.ReadAllBytes(P("data/common/sounds/a.wav")));
        Assert.Equal(fixture.WaveB, File.ReadAllBytes(P("data/common/sounds/b.wav")));
        Assert.Equal(["a.wav", "b.wav"], Directory.GetFiles(P("data/common/sounds")).Select(Path.GetFileName).Order().ToArray());
        // Scripts are text with the modification time the prepared index recorded.
        Assert.Equal("set ZBD_DIR zbd\nmkdir %ZBD_DIR%,,\n", File.ReadAllText(P("gamegen/support/common.gw")));
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(912_000_000), File.GetLastWriteTimeUtc(P("gamegen/support/common.gw")));
        Assert.Equal(report.SourceFiles, Directory.GetFiles(fixture.Project, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task OnlyTheThreeSoundBanksBecomeSounds()
    {
        using var fixture = new SourceFixture();
        File.WriteAllBytes(Path.Combine(fixture.Corpus, "m1", "voices.zbd"), SourceFixture.Archive(("v.wav", fixture.WaveB, new byte[64])));
        var report = await SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, token: Token);
        Assert.Contains("m1/voices.zbd", report.NotReconstructed);
        Assert.Contains(report.Notes, n => n.StartsWith("m1/voices.zbd", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(fixture.Project, "data", "common", "sounds", "v.wav")));
        Assert.Equal(3, report.Families["sounds"]);
    }

    [Fact]
    public async Task ThePlanFollowsTheSourceTree()
    {
        using var fixture = new SourceFixture();
        await SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, token: Token);
        var plan = SourceBuilder.Plan(fixture.Project);
        Assert.Equal(AllOutputs, plan.Select(p => p.Path));
        Assert.Equal(["data/m1/zrdr/ai.zrd", "data/m1/zrdr/envmodels/frcgate.zrd"], plan.Single(p => p.Path == "m1/zrdr.zbd").Inputs);
        Assert.Equal(["gamegen/m1.gs", "gamegen/support/common.gw"], plan.Single(p => p.Path == "interp.zbd").Inputs);
        // A new mission folder becomes a new archive; missions are ordered by number.
        Directory.CreateDirectory(Path.Combine(fixture.Project, "data", "m10", "zrdr"));
        File.WriteAllText(Path.Combine(fixture.Project, "data", "m10", "zrdr", "x.zrd"), "VALUE ( 1 )");
        Directory.CreateDirectory(Path.Combine(fixture.Project, "data", "m2", "zrdr"));
        File.WriteAllText(Path.Combine(fixture.Project, "data", "m2", "zrdr", "y.zrd"), "VALUE ( 2 )");
        Assert.Equal(["m1/zrdr.zbd", "m2/zrdr.zbd", "m10/zrdr.zbd"], SourceBuilder.Plan(fixture.Project).Where(p => p.Path.Contains('/')).Select(p => p.Path));
        Assert.Throws<InvalidDataException>(() => SourceBuilder.Plan(fixture.Corpus));
    }

    [Fact]
    public async Task ExportBuildsWorkingGameFilesFromTheSources()
    {
        using var fixture = new SourceFixture();
        await SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, token: Token);
        string exported = Path.Combine(fixture.Root, "zbd");
        var report = await SourceBuilder.ExportAsync(fixture.Project, exported, token: Token);
        Assert.Equal(6, report.Built); Assert.Equal(0, report.Failed);
        Assert.Equal(AllOutputs.Order(), Directory.GetFiles(exported, "*", SearchOption.AllDirectories).Select(f => SourceProject.Relative(exported, f)).Order());

        var mission = Open(Path.Combine(exported, "m1", "zrdr.zbd"));
        Assert.DoesNotContain(mission.Diagnostics, d => d.Severity == "Error");
        Assert.Equal(["ai.zrd", "frcgate.zrd"], mission.Assets.Select(a => a.Name));
        var shipped = Open(Path.Combine(fixture.Corpus, "m1", "zrdr.zbd"));
        Assert.Equal(Member(shipped, "ai.zrd"), Member(mission, "ai.zrd")); Assert.Equal(Member(shipped, "frcgate.zrd"), Member(mission, "frcgate.zrd"));
        // Archive records name each member's source, so exported files reconstruct into the same folders.
        var members = ArchiveSources.Read(File.ReadAllBytes(Path.Combine(exported, "m1", "zrdr.zbd")));
        Assert.Equal(["data\\m1\\zrdr\\ai.zrd", "data\\m1\\zrdr\\envmodels\\frcgate.zrd"], members.Select(m => m.SourceField));

        var scripts = Open(Path.Combine(exported, "interp.zbd")).Scripts!;
        Assert.Equal(["m1.gs", "support\\common.gw"], scripts.Entries.Select(e => e.Name));
        Assert.Equal(["mkdir", "%ZBD_DIR%", ""], scripts.Entries[1].Instructions[1].Tokens);
        Assert.Equal(912_000_000u, scripts.Entries[1].FileTime);

        // Lower banks are converted from the best-quality source to the formats sounds.zrd declares.
        var high = Open(Path.Combine(exported, "soundsh.zbd")); var medium = Open(Path.Combine(exported, "soundsm.zbd")); var low = Open(Path.Combine(exported, "soundsl.zbd"));
        Assert.Equal(fixture.WaveA, Member(high, "a.wav"));
        Assert.Equal(SourceFixture.Medium, Format(Member(medium, "a.wav")));
        Assert.Equal(SourceFixture.Low, Format(Member(low, "a.wav")));
        Assert.Equal(500u, WaveDecoder.Read(Member(low, "a.wav"), Token).Cues.Single().SampleOffset);
        // A declaration is a ceiling: b.wav is already below every declared format and is never raised.
        Assert.All(new[] { high, medium, low }, bank => Assert.Equal(fixture.WaveB, Member(bank, "b.wav")));
    }

    [Fact]
    public async Task EditedSourcesChangeOnlyTheirOutputs()
    {
        using var fixture = new SourceFixture();
        await SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, token: Token);
        File.WriteAllText(Path.Combine(fixture.Project, "data", "m1", "zrdr", "ai.zrd"), "# edited\nGRAVITY ( -1.0 )\nNEW_VALUE ( 3 \"two words\" )\n");
        File.WriteAllText(Path.Combine(fixture.Project, "gamegen", "m1.gs"), "source support\\common.gw # comment\nset MISSION_DIR m1\n");
        string exported = Path.Combine(fixture.Root, "zbd");
        await SourceBuilder.ExportAsync(fixture.Project, exported, ["m1/zrdr.zbd", "interp.zbd"], token: Token);
        Assert.Equal(["interp.zbd", "m1/zrdr.zbd"], Directory.GetFiles(exported, "*", SearchOption.AllDirectories).Select(f => SourceProject.Relative(exported, f)).Order());
        var ai = ZrdDecoder.Read(Member(Open(Path.Combine(exported, "m1", "zrdr.zbd")), "ai.zrd"), Token);
        Assert.Equal(-1.0f, BitConverter.UInt32BitsToSingle(ai.Children[1].Children[0].Bits));
        Assert.Equal("two words", ai.Children[3].Children[1].Text);
        var script = Open(Path.Combine(exported, "interp.zbd")).Scripts!.Entries.Single(e => e.Name == "m1.gs");
        Assert.Equal([["source", "support\\common.gw"], ["set", "MISSION_DIR", "m1"]], script.Instructions.Select(i => i.Tokens.ToArray()));
        // The edited script's own time is recorded, so the engine keeps preferring a newer loose copy.
        Assert.Equal((uint)new DateTimeOffset(File.GetLastWriteTimeUtc(Path.Combine(fixture.Project, "gamegen", "m1.gs"))).ToUnixTimeSeconds(), script.FileTime);
    }

    [Fact]
    public async Task ExportsReplaceExistingFilesOnlyWhenAllowed()
    {
        using var fixture = new SourceFixture();
        await SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, token: Token);
        string exported = Path.Combine(fixture.Root, "zbd");
        Directory.CreateDirectory(exported); File.WriteAllBytes(Path.Combine(exported, "interp.zbd"), [1]); File.WriteAllBytes(Path.Combine(exported, "keep.txt"), [2]);
        var error = await Assert.ThrowsAsync<IOException>(() => SourceBuilder.ExportAsync(fixture.Project, exported, token: Token));
        Assert.Contains("interp.zbd", error.Message);
        Assert.Equal([1], File.ReadAllBytes(Path.Combine(exported, "interp.zbd")));
        Assert.Equal(2, Directory.GetFiles(exported, "*", SearchOption.AllDirectories).Length);
        await SourceBuilder.ExportAsync(fixture.Project, exported, overwrite: true, token: Token);
        Assert.NotNull(Open(Path.Combine(exported, "interp.zbd")).Scripts);
        Assert.Equal([2], File.ReadAllBytes(Path.Combine(exported, "keep.txt")));
        Assert.Empty(Directory.GetDirectories(exported, ".zstudio-*"));
    }

    [Fact]
    public async Task InvalidSourcesFailWithoutWritingAnything()
    {
        using var fixture = new SourceFixture();
        await SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, token: Token);
        File.WriteAllText(Path.Combine(fixture.Project, "data", "m1", "zrdr", "ai.zrd"), "GRAVITY ( -1.0");
        var check = await SourceBuilder.CheckAsync(fixture.Project, token: Token);
        Assert.Null(check.Destination);
        Assert.Equal(5, check.Built);
        var failed = Assert.Single(check.Outputs, o => o.Status == "failed");
        Assert.Equal("m1/zrdr.zbd", failed.Path); Assert.Contains("data/m1/zrdr/ai.zrd", failed.Error);
        string exported = Path.Combine(fixture.Root, "zbd");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => SourceBuilder.ExportAsync(fixture.Project, exported, token: Token));
        Assert.Contains("Nothing was written", error.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(exported));
        // Two sources that would become the same member are refused: the engine finds members by name.
        File.WriteAllText(Path.Combine(fixture.Project, "data", "m1", "zrdr", "ai.zrd"), "GRAVITY ( -1.0 )");
        File.WriteAllText(Path.Combine(fixture.Project, "data", "m1", "zrdr", "envmodels", "AI.zrd"), "GRAVITY ( -2.0 )");
        failed = Assert.Single((await SourceBuilder.CheckAsync(fixture.Project, ["m1/zrdr.zbd"], token: Token)).Outputs);
        Assert.Contains("both become archive member", failed.Error);
        await Assert.ThrowsAsync<InvalidDataException>(() => SourceBuilder.CheckAsync(fixture.Project, ["m9/zrdr.zbd"], token: Token));
        // Scripts must tokenize into instructions the prepared format can store.
        File.Delete(Path.Combine(fixture.Project, "data", "m1", "zrdr", "envmodels", "AI.zrd"));
        File.WriteAllText(Path.Combine(fixture.Project, "gamegen", "m1.gs"), "set " + new string('x', 70_000) + "\n");
        failed = Assert.Single((await SourceBuilder.CheckAsync(fixture.Project, ["interp.zbd"], token: Token)).Outputs);
        Assert.Equal("failed", failed.Status); Assert.Contains("gamegen/m1.gs", failed.Error);
    }

    [Fact]
    public async Task UndeclaredSoundsKeepTheirSourceFormatInEveryBank()
    {
        using var fixture = new SourceFixture();
        await SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, token: Token);
        byte[] extra = SourceFixture.Tone(new(44100, 16, 2), 441, null);
        File.WriteAllBytes(Path.Combine(fixture.Project, "data", "common", "sounds", "c.wav"), extra);
        var report = await SourceBuilder.ExportAsync(fixture.Project, Path.Combine(fixture.Root, "zbd"), ["soundsh.zbd", "soundsl.zbd"], token: Token);
        Assert.Contains(report.Outputs[0].Warnings, w => w.Contains("c.wav"));
        Assert.Empty(report.Outputs[1].Warnings);
        Assert.Equal(extra, Member(Open(Path.Combine(fixture.Root, "zbd", "soundsl.zbd")), "c.wav"));
        // Sounds are found by name, so a second a.wav in a subfolder is refused.
        Directory.CreateDirectory(Path.Combine(fixture.Project, "data", "common", "sounds", "extra"));
        File.WriteAllBytes(Path.Combine(fixture.Project, "data", "common", "sounds", "extra", "A.wav"), fixture.WaveB);
        var duplicate = (await SourceBuilder.CheckAsync(fixture.Project, ["soundsh.zbd"], token: Token)).Outputs.Single();
        Assert.Equal("failed", duplicate.Status); Assert.Contains("both become sound", duplicate.Error);
        Directory.Delete(Path.Combine(fixture.Project, "data", "common", "sounds", "extra"), true);
        // A sound that cannot be converted is reported with its source.
        File.WriteAllBytes(Path.Combine(fixture.Project, "data", "common", "sounds", "a.wav"), [1, 2, 3]);
        var failed = (await SourceBuilder.CheckAsync(fixture.Project, ["soundsm.zbd"], token: Token)).Outputs.Single();
        Assert.Equal("failed", failed.Status); Assert.Contains("data/common/sounds/a.wav", failed.Error);
    }

    [Fact]
    public void WaveConversionFollowsTheDeclaredCeiling()
    {
        Assert.Equal(new WaveFormat(22000, 8, 1), WaveConverter.Target(new(22000, 16, 1), new(22050, 8, 1)));
        Assert.Equal(new WaveFormat(11025, 8, 1), WaveConverter.Target(new(11025, 8, 1), new(22050, 16, 2)));
        byte[] tone = SourceFixture.Tone(new(22050, 16, 2), 2205, 2000);
        Assert.Equal(tone, WaveConverter.Convert(tone, new(44100, 16, 2), Token));
        // Stereo channels with opposite phase mix to silence; rate halves the frames and moves cues with them.
        byte[] mono = WaveConverter.Convert(tone, new(11025, 8, 1), Token);
        var info = WaveDecoder.Read(mono, Token);
        Assert.Equal((11025u, (ushort)8, (ushort)1), (info.SampleRate, info.BitsPerSample, info.Channels));
        Assert.InRange(info.DataLength, 1101, 1104);
        Assert.Equal(1000u, info.Cues.Single().SampleOffset);
        Assert.All(mono.AsSpan(info.DataOffset, info.DataLength).ToArray(), b => Assert.InRange(b, 126, 130));
        // Downsampling filters content above the new Nyquist frequency instead of folding it back.
        byte[] high = SourceFixture.Tone(new(44100, 16, 1), 4410, null);
        byte[] shrill = new byte[high.Length]; high.CopyTo(shrill, 0);
        var source = WaveDecoder.Read(high, Token);
        for (int f = 0; f < 4410; f++) System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(shrill.AsSpan(source.DataOffset + f * 2), (short)(Math.Sin(2 * Math.PI * 15000 * f / 44100.0) * 16000));
        byte[] result = WaveConverter.Convert(shrill, new(11025, 16, 1), Token); var filtered = WaveDecoder.Read(result, Token);
        double peak = 0;
        for (int f = 100; f < 1000; f++) peak = Math.Max(peak, Math.Abs(System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(result.AsSpan(filtered.DataOffset + f * 2))));
        Assert.True(peak < 1600, $"A 15 kHz tone leaked through at {peak}.");
        Assert.Throws<InvalidDataException>(() => WaveConverter.Convert(tone, new(22050, 24, 1), Token));
        Assert.Throws<InvalidDataException>(() => WaveConverter.Convert(new byte[] { 1, 2, 3 }, new(22050, 16, 1), Token));
    }

    [Fact]
    public async Task ExportedFilesReconstructTheSameTree()
    {
        using var fixture = new SourceFixture();
        await SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, token: Token);
        File.Delete(Path.Combine(fixture.Project, "data", "m1", "zrdr", "blob.bin"));
        string exported = Path.Combine(fixture.Root, "zbd"), again = Path.Combine(fixture.Root, "again");
        await SourceBuilder.ExportAsync(fixture.Project, exported, token: Token);
        var report = await SourceExtractor.ExtractAsync(exported, again, token: Token);
        Assert.Empty(report.Notes); Assert.Empty(report.NotReconstructed);
        var first = Directory.GetFiles(fixture.Project, "*", SearchOption.AllDirectories).ToDictionary(f => SourceProject.Relative(fixture.Project, f), File.ReadAllBytes);
        var second = Directory.GetFiles(again, "*", SearchOption.AllDirectories).ToDictionary(f => SourceProject.Relative(again, f), File.ReadAllBytes);
        Assert.Equal(first.Keys.Order(), second.Keys.Order());
        Assert.All(first, f => Assert.Equal(f.Value, second[f.Key]));
    }
}
