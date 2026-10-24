using zStudio.Core.Formats;

namespace zStudio.Core.Language;

public static partial class RecoilLanguage
{
    private static int ResourceRowHeader(LanguageDocument doc, LanguageRecord? parent, ZrdNode row)
    {
        if (doc.Family == "effects" && parent is { Key: "EFFECTS", Parent: null }) return 1;
        if (doc.Family == "sounds" && parent?.Parent is { Key: "SETS", Parent: null })
            return SoundSyntax(doc) == 2 ? 2 : row.Children.Count;
        return 0;
    }
    private static double? SoundSyntax(LanguageDocument doc)
    {
        var root = doc.Root!;
        while (root.Children.Count == 1 && root.Children[0].Kind == ZrdKind.Array) root = root.Children[0];
        for (int i = 0; i + 1 < root.Children.Count; i++)
            if (root.Children[i] is { Kind: ZrdKind.String, Text: "SYNTAX" })
            {
                var value = root.Children[i + 1];
                return value.Kind == ZrdKind.Array ? value.Children.FirstOrDefault() is { } first ? Numeric(first) : null : Numeric(value);
            }
        return 1;
    }
    private static bool IsResourceScalar(string key, IReadOnlyList<string> path) =>
        path.Count == 0 && key is "SYNTAX" or "SPEED_OF_SOUND"
        || path.Contains("SETS") && key is "VOLUME" or "A3DDIST"
        || path.LastOrDefault() == "EFFECTS" && key == "SPEED"
        || path.Contains("SOUND_GROUPS") && key is "DELAY_REPEAT" or "DELAY_TERMINATION" or "DYNAMIC_WEIGHTS" or "REPEAT" or "DELAY_PLAY" or "PLAY_COUNT" or "WEIGHT"
        || path.Count == 2 && path[1] == "CURSOR" && key == "CAPTURE"
        || IsWidgetContainer(path) && IsBareWidgetInteger(key);
    private static bool IsBareWidgetInteger(string key) => key is "FONT" or "SELECTED_FONT" or "LIST_FONT" or "SPACING";
    private static bool IsWidgetContainer(IReadOnlyList<string> path) => path.Count >= 3 && path[1] is "BUTTONS" or "PRIMITIVES"
        && !path.Skip(3).Any(p => p is "LABEL" or "TEXT" or "BITMAP" or "FILL_BITMAP" or "SOUND" or "POSITION" or "COLOR" or "RATE"
            or "FONT" or "SELECTED_FONT" or "LIST_FONT" or "SPACING" or "LIST_OFFSET" or "LISTSIZE" or "TEXT_OFFSET" or "WORDWRAP" or "ENDP_REL" or "ENDP_ABS" or "MOUSE_RECT");
    // These are consumer-specific layouts, not an inference from a numeric-looking value.
    // f = direct float read, i = direct integer read, n = converting numeric accessor.
    private static IEnumerable<LanguageField> AdditionalResourceFields(string family, string[] path)
    {
        string parent = path.LastOrDefault() ?? "";
        string outer = path.Length > 1 ? path[^2] : "";
        (string Names, string Shape, string Help)[] rules = (family, parent) switch
        {
            ("vehicle", "common_mode") => [
                ("nanite", "i i", "Nanite build rate and maximum level."),
                ("activation not_pursuit_dwell return_range", "f", "Vehicle activation or pursuit setting."),
                ("start_anims", "s", "Animation started for this vehicle."),
                ("aimy", "f f", "Aim yaw rate and maximum."),
                ("camera_ud_swing", "f f f f", "Four vertical camera swing parameters."),
                ("track_switch", "f f f", "Three track-switch distances."),
                ("pickups", "s i", "Pickup type and capacity."),
                ("camback weapons sounds", "", "Shared vehicle configuration.")],
            ("vehicle", "sounds") when path.Length == 3 => outer == "common_mode"
                ? [("weapon_up weapon_select pinging", "s", "Sound sample name.")]
                : [("idle skid engine external collide land", "s", "Sound sample name."), ("pitch_scale volume_scale", "f", "Engine sound scaling.")],
            ("vehicle", _) when path.Length == 2 => [
                ("platform collision sounds", "", "Vehicle mode configuration."),
                ("friction alt_control chas_pitch chas_roll", "f f f", "Three vehicle motion parameters."),
                ("stopping quicksand_slowdown lava_slowdown a_damping mass chas_smooth", "f", "Vehicle motion parameter."),
                ("gun_pitch", "f f", "Gun pitch minimum and rate."),
                ("amphib_wave hover_wave sub_wave", "f f f f f f f", "Pitch and roll wave parameters."),
                ("t2a_anims a2t_anims t2h_anims h2t_anims a2h_anims h2a_anims a2s_anims s2a_anims", "s s?", "Up to two transition animations.")],
            ("network", "") => [
                ("version", "i", "Network format version."), ("name attack_strategy", "s", "Network name or attack strategy."),
                ("activate_rad attack_rad attack_dwell not_pursuit_dwell return_range", "f", "AI activation or pursuit setting."),
                ("hide_times pursuit_params pursuit_range", "f f", "AI timing or pursuit distance pair.")],
            ("weapons", "") => [("MAX_CRATER_RADIUS", "f", "Maximum crater radius."), ("LOCK_ON_WARNING NO_AMMO_WARNING TRIGGER_INACTIVE WEAPON_INACTIVE", "s", "Weapon warning sound.")],
            ("weapons", _) when outer == "BALLISTICS" => [
                ("CRATER QUICKSAND", "i i?", "Crater enable/radius and optional upper radius."),
                ("RELATIVE_SPEED REMOTE_DETONATE RELOAD FLYOUT_HEALTH", "i", "Integer projectile setting."),
                ("TURN_RATE TURN_SUSPEND_TIME PITCH_RATE", "f", "Projectile steering parameter."),
                ("LOCK_ON_LEAD TETHER_GUIDED IMPACT FIRE FLYOUT", "", "Projectile behavior configuration.")],
            ("weapons", "IMPACT") => [("FREEZE HEAT DESIGNATE DAMAGE_ANIM_ON_HEALTH", "", "Impact feedback parameters."),
                ("KILL_ANIMATION DAMAGE_ANIMATION", "s", "Impact animation name."), ("ANIMATION_ALWAYS", "", "Always run impact animation.")],
            ("weapons", _) when parent is "FIRE" or "FLYOUT" || outer == "IMPACT"
                && parent is not ("FREEZE" or "HEAT" or "DESIGNATE" or "DAMAGE_ANIM_ON_HEALTH" or "KILL_ANIMATION" or "DAMAGE_ANIMATION" or "ANIMATION_ALWAYS") => [
                ("RANDOM_ROTATE", "i", "Randomize effect rotation."),
                ("EFFECT MODEL ANIMATION_ATTACHED MODEL_ANIMATION ANIMATION", "s?", "Optional effect resource name."),
                ("SOUND BOUNCE_SOUND", "...", "Effect sound samples.")],
            ("turrets", _) when outer == "TURRET" => [
                ("ACTIVATE_ON_HIT", "f", "Damage threshold for activation."),
                ("ALWAYS_LOOK_AT INTERSECT_BVOL LOS", "i", "Turret behavior flag."), ("EFFECT", "s f", "Effect node and duration."),
                ("DAMAGE_PART", "s", "Damage part node."), ("DEACTIVATE TARGETS", "s ...", "Target node names."),
                ("MSL_LOCK SOUNDS", "", "Turret lock or sound configuration.")],
            ("turrets", "WEAPON") when path.Length == 3 && path[0] == "TURRET" => [("BASE_MOVES", "i", "Allow base movement."), ("FIRE_DWELL", "f", "Firing dwell time.")],
            ("turrets", "SOUNDS") when path.Length == 3 && path[0] == "TURRET" => [("ON START STOP", "s", "Turret sound sample.")],
            ("sounds", "") => [("SPEED_OF_SOUND", "n", "Sound propagation speed."), ("CD_TRACKS SOUND_GROUPS", "", "Sound groups or CD track definitions.")],
            ("sounds", _) when outer == "SETS" => [("VOLUME A3DDIST", "n", "Sample gain or distance scale."), ("RANGE", "f f", "Minimum and maximum sample distance."),
                ("3D LOOPED FREQUENCY HARDWARE PURGEABLE VOICE", "", "Sample presence flag.")],
            ("sounds", _) when path.Contains("SOUND_GROUPS") => [("DELAY_REPEAT DELAY_TERMINATION DYNAMIC_WEIGHTS REPEAT DELAY_PLAY PLAY_COUNT WEIGHT", "n", "Numeric sound-group playback setting."), ("PLAY_SOLO", "", "Play this group alone.")],
            ("hud", "") => [("NANITE OBJECTIVE STATS TARGET WEAPON MODES BRIEFING", "", "HUD layout configuration.")],
            ("hud", "FONTS") => [("OBJ_SUMMARY OBJ_DESCRIPTION STRINGS MESSAGES AMMO", "s i i i ...", "Font family, height, width and weight.")],
            ("ui", _) when path.Length == 1 => [("BACKGROUND_VIDEOS BACKGROUND_TEXT BACKGROUND_SOUNDS CURSOR PRIMITIVES", "", "Dialog background configuration.")],
            ("ui", "CURSOR") when path.Length == 2 => [("CENTER", "i i", "Cursor center offset."), ("CAPTURE", "i", "Cursor capture flag.")],
            ("ui", _) when IsWidgetContainer(path) => [
                ("FONT SELECTED_FONT LIST_FONT SPACING", "i", "Integer font style or list spacing."),
                ("TEXT_OFFSET WORDWRAP ENDP_REL ENDP_ABS", "i i", "Integer pixel coordinates or dimensions."),
                ("MOUSE_RECT", "i i i i", "Mouse hit rectangle."),
                ("COLOR", "i i i", "Red, green and blue channels."),
                ("LISTSIZE", "i i?", "Total and visible list entries."),
                ("LIST_OFFSET LABEL SOUND FLASH FILL_BITMAP", "", "Widget presentation configuration."),
                ("RATE", "f", "Flash rate.")],
            _ => []
        };
        foreach (var rule in rules)
            foreach (string name in rule.Names.Split(' ')) yield return F(name, rule.Help, rule.Shape);
    }

    private static int MinimumResourceValues(LanguageDocument doc, LanguageRecord record) => (doc.Family, record.Parent?.Key ?? "", record.Key) switch
    {
        ("vehicle", "common_mode", "camback") => 3,
        ("ui", _, "LIST_OFFSET") => 2,
        ("hud", "", "TYPEI") => 1,
        ("hud", "", "TYPEII") => 5,
        ("hud", "", "SENSOR") => 7,
        ("hud", "", "STATS" or "OBJECTIVE") => 8,
        ("hud", "", "MODES" or "NANITE") => 4,
        ("hud", "", "WEAPON") => 9,
        ("hud", "", "SHIELD") => 3,
        _ => 0
    };

    private static void ValidateResourceValues(LanguageDocument doc, LanguageRecord record)
    {
        string parent = record.Parent?.Key ?? "";
        int minimum = MinimumResourceValues(doc, record);
        if (record.Values.Count < minimum)
            doc.Report("RECOIL_ARITY", "error", $"{record.Key} requires at least {minimum} values; found {record.Values.Count}.", doc.Span(record.Value!));
        void Rows(string shape, int maximum = int.MaxValue)
        {
            foreach (var row in record.Values.Take(maximum))
                CheckTuple(doc, record, row, shape);
        }
        void Layout(params string[] shapes)
        {
            for (int i = 0; i < Math.Min(record.Values.Count, shapes.Length); i++)
            {
                if (shapes[i].Length == 1 && record.Values[i].Kind == ZrdKind.Array)
                    doc.Report("RECOIL_TYPE", "error", $"{record.Key} value {i + 1} requires a bare {ReadableShape(shapes[i])}, without parentheses around the value.", doc.Span(record.Values[i]));
                else if (shapes[i].Length > 0) CheckTuple(doc, record, record.Values[i], shapes[i]);
            }
        }
        if (doc.Family == "vehicle")
        {
            if (parent == "common_mode")
            {
                if (record.Key == "camback") Rows("f f f", 3);
                if (record.Key == "weapons") Rows("s i i i f f f i i");
            }
            else if (Ancestors(record.Parent).Count() == 2 && record.Key is "platform" or "collision") Rows("f f f");
        }
        if (doc.Family == "weapons" && parent == "IMPACT")
        {
            if (record.Key is "FREEZE" or "HEAT" or "DESIGNATE" && record.Values.Count >= 6)
                CheckShape(doc, record, "f f f f f f ...");
            if (record.Key == "DAMAGE_ANIM_ON_HEALTH") Rows("f s");
        }
        if (doc.Family == "hud" && record.Parent == null)
        {
            const string image = "s i i s s";
            switch (record.Key)
            {
                case "TYPEI": Layout("i i i i"); break;
                case "TYPEII": Layout("i i i i", image, image, image, "s s s s"); break;
                case "SENSOR": Layout(image, "f", "i i", "i i", "i", image, "i i i i"); break;
                case "STATS": Layout("i i", "i i", "i i i", "i i i", "i i", "i i", "i i", "i i"); break;
                case "MODES": Rows("s s s i i", 4); break;
                case "WEAPON": Rows("s s s s s s s i i ...", 9); break;
                case "SHIELD": Layout(image, "x i i ...", "i i i i"); break;
                case "NANITE": Layout("i i i i", image, image, image); break;
                case "OBJECTIVE": Layout("f", image, "i i i i", "i i i", "i i", "i i", "i i i i", "i i"); break;
            }
        }
        if (doc.Family == "sounds" && record.Key == "CD_TRACKS" && record.Parent == null) Rows("s i");
        if (doc.Family == "sounds" && record.Key == "SETS" && record.Parent == null)
        {
            double? version = SoundSyntax(doc);
            foreach (var set in record.Values.Where(v => v.Kind == ZrdKind.Array))
                foreach (var row in set.Children.Where(v => v.Kind == ZrdKind.Array))
                {
                    if (version == 2) CheckTuple(doc, record, row, "s s ...");
                    else if (version == 1)
                    {
                        bool gain = row.Children.Count > 2 && row.Children[2].Kind is ZrdKind.Float or ZrdKind.Int;
                        CheckTuple(doc, record, row, gain ? "s s f s s s f? f?" : "s s s s s f? f?");
                    }
                }
        }
        if (doc.Family == "ui")
        {
            if (record.Parent?.Parent == null)
            {
                if (record.Key == "BACKGROUND_IMAGES") Rows("s i? i?");
                if (record.Key == "BACKGROUND_TEXT") Rows("s i i i");
                if (record.Key == "BACKGROUND_SOUNDS") Rows("s f?");
                if (record.Key == "BACKGROUND_VIDEOS")
                    foreach (var row in record.Values)
                    {
                        CheckTuple(doc, record, row, "s i? i? a?");
                        if (row.Children.Count > 3) CheckTuple(doc, record, row.Children[3], "i i i");
                    }
                if (record.Key == "FONTS")
                    foreach (var row in record.Values.Take(19))
                    {
                        CheckTuple(doc, record, row, "i s i a i? i? s?");
                        if (row.Children.Count <= 3) continue;
                        var color = row.Children[3];
                        if (color.Kind == ZrdKind.Array && color.Children.FirstOrDefault()?.Kind == ZrdKind.Array)
                        {
                            CheckTuple(doc, record, color, "a a");
                            foreach (var rgb in color.Children.Take(2)) CheckTuple(doc, record, rgb, "i i i");
                        }
                        else CheckTuple(doc, record, color, "i i i");
                    }
            }
            if (Ancestors(record.Parent).Contains("BUTTONS") || Ancestors(record.Parent).Contains("PRIMITIVES"))
            {
                if (record.Key is "TEXT" or "LABEL" && record.Values.Count > 0)
                {
                    if (record.Values[0].Kind == ZrdKind.Array) Rows("s i i i");
                    else CheckShape(doc, record, "s i i i");
                }
                if (record.Key == "LIST_OFFSET") Rows("i i", 2);
                if (record.Key == "SOUND") CheckShape(doc, record, "s f?");
                if (record.Key == "FILL_BITMAP") CheckShape(doc, record, "s i? i?");
            }
        }
    }

    private static void CheckTuple(LanguageDocument doc, LanguageRecord record, ZrdNode tuple, string shape) =>
        CheckShape(doc, record with { Value = tuple }, shape);
}
