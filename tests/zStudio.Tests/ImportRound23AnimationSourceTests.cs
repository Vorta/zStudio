using System.Text;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ImportRound23AnimationSourceTests
{
    private const string Root = "data/m1/zrdr/anim.zad", Definition = "data/m1/zrdr/defs.zad", Script = "data/m1/zrdr/shared.zan";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void SharedScriptKeepsFirstSeenOrdinalIdentitiesAndRepeatedTrackSemantics()
    {
        string[] objects = ["door", "Door", .. Enumerable.Range(0, 94).Select(i => $"object{i}")];
        string events = string.Concat(objects.Select(name => Event(name, 10))) + Event("door", 20);
        MemoryFiles files = Definitions($"ANIMATION_DEFINITION ( NAME ( gate ) SEQUENCE_DEFINITION ( NAME ( move ) {events} ) )");
        files.Files[Script] = Encoding.Latin1.GetBytes(string.Concat(objects.Select((name, i) => $"OBJECT {name}\nFRAME 0 POSITION {i} 0 0\nFRAME 1\n")));
        string[] world = ["gate", .. objects];
        var shipped = AnimationCompiler.Compile(files, Root, world, Token).Package;
        files.Files.Remove(Script);
        List<string> notes = [];
        var output = AnimationSources.Reconstruct([new(1, shipped, [], world)], files, notes, Token).Single(o => o.Path == Script);
        var parsed = AnimationScript.Parse(output.Bytes, Script, TestContext.Current.CancellationToken);
        Assert.Equal(objects, parsed.Select(t => t.Object));
        Assert.Contains(notes, n => n.Contains("earlier animation at 10"));
        files.Files[Script] = output.Bytes;
        var rebuilt = AnimationCompiler.Compile(files, Root, world, Token).Package;
        Assert.Null(AnimationComparer.Difference(shipped.Entries[1], rebuilt.Entries[1]));

        static string Event(string name, int rate) => $"OBJECT_MOTION_SI_SCRIPT ( NAME ( {name} ) SCRIPT_FRAME_RATE ( {rate} ) SCRIPT_FILENAME ( shared.zan ) ) ";
    }

    [Fact]
    public void DefinitionRepairSharesOneWorkAllowanceAndKeepsExactReplacementVerification()
    {
        string[] world = Enumerable.Range(0, 16).Select(i => $"root{i}").ToArray();
        string DefinitionsFor(int value) => string.Concat(world.Select(name =>
            $"ANIMATION_DEFINITION ( NAME ( {name} ) SEQUENCE_DEFINITION ( NAME ( run ) CALLBACK ( VALUE ( {value} ) ) ) ) "));
        MemoryFiles files = Definitions(DefinitionsFor(1));
        var shipped = AnimationCompiler.Compile(files, Root, world, Token).Package;
        files.Files[Definition] = Definitions(DefinitionsFor(2)).Files[Definition];
        byte[] original = files.Files[Definition].ToArray();
        var error = Assert.Throws<IOException>(() => AnimationSources.Reconstruct([new(1, shipped, [], world)], files, [], Token,
            maximumRepairWork: 25_000));
        Assert.Contains("definition-repair work limit", error.Message);
        Assert.Equal(original, files.Files[Definition]);
        List<string> notes = [];
        var output = AnimationSources.Reconstruct([new(1, shipped, [], world)], files, notes, Token, maximumRepairWork: 10_000_000);
        Assert.Equal(world.Length, notes.Count(n => n.Contains("it was rebuilt from anim.zbd")));
        foreach (var file in output) files.Files[file.Path] = file.Bytes;
        var rebuilt = AnimationCompiler.Compile(files, Root, world, Token).Package;
        Assert.Equal(shipped.Entries.Count, rebuilt.Entries.Count);
        for (int i = 1; i < shipped.Entries.Count; i++)
            Assert.Null(AnimationComparer.Difference(shipped.Entries[i], rebuilt.Entries[i]));
    }

    private static MemoryFiles Definitions(string definitions) => new(new(StringComparer.Ordinal)
    {
        [Root] = "( ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION_FILE ( defs.zad ) ) ) )"u8.ToArray(),
        [Definition] = Encoding.Latin1.GetBytes($"( ANIMATION_DEFINITIONS ( ANIMATION_LIST ( {definitions} ) ) )"),
    });
    private sealed class MemoryFiles(Dictionary<string, byte[]> files) : IProjectFiles
    {
        public Dictionary<string, byte[]> Files { get; } = files;
        public bool Exists(string relative) => Files.ContainsKey(relative);
        public byte[] Read(string relative, CancellationToken token) { token.ThrowIfCancellationRequested(); return Files[relative]; }
    }
}
