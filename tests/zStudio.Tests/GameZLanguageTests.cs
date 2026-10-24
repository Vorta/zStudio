using zStudio.Core.Language;
using zStudio.Core.Sources;
using Xunit;

namespace zStudio.Tests;

public sealed class GameZLanguageTests
{
    private static LanguageDocument Analyze(string path, string text) => RecoilLanguage.Analyze(path, text, TestContext.Current.CancellationToken);
    private static string Animation(string body) => "(ANIMATION_DEFINITIONS (ANIMATION_LIST (ANIMATION_DEFINITION (NAME (root) SEQUENCE_DEFINITION (NAME (main) " + body + ")))))";
    [Fact]
    public void ExistingAnimationFormsRemainValidAndMissingOperandsAreLocated()
    {
        string text = Animation("OBJECT_MOTION (NAME (root) TRANSLATION (0 0 10 1) TRANSLATION_RANGE_MIN (0 0 1 2) TRANSLATION_RANGE_MAX (1 2 3 4) XYZ_ROTATION (0 0 0 1 1 1)) OBJECT_OPACITY_FROM_TO (NAME (root) OPACITY_FROM (1 ON) OPACITY_TO (0 OFF))");
        Assert.DoesNotContain(Analyze("test.zad", text).Diagnostics, d => d.Severity == "error");
        var missing = Analyze("test.zad", Animation("CALL_ANIMATION ()"));
        Assert.Contains(missing.Diagnostics, d => d.Code == "RECOIL_REQUIRED" && d.Message.Contains("NAME"));
        Assert.DoesNotContain(Analyze("test.zad", Animation("")).Diagnostics, d => d.Message.Contains("ANIMATION_NAME"));
    }
    [Fact]
    public void NumericKindsFollowTheResourceConsumerAndSyntaxSpansPointAtTheToken()
    {
        var weapon = Analyze("weapons.zrd", "(BALLISTICS (wep_1 (DAMAGE (10) AMMO_LIMIT (10.0))))");
        Assert.Equal(2, weapon.Diagnostics.Count(d => d.Code == "RECOIL_TYPE"));
        var good = Analyze("weapons.zrd", "(BALLISTICS (wep_1 (DAMAGE (10.0) AMMO_LIMIT (10))))");
        Assert.Empty(good.Diagnostics);
        string invalid = "(VALUE (12x))";
        Assert.Equal(invalid.IndexOf("12x", StringComparison.Ordinal), Analyze("a.zrd", invalid).Diagnostics.Single().Span.Start);
    }
    [Fact]
    public void IncompleteAnimationStillOffersContextualCompletions()
    {
        string text = "( ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION ( ACTIVATION ( ";
        var doc = Analyze("a.zad", text);
        Assert.False(doc.Complete);
        Assert.Contains(RecoilLanguage.Complete(doc, text.Length), c => c.Label == "ON_CALL");
        Assert.DoesNotContain(RecoilLanguage.Complete(doc, text.Length), c => c.Label == "DAMAGE");
    }
    [Fact]
    public void NestedUiVocabularyDoesNotSelectAnotherResourceFamily()
    {
        var doc = Analyze("dialog.zrd", "(MAINMENU (FONTS ((0 Arial 20 (255 255 255) 1 500)) EFFECTS (NAME (image 0 0))))");
        Assert.Equal("ui", doc.Family); Assert.Empty(doc.Diagnostics);
        var hud = Analyze("hud.zrd", "(FONTS (OBJ_SUMMARY (font1)) TYPEI () RETICULE (one two))");
        Assert.Equal("hud", hud.Family); Assert.Contains(hud.Diagnostics, d => d.Code == "RECOIL_ARITY");
    }
    [Fact]
    public void NetworkSpatialLinksUseTheNestedRecordShape()
    {
        var doc = Analyze("net_01.zrd", "(type (standard) node_00 (1 (0.0 0.0 0.0) (1 -1 -1)))");
        Assert.Contains(doc.Diagnostics, d => d.Code == "RECOIL_NODE_LINK");
        var invalid = Analyze("net_01.zrd", "(type (standard) node_00 (1 (0.0 0.0 0.0) (99 -1 -1)) node_99 (1 (0.0 0.0 0.0) (-1 -1 -1)))");
        Assert.Contains(invalid.Diagnostics, d => d.Code == "RECOIL_NODE_ID");
        Assert.Contains(invalid.Diagnostics, d => d.Code == "RECOIL_NODE_LINK");
    }
    [Fact]
    public void SinglePickupAndEmptyTurretPartsKeepTheirResourceSemantics()
    {
        var pickup = Analyze("puppies.zrd", "((TYPO 1 (0.0 0.0 0.0) (0.0 0.0 0.0) 0.0))");
        Assert.Equal("pickup-placements", pickup.Family);
        Assert.Contains(pickup.Diagnostics, d => d.Code == "RECOIL_PICKUP_TYPE");
        Assert.Empty(Analyze("ai.zrd", "(TURRET (tur_01 (PARTS ())))").Diagnostics);
    }
    [Fact]
    public void MultiRootNamesAndUnfinishedReferenceValuesAreAvailable()
    {
        using var fixture = new SourceWorldFixture();
        const string path = "data/m1/zrdr/anim.zad";
        string text = "(ANIMATION_DEFINITIONS (ANIMATION_LIST (ANIMATION_DEFINITION (NAME (root_a root_b) SEQUENCE_DEFINITION (NAME (main) CALL_ANIMATION (NAME (root_b)))))))";
        fixture.Write(path, text);
        var project = LanguageProject.Analyze(fixture.Project, token: TestContext.Current.CancellationToken);
        Assert.Single(project.Definition(path, text.LastIndexOf("root_b", StringComparison.Ordinal)));
        string partial = text[..text.LastIndexOf("root_b", StringComparison.Ordinal)];
        var overlay = LanguageProject.Analyze(fixture.Project, new Dictionary<string, string> { [path] = partial }, token: TestContext.Current.CancellationToken);
        Assert.Contains(overlay.Complete(path, partial.Length), c => c.Label == "root_b");
        Assert.Contains(overlay.Documents[path].Diagnostics, d => d.Code == "RECOIL_SYNTAX");
        partial = partial.Replace("CALL_ANIMATION", "CALL_SEQUENCE", StringComparison.Ordinal);
        overlay = LanguageProject.Analyze(fixture.Project, new Dictionary<string, string> { [path] = partial }, token: TestContext.Current.CancellationToken);
        Assert.Contains(overlay.Complete(path, partial.Length), c => c.Label == "main");
    }
    [Fact]
    public void ProjectUsesUnsavedDependenciesAndNestedStartupReferencesWithoutWriting()
    {
        using var fixture = new SourceWorldFixture();
        const string root = "data/m1/zrdr/anim.zad", child = "data/m1/zrdr/child.zad", start = "data/m1/zrdr/startanims.zrd";
        fixture.Write(root, "(ANIMATION_DEFINITIONS (ANIMATION_LIST (ANIMATION_DEFINITION_FILE (child.zad))))");
        fixture.Write(child, "(ANIMATION_DEFINITIONS (ANIMATION_LIST (ANIMATION_DEFINITION (NAME (root) ANIMATION_NAME (child)))))");
        fixture.Write(start, "(NEW_GAME_START ((child)))");
        var project = LanguageProject.Analyze(fixture.Project, token: TestContext.Current.CancellationToken);
        var startup = project.Documents[start];
        Assert.DoesNotContain(startup.Diagnostics, d => d.Severity == "error");
        Assert.Single(project.Definition(start, startup.Text.IndexOf("child", StringComparison.Ordinal)));
        string changed = project.Documents[child].Text.Replace("ANIMATION_NAME (child)", "ANIMATION_NAME (renamed)", StringComparison.Ordinal);
        var overlay = LanguageProject.Analyze(fixture.Project, new Dictionary<string, string> { [child] = changed }, token: TestContext.Current.CancellationToken);
        Assert.Empty(overlay.Definition(start, startup.Text.IndexOf("child", StringComparison.Ordinal)));
        Assert.Contains(overlay.Documents[start].Diagnostics, d => d.Code == "RECOIL_REFERENCE");
        Assert.Contains("ANIMATION_NAME (child)", File.ReadAllText(Path.Combine(fixture.Project, child)));
    }
}
