using System.Text;
using zStudio.Core;
using zStudio.Core.Animation;
using zStudio.Core.Formats;
using zStudio.Core.Sources;
using zStudio.Core.Worlds;
using Xunit;

namespace zStudio.Tests;

/// <summary>The explicit splash activation must survive authoring and both RECOIL export formats.</summary>
public sealed class WeaponSplashTests
{
    private const string Root = "data/m1/zrdr/anim.zad";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private sealed class Files(GameVariant game) : IProjectFiles
    {
        public GameVariant Game => game;
        public byte[] Bytes { get; set; } = Encoding.ASCII.GetBytes("ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION ( NAME ( \"wall*\" ) HEALTH ( 7 ) ) ) )");
        public bool Exists(string path) => path == Root;
        public byte[] Read(string path, CancellationToken token) => Read(path, token, ProjectReadLimits.Document);
        public byte[] Read(string path, CancellationToken token, ProjectReadLimits limits) { token.ThrowIfCancellationRequested(); limits.Validate(Bytes); return path == Root ? Bytes : throw new FileNotFoundException(path); }
    }

    [Theory]
    [InlineData(28)]
    [InlineData(200)]
    public void SourceEditRoundTripsExplicitModeWithoutChangingOtherFields(int format)
    {
        Files files = new(GameVariant.Recoil);
        string[] nodes = ["wall1", "wall2"];
        var baseline = AnimationCompiler.Compile(files, Root, nodes, null, FormatRegistry.MaximumDocumentBytes, Token, format: (uint)format);
        var definition = SourceAnimationEdits.Read(files, Root, 0, Token);
        var activation = definition.Records.Single(r => r.Kind == "settings").Fields.Single(f => f.Key == "ACTIVATION");
        Assert.Contains("WEAPON_SPLASH", activation.Choices);
        files.Bytes = SourceAnimationEdits.Plan(files, Root, 0, [new(activation.Id, ["WEAPON_SPLASH"])], Token).Content;
        var compiled = AnimationCompiler.Compile(files, Root, nodes, null, FormatRegistry.MaximumDocumentBytes, Token, format: (uint)format);
        Assert.Null(compiled.EngineRejection);
        Assert.Empty(compiled.Warnings);
        Assert.Equal(3, compiled.Package.Entries.Count);
        foreach (var entry in compiled.Package.Entries.Skip(1))
        {
            Assert.Equal(5, entry.Bytes[153]);
            var old = baseline.Package.Entries[entry.Index].Clone(Token);
            old.Bytes[153] = 5;
            Assert.Null(AnimationComparer.Difference(old, entry, Token));
        }
        var graph = AnimationGraph.Create(AnimationGraphIndex.Create(compiled.Package, token: Token), 1, token: Token);
        Assert.Contains(graph.Nodes().Rows[0].Activation!.Triggers, t => t.Kind == "weaponSplashHit" && t.Phrase.Contains("no collision"));
        var tree = AnimationDefinitionSet.ReplaceDefinition(AnimationDefinitionSet.Read(files, Root, Token), 0,
            AnimationDecompiler.Definition(compiled.Package.Entries[1], _ => throw new InvalidOperationException(), Token), Token);
        files.Bytes = Encoding.ASCII.GetBytes(ZrdText.Write(tree, Token));
        var again = AnimationCompiler.Compile(files, Root, nodes, null, FormatRegistry.MaximumDocumentBytes, Token, format: (uint)format);
        Assert.Empty(again.Warnings);
        Assert.Null(AnimationComparer.Difference(compiled.Package.Entries[1], again.Package.Entries[1], Token));
    }

    [Fact]
    public void Mw3DoesNotOfferOrCompileTheRecoilExtension()
    {
        Files files = new(GameVariant.MechWarrior3);
        var activation = SourceAnimationEdits.Read(files, Root, 0, Token).Records.Single(r => r.Kind == "settings").Fields.Single(f => f.Key == "ACTIVATION");
        Assert.DoesNotContain("WEAPON_SPLASH", activation.Choices);
        Assert.Throws<InvalidDataException>(() => SourceAnimationEdits.Plan(files, Root, 0, [new(activation.Id, ["WEAPON_SPLASH"])], Token));
        files.Bytes = Encoding.ASCII.GetBytes("ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION ( NAME ( wall ) ACTIVATION ( WEAPON_SPLASH ) ) ) )");
        Assert.Contains("not supported by MechWarrior 3", Assert.Throws<InvalidDataException>(() => AnimationCompiler.Compile(files, Root, ["wall"], GameVariant.MechWarrior3, Token)).Message);
    }
}
