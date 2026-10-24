using zStudio.Core.Language;
using zStudio.Core.Sources;
using Xunit;

namespace zStudio.Tests;

public sealed class GameZLanguageTests
{
    private static LanguageDocument Analyze(string path, string text) => RecoilLanguage.Analyze(path, text, TestContext.Current.CancellationToken);
    private static string Animation(string body) => "(ANIMATION_DEFINITIONS (ANIMATION_LIST (ANIMATION_DEFINITION (NAME (root) SEQUENCE_DEFINITION (NAME (main) " + body + ")))))";
    [Theory]
    [InlineData("vehicle.zrd", "(stealth (common_mode (camera_ud_swing (5.5 2.5 1.2 1.2))))", "5.5")]
    [InlineData("vehicle.zrd", "(stealth (common_mode (camback ((0.0 4.5 9.0) (0.0 3.0 2.0) (0.0 3.0 2.0)))) )", "4.5")]
    [InlineData("vehicle.zrd", "(stealth (common_mode (weapons ((gun 1 0 3 1.2 5.0 325.0 1 1)))))", "1.2")]
    [InlineData("vehicle.zrd", "(stealth (common_mode () basic_mode (platform ((0.0 4.5 9.0)))) )", "4.5")]
    [InlineData("vehicle.zrd", "(stealth (common_mode () basic_mode (sounds (pitch_scale (1.5)))))", "1.5")]
    [InlineData("player.zrd", "(camera_zone (0.9) nom_gravity (28.5))", "28.5")]
    [InlineData("net_01.zrd", "(node_00 (0 (0.0 0.0 0.0) (-1 -1 -1)) pursuit_range (20.5 30.0))", "20.5")]
    [InlineData("weapons.zrd", "(BALLISTICS (gun (TURN_RATE (1.5))))", "1.5")]
    [InlineData("weapons.zrd", "(BALLISTICS (gun (IMPACT (FREEZE (1.5 2.0 3.0 4.0 5.0 6.0)))))", "1.5")]
    [InlineData("ai.zrd", "(TURRET (tur_01 (EFFECT (flash 1.5))))", "1.5")]
    [InlineData("sounds.zrd", "(SYNTAX (2) SETS (group ((sound file RANGE (1.5 20.0)))) )", "1.5")]
    [InlineData("sounds.zrd", "(SETS (group ((sound file 1.5 TRUE FALSE TRUE))))", "1.5")]
    [InlineData("weather.zrd", "(MISSION1 (TYPE RAIN WIND_VEL 1.5))", "1.5")]
    [InlineData("objective.zrd", "(OBJECTIVE1 (READ_SOUND (sample 1.5)))", "1.5")]
    [InlineData("hud.zrd", "(FONTS () TYPEI ((0 0 640 480)) SENSOR ((image 0 0 FALSE FALSE) 1.5 (0 0) (10 10) 400))", "1.5")]
    [InlineData("dialog.zrd", "(SHARED_IMAGE_PATH (images) MENU (BACKGROUND_SOUNDS ((sound 1.5))))", "1.5")]
    public void DirectFloatReadsRejectIntegersAtTheEditedToken(string path, string text, string number)
    {
        Assert.DoesNotContain(Analyze(path, text).Diagnostics, d => d.Severity == "error");
        int start = text.IndexOf(number, StringComparison.Ordinal);
        string changed = text[..start] + "7" + text[(start + number.Length)..];
        var diagnostic = Assert.Single(Analyze(path, changed).Diagnostics, d => d.Code == "RECOIL_TYPE");
        Assert.Equal("error", diagnostic.Severity);
        Assert.Equal(new TextSpan(start, 1), diagnostic.Span);
        Assert.Contains("7 is an integer. Use 7.0", diagnostic.Message);
    }
    [Fact]
    public void UnusedFieldsGiveUsageWarningsWithoutInventingNumericRequirements()
    {
        const string text = "(stealth (common_mode (position (1 2 3) rotation (0 0 0) not_pursuit_dwell (2))))";
        var doc = Analyze("vehicle.zrd", text);
        var warnings = doc.Diagnostics.Where(d => d.Severity == "warning").ToArray();
        Assert.Equal(2, warnings.Length);
        Assert.Equal("position is not used in common_mode. Set the vehicle's position in aiv.zrd.", warnings[0].Message);
        Assert.Equal("rotation is not used in common_mode. Set the vehicle's heading in aiv.zrd.", warnings[1].Message);
        Assert.Single(doc.Diagnostics, d => d.Code == "RECOIL_TYPE"); // Read, even though not applied later.
        foreach (var warning in warnings)
            Assert.Equal(warning.Message.Split(' ')[0], text.Substring(warning.Span.Start, warning.Span.Length));
        Assert.DoesNotContain("Value types", RecoilLanguage.Hover(doc, text.IndexOf("position", StringComparison.Ordinal))!.Markdown);
    }
    [Fact]
    public void ConvertingConsumersAcceptIntegersAndNestedIntegerConsumersRejectFloats()
    {
        Assert.Empty(Analyze("effects.zrd", "(EFFECTS ((model NAME (effect) SPEED (5) MAPS (map))))").Diagnostics);
        Assert.Empty(Analyze("effects.zrd", "(EFFECTS ((model NAME (effect) SPEED 5 MAPS (map))))").Diagnostics);
        Assert.Empty(Analyze("sounds.zrd", "(SYNTAX (2) SPEED_OF_SOUND 343 SETS (group ((sample file VOLUME 1 A3DDIST (2)))))").Diagnostics);
        Assert.Empty(Analyze("a.zad", Animation("CALLBACK (VALUE (25) START_TIME (ANIMATION_OFFSET 1))")).Diagnostics);
        const string font = "(SHARED_IMAGE_PATH (images) MENU (FONTS ((0 Arial 12 ((255 255 255) (0 0 0)) 1 400 LEFT))))";
        Assert.Empty(Analyze("dialog.zrd", font).Diagnostics);
        var badFont = Analyze("dialog.zrd", font.Replace("255 255", "255.0 255", StringComparison.Ordinal));
        Assert.Single(badFont.Diagnostics, d => d.Code == "RECOIL_TYPE" && d.Severity == "error");
        Assert.Empty(Analyze("sounds.zrd", "(SYNTAX (2) SETS (group ((sample file HIGH variant.wav))))").Diagnostics);
    }
    [Fact]
    public void WidgetScalarStorageAndCaptionContextsStayDistinct()
    {
        const string text = "(SHARED_IMAGE_PATH (images) MENU (CURSOR (CAPTURE 1) BUTTONS (button (FONT 1 LABEL ((COLOR 1 2 0) (RATE 1 2 0) (FONT 1 2 0) (POSITION 1 2 0)) SOUND (COLOR 1.0)))))";
        var doc = Analyze("ui.zrd", text);
        Assert.Empty(doc.Diagnostics);
        Assert.Empty(Analyze("ui.zrd", text.Replace("CAPTURE 1", "CAPTURE (1)", StringComparison.Ordinal)).Diagnostics);
        var bad = Analyze("ui.zrd", text.Replace("FONT 1 LABEL", "FONT (1) LABEL", StringComparison.Ordinal));
        Assert.Single(bad.Diagnostics, d => d.Code == "RECOIL_TYPE" && d.Message.Contains("bare integer"));
        Assert.DoesNotContain(RecoilLanguage.Complete(doc, text.IndexOf("(RATE", StringComparison.Ordinal) + 1), c => c.Label == "RATE");
        Assert.Empty(Analyze("sounds.zrd", "(SYNTAX (2) SETS () SOUND_GROUPS ((group DELAY_REPEAT 1)))").Diagnostics);
        Assert.Single(Analyze("sounds.zrd", "(SYNTAX (2) SETS () SOUND_GROUPS ((group DELAY_REPEAT (1.0))))").Diagnostics, d => d.Code == "RECOIL_TYPE");
        Assert.Single(Analyze("hud.zrd", "(FONTS () TYPEI ((0 0 640 480)) SENSOR ((image 0 0 FALSE FALSE) (4.0) (0 0) (10 10) 400))").Diagnostics, d => d.Code == "RECOIL_TYPE");
    }
    [Fact]
    public void LiveLintChecksCompilerRequiredConditionsAndEndpointSwitches()
    {
        var invalid = Analyze("a.zad", Animation("OBJECT_OPACITY_FROM_TO (NAME (root) OPACITY_FROM (1 ENABLED)) FBFX_COLOR_FROM_TO (FROM (0 0 0 0)) IF () ELSEIF () OBJECT_MOTION (NAME (root) GRAVITY (LOCAL)) IF (PLAYER_RANGE) ELSEIF (ANIMATION_LOD) OBJECT_MOTION (NAME (root) GRAVITY)"));
        Assert.Single(invalid.Diagnostics, d => d.Code == "RECOIL_ENUM");
        Assert.Equal(7, invalid.Diagnostics.Count(d => d.Code == "RECOIL_REQUIRED"));
        var valid = Analyze("a.zad", Animation("OBJECT_OPACITY_FROM_TO (NAME (root) OPACITY_FROM (1 ON) OPACITY_TO (0 OFF)) OBJECT_OPACITY_STATE (NAME (root) STATE (ANYTHING)) FBFX_COLOR_FROM_TO (FROM (0 0 0 0) TO (1 1 1 1)) IF (PLAYER_RANGE (10)) ELSEIF (ANIMATION_LOD (HIGH)) ENDIF () OBJECT_MOTION (NAME (root) GRAVITY (LOCAL 9.8))"));
        Assert.Empty(valid.Diagnostics);
    }
    [Fact]
    public void ProjectCompletionFiltersBeforeCappingAndReplacesTheWholeSourceToken()
    {
        using var fixture = new SourceWorldFixture();
        const string path = "data/m1/zrdr/anim.zad";
        var definitions = string.Concat(Enumerable.Range(0, 205).Select(i => $"ANIMATION_DEFINITION (NAME (early_{i})) "));
        string text = "(ANIMATION_DEFINITIONS (ANIMATION_LIST (" + definitions + "ANIMATION_DEFINITION (NAME (\"rocket_port1*\")) ANIMATION_DEFINITION (NAME (caller) SEQUENCE_DEFINITION (CALL_ANIMATION (NAME (\"rocket_p\")))))))";
        fixture.Write(path, text);
        var project = LanguageProject.Analyze(fixture.Project, token: TestContext.Current.CancellationToken);
        int at = text.LastIndexOf("rocket_p\"", StringComparison.Ordinal) + "rocket_p".Length;
        var completion = Assert.Single(project.Complete(path, at));
        Assert.Equal("rocket_port1*", completion.Label);
        var span = completion.ReplacementSpan!.Value;
        string changed = text[..span.Start] + completion.InsertText + text[span.End..];
        Assert.Contains("CALL_ANIMATION (NAME (\"rocket_port1*\"))", changed);
        Assert.Empty(Analyze(path, changed).Diagnostics);
        Assert.Contains(project.Complete(path, at + 2), c => c.Label == "WITH_NODE"); // After the closed NAME array.
        string partial = text[..(at + 1)]; // A closed string in an unfinished NAME array at EOF.
        var unfinished = LanguageProject.Analyze(fixture.Project, new Dictionary<string, string> { [path] = partial }, token: TestContext.Current.CancellationToken);
        Assert.Contains(unfinished.Complete(path, partial.Length), c => c.Label == "rocket_port1*");
        var callback = Analyze("a.zad", Animation("CALLBACK (VALUE (9))"));
        at = callback.Text.IndexOf("(9)", StringComparison.Ordinal) + 2;
        var numeric = RecoilLanguage.Complete(callback, at).Single(c => c.Label == "912");
        Assert.DoesNotContain(RecoilLanguage.Complete(callback, at), c => c.Label == "0");
        span = numeric.ReplacementSpan!.Value;
        Assert.Equal("9", callback.Text.Substring(span.Start, span.Length));
    }
    [Fact]
    public void CommonResourceMembersHideMissionOverridesForReferenceNavigation()
    {
        using var fixture = new SourceWorldFixture();
        fixture.Write("data/common/zrdr/weapons.zrd", "(BALLISTICS (common_gun (DAMAGE (1.0))))");
        fixture.Write("data/m1/zrdr/weapons.zrd", "(BALLISTICS (mission_gun (DAMAGE (2.0))))");
        const string path = "data/m1/zrdr/ai.zrd";
        const string text = "(TURRET (tur_01 (WEAPON (NAME (mission_gun)))))";
        fixture.Write(path, text);
        var project = LanguageProject.Analyze(fixture.Project, token: TestContext.Current.CancellationToken);
        int at = text.IndexOf("mission_gun", StringComparison.Ordinal);
        Assert.Empty(project.Definition(path, at));
        Assert.Equal(["common_gun"], project.Complete(path, at).Select(c => c.Label));
    }
    [Fact]
    public void CallbackHelpExplainsEngineDispatchAndOffersAdvisoryIds()
    {
        string text = Animation("CALLBACK (START_TIME (EVENT_OFFSET 0.5) VALUE (912)) CALLBACK (VALUE (12345))");
        var doc = Analyze("a.zad", text);
        Assert.Empty(doc.Diagnostics);
        Assert.Contains("registered callback", RecoilLanguage.Hover(doc, text.IndexOf("CALLBACK", StringComparison.Ordinal))!.Markdown);
        Assert.Contains("vwbus", RecoilLanguage.Hover(doc, text.IndexOf("912", StringComparison.Ordinal))!.Markdown);
        Assert.Contains("unknown IDs are not rejected", RecoilLanguage.Hover(doc, text.IndexOf("12345", StringComparison.Ordinal))!.Markdown);
        string partial = text[..text.IndexOf("912", StringComparison.Ordinal)];
        var choices = RecoilLanguage.Complete(Analyze("a.zad", partial), partial.Length);
        Assert.Contains(choices, c => c.Label == "912" && c.Documentation!.Contains("Player::AsyncCommandCallback"));
        Assert.Contains(choices, c => c.Label == "25");
        partial += "912 ";
        Assert.Empty(RecoilLanguage.Complete(Analyze("a.zad", partial), partial.Length));
    }
    [Fact]
    public void TimingIsSuggestedForEventsButIgnoredSequenceTimingRemainsInspectable()
    {
        string text = Animation("START_TIME (ANYTHING) ACTIVATION (ON_STARTUP) CALLBACK (START_TIME (SEQUENCE_OFFSET 1.0))");
        var doc = Analyze("a.zad", text);
        Assert.Equal(2, doc.Diagnostics.Count(d => d.Code == "RECOIL_IGNORED"));
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == "error");
        Assert.Contains("Ignored", RecoilLanguage.Hover(doc, text.IndexOf("START_TIME", StringComparison.Ordinal))!.Markdown);
        int at = text.IndexOf("START_TIME", StringComparison.Ordinal);
        Assert.DoesNotContain(RecoilLanguage.Complete(doc, at), c => c.Label == "START_TIME");
        at = text.LastIndexOf("START_TIME", StringComparison.Ordinal);
        Assert.Contains(RecoilLanguage.Complete(doc, at), c => c.Label == "START_TIME" && c.Documentation!.Contains("preceding event"));
        string partial = text[..text.IndexOf("SEQUENCE_OFFSET", StringComparison.Ordinal)];
        Assert.Equal(3, RecoilLanguage.Complete(Analyze("a.zad", partial), partial.Length).Count);
        partial += "SEQUENCE_OFFSET ";
        Assert.Empty(RecoilLanguage.Complete(Analyze("a.zad", partial), partial.Length));
    }
    [Fact]
    public void ResourceHelpUsesLocalMeaningAndAcceptedScalarForms()
    {
        var sound = Analyze("sounds.zrd", "(SYNTAX (2) SETS (COMMON ((sample file.wav HIGH alternate.wav MED (22050 16 1)))))");
        Assert.Empty(sound.Diagnostics);
        Assert.Contains("bare filename", RecoilLanguage.Hover(sound, sound.Text.IndexOf("HIGH", StringComparison.Ordinal))!.Markdown);
        var vehicle = Analyze("vehicle.zrd", "(tank (common_mode (health (100.0 80.0)) mode_1 (mode (tracked) rates (10.0 30.0) mode_alt (2.0))))");
        Assert.Empty(vehicle.Diagnostics);
        Assert.Contains("acceleration and maximum speed", RecoilLanguage.Hover(vehicle, vehicle.Text.IndexOf("rates", StringComparison.Ordinal))!.Markdown);
        Assert.Contains("Transition time in seconds", RecoilLanguage.Hover(vehicle, vehicle.Text.IndexOf("mode_alt", StringComparison.Ordinal))!.Markdown);
        var weapon = Analyze("weapons.zrd", "(BALLISTICS (laser (FIRE_RATE (2.0))))");
        var turret = Analyze("ai.zrd", "(TURRET (tur_01 (WEAPON (NAME (laser) FIRE_RATE (2.0)))))");
        Assert.Contains("reciprocal", RecoilLanguage.Hover(weapon, weapon.Text.IndexOf("FIRE_RATE", StringComparison.Ordinal))!.Markdown);
        Assert.Contains("interval directly", RecoilLanguage.Hover(turret, turret.Text.IndexOf("FIRE_RATE", StringComparison.Ordinal))!.Markdown);
        Assert.DoesNotContain(RecoilLanguage.Complete(weapon, weapon.Text.IndexOf("FIRE_RATE", StringComparison.Ordinal)), c => c.Label is "BALLISTICS" or "VERSION");
    }
    [Fact]
    public void AnimationFallbackSwitchesAndUnusedForwardMarkerStayAccepted()
    {
        var doc = Analyze("a.zad", Animation("FOG_STATE (TYPE (LINEAR)) OBJECT_MOTION (NAME (root) FORWARD_ROTATION (TIME 90 0)) CALLBACK (VALUE (25.9))"));
        Assert.Empty(doc.Diagnostics);
        var bad = Analyze("a.zad", Animation("CALLBACK (START_TIME (WRONG 0))"));
        Assert.Contains(bad.Diagnostics, d => d.Code == "RECOIL_ENUM");
        var light = Analyze("a.zad", Animation("LIGHT_ANIMATION (NAME (light) RANGE (1 2 3 4))"));
        Assert.Contains("acceleration", RecoilLanguage.Hover(light, light.Text.IndexOf("RANGE", StringComparison.Ordinal))!.Markdown);
    }
    [Fact]
    public void WeatherCompletionAndLintFollowItsBareScalarSyntax()
    {
        const string text = "(MISSION1 (TYPE RAIN PARTICLES 100 WIND_DIR 0.0 COLOR (255 255 255)))";
        var doc = Analyze("weather.zrd", text);
        Assert.Empty(doc.Diagnostics);
        Assert.Equal(["RAIN", "SNOW"], RecoilLanguage.Complete(doc, text.IndexOf("RAIN", StringComparison.Ordinal)).Select(c => c.Label));
        Assert.Contains("emitter", RecoilLanguage.Hover(doc, text.IndexOf("RAIN", StringComparison.Ordinal))!.Markdown);
        const string partial = "(MISSION1 (TYPE ";
        Assert.Equal(["RAIN", "SNOW"], RecoilLanguage.Complete(Analyze("weather.zrd", partial), partial.Length).Select(c => c.Label));
        const string aligned = "(MISSION1 (TYPE   RAIN))";
        Assert.Equal(["RAIN", "SNOW"], RecoilLanguage.Complete(Analyze("weather.zrd", aligned), aligned.IndexOf("TYPE", StringComparison.Ordinal) + 5).Select(c => c.Label));
        var wrapped = Analyze("weather.zrd", "(MISSION1 (TYPE (RAIN) PARTICLES (100) WIND_DIR (0.0)))");
        Assert.Equal(3, wrapped.Diagnostics.Count(d => d.Code == "RECOIL_TYPE"));
    }
    [Fact]
    public void EmptyPrerequisiteListIsValidAndCallHelpSeparatesPlacementFromRecipient()
    {
        const string text = "(ANIMATION_DEFINITIONS (ANIMATION_LIST (ANIMATION_DEFINITION (NAME (root) ACTIVATION_PREREQUISITE (REQUIRED (ANIMATION_LIST ()))))))";
        Assert.Empty(Analyze("a.zad", text).Diagnostics);
        var invalid = Analyze("a.zad", text.Replace("REQUIRED (ANIMATION_LIST ()", "REQUIRED (ANIMATION_LIST (first 2)", StringComparison.Ordinal));
        Assert.Contains(invalid.Diagnostics, d => d.Code == "RECOIL_TYPE");
        var doc = Analyze("a.zad", Animation("CALL_ANIMATION (NAME (child) WITH_NODE (position) OPERAND_NODE (actor))"));
        Assert.Contains("positional reference", RecoilLanguage.Hover(doc, doc.Text.IndexOf("WITH_NODE", StringComparison.Ordinal))!.Markdown);
        Assert.Contains("recipient", RecoilLanguage.Hover(doc, doc.Text.IndexOf("OPERAND_NODE", StringComparison.Ordinal))!.Markdown);
    }
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
