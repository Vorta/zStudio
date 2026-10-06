using System.Numerics;
using System.Text;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class SourceWorldTests
{
    [Fact]
    public void ModelInsertionUsesTheBuildsCaseSensitivePrefixDispatch()
    {
        var rewritten = Encoding.ASCII.GetString(SourceWorlds.InsertIntoScript("quit\nGameZWriteZBDFileExtra world.zbd\nQuit\n"u8, [new("data/m1/models/a.gltf", "a")]));
        Assert.Contains("LoadGameGen a.gltf a\nGameZWriteZBDFileExtra", rewritten);
    }
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private sealed class OnReport(Action<SourceProgress> action) : IProgress<SourceProgress> { public void Report(SourceProgress value) => action(value); }

    [Fact]
    public async Task PreviewBuildRefusesALinkInstalledAfterPlanning()
    {
        using var fixture = new SourceWorldFixture();
        string destination = Path.Combine(SourceWorlds.PreviewRoot(fixture.Project), "test"), outside = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(outside); bool replaced = false;
        var progress = new OnReport(_ =>
        {
            if (replaced) return; replaced = true;
            Directory.Delete(destination); Directory.CreateSymbolicLink(destination, outside);
        });
        try
        {
            Assert.Contains("is a link", (await Assert.ThrowsAsync<InvalidDataException>(() => SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", destination, progress: progress, token: Token))).Message);
            Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
        }
        finally { if (Directory.Exists(destination) && File.GetAttributes(destination).HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(destination); }
    }

    [Fact]
    public void ModelLinesGoBeforeTheWorldIsWritten()
    {
        byte[] script = Encoding.ASCII.GetBytes("source support\\load.gw\r\n# keep\r\nGameZWriteZBDFile %MissionZBDFile%\r\nsource support\\tex_fx.gw\r\nQuit\r\n");
        var result = Encoding.ASCII.GetString(SourceWorlds.InsertIntoScript(script, [
            new("data/m2/models/bft/ltank.gltf", "ltank"),
            new("data/common/models/crate.glb", "crate2", new(2417.5f, 0, -12), -70)]));
        Assert.Equal("""
            source support\load.gw
            # keep
            SetModelDirectory ..\data\m2\models\bft
            LoadGameGen ltank.gltf ltank
            SetModelDirectory ..\data\common\models
            LoadGameGen crate.glb crate2
            Object3DTranslate 2417.5 0.0 -12.0
            Object3DRotate 0.0 -70.0 0.0
            FindNode %worldName%
            AddChild crate2
            GameZWriteZBDFile %MissionZBDFile%
            source support\tex_fx.gw
            Quit

            """.ReplaceLineEndings("\r\n"), result);
        // Nothing to add keeps the file; a script that does not write the world (or only after Quit) is refused.
        Assert.Equal(script, SourceWorlds.InsertIntoScript(script, []));
        Assert.Throws<InvalidDataException>(() => SourceWorlds.InsertIntoScript("source support\\m1.gw\nQuit\nGameZWriteZBDFile x\n"u8, [new("data/m1/models/a.gltf", "a")]));
    }

    [Fact]
    public void DefinitionFilesJoinTheAnimationList()
    {
        byte[] root = """
            (
              ANIMATION_DEFINITIONS (
                GRAVITY ( -9.8 )
                ANIMATION_LIST (
                  ANIMATION_DEFINITION_FILE ( "..\\data\\\\common\\zrdr\\enemies\\drone.zad" )
                )
              )
            )
            """u8.ToArray();
        byte[] added = SourceWorlds.AddDefinitionFiles(root, ["data/common/zrdr/enemies/ltank.zad", "data/common/zrdr/enemies/drone.zad"], Token);
        string text = Encoding.ASCII.GetString(added);
        // A file already listed (even with a doubled separator) is not listed again; the new one follows the list.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, @"drone\.zad"));
        Assert.True(text.IndexOf("ltank.zad", StringComparison.Ordinal) > text.IndexOf("drone.zad", StringComparison.Ordinal));
        Assert.Contains("ANIMATION_DEFINITION_FILE", text[text.IndexOf("drone.zad", StringComparison.Ordinal)..]);
        // New files join the last list the compiler reads; a file any list names is not listed again.
        byte[] twoLists = """
            (
              ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION_FILE ( "..\\data\\m1\\zrdr\\a.zad" ) ) )
              ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION_FILE ( "..\\data\\m1\\zrdr\\b.zad" ) ) )
            )
            """u8.ToArray();
        string joined = Encoding.ASCII.GetString(SourceWorlds.AddDefinitionFiles(twoLists, ["data/m1/zrdr/a.zad", "data/m1/zrdr/c.zad"], Token));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(joined, @"a\.zad"));
        Assert.True(joined.IndexOf("c.zad", StringComparison.Ordinal) > joined.IndexOf("b.zad", StringComparison.Ordinal));
        // Compiled definitions stay compiled; a file without an animation list gets one.
        byte[] compiled = ZrdWriter.Write(ZrdText.Parse("( ANIMATION_DEFINITIONS ( GRAVITY ( -9.8 ) ) )"u8, Token), Token);
        byte[] listed = SourceWorlds.AddDefinitionFiles(compiled, ["data/m1/zrdr/gate.zad"], Token);
        Assert.False(ZrdText.LooksLikeText(listed));
        string written = ZrdText.Write(ZrdDecoder.Read(listed, Token), Token);
        Assert.Contains("ANIMATION_LIST", written); Assert.Contains("gate.zad", written);
        Assert.Throws<InvalidDataException>(() => SourceWorlds.AddDefinitionFiles("( GRAVITY ( 1.0 ) )"u8, ["data/a.zad"], Token));
    }

    [Fact]
    public async Task AModelFromAnotherMissionBringsItsTexturesAndAnimations()
    {
        using SourceWorldFixture fixture = new();
        string root = fixture.Project;
        Assert.Equal(["m1", "m2"], SourceWorlds.Missions(root, TestContext.Current.CancellationToken));
        Assert.Contains(SourceWorlds.Models(root, TestContext.Current.CancellationToken), m => m.Path == fixture.Tank && m.Name == "tank" && m.Folder == "data/m2/models/bft");
        // m2 lists the tank's definitions; m1 does not yet.
        var definitions = SourceWorlds.DefinitionsFor(root, "m1", "tank", token: Token);
        Assert.Equal(SourceWorldFixture.TankDefinitions, definitions.Single().Path);
        Assert.Equal(["tank_die"], definitions.Single().Animations); Assert.Equal(["m2"], definitions.Single().Missions);
        Assert.Empty(SourceWorlds.DefinitionsFor(root, "m2", "tank", token: Token));

        SourceWorkspace workspace = new(root);
        Assert.False(workspace.IsDirty);
        SourceWorlds.AddModel(workspace, "m1", new(new(fixture.Tank, "tank"), [SourceWorldFixture.TankDefinitions]), Token);
        SourceWorlds.AddModel(workspace, "m1", new(new(fixture.Tank, "tank_wreck", new(100, 0, -50), 90), []), Token);
        Assert.True(workspace.IsDirty); Assert.Equal(2, workspace.UndoCount);
        Assert.Equal(["data/m1/zrdr/anim.zad", "gamegen/m1.gs"], workspace.DirtyFiles);

        // The preview is built privately, in zStudio's working folder of the project (never elsewhere), from the pending sources.
        string preview = Path.Combine(SourceWorlds.PreviewRoot(root), "preview");
        Assert.Equal(Path.Combine(root, "zstudio", "cache", "worlds", "preview"), preview);
        foreach (string elsewhere in new[] { Path.Combine(root, "preview"), Path.Combine(root, "zstudio", "preview"), SourceWorlds.PreviewRoot(root), Path.Combine(fixture.Root, "preview") })
            await Assert.ThrowsAsync<InvalidDataException>(() => SourceWorlds.BuildPreviewAsync(root, "m1", elsewhere, workspace.Overlay(), token: Token));
        Assert.False(Directory.Exists(Path.Combine(root, "preview"))); Assert.False(Directory.Exists(Path.Combine(fixture.Root, "preview")));
        var build = await SourceWorlds.BuildPreviewAsync(root, "m1", preview, workspace.Overlay(), token: Token);
        Assert.All(build.Outputs, o => Assert.Equal("built", o.Status));
        Assert.Contains("data/m2/models/bft/tank.gltf", build.Inputs.Keys);
        Assert.DoesNotContain("gamegen/m1.gs", build.Inputs.Keys);
        // The build records what it read, from the workspace or the disk.
        Assert.Contains("gamegen/m1.gs", build.Dependencies); Assert.Contains("data/m2/models/bft/tank.gltf", build.Dependencies);
        var world = GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", await File.ReadAllBytesAsync(build.WorldPath, Token), token: Token), Token);
        var tank = world.Nodes.Single(n => n.Name == "tank"); var wreck = world.Nodes.Single(n => n.Name == "tank_wreck");
        Assert.Empty(tank.Parents);
        Assert.Equal(WorldNodeClass.World, wreck.Parents.Single().Class);
        Assert.Equal(new Vector3(100, 0, -50), WorldUpdate.LocalMatrix(wreck)!.Value.Translation);
        Assert.Contains(world.Textures, t => t.Name == "camo");
        var pack = FormatRegistry.Default.OpenBytes("rtexture16.zbd", await File.ReadAllBytesAsync(Path.Combine(preview, "m1", "rtexture16.zbd"), Token), token: Token);
        Assert.Contains(pack.Assets, a => a.Kind == AssetKind.Texture && a.Name == "camo");
        var animations = FormatRegistry.Default.OpenBytes("anim.zbd", await File.ReadAllBytesAsync(Path.Combine(preview, "m1", "anim.zbd"), Token), token: Token).Animations!;
        Assert.Contains(animations.Entries, e => e.Name == "tank_die" && e.RootName == "tank");
        Assert.Contains(animations.Entries, e => e.Name == "sink");
        // A preview folder must be new.
        await Assert.ThrowsAsync<IOException>(() => SourceWorlds.BuildPreviewAsync(root, "m1", preview, token: Token));

        // Undo and redo move through the history; saving writes every changed source together.
        workspace.Undo(); Assert.Equal(1, workspace.UndoCount); workspace.Redo();
        string script = fixture.Path("gamegen/m1.gs"), list = fixture.Path("data/m1/zrdr/anim.zad");
        string listBefore = await File.ReadAllTextAsync(list, Token);
        Assert.Equal(["data/m1/zrdr/anim.zad", "gamegen/m1.gs"], workspace.Save(Token).Order(StringComparer.Ordinal));
        Assert.False(workspace.IsDirty); Assert.Empty(workspace.Save(Token));
        Assert.False(Directory.Exists(Path.Combine(root, "zstudio", "staging")) && Directory.EnumerateFileSystemEntries(Path.Combine(root, "zstudio", "staging")).Any());
        string saved = await File.ReadAllTextAsync(script, Token);
        Assert.Contains("SetModelDirectory ..\\data\\m2\\models\\bft\r\nLoadGameGen tank.gltf tank\r\n", saved);
        Assert.EndsWith("AddChild tank_wreck\r\nGameZWriteZBDFile ..\\m1\\gamez.zbd\r\nQuit\r\n", saved);
        string listSaved = await File.ReadAllTextAsync(list, Token);
        Assert.Contains("enemies\\\\tank.zad", listSaved);
        // The animation list keeps its layout: only the new entry's lines were added.
        Assert.StartsWith(listBefore[..listBefore.IndexOf("ANIMATION_DEFINITION_FILE", StringComparison.Ordinal)], listSaved);
        Assert.Contains("gates.zad", listSaved);
        // The export of m1 now holds the tank, its texture in every pack and its animation.
        string exported = Path.Combine(fixture.Root, "zbd");
        var report = await SourceBuilder.ExportAsync(root, exported, ["m1/gamez.zbd", "m1/anim.zbd", "m1/texture2.zbd", "m1/rtexture4.zbd"], token: Token);
        Assert.Equal(4, report.Built);
        foreach (string packName in new[] { "texture2.zbd", "rtexture4.zbd" })
            Assert.Contains(FormatRegistry.Default.OpenBytes(packName, await File.ReadAllBytesAsync(Path.Combine(exported, "m1", packName), Token), token: Token).Assets, a => a.Name == "camo");

        // Undoing past the save leaves the sources to restore; a file changed on disk is never overwritten.
        workspace.Undo(); Assert.True(workspace.IsDirty);
        Assert.Empty(workspace.ExternalChanges());
        await File.AppendAllTextAsync(script, "# edited elsewhere\r\n", Token);
        File.SetLastWriteTimeUtc(script, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(["gamegen/m1.gs"], workspace.ExternalChanges());
        Assert.ThrowsAny<IOException>(() => workspace.Save(Token));
        Assert.EndsWith("# edited elsewhere\r\n", await File.ReadAllTextAsync(script, Token));
        Assert.True(workspace.IsDirty);
        // Further edits of a file with unsaved edits that changed on disk are refused too.
        Assert.Throws<SourceFileChangedException>(() => SourceWorlds.AddModel(workspace, "m1", new(new("data/m1/models/m1.gltf", "extra"), []), Token));

        // A new change after undoing past the save cannot look clean, and a withdrawn change cannot be redone; the step it
        // displaced can be again.
        SourceWorkspace again = new(root);
        SourceWorlds.AddModel(again, "m2", new(new("data/m1/models/m1.gltf", "extra"), []), Token);
        Assert.Equal(["gamegen/m2.gs"], again.Save(Token));
        again.Undo(); var other = SourceWorlds.AddModel(again, "m2", new(new("data/m1/models/m1.gltf", "other"), []), Token);
        Assert.True(again.IsDirty);
        again.Retract(other); again.Redo(); Assert.False(again.CanRedo); Assert.False(again.IsDirty);
    }

    [Fact]
    public async Task APlacedModelIsTheNodeItsLinesAttach()
    {
        using SourceWorldFixture fixture = new();
        string root = fixture.Project;
        // The tank model's own root node is "hull". AddChild takes the newest node with a name, and the model's nodes are
        // newer than the root LoadGameGen names, so a placed model named "hull" would leave its root (and placement) out.
        SourceWorkspace workspace = new(root);
        SourceModelAddition hull = new(fixture.Tank, "hull", new(100, 0, -50));
        SourceWorlds.AddModel(workspace, "m1", new(hull, []), Token);
        var refused = await Assert.ThrowsAsync<InvalidDataException>(() => SourceWorlds.BuildPreviewAsync(root, "m1", Path.Combine(SourceWorlds.PreviewRoot(root), "p1"), workspace.Overlay(), token: Token, additions: [hull]));
        Assert.Contains("node of its own named hull", refused.Message);
        // The same world without the check shows the defect: the inner node is in the world, the placed root is not.
        var unchecked_ = await SourceWorlds.BuildPreviewAsync(root, "m1", Path.Combine(SourceWorlds.PreviewRoot(root), "p2"), workspace.Overlay(), token: Token);
        Assert.Contains(unchecked_.Outputs.Single(o => o.Family == "world").Warnings, w => w.Contains("node of its own named hull", StringComparison.Ordinal));
        var world = GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", await File.ReadAllBytesAsync(unchecked_.WorldPath, Token), token: Token), Token);
        Assert.Contains(world.Nodes, n => n.Name == "hull" && n.Parents.Any(p => p.Class == WorldNodeClass.World));
        Assert.DoesNotContain(world.Nodes, n => n.Name == "hull" && n.Parents.Any(p => p.Class == WorldNodeClass.World) && WorldUpdate.LocalMatrix(n)?.Translation == new Vector3(100, 0, -50));

        // Unplaced, the name only makes lookups find the model's node, as the engine would; a new name places the model.
        SourceModelAddition unplaced = new(fixture.Tank, "hull"), placedAt = new(fixture.Tank, "tank_at", new(100, 0, -50));
        workspace.Undo(); SourceWorlds.AddModel(workspace, "m1", new(unplaced, []), Token); SourceWorlds.AddModel(workspace, "m1", new(placedAt, []), Token);
        var build = await SourceWorlds.BuildPreviewAsync(root, "m1", Path.Combine(SourceWorlds.PreviewRoot(root), "p3"), workspace.Overlay(), token: Token, additions: [unplaced, placedAt]);
        world = GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", await File.ReadAllBytesAsync(build.WorldPath, Token), token: Token), Token);
        var placed = world.Nodes.Single(n => n.Name == "tank_at");
        Assert.Equal(WorldNodeClass.World, placed.Parents.Single().Class);
        Assert.Equal(new Vector3(100, 0, -50), WorldUpdate.LocalMatrix(placed)!.Value.Translation);
        // Additions the script does not run where it writes the world are refused too.
        await Assert.ThrowsAsync<InvalidDataException>(() => SourceWorlds.BuildPreviewAsync(root, "m1", Path.Combine(SourceWorlds.PreviewRoot(root), "p4"), token: Token, additions: [new(fixture.Tank, "tank_at", new(1, 2, 3))]));
    }

    [Fact]
    public void ARefusedOrFailedSaveLeavesTheProjectAsItWas()
    {
        using SourceWorldFixture fixture = new();
        string root = fixture.Project, script = fixture.Path("gamegen/m1.gs"), list = fixture.Path("data/m1/zrdr/anim.zad");
        byte[] scriptBefore = File.ReadAllBytes(script), listBefore = File.ReadAllBytes(list);
        SourceWorkspace workspace = new(root);
        // One addition changes both files.
        SourceWorlds.AddModel(workspace, "m1", new(new(fixture.Tank, "tank"), [SourceWorldFixture.TankDefinitions]), Token);

        // The animation list changed elsewhere: neither file is written, although the list sorts first.
        File.AppendAllText(list, "# elsewhere\r\n"); File.SetLastWriteTimeUtc(list, DateTime.UtcNow.AddMinutes(1));
        var conflict = Assert.Throws<SourceConflictException>(() => workspace.Save(Token));
        Assert.Contains("data/m1/zrdr/anim.zad", conflict.Files);
        Assert.Equal(scriptBefore, File.ReadAllBytes(script));
        Assert.True(workspace.IsDirty);

        // A list another program holds open fails while the files are published: whatever was published is put back.
        SourceWorkspace again = new(root);
        SourceWorlds.AddModel(again, "m1", new(new(fixture.Tank, "tank"), [SourceWorldFixture.TankDefinitions]), Token);
        byte[] listNow = File.ReadAllBytes(list);
        using (FileStream held = new(list, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<IOException>(() => again.Save(Token));
            Assert.Equal(scriptBefore, File.ReadAllBytes(script)); Assert.Equal(listNow, File.ReadAllBytes(list));
            Assert.True(again.IsDirty); Assert.Empty(again.ExternalChanges());
        }
        Assert.Empty(new SourcePublisher(root).FindInterrupted(Token));
        // Once the list is free, the same edits save both files.
        Assert.Equal(2, again.Save(Token).Count);
        Assert.NotEqual(scriptBefore, File.ReadAllBytes(script)); Assert.NotEqual(listBefore, File.ReadAllBytes(list));
    }

    [Fact]
    public void AdditionsAreValidatedBeforeTheyReachAScript()
    {
        using SourceWorldFixture fixture = new();
        string root = fixture.Project;
        SourceWorkspace workspace = new(root);
        void Refused(SourceModelAddition addition) => Assert.Throws<InvalidDataException>(() => SourceWorlds.AddModel(workspace, "m1", new(addition, []), Token));
        Refused(new("data/m2/models/bft/missing.gltf", "tank"));
        Refused(new("gamegen/m1.gs", "tank"));
        Refused(new("data/../gamegen/tank.gltf", "tank"));
        Refused(new(fixture.Tank, "two words"));
        Refused(new(fixture.Tank, new string('a', 32)));
        Refused(new(fixture.Tank, "tank", new(float.NaN, 0, 0)));
        Refused(new(fixture.Tank, "tank", new(0, 0, 0), 400));
        Assert.Throws<InvalidDataException>(() => SourceWorlds.AddModel(workspace, "m1", new(new(fixture.Tank, "tank"), ["gamegen/m1.gs"]), Token));
        Assert.False(workspace.IsDirty);
        // A mission without a world script has no world to edit.
        Assert.Throws<InvalidDataException>(() => SourceWorlds.AddModel(workspace, "m3", new(new(fixture.Tank, "tank"), []), Token));
    }
}
