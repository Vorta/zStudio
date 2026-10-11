using System.Text;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ImportRound24DefinitionsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private sealed class Files(Dictionary<string, byte[]> files) : IProjectFiles
    {
        public bool Exists(string relative) => files.ContainsKey(relative);
        public byte[] Read(string relative, CancellationToken token) => Read(relative, token, ProjectReadLimits.Document);
        public byte[] Read(string relative, CancellationToken token, ProjectReadLimits limits) { token.ThrowIfCancellationRequested(); byte[] result = files[relative]; limits.Validate(result); return result; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnusedReferenceKeywordsDoNotSuppressTheCompilerVisibleAddition(bool compiled)
    {
        const string path = "data/common/new.zad";
        const string original = """
            ( ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ) )
              UNUSED ( ANIMATION_DEFINITION_FILE ( "..\\data\\common\\new.zad" ) ) )
            """;
        byte[] before = Encoding.Latin1.GetBytes(original);
        if (compiled) before = ZrdWriter.Write(ZrdText.Parse(before, Token), Token);
        byte[] after = SourceWorlds.AddDefinitionFiles(before, [path], Token);
        Files files = new(new(StringComparer.OrdinalIgnoreCase)
        {
            ["data/m1/zrdr/anim.zad"] = after,
            [path] = Encoding.Latin1.GetBytes("( ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION ( NAME ( model ) ANIMATION_NAME ( animation ) ) ) ) )"),
        });
        var set = AnimationDefinitionSet.Load(files, "data/m1/zrdr/anim.zad", Token);
        Assert.Contains(path, set.Files);
        Assert.Single(set.Definitions);
        Assert.Equal(after, SourceWorlds.AddDefinitionFiles(after, [path], Token));
        if (!compiled) Assert.Contains("UNUSED ( ANIMATION_DEFINITION_FILE", Encoding.Latin1.GetString(after));
    }
}
