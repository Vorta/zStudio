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
    private static CancellationToken Token => TestContext.Current.CancellationToken;

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
            LoadGameGen ltank.flt ltank
            SetModelDirectory ..\data\common\models
            LoadGameGen crate.flt crate2
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
                  ANIMATION_DEFINITION_FILE ( "..\\data\\\\common\\zrdr\\enemies\\drone.zrd" )
                )
              )
            )
            """u8.ToArray();
        byte[] added = SourceWorlds.AddDefinitionFiles(root, ["data/common/zrdr/enemies/ltank.zrd", "data/common/zrdr/enemies/drone.zrd"], Token);
        string text = Encoding.ASCII.GetString(added);
        // A file already listed (even with a doubled separator) is not listed again; the new one follows the list.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, @"drone\.zrd"));
        Assert.True(text.IndexOf("ltank.zrd", StringComparison.Ordinal) > text.IndexOf("drone.zrd", StringComparison.Ordinal));
        Assert.Contains("ANIMATION_DEFINITION_FILE", text[text.IndexOf("drone.zrd", StringComparison.Ordinal)..]);
        // New files join the last list the compiler reads; a file any list names is not listed again.
        byte[] twoLists = """
            (
              ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION_FILE ( "..\\data\\m1\\zrdr\\a.zrd" ) ) )
              ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION_FILE ( "..\\data\\m1\\zrdr\\b.zrd" ) ) )
            )
            """u8.ToArray();
        string joined = Encoding.ASCII.GetString(SourceWorlds.AddDefinitionFiles(twoLists, ["data/m1/zrdr/a.zrd", "data/m1/zrdr/c.zrd"], Token));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(joined, @"a\.zrd"));
        Assert.True(joined.IndexOf("c.zrd", StringComparison.Ordinal) > joined.IndexOf("b.zrd", StringComparison.Ordinal));
        // Compiled definitions stay compiled; a file without an animation list gets one.
        byte[] compiled = ZrdWriter.Write(ZrdText.Parse("( ANIMATION_DEFINITIONS ( GRAVITY ( -9.8 ) ) )"u8, Token), Token);
        byte[] listed = SourceWorlds.AddDefinitionFiles(compiled, ["data/m1/zrdr/gate.zrd"], Token);
        Assert.False(ZrdText.LooksLikeText(listed));
        string written = ZrdText.Write(ZrdDecoder.Read(listed, Token), Token);
        Assert.Contains("ANIMATION_LIST", written); Assert.Contains("gate.zrd", written);
        Assert.Throws<InvalidDataException>(() => SourceWorlds.AddDefinitionFiles("( GRAVITY ( 1.0 ) )"u8, ["data/a.zrd"], Token));
    }

    [Fact]
    public async Task AModelFromAnotherMissionBringsItsTexturesAndAnimations()
    {
        using SourceWorldFixture fixture = new();
        string root = fixture.Project;
        Assert.Equal(["m1", "m2"], SourceWorlds.Missions(root));
        Assert.Contains(SourceWorlds.Models(root), m => m.Path == fixture.Tank && m.Name == "tank" && m.Folder == "data/m2/models/bft");
        // m2 lists the tank's definitions; m1 does not yet.
        var definitions = SourceWorlds.DefinitionsFor(root, "m1", "tank", token: Token);
        Assert.Equal(SourceWorldFixture.TankDefinitions, definitions.Single().Path);
        Assert.Equal(["tank_die"], definitions.Single().Animations); Assert.Equal(["m2"], definitions.Single().Missions);
        Assert.Empty(SourceWorlds.DefinitionsFor(root, "m2", "tank", token: Token));

        SourceWorldEdits edits = new(root, "m1");
        Assert.False(edits.IsDirty);
        edits.Add(new(new(fixture.Tank, "tank"), [SourceWorldFixture.TankDefinitions]), Token);
        edits.Add(new(new(fixture.Tank, "tank_wreck", new(100, 0, -50), 90), []), Token);
        Assert.True(edits.IsDirty); Assert.Equal(2, edits.Additions.Count);

        // The preview is built privately, outside the project, from the pending sources.
        string preview = Path.Combine(fixture.Root, "preview");
        await Assert.ThrowsAsync<InvalidDataException>(() => SourceWorlds.BuildPreviewAsync(root, "m1", Path.Combine(root, "preview"), edits.Overlay(Token), token: Token));
        var build = await SourceWorlds.BuildPreviewAsync(root, "m1", preview, edits.Overlay(Token), token: Token);
        Assert.All(build.Outputs, o => Assert.Equal("built", o.Status));
        Assert.Contains("data/m2/models/bft/tank.gltf", build.Inputs.Keys);
        Assert.DoesNotContain(edits.ScriptPath, build.Inputs.Keys);
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

        // Undo and redo move through the additions; saving writes only the changed sources.
        edits.Undo(); Assert.Single(edits.Additions); edits.Redo();
        string script = fixture.Path("gamegen/m1.gs"), list = fixture.Path("data/m1/zrdr/anim.zrd");
        Assert.Equal(["gamegen/m1.gs", "data/m1/zrdr/anim.zrd"], edits.Save(Token));
        Assert.False(edits.IsDirty); Assert.Empty(edits.Save(Token));
        string saved = await File.ReadAllTextAsync(script, Token);
        Assert.Contains("SetModelDirectory ..\\data\\m2\\models\\bft\r\nLoadGameGen tank.flt tank\r\n", saved);
        Assert.EndsWith("AddChild tank_wreck\r\nGameZWriteZBDFile ..\\m1\\gamez.zbd\r\nQuit\r\n", saved);
        Assert.Contains("enemies\\\\tank.zrd", await File.ReadAllTextAsync(list, Token));
        // The export of m1 now holds the tank, its texture in every pack and its animation.
        string exported = Path.Combine(fixture.Root, "zbd");
        var report = await SourceBuilder.ExportAsync(root, exported, ["m1/gamez.zbd", "m1/anim.zbd", "m1/texture2.zbd", "m1/rtexture4.zbd"], token: Token);
        Assert.Equal(4, report.Built);
        foreach (string packName in new[] { "texture2.zbd", "rtexture4.zbd" })
            Assert.Contains(FormatRegistry.Default.OpenBytes(packName, await File.ReadAllBytesAsync(Path.Combine(exported, "m1", packName), Token), token: Token).Assets, a => a.Name == "camo");

        // Undoing past the save leaves the sources to restore; a file changed on disk is never overwritten.
        edits.Undo(); Assert.True(edits.IsDirty);
        Assert.False(edits.HasExternalChanges());
        await File.AppendAllTextAsync(script, "# edited elsewhere\r\n", Token);
        File.SetLastWriteTimeUtc(script, DateTime.UtcNow.AddMinutes(1));
        Assert.True(edits.HasExternalChanges());
        Assert.Throws<IOException>(() => edits.Save(Token));
        Assert.EndsWith("# edited elsewhere\r\n", await File.ReadAllTextAsync(script, Token));

        // A new addition after undoing past the save cannot look clean.
        SourceWorldEdits again = new(root, "m2");
        again.Add(new(new("data/m1/models/m1.gltf", "extra"), []), Token);
        Assert.Equal([again.ScriptPath], again.Save(Token));
        again.Undo(); again.Add(new(new("data/m1/models/m1.gltf", "other"), []), Token);
        Assert.True(again.IsDirty);
        again.Retract(); Assert.False(again.CanRedo);
    }

    [Fact]
    public void AdditionsAreValidatedBeforeTheyReachAScript()
    {
        using SourceWorldFixture fixture = new();
        string root = fixture.Project;
        SourceWorldEdits edits = new(root, "m1");
        void Refused(SourceModelAddition addition) => Assert.Throws<InvalidDataException>(() => edits.Add(new(addition, []), Token));
        Refused(new("data/m2/models/bft/missing.gltf", "tank"));
        Refused(new("gamegen/m1.gs", "tank"));
        Refused(new("data/../gamegen/tank.gltf", "tank"));
        Refused(new(fixture.Tank, "two words"));
        Refused(new(fixture.Tank, new string('a', 32)));
        Refused(new(fixture.Tank, "tank", new(float.NaN, 0, 0)));
        Refused(new(fixture.Tank, "tank", new(0, 0, 0), 400));
        Assert.Throws<InvalidDataException>(() => edits.Add(new(new(fixture.Tank, "tank"), ["gamegen/m1.gs"]), Token));
        Assert.False(edits.IsDirty);
        // A mission without a world script has no world to edit.
        Assert.Throws<InvalidDataException>(() => new SourceWorldEdits(root, "m3"));
    }
}
