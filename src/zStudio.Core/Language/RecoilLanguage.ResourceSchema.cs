namespace zStudio.Core.Language;

public static partial class RecoilLanguage
{
    private static IEnumerable<LanguageField> ResourceContext(string family, string parent, IReadOnlyList<string> context)
    {
        var path = context.Where(p => p.Length > 0).ToArray();
        string outer = path.Length > 1 ? path[^2] : "";
        string[]? allowed = family switch
        {
            "player" or "network" => parent.Length == 0 ? null : [],
            "weapons" => parent.Length == 0 ? ["VERSION", "BALLISTICS"] : outer == "BALLISTICS" ? ResourceFields[family].Where(f => f.Name is not ("VERSION" or "BALLISTICS")).Select(f => f.Name).ToArray() : [],
            "weather" => parent.StartsWith("MISSION", StringComparison.Ordinal) ? null : [],
            "objectives" => parent.Length == 0 ? ["READ_TIME", "REVIEW_DELAY", "REVIEW_SOUND", "OBJECTIVE_SOUND", "FINAL_MISSION"]
                : parent.StartsWith("OBJECTIVE", StringComparison.Ordinal) && int.TryParse(parent.AsSpan(9), out _) ? ["ACTIVE", "INACTIVE", "READ_SOUND", "AUTOPLAY"] : [],
            "sounds" => parent.Length == 0 ? ["SYNTAX", "SOUND_PATH", "SETS"] : outer == "SETS" ? ["HIGH", "MED", "LOW"] : [],
            "effects" => parent.Length == 0 ? ["EFFECTS"] : parent == "EFFECTS" ? ["NAME", "SPEED", "MAPS", "LOOPING"] : [],
            "pickups" => parent.Length == 0 ? ["PICKUP_DATA"] : outer == "PICKUP_DATA" ? ["SOUND", "IMAGE"] : [],
            "vehicle" => parent == "common_mode" ? ["health", "position", "rotation"] : path.Length == 1 ? ["common_mode"] : path.Length == 2 ? ["mode", "rates", "turns", "turn_damping", "rate_damping", "collision_d", "mode_alt"] : [],
            "turrets" => parent.Length == 0 ? ["TURRET", "DESTROY_ANIM"] : parent == "WEAPON" && path.Contains("TURRET") ? ["NAME", "AMMO", "DAMAGE_MODIFIER", "DETECTION_RANGE", "FIRE_RATE", "FIRE_LIMITS"]
                : outer == "TURRET" ? ["DESTROY_ANIM", "FIRE_ANIM", "HEALTH", "PARTS", "WEAPON"] : [],
            "hud" => parent.Length == 0 ? ["VERSION", "FONTS", "RETICULE", "TYPEI", "TYPEII", "SENSOR", "SHIELD"] : [],
            "fonts" or "startanims" => parent.Length == 0 ? null : [],
            // Dialogs have named sections/widgets. Never offer pixel-layout keys inside scalar value lists.
            "ui" => parent.Length == 0 ? ["SHARED_IMAGE_PATH"] : path.Any(p => p is "POSITION" or "BITMAP" or "FONTS" or "IMAGE_PATH" or "SHARED_IMAGE_PATH") ? [] : null,
            _ => []
        };
        var fields = ResourceFields.GetValueOrDefault(family) ?? [];
        return fields.Where(f => allowed == null || allowed.Contains(f.Name, StringComparer.Ordinal)).Select(f => ResourceHelp(family, f));
    }

    private static LanguageField ResourceHelp(string family, LanguageField field)
    {
        var key = (family, field.Name);
        string? description = key switch
        {
            ("player", "camera_zone") => "Set the camera's normalized dead-zone threshold.",
            ("player", "burning_anim") => "Animation used for the player's recent-hit/burning effect.",
            ("vehicle", "rates") => "Set movement acceleration and maximum speed.",
            ("vehicle", "turns") => "Set yaw acceleration and maximum yaw rate.",
            ("vehicle", "rate_damping") => "Set acceleration and deceleration damping.",
            ("vehicle", "mode_alt") => "Set the locomotion mode's altitude-transition time.",
            ("vehicle", "position") or ("vehicle", "rotation") => "Retained authored field; the retail common-mode loader does not apply it.",
            ("network", "path_width") => "Set the corridor width copied into AI link probe data.",
            ("network", "activate_buddy") => "Read an unused activation-buddy flag; presence also affects attack_buddy loading.",
            ("network", "attack_range") => "Ignored spelling in the retail AI network loader.",
            ("weapons", "BEAM") => "Enable beam behavior and set its reciprocal timing, interpolation parameter and flag.",
            ("weapons", "CATCHES_FIRE") => "Set bit 12 of the weapon flags from the low bit of this integer.",
            ("weapons", "MINE") => "Set the mine/full-probe-damage flag and enable the shared low flag bit.",
            ("weapons", "FIRE_RATE") => "Set shots per second; the loader stores the reciprocal interval.",
            ("weapons", "EXPIRES") => "Set the weapon-expiry flag from the low bit of this integer.",
            ("weapons", "FIXED_ROTATE") => "Set the fixed-projectile-rotation flag from the low bit of this integer.",
            ("weapons", "INSTANT") => "Set the instant-impact flag from the low bit of this integer.",
            ("weapons", "MULTI_TARGET") => "Set the multiple-target flag from the low bit of this integer.",
            ("effects", "SPEED") => "Set the effect model's texture-cycle speed.",
            ("sounds", "HIGH") or ("sounds", "MED") or ("sounds", "LOW") => "Choose this sample's quality variant or its playback format.",
            ("turrets", "FIRE_RATE") => "Set the turret's firing interval directly.",
            ("turrets", "FIRE_LIMITS") => "Set the two firing-limit values passed to the turret weapon controller.",
            ("turrets", "PARTS") => "Bind the turret's barrel and firing points, optionally with a rotating base.",
            ("turrets", "WEAPON") => "Configure this turret's weapon, ammunition and firing behavior.",
            ("fonts", "FONTS") => "Retained bitmap-font resource list; retail RECOIL does not request fonts.zrd.",
            ("hud", "VERSION") => "Retained source version marker; the retail HUD loader does not read it.",
            ("hud", "FONTS") => "Assign font descriptions to HUD roles such as objective summary and messages.",
            ("hud", "TYPEI") => "Configure the software-renderer HUD viewport layout.",
            ("hud", "TYPEII") => "Configure the hardware-renderer HUD viewport and image layout.",
            ("ui", "FONTS") => "Define numbered font styles used by this dialog's widgets.",
            ("ui", "ROLLOVER") => "Configure the button's presentation when the pointer is over it.",
            ("startanims", _) => "Restart the listed animations when entering the mission in single-player mode.",
            _ => null
        };
        string? arguments = key switch
        {
            ("player", "mouse_push") => "Horizontal response, vertical response",
            ("player", "fp_cam_el_lim") => "Minimum elevation, maximum elevation (radians)",
            ("player", "underwater_cam") => "Distance, height, integer step count, far distance, integer red/green/blue (0–255), alpha",
            ("player", "low_shield_snd") => "Warning sample, interval in seconds, looping sample",
            ("player", "max_cam_tether_angle") => "Angle in degrees",
            ("player", "max_slope") => "Slope comparison threshold (default 0.707)",
            ("player", "burning_anim") => "Animation name; trailing authored values are not read here",
            ("vehicle", "health") => "Single-player health, multiplayer health (second value used for player-controlled types)",
            ("vehicle", "rates") => "Acceleration, maximum speed",
            ("vehicle", "turns") => "Yaw acceleration, maximum yaw rate (radians)",
            ("vehicle", "rate_damping") => "Acceleration damping, deceleration damping",
            ("vehicle", "collision_d") => "Two collision damping coefficients",
            ("vehicle", "mode_alt") => "Transition time in seconds",
            ("vehicle", "mode") => "Mode name: basic, track, hover, amphib, sub or fly",
            ("weapons", "BEAM") => "Reciprocal velocity/timing float, interpolation-rate float, integer low-bit flag",
            ("weapons", "FIRE_RATE") => "Shots per second",
            ("weapons", "LOCK_ON") => "Lock-on time in seconds",
            ("weapons", "IMPACT_TYPE") => "Integer damage-mask slot index",
            ("weather", "TYPE") => "Scalar RAIN or SNOW (without an enclosing value array)",
            ("weather", "PARTICLES") => "Scalar integer particle count",
            ("weather", "WIND_DIR") => "Scalar direction value passed to the weather renderer",
            ("weather", "WIND_VEL") => "Scalar wind speed",
            ("weather", "GRAVITY") => "Scalar gravity value",
            ("weather", "COLOR") => "Red, green, blue integers",
            ("weather", "ALPHA_GRADIENT") => "Start alpha, end alpha",
            ("objectives", "ACTIVE") or ("objectives", "INACTIVE") => "Root node followed by optional child names",
            ("objectives", "READ_SOUND") => "Sound sample, optional float delay in seconds",
            ("sounds", "HIGH") or ("sounds", "MED") or ("sounds", "LOW") => "Array: sample rate in Hz, bits per sample, channel count; or a bare filename string",
            ("effects", "MAPS") => "Texture names in playback order",
            ("effects", "EFFECTS") => "Nested records: model node name followed by NAME, SPEED, MAPS and optional LOOPING",
            ("turrets", "PARTS") => "Barrel and firepoint; or base, barrel, firepoint; or base, barrel, two firepoints",
            ("turrets", "FIRE_RATE") => "Interval in seconds",
            ("ui", "POSITION") => "X and Y integer pixel coordinates",
            ("ui", "BITMAP") => "Image name, optional integer X and Y offsets",
            ("ui", "FONTS") => "Nested font-style records: integer ID, family, integer size, RGB tuple (or foreground/background RGB tuples), optional shadow flag, weight and LEFT/RIGHT/CENTER alignment",
            ("hud", "RETICULE") => "Range image, off image, on image",
            ("startanims", _) => "Nested records whose first value is an animation name",
            _ => null
        };
        string? notes = key switch
        {
            ("player", "camera_zone") => "Default: 0.9. Only values strictly between 0 and 1 replace the default; other values are ignored.",
            ("player", "max_cam_yaw_rate") => "Default: 2.0.",
            ("player", "mouse_push") => "Defaults: 0.002 horizontal, 0.01 vertical.",
            ("player", "fp_cam_el_rate") => "Default: 5.0.",
            ("player", "fp_cam_el_lim") => "Defaults: -0.75 and 1.0 radians.",
            ("player", "underwater_cam") => "Defaults: distance 5, height 4, 12 steps, far distance 100, alpha 0.5.",
            ("player", "max_cam_tether_angle") => "The loader converts degrees to radians.",
            ("player", "nom_gravity") => "Default: 28.0.",
            ("player", "wat_gravity") => "Default: one third of nominal gravity.",
            ("player", "qsd_gravity") => "Default: one sixth of nominal gravity.",
            ("player", "qsand_sink") => "Default: 0.9.",
            ("player", "lava_sink") => "Default: 0.6.",
            ("vehicle", "rates") => "Defaults: acceleration 10, maximum speed 30. These are not forward/reverse speeds.",
            ("vehicle", "turns") => "Defaults: yaw acceleration 0.6, maximum yaw rate 2.0.",
            ("vehicle", "turn_damping") => "Default: 30.0.",
            ("vehicle", "rate_damping") => "Both defaults are 30.0.",
            ("vehicle", "collision_d") => "Defaults: 0.5 and 0.15. The exact artistic interpretation of each coefficient is not established.",
            ("vehicle", "mode_alt") => "Default: 2.0 seconds.",
            ("vehicle", "mode") => "The loader dispatches on the first two letters; suggestions list the established full spellings.",
            ("vehicle", "position") or ("vehicle", "rotation") => "Use the aiv.zrd placement record to position a vehicle. This field is preserved for inspection but is not suggested for new authoring.",
            ("network", "path_width") => "Default: 10. The recorded retail data-flow audit found no reader of the resulting probe data.",
            ("network", "activate_buddy") => "Its own stored value is unused. Omitting this field also zeroes attack_buddy in the retail loader.",
            ("network", "attack_buddy") => "A nonzero flag allows an attacking or hit vehicle to alert active peers in its network. Missing activate_buddy clears this flag.",
            ("network", "attack_range") => "The supported pursuit-distance fields are pursuit_params or pursuit_range. attack_range is preserved but is not suggested or type-validated as an effective field.",
            ("network", "type") => "The first two letters select standard, hide, fixed or defilade; unrecognized values fall back to standard.",
            ("weapons", "AMMO_LIMIT") => "Default: 50. Stored as an integer before conversion to the weapon's float capacity.",
            ("weapons", "DAMAGE") => "Default: 0.1.",
            ("weapons", "RANGE") => "Default: 500. The loader also stores its square.",
            ("weapons", "VELOCITY") => "Default: 120. A BEAM block also sets the velocity field through its reciprocal timing value.",
            ("weapons", "GRAVITY") => "The loader applies this float before processing LOCK_ON; do not assume this field alone determines every projectile's runtime motion.",
            ("weapons", "FIRE_RATE") or ("weapons", "BEAM") => "A zero reciprocal divisor produces a nonfinite value and is diagnosed.",
            ("weapons", "NAME") => "Defaults to the weapon's BALLISTICS key when absent.",
            ("weapons", "KILL_VERB") => "The optional weapon catalog resolves this message key. When absent it uses localized message 0x250.",
            ("weapons", "DESC") or ("weapons", "MILITARY_NAME") => "The loader resolves a message key with a fallback. When absent it uses the weapon's BALLISTICS key.",
            ("weapons", "CATCHES_FIRE") => "The spelling is historical. The reconstructed runtime calls this the immediate-probe-impact flag; a broader fire-related effect is not established by the loader alone.",
            ("weather", "PARTICLES") => "Default: 100.",
            ("weather", "TYPE") => "Only exact RAIN and SNOW create an emitter. Other fields apply only after an emitter is created.",
            ("objectives", "READ_TIME") or ("objectives", "REVIEW_DELAY") => "Default: 4 seconds. The loader reads stored integers, so 4.0 is not equivalent to 4.",
            ("objectives", "ACTIVE") or ("objectives", "INACTIVE") => "ACTIVE takes precedence when both fields exist. An unresolved path never completes the objective.",
            ("objectives", "FINAL_MISSION") => "Presence flag. Retail mission flow ends the game on the fifth objective completion when this is set.",
            ("objectives", "AUTOPLAY") => "Presence flag, commonly written AUTOPLAY ( ).",
            ("sounds", "HIGH") => "Default: 44100 Hz, 16 bits, 2 channels. A scalar filename selects a named variant instead.",
            ("sounds", "SYNTAX") => "1 selects legacy positional sets (the default); 2 selects named samples with tagged settings. Other values load neither syntax.",
            ("sounds", "SETS") => "With SYNTAX 2, each named set contains anonymous sample records beginning with sample ID and resource filename, followed by optional settings.",
            ("sounds", "MED") => "Default: 22050 Hz, 16 bits, 1 channel. A scalar filename selects a named variant instead.",
            ("sounds", "LOW") => "Default: 11025 Hz, 8 bits, 1 channel. A scalar filename selects a named variant instead.",
            ("effects", "SPEED") => "Default: 0. The zReader numeric accessor accepts an integer or float here.",
            ("effects", "NAME") => "The logical template name referenced by animation EFFECT events; the record's leading scalar separately names its model node.",
            ("effects", "LOOPING") => "ON enables looping. Missing or other values leave the cycle non-looping.",
            ("turrets", "HEALTH") => "Default: 100.",
            ("turrets", "AMMO") => "Default: 50.",
            ("turrets", "DAMAGE_MODIFIER") => "Default: 1.0.",
            ("turrets", "DETECTION_RANGE") => "Default: 200.",
            ("turrets", "FIRE_RATE") => "Default: 1.0. Unlike weapon BALLISTICS/FIRE_RATE, this value is not inverted by its loader.",
            ("turrets", "NAME") => "Required inside WEAPON; an absent or unresolved weapon can crash the game.",
            ("turrets", "DESTROY_ANIM") => "An explicit per-turret value wins over an animation named for the turret, which wins over the root default.",
            ("turrets", "PARTS") => "Parts bind only when a healthy node is found. An empty PARTS block binds no parts and occurs in shipped data.",
            ("fonts", "FONTS") => "This source can be inspected, but it is not the HUD/dialog FONTS schema.",
            ("startanims", _) => "Missing animations are skipped. NEW_GAME_START and LOAD_GAME_START apply to different mission-entry paths; neither changes multiplayer startup.",
            _ => null
        };
        bool suggested = key is not (("vehicle", "position" or "rotation") or ("network", "attack_range") or ("hud", "VERSION"));
        return field with { Description = description ?? field.Description, Arguments = arguments, Notes = notes, Suggested = suggested,
            Shape = key == ("network", "attack_range") ? "" : field.Shape,
            Choices = key == ("vehicle", "mode") ? ["basic", "track", "hover", "amphib", "sub", "fly"] : field.Choices };
    }
}
