using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>The 1998 demos' worlds (GameZ version 13) open read-only, and reconstruction reads only what the game reads.</summary>
public sealed class DemoWorldReadOnlyTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static byte[] Demo() => DemoWorldFixture.FromVersion15(GameZWriter.Write(GameZVersion13Tests.SampleWorld(), Token));

    [Fact]
    public void APosedNodeOfADemoWorldMovesItsTranslationToo()
    {
        // A mission scene poses vehicle clones with SetPose: version 13 stores the translation beside the rotation and scale.
        foreach (var (bytes, demo) in new[] { (Demo(), true), (GameZWriter.Write(GameZVersion13Tests.SampleWorld(), Token), false) })
        {
            var scene = FormatRegistry.Default.OpenBytes("gamez.zbd", bytes, token: Token).Scene!;
            int tower = scene.Nodes.Single(n => n.Name == "tower").Index;
            MissionSceneLoader.SetPose(scene, tower, Matrix4x4.CreateRotationY(0.5f) * Matrix4x4.CreateTranslation(7, 8, 9));
            var data = scene.Nodes[tower].Data;
            if (demo) Assert.Equal((7f, 8f, 9f), (data["translate"].Float("x"), data["translate"].Float("y"), data["translate"].Float("z")));
            else Assert.Null(data["translate"]);
            Assert.Equal(9f, (data["transform"] as JsonArray)![11]!.GetValue<float>());
        }
    }

    /// <summary>A mission folder with a world and a resource archive holding one pickup.</summary>
    private static string Mission(byte[] world)
    {
        string folder = Path.Combine(Path.GetTempPath(), "zstudio-demo-pickups-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        static ZrdNode A(params ZrdNode[] c) => new(Guid.NewGuid(), ZrdKind.Array, 0, "", c);
        static ZrdNode F(float v) => new(Guid.NewGuid(), ZrdKind.Float, BitConverter.SingleToUInt32Bits(v), "", []);
        var row = A(new(Guid.NewGuid(), ZrdKind.String, 0, "HEMORTAR_AMMO", []), new(Guid.NewGuid(), ZrdKind.Int, 1, "", []), A(F(12), F(8), F(-5)), A(F(0), F(0), F(0)), F(12.5f));
        File.WriteAllBytes(Path.Combine(folder, "zrdr.zbd"), SourceFixture.Archive(("puppies.zrd", ZrdWriter.Write(A(A(row))), new byte[64])));
        File.WriteAllBytes(Path.Combine(folder, "gamez.zbd"), world);
        return folder;
    }

    [Fact]
    public async Task ADemoWorldsPlacementsCanBeInspectedButNotEdited()
    {
        string demo = Mission(Demo()), release = Mission(GameZWriter.Write(GameZVersion13Tests.SampleWorld(), Token));
        try
        {
            using (var resolver = new AssetResolver(demo))
            {
                string archive = Path.Combine(demo, "zrdr.zbd"); byte[] before = File.ReadAllBytes(archive);
                var edits = await PickupPlacementEditSession.LoadAsync(Path.Combine(demo, "gamez.zbd"), resolver, Token);
                var record = Assert.Single(edits.Records);
                Assert.Contains("1998 demo", edits.ReadOnlyReason);
                Assert.Throws<InvalidOperationException>(() => edits.MoveTo(record.Source, record.OriginalPosition + Vector3.UnitY));
                Assert.Equal(record.OriginalPosition, edits.Position(record.Source));
                Assert.False(edits.IsDirty);
                await Assert.ThrowsAsync<InvalidOperationException>(() => edits.SaveAsync(new Dictionary<string, string> { [archive] = Path.Combine(demo, "copy.zbd") }, token: Token));
                Assert.Equal(before, File.ReadAllBytes(archive));
                Assert.False(File.Exists(Path.Combine(demo, "copy.zbd")));
            }
            using (var resolver = new AssetResolver(release))
            {
                var edits = await PickupPlacementEditSession.LoadAsync(Path.Combine(release, "gamez.zbd"), resolver, Token);
                var record = Assert.Single(edits.Records);
                Assert.Null(edits.ReadOnlyReason);
                Assert.True(edits.MoveTo(record.Source, record.OriginalPosition + Vector3.UnitY));
            }
        }
        finally { Directory.Delete(demo, true); Directory.Delete(release, true); }
    }

    [Fact]
    public async Task OnlyTheGamesFoldersAreReconstructedAndDecide()
    {
        // A demo copied into a subfolder of a release's data folder: its world, scripts and archives are not the game's.
        using var fixture = new SourceFixture();
        foreach (var file in Directory.GetFiles(fixture.Corpus, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(fixture.Corpus, "demo", Path.GetRelativePath(fixture.Corpus, file)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
        }
        File.WriteAllBytes(Path.Combine(fixture.Corpus, "demo", "m1", "gamez.zbd"), Demo());
        // MechWarrior 3 data in a subfolder is not read either.
        byte[] mw3 = new byte[64]; BitConverter.TryWriteBytes(mw3, GameZWriter.Magic); BitConverter.TryWriteBytes(mw3.AsSpan(4), 27u);
        Directory.CreateDirectory(Path.Combine(fixture.Corpus, "mw3", "c1")); File.WriteAllBytes(Path.Combine(fixture.Corpus, "mw3", "c1", "gamez.zbd"), mw3);
        var report = await SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, token: Token);
        Assert.Equal(2, report.Families["resources"]); Assert.Equal(1, report.Families["scripts"]); Assert.Equal(3, report.Families["sounds"]);
        Assert.Contains("demo/m1/gamez.zbd", report.NotReconstructed); Assert.Contains("demo/interp.zbd", report.NotReconstructed); Assert.Contains("mw3/c1/gamez.zbd", report.NotReconstructed);
        Assert.DoesNotContain(report.Notes, n => n.Contains("another version", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(fixture.Project, "data", "demo")));

        // A MechWarrior 3 folder, whose own folders hold its worlds, is still refused as one.
        string folder = Path.Combine(fixture.Root, "mw3game"); Directory.CreateDirectory(Path.Combine(folder, "c1"));
        File.Copy(Path.Combine(fixture.Corpus, "interp.zbd"), Path.Combine(folder, "interp.zbd")); File.WriteAllBytes(Path.Combine(folder, "c1", "gamez.zbd"), mw3);
        var refused = await Assert.ThrowsAsync<InvalidDataException>(() => SourceExtractor.ExtractAsync(folder, Path.Combine(fixture.Root, "project-mw3"), token: Token));
        Assert.Contains("MechWarrior 3", refused.Message);
    }
}
