using System.Text;
using zStudio.Core;
using zStudio.Core.Animation;
using zStudio.Core.Formats;
using zStudio.Core.Sources;
using zStudio.Core.Worlds;
using Xunit;

namespace zStudio.Tests;

public sealed class Mw3AnimationRecoveryTests
{
    private const string Shared = "data/common/zrdr/soil/motion.zad";
    private const string Ancestor = "data/common/zrdr/commonAnim.zad";
    private const string Call = "CALL_ANIMATION ( NAME ( child ) )";
    private const string Motion = "OBJECT_MOTION ( NAME ( ground ) TRANSLATION ( 0 45 2 0 ) RUN_TIME ( 1 ) {0} )";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitializationPublishesVerifiedRepairsOrRollsBackTheWholeProject(bool unrelatedDifference)
    {
        string scratch = Path.Combine(Path.GetTempPath(), "zstudio-mw3-recovery-" + Guid.NewGuid().ToString("N"));
        string corpus = Path.Combine(scratch, "zbd"), project = Path.Combine(scratch, "project");
        try
        {
            Mw3TestCorpus.Create(corpus, staleGravity: true);
            if (unrelatedDifference)
            {
                string archive = Path.Combine(corpus, "c1/reader.zbd");
                var members = ArchiveSources.Read(File.ReadAllBytes(archive)).Select(m =>
                {
                    byte[] payload = m.Payload.ToArray();
                    if (m.Name == "ground.zrd") payload = ZrdWriter.Write(ZrdText.Parse(ZrdText.Write(ZrdDecoder.Read(payload))
                        .Replace("RUN_TIME ( 1 )", "RUN_TIME ( 2 )", StringComparison.Ordinal)));
                    return new ArchiveSources.Entry(m.Name, m.SourceField ?? "", payload);
                }).ToArray();
                File.WriteAllBytes(archive, ArchiveSources.Write(members));
            }
            var before = Directory.GetFiles(corpus, "*", SearchOption.AllDirectories).ToDictionary(p => p, p => File.ReadAllBytes(p));
            if (unrelatedDifference)
            {
                await Assert.ThrowsAsync<InvalidDataException>(() => SourceExtractor.ExtractAsync(corpus, project, token: Token));
                Assert.False(Directory.Exists(project));
            }
            else
            {
                await SourceExtractor.ExtractAsync(corpus, project, token: Token);
                Assert.Contains("GRAVITY ( LOCAL 0.0 )", File.ReadAllText(Path.Combine(project, Mw3TestCorpus.Definitions)));
            }
            foreach (var pair in before) Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key));
        }
        finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
    }

    [Theory]
    [InlineData("", "GRAVITY ( LOCAL 0.0 )")]
    [InlineData("GRAVITY ( LOCAL -9.8 )", "")]
    [InlineData("GRAVITY ( LOCAL 0.0 )", "GRAVITY ( COMPLEX -2.0 NO_ALTITUDE )")]
    public void SharedDefinitionsKeepEachCampaignsGravityAndIncludeResolution(string archived, string required)
    {
        var files = Files(Events(archived), includeScript: true);
        var before = files.Data.ToDictionary();
        var missions = new[] { Mission("c1", files, Events(required)), Mission("c3", files) };
        var recovered = Recover(files, missions);
        Assert.False(recovered.ContainsKey(Shared)); Assert.False(recovered.ContainsKey(Ancestor));
        Assert.Equal(before[Shared], files.Data[Shared]);
        Assert.Contains("data/c1/zrdr/soil/motion.zad", recovered.Keys);
        string copy = Encoding.Latin1.GetString(recovered["data/c1/zrdr/commonAnim.zad"]);
        Assert.Contains("../data/c1/zrdr/soil/motion.zad", copy);
        Assert.Contains("../data/common/zrdr/neighbor.zad", copy);
        Assert.Contains("../data/common/zrdr/soil/ground.zan", Encoding.Latin1.GetString(recovered["data/c1/zrdr/soil/motion.zad"]));
        Assert.False(recovered.ContainsKey("data/c3/zrdr/anim.zad"));
        Verify(files, recovered, missions);
    }

    [Fact]
    public void IdenticalConsumersRepairTheSharedFileOnce()
    {
        var files = Files(Events(""));
        var missions = new[] { Mission("c1", files, Events("GRAVITY ( LOCAL 0.0 )")), Mission("c3", files, Events("GRAVITY ( LOCAL 0.0 )")) };
        var recovered = Recover(files, missions);
        Assert.Equal([Shared], recovered.Keys);
        Verify(files, recovered, missions);
    }

    [Fact]
    public void VariantsAvoidExistingFilesAndRetainAllAncestors()
    {
        var files = Files(Events(""));
        const string occupied = "data/c1/zrdr/soil/motion.zad";
        files.Data.Add(occupied, Encoding.ASCII.GetBytes("sentinel"));
        var missions = new[] { Mission("c1", files, Events("GRAVITY ( LOCAL 0.0 )")), Mission("c3", files) };
        var recovered = Recover(files, missions);
        Assert.False(recovered.ContainsKey(occupied));
        Assert.Contains("data/c1/zrdr/soil/motion_recovered.zad", recovered.Keys);
        Assert.Equal("sentinel", Encoding.ASCII.GetString(files.Data[occupied]));
        Verify(files, recovered, missions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SurplusCallTailIsRemovedOnlyWhenTheWholeEntryMatches(bool gravity)
    {
        var files = Files(Events("") + Call + Call);
        var missions = new[] { Mission("c1", files, Events(gravity ? "GRAVITY ( LOCAL 0.0 )" : "") + Call), Mission("c3", files) };
        var recovered = Recover(files, missions);
        Verify(files, recovered, missions);
        Assert.False(recovered.ContainsKey(Shared));
    }

    [Theory]
    [InlineData("OBJECT_ACTIVE_STATE ( NAME ( ground ) STATE ( ACTIVE ) )", false)]
    [InlineData(Call, true)]
    public void UnsupportedTailOrChangedPrefixDoesNotPublish(string surplus, bool changePrefix)
    {
        var files = Files(Events("") + Call + surplus);
        var before = files.Data.ToDictionary();
        string desired = Events("") + Call;
        if (changePrefix) desired = desired.Replace("RUN_TIME ( 1 )", "RUN_TIME ( 2 )", StringComparison.Ordinal);
        var mission = Mission("c1", files, desired);
        Assert.Throws<InvalidDataException>(() => Recover(files, [mission]));
        Assert.Equal(before.OrderBy(p => p.Key), files.Data.OrderBy(p => p.Key));
    }

    private static string Events(string gravity) => Motion.Replace("{0}", gravity, StringComparison.Ordinal);
    private static string Definition(string events, bool script = false) => "ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION ( NAME ( ground ) ACTIVATION ( ON_CALL ) SEQUENCE_DEFINITION ( NAME ( move ) " + events +
        (script ? " OBJECT_MOTION_SI_SCRIPT ( NAME ( ground ) SCRIPT_FILENAME ( ground.zan ) SCRIPT_FRAME_RATE ( 30 ) ) " : "") + ") ) ) )";

    private static MemoryFiles Files(string events, bool includeScript = false)
    {
        MemoryFiles files = new(new(StringComparer.OrdinalIgnoreCase));
        files.Set(Shared, Definition(events, includeScript));
        files.Set(Ancestor, "ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION_FILE ( \"../data/common/zrdr/soil/motion.zad\" ) ANIMATION_DEFINITION_FILE ( neighbor.zad ) ANIMATION_DEFINITION_FILE ( missing.zad ) ) )");
        files.Set("data/common/zrdr/neighbor.zad", "ANIMATION_DEFINITIONS ( ANIMATION_LIST () )");
        foreach (string campaign in new[] { "c1", "c3" }) files.Set($"data/{campaign}/zrdr/anim.zad", "ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION_FILE ( \"../data/common/zrdr/commonAnim.zad\" ) ) )");
        if (includeScript) files.Set("data/common/zrdr/soil/ground.zan", "SI Animation Script\nFRAMES: 2\nOBJECTS: 1\nFrame: 1\nObject: ground\nScaling: 1 1 1\nRotation: 0 0 0\nTranslation: 0 0 0\nFrame: 3\nObject: ground\nScaling: 1 1 1\nRotation: 0 0 0\nTranslation: 0 0.06666667 0\n");
        return files;
    }
    private static Mw3AnimationSources.Mission Mission(string name, MemoryFiles files, string? desired = null)
    {
        var original = files.Data[Shared];
        if (desired != null) files.Set(Shared, Definition(desired, files.Data.ContainsKey("data/common/zrdr/soil/ground.zan")));
        try
        {
            var result = AnimationCompiler.Compile(files, $"data/{name}/zrdr/anim.zad", ["ground"], GameVariant.MechWarrior3, Token);
            Assert.Null(result.EngineRejection);
            Assert.Equal(2, result.Package.Entries.Count);
            return new(name, result.Package, ["ground"]);
        }
        finally { files.Data[Shared] = original; }
    }
    private static Dictionary<string, byte[]> Recover(MemoryFiles files, IReadOnlyList<Mw3AnimationSources.Mission> missions)
        => Mw3AnimationSources.Reconstruct(missions, files, [], Token).ToDictionary(f => f.Path, f => f.Bytes, StringComparer.OrdinalIgnoreCase);
    private static void Verify(MemoryFiles original, Dictionary<string, byte[]> recovered, IEnumerable<Mw3AnimationSources.Mission> missions)
    {
        MemoryFiles files = new(new(original.Data, StringComparer.OrdinalIgnoreCase));
        foreach (var pair in recovered) files.Data[pair.Key] = pair.Value;
        foreach (var mission in missions)
        {
            var built = AnimationCompiler.Compile(files, $"data/{mission.Name}/zrdr/anim.zad", ["ground"], GameVariant.MechWarrior3, Token);
            Assert.Null(built.EngineRejection); Assert.Equal(mission.Package.Entries.Count, built.Package.Entries.Count);
            foreach (var pair in mission.Package.Entries.Zip(built.Package.Entries)) Assert.Null(Mw3AnimationSources.Difference(pair.First, pair.Second, Token));
        }
    }
    private sealed class MemoryFiles(Dictionary<string, byte[]> data) : IProjectFiles
    {
        internal Dictionary<string, byte[]> Data => data;
        internal void Set(string path, string text) => data[path] = Encoding.Latin1.GetBytes(text);
        public bool Exists(string path) => data.ContainsKey(path);
        public byte[] Read(string path, CancellationToken token, ProjectReadLimits limits) { token.ThrowIfCancellationRequested(); var bytes = data[path]; limits.Validate(bytes); return bytes; }
    }
}
