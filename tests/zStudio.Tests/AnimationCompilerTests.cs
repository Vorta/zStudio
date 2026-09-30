using System.Numerics;
using System.Text;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class AnimationCompilerTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class MemoryFiles(Dictionary<string, byte[]> files) : IProjectFiles
    {
        public Dictionary<string, byte[]> Files { get; } = files;
        public bool Exists(string relative) => Files.ContainsKey(relative);
        public byte[] Read(string relative, CancellationToken token) => Files[relative];
    }

    private const string Root = """
        (
          ANIMATION_DEFINITIONS (
            GRAVITY ( -9.8 )
            ANIMATION_PATH ( "..\\data\\m1\\zrdr\\envmodels" )
            ANIMATION_LIST (
              ANIMATION_DEFINITION_FILE ( gate.zrd )
              ANIMATION_DEFINITION_FILE ( "..\\data\\m1\\zrdr\\missing.zrd" )
            )
          )
        )
        """;

    private const string Gate = """
        (
          ANIMATION_DEFINITIONS (
            ANIMATION_LIST (
              ANIMATION_DEFINITION (
                NAME ( gate )
                ANIMATION_NAME ( open_gate )
                ACTIVATION ( ON_CALL )
                HEALTH ( 6.0 )
                RESET_TIME ( -1.0 )
                EXECUTION_BY_RANGE ( 9 )
                SAVE_LOG ( OFF )
                RESET_STATE (
                  OBJECT_ACTIVE_STATE ( NAME ( door ) STATE ( ACTIVE ) )
                )
                SEQUENCE_DEFINITION (
                  NAME ( swing )
                  OBJECT_MOTION_FROM_TO ( NAME ( door ) ROTATE_FROM ( 0.0 0.0 0.0 ) ROTATE_TO ( 0.0 90.0 0.0 ) RUN_TIME ( 2.0 ) )
                  SOUND ( NAME ( snd_gate ) START_TIME ( EVENT_OFFSET 0.5 ) AT_NODE ( door 0.0 1.0 0.0 ) )
                  CALL_ANIMATION ( NAME ( sparks ) AT_NODE ( door ) WAIT_FOR_COMPLETION () )
                  IF ( PLAYER_RANGE ( 15.0 ) )
                  CALLBACK ( VALUE ( 912 ) )
                  ENDIF ()
                )
                SEQUENCE_DEFINITION (
                  NAME ( fly )
                  ACTIVATION ( ON_CALL )
                  OBJECT_MOTION ( NAME ( door ) GRAVITY ( DEFAULT ) TRANSLATION ( 0 75 20 0 ) XYZ_ROTATION ( 90.0 0.0 0.0 ) RUN_TIME ( 1.0 ) )
                  OBJECT_MOTION_SI_SCRIPT ( NAME ( door ) SCRIPT_FRAME_RATE ( 10.0 ) SCRIPT_FILENAME ( gate.zan ) )
                  LOOP ( LOOP_COUNT ( -1 ) )
                )
              )
              ANIMATION_DEFINITION (
                NAME ( "lamp*" )
                ANIMATION_NAME ( "blink*" )
                ACTIVATION ( ON_STARTUP )
                SEQUENCE_DEFINITION ( NAME ( on ) OBJECT_ACTIVE_STATE ( NAME ( "lamp*" ) STATE ( INACTIVE ) ) )
              )
              ANIMATION_DEFINITION (
                NAME ( turret_a turret_b )
                ANIMATION_NAME ( destroy_turret )
                SEQUENCE_DEFINITION ( NAME ( boom ) OBJECT_ACTIVE_STATE ( NAME ( healthy ) STATE ( INACTIVE ) ) )
              )
              ANIMATION_DEFINITION (
                NAME ( absent )
                SEQUENCE_DEFINITION ( NAME ( never ) CALLBACK ( VALUE ( 1 ) ) )
              )
            )
          )
        )
        """;

    private const string Script = """
        OBJECT door
        FRAME 0 POSITION 0 0 0 ROTATION 1 0 0 0
        FRAME 5 POSITION 1 0 0
        FRAME 3 POSITION 2 0 0
        FRAME 10
        """;

    private static readonly string[] World = ["world1", "gate", "door", "lamp1", "lamp2", "turret_b", "healthy"];

    private static MemoryFiles Project() => new(new(StringComparer.Ordinal)
    {
        ["data/m1/zrdr/anim.zrd"] = Encoding.ASCII.GetBytes(Root),
        ["data/m1/zrdr/envmodels/gate.zrd"] = Encoding.ASCII.GetBytes(Gate),
        ["data/m1/zrdr/envmodels/gate.zan"] = Encoding.ASCII.GetBytes(Script),
    });

    [Fact]
    public void DefinitionsCompileAsTheOriginalToolDid()
    {
        var result = AnimationCompiler.Compile(Project(), "data/m1/zrdr/anim.zrd", World, Token);
        // A missing definition file is skipped with a warning; a definition whose root the world lacks has no entry.
        Assert.Contains(result.Warnings, w => w.Contains("missing.zrd"));
        var entries = result.Package.Entries;
        Assert.Equal(["", "open_gate", "blink1", "blink2", "destroy_turret"], entries.Select(e => e.Name));
        // Several roots bind to the first one the world has; a pattern expands to every match, digits carried over.
        Assert.Equal("turret_b", entries[4].RootName);
        Assert.Equal(["lamp1", "lamp2"], entries.Skip(2).Take(2).Select(e => e.RootName));
        Assert.Equal("lamp2", entries[3].References[1][1].Text(0, 36));

        var gate = entries[1];
        Assert.Equal("gate", gate.RootName); Assert.Equal("gate", gate.AttachName);
        Assert.Equal(0x20u | 0x02 | 0x1000 | 0x10, gate.U32(148)); // single reset time, range, no save log, callback
        Assert.Equal(3, gate.Bytes[153]); Assert.Equal(81f, gate.F32(160)); Assert.Equal(-1f, gate.F32(164)); Assert.Equal(6f, gate.F32(172));
        Assert.Equal("RESET_SEQUENCE", gate.Primary.Name);
        Assert.Equal(["swing", "fly"], gate.Sequences.Select(s => s.Name));
        Assert.Equal(3, gate.Sequences[1].Bytes[32]);
        // Tables: tracked nodes and node references in first-use order after a blank; samples; a waited-for child.
        Assert.Equal(["", "door"], gate.References[0].Select(r => r.Text(0, 36)));
        Assert.Equal(["", "door"], gate.References[1].Select(r => r.Text(0, 36)));
        Assert.Equal(["", "snd_gate"], gate.References[4].Select(r => r.Text(0, 32)));
        Assert.Equal("sparks", gate.References[7].Single().Text(0, 32));

        var swing = gate.Sequences[0].Events;
        Assert.Equal([11, 1, 24, 31, 35, 34], swing.Select(e => (int)e.Type));
        Assert.Equal(2u, swing[0].U32(12)); Assert.Equal((float)(Math.PI / 2), swing[0].F32(84)); Assert.Equal((float)(Math.PI / 2 / 2), swing[0].F32(96));
        Assert.Equal(3, swing[1].StartMode); Assert.Equal(0.5f, swing[1].Threshold); Assert.Equal(1, swing[1].I16(14)); Assert.Equal(new Vector3(0, 1, 0), swing[1].Vector(16));
        Assert.Equal(1 | 0x10, swing[2].I16(46)); Assert.Equal(0, swing[2].I16(50));
        Assert.Equal(2u, swing[3].U32(12)); Assert.Equal(225f, swing[3].F32(20));

        var fly = gate.Sequences[1].Events;
        // Launch: linear pitch (75° is 5/6 up), the rest along −Z; velocity = direction × speed in single precision.
        Assert.Equal(0x1u | 0x4 | 0x20 | 0x400, fly[0].U32(12)); Assert.Equal(-9.8f, fly[0].F32(24));
        Assert.Equal(AnimationCompiler.LaunchDirection(0, 75), fly[0].Vector(112));
        Assert.Equal(AnimationCompiler.LaunchDirection(0, 75) * 20, fly[0].Vector(64));
        Assert.Equal(0.8333333f, fly[0].F32(116), 6);
        // Keyframes from the script: frame × single-precision frame length, a reversed segment kept as authored.
        var frames = fly[1].Keyframes(Token);
        Assert.Equal([(0f, 0.5f), (0.5f, 0.3f), (0.3f, 1f)], frames.Select(f => (f.Start, f.End)));
        Assert.Equal(3, fly[1].I32(16));
        Assert.Equal(new Vector3(2, 0, 0), frames[0].Vector(frames[0].ChannelOffset(0) + 16)); // reaches frame 5's position in half a second
        Assert.Equal(65535, fly[2].I32(16));
    }

    [Fact]
    public void CompiledEntriesDecompileToDefinitionsThatCompileTheSame()
    {
        var files = Project();
        var package = AnimationCompiler.Compile(files, "data/m1/zrdr/anim.zrd", World, Token).Package;
        // Rewrite every definition from its compiled entry and compile again.
        var gate = package.Entries[1];
        var items = AnimationDecompiler.Definition(gate, _ => ("gate.zan", 10f));
        var tree = AnimationDefinitionSet.ReplaceDefinition(AnimationDefinitionSet.Read(files, "data/m1/zrdr/envmodels/gate.zrd", Token), 0, items);
        files.Files["data/m1/zrdr/envmodels/gate.zrd"] = Encoding.ASCII.GetBytes(ZrdText.Write(tree, Token));
        var again = AnimationCompiler.Compile(files, "data/m1/zrdr/anim.zrd", World, Token).Package;
        Assert.Equal(package.Entries.Count, again.Entries.Count);
        for (int i = 1; i < package.Entries.Count; i++) Assert.Null(AnimationComparer.Difference(package.Entries[i], again.Entries[i]));
    }

    [Fact]
    public void ScriptsRoundTripAndRefuseBadInput()
    {
        var tracks = AnimationScript.Parse(Encoding.ASCII.GetBytes(Script), "gate.zan");
        var frames = AnimationScript.Compile(AnimationScript.Track(tracks, "door")!, 10, "gate.zan");
        string text = AnimationScript.Decompile(frames, 10)!;
        var reparsed = AnimationScript.Parse(Encoding.ASCII.GetBytes(AnimationScript.Write([("door", text)])), "again.zan");
        var again = AnimationScript.Compile(AnimationScript.Track(reparsed, "door")!, 10, "again.zan");
        Assert.Equal(frames.Select(f => Convert.ToHexString(f.Bytes)), again.Select(f => Convert.ToHexString(f.Bytes)));
        // Times off the frame grid cannot be a script.
        Assert.Null(AnimationScript.Decompile(frames, 7));
        Assert.Null(AnimationScript.Track(tracks, "other"));
        Assert.Throws<InvalidDataException>(() => AnimationScript.Parse("FRAME 0 POSITION 1 2\nFRAME 1"u8, "bad.zan"));
        Assert.Throws<InvalidDataException>(() => AnimationScript.Parse("FRAME 0 VELOCITY 1 2 3\nFRAME 1"u8, "bad.zan"));
        Assert.Throws<InvalidDataException>(() => AnimationScript.Parse("OBJECT a\nFRAME 0 POSITION 0 0 0\nOBJECT a\nFRAME 0 POSITION 0 0 0\nFRAME 1"u8, "bad.zan"));
    }

    private sealed class CountingFiles(Dictionary<string, byte[]> files) : IProjectFiles
    {
        public Dictionary<string, int> Reads { get; } = new(StringComparer.Ordinal);
        public bool Exists(string relative) => files.ContainsKey(relative);
        public byte[] Read(string relative, CancellationToken token) { Reads[relative] = Reads.GetValueOrDefault(relative) + 1; return files[relative]; }
    }
    private static Dictionary<string, byte[]> Definitions(string definitions, params (string Path, string Text)[] more)
    {
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal)
        {
            ["data/m1/zrdr/anim.zrd"] = "( ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION_FILE ( defs.zrd ) ) ) )"u8.ToArray(),
            ["data/m1/zrdr/defs.zrd"] = Encoding.Latin1.GetBytes($"( ANIMATION_DEFINITIONS ( ANIMATION_LIST ( {definitions} ) ) )"),
        };
        foreach (var (path, text) in more) files[path] = Encoding.Latin1.GetBytes(text);
        return files;
    }

    [Fact]
    public void EffectsTheGameCannotFindAreReported()
    {
        // LoadZbd (retail 0x45F899) rejects the whole file when FindTemplateIndexByName, an exact comparison with the
        // NAME of each effects.zrd entry, finds no template.
        var files = new MemoryFiles(Definitions("ANIMATION_DEFINITION ( NAME ( gate ) SEQUENCE_DEFINITION ( NAME ( s ) EFFECT ( NAME ( smoke1 ) ) EFFECT ( NAME ( Smoke2 ) ) ) )"));
        var effects = AnimationCompiler.EffectNames(ZrdText.Parse("( EFFECTS ( ( smoke1.flt NAME ( smoke1 ) SPEED ( 3.0 ) MAPS ( a.tif ) ) ( smoke2.flt NAME ( smoke2 ) ) ) )"u8.ToArray(), Token)).ToArray();
        Assert.Equal(["smoke1", "smoke2"], effects);
        var result = AnimationCompiler.Compile(files, "data/m1/zrdr/anim.zrd", ["gate"], effects, Token);
        Assert.Single(result.Warnings, w => w.Contains("Smoke2") && w.Contains("rejects"));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("smoke1"));
        // Without effects.zrd nothing can be checked.
        Assert.DoesNotContain(AnimationCompiler.Compile(files, "data/m1/zrdr/anim.zrd", ["gate"], Token).Warnings, w => w.Contains("Smoke2"));
    }

    [Theory]
    [InlineData("EXECUTION_PRIORITY ( 300 )", "EXECUTION_PRIORITY")]
    [InlineData("EXECUTION_PRIORITY ( -1 )", "EXECUTION_PRIORITY")]
    [InlineData("ACTIVATION_PREREQUISITE ( MINIMUM_TO_SATISFY ( 256 ) ANIMATION_LIST ( a ) )", "MINIMUM_TO_SATISFY")]
    [InlineData("SEQUENCE_DEFINITION ( NAME ( s ) LOOP ( LOOP_COUNT ( 70000 ) ) )", "LOOP_COUNT")]
    public void ValuesOutsideTheirStoredRangeAreReportedWhereTheyAre(string setting, string keyword)
    {
        var files = new MemoryFiles(Definitions($"ANIMATION_DEFINITION ( NAME ( gate ) {setting} )"));
        var error = Assert.Throws<InvalidDataException>(() => AnimationCompiler.Compile(files, "data/m1/zrdr/anim.zrd", ["gate"], Token));
        Assert.Contains("defs.zrd", error.Message); Assert.Contains(keyword, error.Message);
    }

    [Fact]
    public void ReferenceTablesStopAtTheirCountByte()
    {
        string sounds = string.Concat(Enumerable.Range(0, 300).Select(i => $"SOUND ( NAME ( s{i} ) ) "));
        var files = new MemoryFiles(Definitions($"ANIMATION_DEFINITION ( NAME ( gate ) SEQUENCE_DEFINITION ( NAME ( s ) {sounds}) )"));
        var error = Assert.Throws<InvalidDataException>(() => AnimationCompiler.Compile(files, "data/m1/zrdr/anim.zrd", ["gate"], Token));
        Assert.Contains("254", error.Message);
    }

    [Fact]
    public void EntryCountStaysWithinTheEnginesSignedCount()
    {
        // LoadZbd sign-extends the 16-bit entry count (retail 0x45F18C movsx); 32,768 entries would allocate a negative size.
        Assert.Equal(short.MaxValue, AnimationCompiler.MaximumEntries);
        string[] world = Enumerable.Range(0, 32_768).Select(i => $"n{i:D5}").ToArray();
        var files = new MemoryFiles(Definitions("ANIMATION_DEFINITION ( NAME ( \"n*****\" ) )"));
        var error = Assert.Throws<InvalidDataException>(() => AnimationCompiler.Compile(files, "data/m1/zrdr/anim.zrd", world, Token));
        Assert.Contains("32767", error.Message.Replace(",", ""));
    }

    [Fact]
    public void KeysAtTheSameFrameJumpWithoutNonFiniteRates()
    {
        // A key repeated at one frame is a cut: its zero-length segment holds its value, and the engine samples it at time 0.
        var tracks = AnimationScript.Parse("FRAME 0 POSITION 0 0 0 ROTATION 1 0 0 0\nFRAME 0 POSITION 5 5 5 ROTATION 0 1 0 0\nFRAME 10 POSITION 5 5 5\nFRAME 20"u8, "cut.zan");
        var frames = AnimationScript.Compile(AnimationScript.Track(tracks, "any")!, 10, "cut.zan");
        Assert.Equal(Vector3.Zero, frames[0].Vector(frames[0].ChannelOffset(0) + 16));
        Assert.Equal(Vector3.Zero, frames[0].Vector(frames[0].ChannelOffset(1) + 16));
        Assert.All(frames, f => f.Validate());
        _ = new AnimationEvent(new byte[32] { 12, 1, 0, 0, 32, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }).WithKeyframes(frames);
    }

    [Fact]
    public void ScriptsRefuseRotationsThatAreNotQuaternions()
    {
        var error = Assert.Throws<InvalidDataException>(() => AnimationScript.Parse("FRAME 0 ROTATION 0 0 0 0\nFRAME 1"u8, "zero.zan"));
        Assert.Contains("line 1", error.Message); Assert.Contains("ROTATION", error.Message);
    }

    [Fact]
    public void RepeatedDefinitionFilesAreBounded()
    {
        // Each file lists the next twice: 2^14 reads of files that define nothing.
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        string List(string next) => $"( ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION_FILE ( {next} ) ANIMATION_DEFINITION_FILE ( {next} ) ) ) )";
        files["data/m1/zrdr/anim.zrd"] = Encoding.ASCII.GetBytes(List("f1.zrd"));
        for (int i = 1; i < 14; i++) files[$"data/m1/zrdr/f{i}.zrd"] = Encoding.ASCII.GetBytes(List($"f{i + 1}.zrd"));
        files["data/m1/zrdr/f14.zrd"] = "( ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ) ) )"u8.ToArray();
        CountingFiles counting = new(files);
        var error = Assert.Throws<InvalidDataException>(() => AnimationDefinitionSet.Load(counting, "data/m1/zrdr/anim.zrd", Token));
        Assert.Contains("definition files", error.Message);
        Assert.True(counting.Reads.Values.Sum() <= AnimationDefinitionSet.MaximumFileReads);
    }

    [Fact]
    public void ScriptsAreReadOnceAndOutputIsBounded()
    {
        string events = string.Concat(Enumerable.Range(0, 40).Select(_ => "OBJECT_MOTION_SI_SCRIPT ( NAME ( door ) SCRIPT_FRAME_RATE ( 10.0 ) SCRIPT_FILENAME ( gate.zan ) ) "));
        string keys = string.Concat(Enumerable.Range(0, 200).Select(i => $"FRAME {i} POSITION {i} 0 0\n")) + "FRAME 200\n";
        CountingFiles files = new(Definitions($"ANIMATION_DEFINITION ( NAME ( gate ) SEQUENCE_DEFINITION ( NAME ( s ) {events}) )", ("data/m1/zrdr/gate.zan", "OBJECT door\n" + keys)));
        var result = AnimationCompiler.Compile(files, "data/m1/zrdr/anim.zrd", ["gate", "door"], null, 1 << 20, Token);
        Assert.Equal(40, result.Package.Entries[1].Sequences[0].Events.Count);
        Assert.Equal(1, files.Reads["data/m1/zrdr/gate.zan"]);
        // Forty 200-key streams are about 320 KiB: a smaller budget refuses the output instead of growing without bound.
        var error = Assert.Throws<InvalidDataException>(() => AnimationCompiler.Compile(files, "data/m1/zrdr/anim.zrd", ["gate", "door"], null, 64 * 1024, Token));
        Assert.Contains("larger than", error.Message);
    }

    [Fact]
    public void ReconstructionSkipsTracksAScriptCannotNameAndKeepsLatin1Names()
    {
        const string Latin = "tür";
        var files = new MemoryFiles(Definitions(
            $"ANIMATION_DEFINITION ( NAME ( \"my door\" ) SEQUENCE_DEFINITION ( NAME ( s ) OBJECT_MOTION_SI_SCRIPT ( NAME ( \"my door\" ) SCRIPT_FILENAME ( loose.zan ) ) ) ) " +
            $"ANIMATION_DEFINITION ( NAME ( \"{Latin}\" ) SEQUENCE_DEFINITION ( NAME ( s ) OBJECT_MOTION_SI_SCRIPT ( NAME ( \"{Latin}\" ) SCRIPT_FILENAME ( named.zan ) ) ) )",
            ("data/m1/zrdr/loose.zan", "FRAME 0 POSITION 0 0 0\nFRAME 5\n"), ("data/m1/zrdr/named.zan", $"OBJECT {Latin}\nFRAME 0 POSITION 1 0 0\nFRAME 5\n")));
        string[] world = ["my door", Latin];
        var package = AnimationCompiler.Compile(files, "data/m1/zrdr/anim.zrd", world, Token).Package;
        List<string> notes = [];
        var outputs = AnimationSources.Reconstruct([new(1, package, [], world)], files, notes, Token);
        Assert.Contains(notes, n => n.Contains("my door"));
        var named = outputs.Single(o => o.Path == "data/m1/zrdr/named.zan");
        Assert.NotNull(AnimationScript.Track(AnimationScript.Parse(named.Bytes, named.Path), Latin));
    }
}
