using zStudio.Core.Animation;
using zStudio.Core.Formats;
using zStudio.Core.Sources;

namespace zStudio.Core.Language;

public static partial class RecoilLanguage
{
    private static readonly Dictionary<string, LanguageField[]> ResourceFields = new(StringComparer.Ordinal)
    {
        ["player"] = [F("camera_zone", "Camera dead zone; accepted only between zero and one.", "f"), F("max_cam_yaw_rate", "Maximum camera yaw rate.", "f"),
            F("mouse_push", "Horizontal and vertical mouse camera response.", "f f"), F("fp_cam_el_rate", "First-person elevation rate.", "f"), F("fp_cam_el_lim", "Minimum and maximum camera elevation.", "f f"),
            F("underwater_cam", "Underwater camera distance, height, steps, far distance, RGB and alpha.", "f f i f i i i f"),
            F("camera_elastic", "Camera elasticity.", "f"), F("max_cam_tether_angle", "Camera tether angle in degrees.", "f"),
            F("nom_gravity", "Normal gravity.", "f"), F("wat_gravity", "Underwater gravity.", "f"), F("qsd_gravity", "Quicksand gravity.", "f"),
            F("qsand_sink", "Quicksand sinking speed.", "f"), F("lava_sink", "Lava sinking speed.", "f"), F("max_slope", "Slope threshold.", "f"),
            F("make_hot", "Weapon applied by heat.", "s", reference: "weapon"), F("make_cold", "Weapon applied by cold.", "s", reference: "weapon"),
            F("burning_anim", "Animation used for burning; trailing source parameters are preserved.", "s ...", reference: "animation"),
            F("low_shield_snd", "Warning sample, interval, and looping sample.", "s f s")],
        ["weapons"] = [F("VERSION", "Weapon resource version.", "i"), F("BALLISTICS", "Named weapon definitions."),
            F("AMMO_LIMIT", "Maximum ammunition, stored as an integer.", "i"), F("DAMAGE", "Damage per impact.", "f"),
            F("FIRE_RATE", "Fire frequency; the engine stores its reciprocal.", "f"), F("ACCELERATION", "Projectile acceleration.", "f"),
            F("IMPACT_PROXIMITY", "Impact proximity radius.", "f"), F("IMPACT_TYPE", "Damage-mask slot.", "i"),
            F("RANGE", "Weapon range.", "f"), F("VELOCITY", "Projectile velocity.", "f"), F("GRAVITY", "Projectile gravity.", "f"),
            F("DETONATION_DISTANCE", "Detonation radius.", "f"), F("BEAM", "Beam timing and flags.", "f f i"),
            F("CATCHES_FIRE", "Fire flag.", "i"), F("EXPIRES", "Expiry flag.", "i"), F("FIXED_ROTATE", "Fixed rotation flag.", "i"),
            F("INSTANT", "Instant impact flag.", "i"), F("LOCK_ON", "Lock-on duration.", "f"), F("MINE", "Mine flag.", "i"),
            F("MULTI_TARGET", "Multiple-target flag.", "i"), F("NAME", "Display name.", "s"), F("DESC", "Dialog message key.", "s", reference: "message"),
            F("KILL_VERB", "Dialog kill-verb key.", "s", reference: "message"), F("MILITARY_NAME", "Dialog military-name key.", "s", reference: "message")],
        ["weather"] = [F("TYPE", "Weather emitter type.", "s", ["RAIN", "SNOW"]), F("PARTICLES", "Number of weather particles.", "i"),
            F("COLOR", "Particle RGB values.", "i i i"), F("WIND_DIR", "Wind direction.", "f"), F("WIND_VEL", "Wind speed.", "f"),
            F("GRAVITY", "Particle gravity.", "f"), F("ALPHA_GRADIENT", "Start and end alpha.", "f f")],
        ["objectives"] = [F("READ_TIME", "Objective display time in seconds; the loader reads an integer.", "i"), F("REVIEW_DELAY", "Review delay in seconds, stored as an integer.", "i"),
            F("REVIEW_SOUND", "Review sample.", "s", reference: "sound"), F("OBJECTIVE_SOUND", "Objective notification sample.", "s", reference: "sound"),
            F("ACTIVE", "Complete when the named node path becomes active. ACTIVE takes precedence over INACTIVE.", "s ..."),
            F("INACTIVE", "Complete when the named node path becomes inactive.", "s ..."), F("READ_SOUND", "Narration sample with an optional delay.", "s f?", reference: "sound"),
            F("AUTOPLAY", "Automatically play objective narration."), F("FINAL_MISSION", "Marks the final mission.")],
        ["sounds"] = [F("SYNTAX", "Sound resource syntax version.", "i"), F("SOUND_PATH", "Source sound directory.", "s"), F("SETS", "Named sound sets."),
            F("HIGH", "Sample rate, bit depth, and channel count.", "i i i"), F("MED", "Sample rate, bit depth, and channel count.", "i i i"), F("LOW", "Sample rate, bit depth, and channel count.", "i i i")],
        ["effects"] = [F("EFFECTS", "Effect template declarations."), F("NAME", "Effect template name.", "s"), F("SPEED", "Texture animation speed.", "n"),
            F("MAPS", "Ordered texture frames.", "s ..."), F("LOOPING", "Repeat texture animation.", "s", ["ON", "OFF"])],
        ["pickups"] = [F("PICKUP_DATA", "Pickup presentation definitions."), F("SOUND", "Sample played on pickup.", "s", reference: "sound"), F("IMAGE", "Pickup image name.", "s")],
        ["vehicle"] = [F("common_mode", "Shared vehicle configuration."), F("mode", "Vehicle locomotion mode.", "s"), F("health", "Health; player common-mode configuration also has a multiplayer value.", "f f?"),
            F("position", "Initial XYZ position.", "f f f"), F("rotation", "Initial XYZ rotation.", "f f f"), F("rates", "Forward and reverse rates.", "f f"),
            F("turns", "Turning rates.", "f f"), F("turn_damping", "Turn damping.", "f"), F("rate_damping", "Rate damping parameters.", "f f"),
            F("collision_d", "Collision response parameters.", "f f"), F("mode_alt", "Mode altitude.", "f")],
        ["network"] = [F("type", "AI network type.", "s"), F("path_width", "Navigation corridor width.", "f"), F("attack_buddy", "Alert other vehicles in this network.", "i"),
            F("activate_buddy", "Authored activation-buddy flag.", "i"), F("attack_range", "Attack strategy distance pair.", "f f")],
        ["hud"] = [F("VERSION", "HUD resource version.", "i"), F("FONTS", "Named font roles."), F("RETICULE", "Range, off and on reticle images.", "s s s"),
            F("TYPEI", "Type-I viewport configuration."), F("TYPEII", "Type-II viewport and image layout."), F("SENSOR", "Sensor layout and tuning."), F("SHIELD", "Shield display layout.")],
        ["fonts"] = [F("FONTS", "Ordered bitmap font resource names.", "s ...")],
        ["ui"] = [F("SHARED_IMAGE_PATH", "Shared dialog image search directory.", "s"), F("IMAGE_PATH", "Image directory for this dialog section.", "s"),
            F("POSITION", "UI position in integer pixels.", "i i"), F("BITMAP", "Image name and optional integer X/Y offsets.", "s i? i?"),
            F("FONTS", "Font style records: ID, family, size, color, style and weight."), F("BACKGROUND_IMAGES", "Background image layout records."),
            F("BUTTONS", "Named button definitions."), F("TEXT", "Text widget configuration."), F("ROLLOVER", "Rollover presentation.")],
        ["turrets"] = [F("TURRET", "Turret pattern and definition pairs; each * matches one decimal digit."), F("DESTROY_ANIM", "Destruction animation.", "s", reference: "animation"),
            F("FIRE_ANIM", "Firing animation.", "s", reference: "animation"), F("HEALTH", "Turret health.", "f"), F("PARTS", "Two to four names under the healthy node.", "s s s? s?"),
            F("WEAPON", "Turret weapon configuration."), F("AMMO", "Ammunition count.", "i"), F("DAMAGE_MODIFIER", "Weapon damage multiplier.", "f"), F("DETECTION_RANGE", "Detection distance.", "f"),
            F("FIRE_RATE", "Turret firing interval.", "f"), F("FIRE_LIMITS", "Minimum and maximum firing limits.", "f f"), F("NAME", "Weapon definition name.", "s", reference: "weapon")],
        ["startanims"] = [F("NEW_GAME_START", "List of animation records called for a new game.", reference: "animation"), F("LOAD_GAME_START", "List of animation records called for a loaded game.", reference: "animation")],
        ["resource"] = []
    };
    private static LanguageField F(string name, string description, string shape = "", string[]? choices = null, string? reference = null) => new(name, description, shape, choices, reference);
    private static string Classify(LanguageDocument doc)
    {
        bool Has(string key) => doc.Records.Any(r => r.Parent == null && r.Key == key && r.Value?.Kind == ZrdKind.Array);
        if (Has("ANIMATION_DEFINITIONS")) return "animation";
        if (Has("TURRET")) return "turrets";
        if (Has("SHARED_IMAGE_PATH") || doc.Records.Any(r => r.Parent is { Parent: null } && r.Key == "FONTS" && r.Value?.Children.Any(n => n.Kind == ZrdKind.Array) == true)) return "ui";
        if (Has("BALLISTICS")) return "weapons";
        if (Has("PICKUP_DATA")) return "pickups";
        if (Has("EFFECTS")) return "effects";
        if (Has("SETS") && Has("SYNTAX")) return "sounds";
        if (doc.Records.Any(r => r.Parent?.Parent == null && r.Key == "common_mode" && r.Value?.Kind == ZrdKind.Array)) return "vehicle";
        if (Has("camera_zone") || Has("underwater_cam")) return "player";
        if (doc.Records.Any(r => r.Parent?.Key.StartsWith("MISSION", StringComparison.Ordinal) == true && r.Key == "TYPE")) return "weather";
        if (Has("OBJECTIVE1") || Has("READ_TIME") && Has("REVIEW_DELAY")) return "objectives";
        if (Has("NEW_GAME_START") || Has("LOAD_GAME_START")) return "startanims";
        if (Has("FONTS")) return Has("TYPEI") || Has("TYPEII") ? "hud" : "fonts";
        if (doc.Records.Any(r => r.Key.StartsWith("node_", StringComparison.Ordinal) && r.Value?.Kind == ZrdKind.Array)) return "network";
        string file = System.IO.Path.GetFileNameWithoutExtension(doc.Path).ToLowerInvariant();
        var body = doc.Root!; while (body.Children.Count == 1 && body.Children[0].Kind == ZrdKind.Array && body.Children[0].Children.FirstOrDefault()?.Kind == ZrdKind.Array) body = body.Children[0];
        if (file is "aiv" or "aiv_easy" or "aiv_hard" && doc.Records.Any(r => r.Parent == null && r.Value?.Kind == ZrdKind.Array && r.Values.Count == 3 && r.Values[1].Kind == ZrdKind.Array)) return "placements";
        if (file is "puppies" or "puppies_easy" or "puppies_hard" && body.Children.Any(n => n.Kind == ZrdKind.Array && n.Children.FirstOrDefault()?.Kind == ZrdKind.String)) return "pickup-placements";
        return "resource";
    }
    private static void Validate(LanguageDocument doc, CancellationToken token)
    {
        foreach (var record in doc.Records)
        {
            token.ThrowIfCancellationRequested();
            var field = Field(doc.Family, record.Key, record.Parent?.Key ?? "", Ancestors(record.Parent));
            if (field == null || record.Value == null) continue;
            if (doc.Family == "weather" && IsWeatherScalar(record.Key) && record.Value.Kind == ZrdKind.Array)
            {
                doc.Report("RECOIL_TYPE", "error", $"Weather {record.Key} requires a bare scalar value, without parentheses around the value.", doc.Span(record.Value));
                continue;
            }
            if (!field.Suggested)
            {
                doc.Report("RECOIL_IGNORED", "warning", field.Description + " " + field.Notes, doc.Span(record.KeyNode));
                continue;
            }
            if (doc.Family == "animation" && record.Key == "ACTIVATION" && record.Parent?.Key == "SEQUENCE_DEFINITION" && record.FirstText != "ON_CALL")
                doc.Report("RECOIL_IGNORED", "warning", "Only ON_CALL has an effect on sequence activation; this value is ignored.", doc.Span(record.KeyNode));
            // Empty PARTS is a shipped no-part turret form; the loader only binds parts for 2–4 names.
            bool filenameVariant = doc.Family == "sounds" && record.Key is "HIGH" or "MED" or "LOW" && record.Value.Kind == ZrdKind.String;
            if (field.Shape.Length > 0 && !filenameVariant && !(doc.Family == "turrets" && record.Key == "PARTS" && record.Values.Count == 0)) CheckShape(doc, record, field.Shape);
            if (doc.Family == "animation" && record.Key == "ANIMATION_LIST" && Ancestors(record.Parent).Contains("ACTIVATION_PREREQUISITE"))
                foreach (var value in record.Values.Skip(1)) CheckShape(doc, record with { Value = value }, "s");
            // Most engine switches have fallbacks; completion choices are advisory, not closed enums.
            bool closedEnum = doc.Family == "animation" && (record.Key is "START_TIME" or "ANIMATION_LOD" || record.Key == "ACTIVATION" && record.Parent?.Key == "ANIMATION_DEFINITION");
            if (closedEnum && field.Choices is { Length: > 0 } choices && record.Scalars.FirstOrDefault() is { Kind: ZrdKind.String } scalar && !choices.Contains(scalar.Text, StringComparer.Ordinal))
                doc.Report("RECOIL_ENUM", "error", $"{record.Key} expects {string.Join(", ", choices)}.", doc.Span(scalar));
            if (doc.Family == "animation" && record.Key == "VALUE" && record.Parent?.Key == "CALLBACK" && record.Scalars.FirstOrDefault() is { } id && Numeric(id) is double number && (number < int.MinValue || number > int.MaxValue))
                doc.Report("RECOIL_RANGE", "error", "Callback VALUE must fit in a signed 32-bit integer.", doc.Span(id));
            if (doc.Family == "animation" && record.Key == "EXECUTION_PRIORITY" && record.Scalars.FirstOrDefault() is { } priority && Numeric(priority) is double p && (p < 0 || p > 255))
                doc.Report("RECOIL_RANGE", "error", "Execution priority must fit in one byte (0–255).", doc.Span(priority));
            if (doc.Family == "weapons" && record.Key is "FIRE_RATE" or "BEAM" && record.Scalars.FirstOrDefault() is { } rate && Numeric(rate) == 0)
                doc.Report("RECOIL_ZERO_DIVISOR", "warning", "The engine divides by this value; zero produces a nonfinite result.", doc.Span(rate));
        }
        if (doc.Family == "animation")
        {
            foreach (var def in doc.Records.Where(r => r.Key == "ANIMATION_DEFINITION"))
                foreach (string required in new[] { "NAME" })
                    if (!doc.Records.Any(r => ReferenceEquals(r.Parent, def) && r.Key == required && r.Scalars.Any()))
                        doc.Report("RECOIL_REQUIRED", "error", $"Animation definition needs {required}.", doc.Span(def.KeyNode));
            foreach (var record in doc.Records.Where(r => r.Parent?.Key is "SEQUENCE_DEFINITION" or "RESET_STATE" || r.Key == "BOUNCE_SOUND"))
            {
                string[] required = record.Key switch
                {
                    "OBJECT_MOTION_SI_SCRIPT" => ["NAME", "SCRIPT_FILENAME"],
                    "OBJECT_ADD_CHILD" or "OBJECT_DELETE_CHILD" => ["PARENT_CHILD"],
                    "BOUNCE_SOUND" => ["NAME"],
                    "OBJECT_ACTIVE_STATE" or "OBJECT_SCALE_STATE" or "OBJECT_OPACITY_STATE" => ["NAME", "STATE"],
                    _ => AnimationSourceFields.EventFields.TryGetValue(record.Key, out string? fields) && fields.Split(' ').Contains("NAME") ? ["NAME"] : []
                };
                foreach (string name in required)
                    if (!doc.Records.Any(r => ReferenceEquals(r.Parent, record) && r.Key == name && r.Scalars.Any()))
                        doc.Report("RECOIL_REQUIRED", "error", $"{record.Key} needs {name}.", doc.Span(record.KeyNode));
            }
            foreach (var record in doc.Records.Where(r => r.Value != null && r.Parent != null))
            {
                string parent = record.Parent!.Key;
                if (parent is not ("ANIMATION_DEFINITION" or "SEQUENCE_DEFINITION" or "RESET_STATE")) continue;
                if (Field("animation", record.Key, parent) == null)
                    doc.Report("RECOIL_IGNORED", "warning", $"{Shown(record.Key)} is not recognized in {parent}; check its spelling and placement.", doc.Span(record.KeyNode));
            }
        }
        if (doc.Family == "network") ValidateNetwork(doc);
        if (doc.Family is "placements" or "pickup-placements") ValidatePlacements(doc);
        if (doc.Family == "turrets")
            foreach (var weapon in doc.Records.Where(r => r.Key == "WEAPON"))
                if (!doc.Records.Any(r => ReferenceEquals(r.Parent, weapon) && r.Key == "NAME" && r.FirstText != null))
                    doc.Report("RECOIL_REQUIRED", "error", "Turret WEAPON needs a valid NAME.", doc.Span(weapon.KeyNode));
        if (doc.Family == "objectives")
        {
            var objectives = doc.Records.Where(r => r.Parent == null && r.Key.StartsWith("OBJECTIVE", StringComparison.Ordinal) && int.TryParse(r.Key.AsSpan(9), out _)).ToArray();
            var numbers = objectives.Select(r => int.Parse(r.Key.AsSpan(9), System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();
            foreach (var objective in objectives)
            {
                int number = int.Parse(objective.Key.AsSpan(9), System.Globalization.CultureInfo.InvariantCulture);
                if (number > 10) doc.Report("RECOIL_OBJECTIVE_LIMIT", "error", "The game supports at most ten objectives.", doc.Span(objective.KeyNode));
                if (number > 1 && !numbers.Contains(number - 1)) doc.Report("RECOIL_OBJECTIVE_GAP", "warning", "The loader stops at the first missing objective number.", doc.Span(objective.KeyNode));
            }
        }
    }
    private static void ValidatePlacements(LanguageDocument doc)
    {
        if (doc.Family == "placements")
        {
            foreach (var row in doc.Records.Where(r => r.Parent == null && r.Value != null))
            {
                var values = row.Values;
                if (values.Count != 3 || values[1].Kind != ZrdKind.Array) { doc.Report("RECOIL_PLACEMENT", "error", "Vehicle placement expects a network integer, XYZ tuple, and yaw float.", doc.Span(row.Value!)); continue; }
                CheckShape(doc, row with { Value = values[1] }, "f f f");
                if (values[0].Kind != ZrdKind.Int || values[2].Kind != ZrdKind.Float) doc.Report("RECOIL_TYPE", "error", "Vehicle placement network must be an integer and yaw a stored float.", doc.Span(row.Value!));
            }
            return;
        }
        var root = doc.Root!; while (root.Children.Count == 1 && root.Children[0].Kind == ZrdKind.Array && root.Children[0].Children.FirstOrDefault()?.Kind == ZrdKind.Array) root = root.Children[0];
        foreach (var row in root.Children.Where(n => n.Kind == ZrdKind.Array))
        {
            var values = row.Children;
            if (values.Count != 5 || values[0].Kind != ZrdKind.String || values[1].Kind != ZrdKind.Int || values[2].Kind != ZrdKind.Array || values[3].Kind != ZrdKind.Array || values[4].Kind != ZrdKind.Float)
            { doc.Report("RECOIL_PLACEMENT", "error", "Pickup row expects type, integer amount, XYZ tuple, rotation tuple, and float respawn delay.", doc.Span(row)); continue; }
            if (!zStudio.Core.MissionPickupType.Catalog.Any(t => t.Name == values[0].Text))
                doc.Report("RECOIL_PICKUP_TYPE", "error", "Unknown pickup type.", doc.Span(values[0]));
            var record = new LanguageRecord("Pickup position", values[0], values[2], null); CheckShape(doc, record, "f f f");
            CheckShape(doc, record with { Key = "Pickup rotation", Value = values[3] }, "f f f");
        }
    }
    internal static double? Numeric(ZrdNode value) => value.Kind == ZrdKind.Int ? unchecked((int)value.Bits) : value.Kind == ZrdKind.Float ? BitConverter.UInt32BitsToSingle(value.Bits) : null;
    private static void CheckShape(LanguageDocument doc, LanguageRecord record, string shape)
    {
        string[] expected = shape.Split(' '); var actual = record.Values;
        int required = expected.Count(s => s != "..." && !s.EndsWith('?'));
        bool extra = expected[^1] == "...";
        if (actual.Count < required || !extra && actual.Count > expected.Length)
        { doc.Report("RECOIL_ARITY", "error", $"{record.Key} expects {ReadableShape(shape)}; found {actual.Count} values.", doc.Span(record.Value!)); return; }
        for (int i = 0; i < actual.Count && i < expected.Length && expected[i] != "..."; i++)
        {
            var value = actual[i]; char kind = expected[i][0];
            bool valid = kind switch { 's' => value.Kind == ZrdKind.String, 'i' => value.Kind == ZrdKind.Int, 'f' => value.Kind == ZrdKind.Float, 'n' => value.Kind is ZrdKind.Float or ZrdKind.Int, _ => true };
            if (!valid) doc.Report("RECOIL_TYPE", "error", $"{record.Key} value {i + 1} requires {(kind == 'f' ? "a stored float (for example 10.0, not 10)" : kind == 'i' ? "a stored integer" : kind == 'n' ? "a number" : "a string")}.", doc.Span(value));
            else if (kind is 'f' or 'n' && Numeric(value) is double n && !double.IsFinite(n)) doc.Report("RECOIL_FINITE", "error", $"{record.Key} requires a finite number.", doc.Span(value));
        }
    }
    private static void ValidateNetwork(LanguageDocument doc)
    {
        foreach (var group in doc.Records.Where(r => r.Key.StartsWith("node_", StringComparison.Ordinal) && r.Value?.Kind == ZrdKind.Array).GroupBy(r => r.Parent))
        {
            bool Eligible(LanguageRecord r) => r.Key.Length == 7 && int.TryParse(r.Key.AsSpan(5), out int index) && index is >= 0 and <= 98;
            bool Spatial(LanguageRecord r) => r.Values.Count == 3 && r.Values[0].Kind == ZrdKind.Int
                && r.Values[1].Kind == ZrdKind.Array && r.Values[1].Children.Count == 3 && r.Values[1].Children.All(n => n.Kind == ZrdKind.Float)
                && r.Values[2].Kind == ZrdKind.Array && r.Values[2].Children.Count == 3 && r.Values[2].Children.All(n => n.Kind == ZrdKind.Int);
            var known = group.Where(r => Eligible(r) && Spatial(r)).Select(r => int.Parse(r.Key.AsSpan(5), System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();
            foreach (var node in group)
            {
                if (!Eligible(node)) { doc.Report("RECOIL_NODE_ID", "error", "Spatial node names must be node_00 through node_98.", doc.Span(node.KeyNode)); continue; }
                // Spatial records are distinct from interleaved valve/constraint records.
                if (node.Values.Count != 3 || node.Values[1].Kind != ZrdKind.Array || node.Values[2].Kind != ZrdKind.Array)
                { doc.Report("RECOIL_NODE_SHAPE", "error", "Spatial node expects an integer ID, an XYZ float tuple, and three integer links.", doc.Span(node.Value!)); continue; }
                if (node.Values[0].Kind != ZrdKind.Int) doc.Report("RECOIL_TYPE", "error", "Spatial node ID must be a stored integer.", doc.Span(node.Values[0]));
                CheckShape(doc, node with { Value = node.Values[1] }, "f f f");
                CheckShape(doc, node with { Value = node.Values[2] }, "i i i");
                foreach (var link in node.Values[2].Children.Where(n => n.Kind == ZrdKind.Int))
                    if (Numeric(link) is double target && target >= 0 && !known.Contains((int)target))
                        doc.Report("RECOIL_NODE_LINK", "error", $"Link names missing node_{target:00}; the game cannot resolve this network edge.", doc.Span(link));
            }
        }
    }
}
