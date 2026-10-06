using System.IO;
using System.Text;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>
/// Review fixes for reconstruction and export inputs: a reconstruction refuses files that together need more memory than it
/// holds at once (also members that share one payload), an archive member records its whole source path or its archive
/// fails, and an export refuses a project whose profile, sources or texture sizes changed after the export was planned.
/// </summary>
public sealed class ReconstructionPlanningReviewFixTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private sealed class OnReport(Action<SourceProgress> action) : IProgress<SourceProgress> { public void Report(SourceProgress value) => action(value); }
    private static ZrdNode A(params ZrdNode[] children) => new(Guid.NewGuid(), ZrdKind.Array, 0, "", children);
    private static ZrdNode F(float value) => new(Guid.NewGuid(), ZrdKind.Float, BitConverter.SingleToUInt32Bits(value), "", []);
    private static bool Empty(string folder) => !Directory.Exists(folder) || !Directory.EnumerateFileSystemEntries(folder).Any();

    [Fact]
    public async Task ReconstructionRefusesFilesThatTogetherNeedMoreThanItHoldsAtOnce()
    {
        using SourceFixture fixture = new();
        // Three sound banks of about 1 MB each, which reconstruction keeps until every file is read: one fits the budget,
        // all three do not.
        byte[] wave = SourceFixture.Tone(SourceFixture.High, 500_000, null);
        foreach (string bank in (string[])["soundsh.zbd", "soundsm.zbd", "soundsl.zbd"])
            File.WriteAllBytes(Path.Combine(fixture.Corpus, bank), SourceFixture.Archive(("a.wav", wave, new byte[64]), ("b.wav", fixture.WaveB, new byte[64])));
        long size = new FileInfo(Path.Combine(fixture.Corpus, "soundsh.zbd")).Length;

        var refused = await Assert.ThrowsAsync<IOException>(() => SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, 2 * size + size / 2, null, Token));
        Assert.Contains("more memory than reconstruction holds at once", refused.Message, StringComparison.Ordinal);
        // Refused like any stopped reconstruction: what it wrote is removed, so the folder can be used again.
        Assert.True(Empty(fixture.Project));

        var report = await SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, 4 * size, null, Token);
        Assert.Equal(3, report.Families["sounds"]);
    }

    [Fact]
    public async Task MembersSharingOnePayloadAreEachCountedAsTheTreeTheyDecodeTo()
    {
        using SourceFixture fixture = new();
        // A 64 KB resource that 64 members share: the archive is about 73 KB, but each member is decoded and kept as its own
        // tree, together far more than 16 MB.
        byte[] payload = ZrdWriter.Write(A([.. Enumerable.Range(0, 8000).Select(i => F(i))]), Token);
        using (MemoryStream s = new())
        using (BinaryWriter w = new(s))
        {
            w.Write(payload);
            for (int i = 0; i < 64; i++)
            {
                w.Write(0u); w.Write(payload.Length);
                byte[] name = new byte[64]; Encoding.Latin1.GetBytes($"r{i}.zrd").CopyTo(name, 0); w.Write(name);
                w.Write(0u); w.Write(new byte[64]); w.Write(0ul);
            }
            w.Write(1); w.Write(64);
            File.WriteAllBytes(Path.Combine(fixture.Corpus, "m1", "zrdr.zbd"), s.ToArray());
        }
        Assert.True(new FileInfo(Path.Combine(fixture.Corpus, "m1", "zrdr.zbd")).Length < 100_000);

        var refused = await Assert.ThrowsAsync<IOException>(() => SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, 16L << 20, null, Token));
        Assert.Contains("more memory than reconstruction holds at once", refused.Message, StringComparison.Ordinal);
        Assert.True(Empty(fixture.Project));
    }

    [Fact]
    public void AnArchiveRecordsASourcePathWholeOrRefusesIt()
    {
        string fits = "data\\m1\\zrdr\\" + new string('a', 46) + ".zrd";
        Assert.Equal(63, fits.Length);
        var member = Assert.Single(ArchiveSources.Read(ArchiveSources.Write([new("a.zrd", fits, [1, 2, 3])], DateTime.UtcNow)));
        Assert.Equal(fits, member.SourceField);
        // One character more, or one the field cannot store, would record another path than the source's.
        Assert.Throws<InvalidDataException>(() => ArchiveSources.Write([new("a.zrd", "d" + fits, [1, 2, 3])], DateTime.UtcNow));
        Assert.Throws<InvalidDataException>(() => ArchiveSources.Write([new("a.zrd", "data\\m1\\zrdr\\Ж.zrd", [1, 2, 3])], DateTime.UtcNow));
    }

    [Fact]
    public async Task AResourceWhosePathTheArchiveCannotHoldFailsInsteadOfNamingAnotherSource()
    {
        using SourceWorldFixture fixture = new();
        // The common resource's path is exactly the last 63 characters of the mission resource's: recorded shortened, an
        // edit of the mission archive's member would be written to the common file, which compiles to the same structure.
        string shared = "data/common/zrdr/" + new string('p', 42) + ".zrd", deep = "data/m1/zrdr/deep/" + shared;
        Assert.Equal(63, shared.Length);
        fixture.Write(shared, "( SPOT ( 1.0 2.0 3.0 ) )\n");
        fixture.Write(deep, "( SPOT ( 4.0 5.0 6.0 ) )\n");

        var report = await SourceBuilder.CheckAsync(fixture.Project, ["zrdr.zbd", "m1/zrdr.zbd"], token: Token);
        Assert.Equal("built", report.Outputs.Single(o => o.Path == "zrdr.zbd").Status);
        var failed = report.Outputs.Single(o => o.Path == "m1/zrdr.zbd");
        Assert.Equal("failed", failed.Status);
        Assert.Contains(deep, failed.Error, StringComparison.Ordinal);

        // A path that fits is recorded whole, and an edit of the built member goes back to exactly that file.
        string destination = Path.Combine(fixture.Root, "game");
        await SourceBuilder.ExportAsync(fixture.Project, destination, ["zrdr.zbd"], token: Token);
        byte[] archive = await File.ReadAllBytesAsync(Path.Combine(destination, "zrdr.zbd"), Token);
        var member = Assert.Single(ArchiveSources.Read(archive));
        var two = Assert.Single(Nodes(ZrdDecoder.Read(member.Payload, Token)), n => n.Kind == ZrdKind.Float && BitConverter.UInt32BitsToSingle(n.Bits) == 2f);
        var change = Assert.Single(SourceResourceEdits.SourceChanges(archive, [new(member.Offset + two.SourceOffset, SourceResourceEdits.Float(7.5f))], r => File.ReadAllBytes(fixture.Path(r)), Token));
        Assert.Equal(shared, change.Relative);
        Assert.Contains("7.5", Encoding.Latin1.GetString(change.Content), StringComparison.Ordinal);

        static IEnumerable<ZrdNode> Nodes(ZrdNode node) => node.Children.SelectMany(Nodes).Prepend(node);
    }

    [Fact]
    public async Task ASoundWhosePathTheBankCannotHoldIsBuiltWithoutIt()
    {
        using SourceWorldFixture fixture = new();
        // Nothing reads a sound's source field, so a long path is left out rather than shortened or refused.
        string deep = $"data/common/sounds/{new string('v', 50)}/c.wav", near = "data/common/sounds/d.wav";
        byte[] wave = SourceFixture.Tone(SourceFixture.Low, 400, null);
        fixture.Write(deep, wave); fixture.Write(near, wave.ToArray());
        string destination = Path.Combine(fixture.Root, "game");
        var report = await SourceBuilder.ExportAsync(fixture.Project, destination, ["soundsh.zbd"], token: Token);
        Assert.Equal("built", Assert.Single(report.Outputs).Status);
        var members = ArchiveSources.Read(await File.ReadAllBytesAsync(Path.Combine(destination, "soundsh.zbd"), Token));
        Assert.Equal("", members.Single(m => m.Name == "c.wav").SourceField);
        Assert.Equal("data\\common\\sounds\\d.wav", members.Single(m => m.Name == "d.wav").SourceField);
    }

    [Fact]
    public async Task ASourceAddedAfterPlanningIsRefusedRatherThanLeftOut()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write("data/m1/zrdr/ai.zrd", "( GRAVITY ( -9.8 ) )\n");
        string destination = Path.Combine(fixture.Root, "game");
        // Added once the outputs are planned, before the archive is built from the plan's list.
        bool added = false;
        OnReport progress = new(_ => { if (!added) { added = true; fixture.Write("data/m1/zrdr/late.zrd", "( LATE ( 1 ) )\n"); } });

        var refused = await Assert.ThrowsAsync<InvalidDataException>(() => SourceBuilder.ExportAsync(fixture.Project, destination, ["m1/zrdr.zbd"], progress: progress, token: Token));
        Assert.True(added);
        Assert.Contains("changed while exporting", refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(destination, "m1", "zrdr.zbd")));

        // Exported again, the archive holds both.
        var report = await SourceBuilder.ExportAsync(fixture.Project, destination, ["m1/zrdr.zbd"], token: Token);
        Assert.Equal(2, Assert.Single(report.Outputs).Items);
    }

    [Fact]
    public async Task AProfileChangedAfterPlanningIsRefused()
    {
        using SourceWorldFixture fixture = new();
        const string Profile = """{ "format": "recoil-build-profile", "version": 1, "description": "first", "texturePacks": [ { "file": "rtexture2.zbd" } ] }""";
        fixture.Write("gamegen/build-profiles/custom.json", Profile);
        string destination = Path.Combine(fixture.Root, "game");
        bool changed = false;
        OnReport progress = new(_ => { if (!changed) { changed = true; fixture.Write("gamegen/build-profiles/custom.json", Profile.Replace("first", "second", StringComparison.Ordinal)); } });

        var refused = await Assert.ThrowsAsync<InvalidDataException>(() => SourceBuilder.ExportAsync(fixture.Project, destination, ["m1/rtexture2.zbd"], progress: progress, token: Token, profile: "custom"));
        Assert.True(changed);
        Assert.Contains("changed while exporting", refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(destination, "m1", "rtexture2.zbd")));
    }

    [Fact]
    public async Task ATextureResizedAfterPlanningIsRefusedRatherThanPublishedUnderTheOldPackName()
    {
        using SourceWorldFixture fixture = new();
        // One fixed 1 MB Direct3D pack: three 512-texel textures (1.5 MB at two bytes a texel) make the automatic pack rtexture2.
        fixture.Write("gamegen/build-profiles/small.json", """{ "format": "recoil-build-profile", "version": 1, "texturePacks": [ { "file": "rtexture1.zbd" }, { "file": "rtexture*.zbd", "budgetMiB": 64 } ] }""");
        for (int i = 0; i < 3; i++) fixture.Write($"data/m1/textures/glass{i}.png", Png(512));
        Assert.Contains(SourceBuilder.Plan(fixture.Project, null, BuildProfiles.Find(fixture.Project, "small")), p => p.Path == "m1/rtexture2.zbd" && p.Automatic);
        string destination = Path.Combine(fixture.Root, "game");
        // Once planned, one texture doubles: the textures now need 3 MB, which names the pack rtexture4.
        bool resized = false;
        OnReport progress = new(_ => { if (!resized) { resized = true; fixture.Write("data/m1/textures/glass0.png", Png(1024)); } });

        var refused = await Assert.ThrowsAsync<InvalidDataException>(() => SourceBuilder.ExportAsync(fixture.Project, destination, ["m1/rtexture2.zbd"], progress: progress, token: Token, profile: "small"));
        Assert.True(resized);
        Assert.Contains("changed while exporting", refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(destination, "m1", "rtexture2.zbd")));
        Assert.Contains(SourceBuilder.Plan(fixture.Project, null, BuildProfiles.Find(fixture.Project, "small")), p => p.Path == "m1/rtexture4.zbd" && p.Automatic);

        static byte[] Png(int side)
        {
            byte[] rgba = new byte[side * side * 4];
            for (int i = 0; i < side * side; i++) { rgba[i * 4] = (byte)i; rgba[i * 4 + 1] = (byte)(i >> 8); rgba[i * 4 + 2] = 90; rgba[i * 4 + 3] = 255; }
            return PngEncoder.Encode(new(side, side, rgba));
        }
    }
}
