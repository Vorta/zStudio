using System.Text;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class EffectDiscoveryBudgetTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Common = "data/common/one/zrdr/effects.zrd";
    private const string Empty = "( EFFECTS ( ) )";
    private static string Effects(params string[] names) => "( EFFECTS ( " + string.Join(" ", names.Select(n => $"( template.flt NAME ( \"{n}\" ) )")) + " ) )";

    [Fact]
    public async Task AnimationOnlyExportRefusesAggregateInputBeforeReadingTheNextSource()
    {
        using Project project = new();
        byte[] source = Encoding.Latin1.GetBytes(Empty.PadRight(128));
        project.Write(Common, source); project.Write("data/common/two/zrdr/effects.zrd", source);
        // The duplicate-basename common archive is planned but deliberately not selected.
        string target = project.Output("m1/anim.zbd");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        byte[] sentinel = [97, 98, 99]; await File.WriteAllBytesAsync(target, sentinel, Token);
        var limits = new SourceBuilder.EffectLimits(InputBytes: 255);
        var snapshot = new SourceBuilder.Snapshot(project.Root, effectLimits: limits);
        var error = Assert.Throws<InvalidDataException>(() => snapshot.Effects("m1", Token));
        Assert.Contains("input bytes", error.Message);
        Assert.Equal(128, snapshot.EffectInputBytes); Assert.Equal(1, snapshot.EffectFilesDecoded);
        Assert.Single(snapshot.Hashes()); // The next file never reached Snapshot's full read/hash.
        await Assert.ThrowsAsync<InvalidDataException>(() => SourceBuilder.RunAsync(project.Root, project.Destination,
            ["m1/anim.zbd"], true, null, Token, null, limits));
        Assert.Equal(sentinel, await File.ReadAllBytesAsync(target, Token));
        Assert.Equal(source, await File.ReadAllBytesAsync(project.Path(Common), Token));
        Assert.Single(Directory.GetFiles(project.Destination, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ExactUniqueInputBoundarySharesCommonNamesAndShadowsMissionEffects()
    {
        using Project project = new();
        byte[] common = Encoding.Latin1.GetBytes(Effects("fire", "Fire", "fire"));
        byte[] local = ZrdWriter.Write(ZrdText.Parse(Effects("smoke"), Token), Token);
        project.Write(Common, common); project.Write("data/m2/zrdr/effects.zrd", local);
        project.Definitions("m1", "fire"); project.Definitions("m2", "smoke");
        // The game opens effects.zrd from the first mounted archive that holds it: the common zrdr.zbd, mounted before
        // m2's, so m2's own effects.zrd is never read and its anim.zbd would be rejected.
        var shadowed = new SourceBuilder.Snapshot(project.Root, effectLimits: new(InputBytes: common.Length));
        Assert.Equal(["Fire", "fire"], shadowed.Effects("m1", Token)!.Order(StringComparer.Ordinal));
        Assert.Equal(["Fire", "fire"], shadowed.Effects("m2", Token)!.Order(StringComparer.Ordinal));
        Assert.Equal(common.Length, shadowed.EffectInputBytes); Assert.Equal(1, shadowed.EffectFilesDecoded);
        var check = await SourceBuilder.RunAsync(project.Root, null, ["m1/anim.zbd", "m2/anim.zbd", "m2/zrdr.zbd"], false, null, Token, null, new(InputBytes: common.Length));
        Assert.Equal(["built", "failed", "built"], check.Outputs.Select(o => o.Status));
        Assert.Contains("smoke", check.Outputs[1].Error);
        Assert.Contains(Common, Assert.Single(check.Outputs[2].Warnings, w => w.Contains("data/m2/zrdr/effects.zrd")));
        // Without a common effects.zrd the mission's own is the one the game reads.
        File.Delete(project.Path(Common));
        var limits = new SourceBuilder.EffectLimits(InputBytes: local.Length);
        var snapshot = new SourceBuilder.Snapshot(project.Root, effectLimits: limits);
        Assert.Null(snapshot.Effects("m1", Token));
        Assert.Equal("smoke", Assert.Single(snapshot.Effects("m2", Token)!));
        Assert.Equal(local.Length, snapshot.EffectInputBytes);
        Assert.Equal(1, snapshot.EffectFilesDecoded);
        snapshot.CheckUnchanged(Token);
        var result = await SourceBuilder.RunAsync(project.Root, project.Destination, ["m1/anim.zbd", "m2/anim.zbd"], false, null, Token, null, limits);
        Assert.Equal(2, result.Built); Assert.Equal(0, result.Failed);
        foreach (string mission in new[] { "m1", "m2" })
        {
            var package = AnimationPackage.Read(await File.ReadAllBytesAsync(project.Output(mission + "/anim.zbd"), Token), Token);
            Assert.Equal("gate", package.Entries[1].RootName);
            Assert.NotEmpty(package.Entries[1].Sequences[0].Events);
        }
    }

    [Fact]
    public async Task MissingEmptyAndCaseDistinctEffectsRetainCompilerSemantics()
    {
        using Project project = new();
        project.Definitions("m1", "Fire");
        Assert.Null(new SourceBuilder.Snapshot(project.Root).Effects("m1", Token));
        Assert.Equal(1, (await SourceBuilder.CheckAsync(project.Root, ["m1/anim.zbd"], token: Token)).Built);
        project.Write(Common, Empty);
        Assert.Empty(new SourceBuilder.Snapshot(project.Root).Effects("m1", Token)!);
        Assert.Equal(1, (await SourceBuilder.CheckAsync(project.Root, ["m1/anim.zbd"], token: Token)).Failed);
        project.Write(Common, Effects("fire"));
        Assert.Equal(1, (await SourceBuilder.CheckAsync(project.Root, ["m1/anim.zbd"], token: Token)).Failed);
        project.Write(Common, Effects("Fire"));
        Assert.Equal(1, (await SourceBuilder.CheckAsync(project.Root, ["m1/anim.zbd"], token: Token)).Built);
    }

    [Fact]
    public void RetainedNamesRefuseBeforeCachingAndCannotResetForAnotherMission()
    {
        using Project project = new();
        project.Write(Common, Effects(new string('x', 400)));
        var snapshot = new SourceBuilder.Snapshot(project.Root, effectLimits: new(RetainedBytes: 1024));
        Assert.Contains("retained names", Assert.Throws<InvalidDataException>(() => snapshot.Effects("m1", Token)).Message);
        Assert.Equal(0, snapshot.EffectFilesDecoded);
        Assert.Throws<InvalidDataException>(() => snapshot.Effects("m2", Token));
        Assert.Equal(new string('x', 400), Assert.Single(new SourceBuilder.Snapshot(project.Root).Effects("m1", Token)!));
    }

    [Fact]
    public void RepeatedCachedUnionsStillConsumeOperationWork()
    {
        using Project project = new();
        project.Write(Common, Effects("fire"));
        var snapshot = new SourceBuilder.Snapshot(project.Root, effectLimits: new(Work: 20_000));
        Assert.Equal("fire", Assert.Single(snapshot.Effects("m1", Token)!));
        int accepted = 1;
        var error = Assert.Throws<InvalidDataException>(() =>
        {
            for (; accepted <= 100; accepted++) Assert.Equal("fire", Assert.Single(snapshot.Effects("m1", Token)!));
        });
        Assert.Contains("discovery work", error.Message); Assert.InRange(accepted, 2, 100);
        Assert.Equal(1, snapshot.EffectFilesDecoded);
        Assert.Equal(new FileInfo(project.Path(Common)).Length, snapshot.EffectInputBytes);
    }

    [Fact]
    public void CachedNamesStillRejectSameStampContentMutationAndNewInventory()
    {
        using Project project = new();
        project.Write(Common, Effects("fire"));
        var snapshot = new SourceBuilder.Snapshot(project.Root);
        Assert.Equal("fire", Assert.Single(snapshot.Effects("m1", Token)!));
        var stamp = FileStamp.Read(project.Path(Common));
        project.Write(Common, Effects("mist"));
        File.SetLastWriteTimeUtc(project.Path(Common), stamp.LastWriteUtc);
        Assert.Equal(stamp, FileStamp.Read(project.Path(Common)));
        // Cache identity is frozen, but publication must reject its changed source even at the same stamp.
        Assert.Equal("fire", Assert.Single(snapshot.Effects("m2", Token)!));
        Assert.Throws<InvalidDataException>(() => snapshot.CheckUnchanged(Token));
        var absent = new SourceBuilder.Snapshot(project.Root);
        _ = absent.Effects("m1", Token);
        project.Write("data/common/new/zrdr/effects.zrd", Empty);
        Assert.Contains("inventory", Assert.Throws<InvalidDataException>(() => absent.CheckUnchanged(Token)).Message);
    }

    [Fact]
    public void FailedParsingConsumesInputAndCancellationDoesNotPublishCacheEntries()
    {
        using Project project = new();
        byte[] malformed = "( EFFECTS ( "u8.ToArray(); project.Write(Common, malformed);
        var snapshot = new SourceBuilder.Snapshot(project.Root, effectLimits: new(InputBytes: malformed.Length));
        Assert.Throws<InvalidDataException>(() => snapshot.Effects("m1", Token));
        Assert.Equal(malformed.Length, snapshot.EffectInputBytes); Assert.Equal(0, snapshot.EffectFilesDecoded);
        Assert.Contains("input bytes", Assert.Throws<InvalidDataException>(() => snapshot.Effects("m2", Token)).Message);
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        var fresh = new SourceBuilder.Snapshot(project.Root);
        Assert.ThrowsAny<OperationCanceledException>(() => fresh.Effects("m1", cancelled.Token));
        Assert.Equal(0, fresh.EffectInputBytes); Assert.Equal(0, fresh.EffectFilesDecoded); Assert.Empty(fresh.Hashes());
    }

    [Fact]
    public async Task SameStampMutationAtPublicationKeepsTheExistingDestination()
    {
        using Project project = new();
        project.Write(Common, Effects("fire")); project.Definitions("m1", "fire");
        string target = project.Output("m1/anim.zbd"); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        byte[] sentinel = [20, 21, 22]; await File.WriteAllBytesAsync(target, sentinel, Token);
        var stamp = FileStamp.Read(project.Path(Common)); bool changed = false;
        var progress = new ImmediateProgress(value =>
        {
            if (value.Item != "Publishing") return;
            project.Write(Common, Effects("mist")); File.SetLastWriteTimeUtc(project.Path(Common), stamp.LastWriteUtc);
            Assert.Equal(stamp, FileStamp.Read(project.Path(Common))); changed = true;
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => SourceBuilder.ExportAsync(project.Root, project.Destination,
            ["m1/anim.zbd"], true, progress, Token));
        Assert.True(changed); Assert.Equal(sentinel, await File.ReadAllBytesAsync(target, Token));
        Assert.Single(Directory.GetFiles(project.Destination, "*", SearchOption.AllDirectories));
    }

    private sealed class ImmediateProgress(Action<SourceProgress> action) : IProgress<SourceProgress>
    { public void Report(SourceProgress value) => action(value); }

    private sealed class Project : IDisposable
    {
        private readonly string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zstudio-effects-admission-" + Guid.NewGuid().ToString("N"));
        internal string Root => System.IO.Path.Combine(folder, "project");
        internal string Destination => System.IO.Path.Combine(folder, "export");
        internal Project()
        {
            Directory.CreateDirectory(System.IO.Path.Combine(Root, "gamegen"));
            Definitions("m1", null); Definitions("m2", null);
        }
        internal void Definitions(string mission, string? effect) => Write($"data/{mission}/zrdr/anim.zad",
            "( ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION ( NAME ( gate ) SEQUENCE_DEFINITION ( NAME ( s ) "
            + (effect == null ? "" : $"EFFECT ( NAME ( {effect} ) )") + " ) ) ) ) )");
        internal string Path(string relative) => System.IO.Path.Combine(Root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        internal string Output(string relative) => System.IO.Path.Combine(Destination, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        internal void Write(string relative, string text) => Write(relative, Encoding.Latin1.GetBytes(text));
        internal void Write(string relative, byte[] bytes) { string path = Path(relative); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes); }
        public void Dispose() { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
