using zStudio.Core.Animation;

namespace zStudio.Core.Language;

public static partial class RecoilLanguage
{
    internal static string[] Ancestors(LanguageRecord? record)
    {
        List<string> path = [];
        for (; record != null; record = record.Parent) path.Add(record.Key);
        path.Reverse(); return path.ToArray();
    }
    internal static IEnumerable<LanguageField> Fields(string family, string parent, IReadOnlyList<string>? context = null)
    {
        if (family != "animation") return ResourceContext(family, parent, context ?? (parent.Length == 0 ? [] : [parent]));
        IEnumerable<string> names = parent switch
        {
            "" => ["ANIMATION_DEFINITIONS"],
            "ANIMATION_DEFINITIONS" => ["GRAVITY", "ANIMATION_PATH", "ANIMATION_LIST"],
            "ANIMATION_LIST" when context?.Contains("ACTIVATION_PREREQUISITE") != true => ["ANIMATION_DEFINITION_FILE", "ANIMATION_DEFINITION"],
            "ANIMATION_DEFINITION" => AnimationSourceFields.Settings.Concat(["NAME", "ANIMATION_NAME", "ANIMATION_ROOT_NAME", "ACTIVATION_PREREQUISITE", "RESET_STATE", "SEQUENCE_DEFINITION"]),
            "SEQUENCE_DEFINITION" => AnimationSourceFields.EventFields.Keys.Concat(["NAME", "ACTIVATION", "START_TIME"]),
            "RESET_STATE" => AnimationSourceFields.EventFields.Keys,
            "ACTIVATION_PREREQUISITE" => ["REQUIRED", "OPTIONS", "ANIMATION_LIST", "OBJECT_ACTIVE_LIST", "MINIMUM_TO_SATISFY"],
            "REQUIRED" or "OPTIONS" => ["ANIMATION_LIST", "OBJECT_ACTIVE_LIST", "MINIMUM_TO_SATISFY"],
            "BOUNCE_SOUND" => ["NAME", "FULL_VOLUME_VELOCITY"],
            // ANIM_VERBOSE is a scalar switch. Other compiled events share the runtime timing header.
            "ANIM_VERBOSE" => [],
            _ => AnimationSourceFields.EventFields.TryGetValue(parent, out string? fields) ? fields.Split(' ', StringSplitOptions.RemoveEmptyEntries).Concat(["START_TIME"]) : []
        };
        return names.Distinct(StringComparer.Ordinal).Select(n => AnimationField(n, parent, context));
    }
    internal static LanguageField? Field(string family, string name, string parent, IReadOnlyList<string>? context = null) => Fields(family, parent, context).FirstOrDefault(f => f.Name == name);
    private static LanguageField AnimationField(string name, string parent, IReadOnlyList<string>? context)
    {
        bool prerequisite = context?.Contains("ACTIVATION_PREREQUISITE") == true;
        if (AnimationSourceFields.EventFields.ContainsKey(name))
        {
            string summary = name switch
            {
                "SOUND" => "Play a named sound sample, optionally at a node and offset.",
                "SOUND_NODE" => "Create or update a named sound node and its active state or attachment.",
                "EFFECT" => "Spawn a named effect template, optionally at a node and offset.",
                "LIGHT_STATE" => "Create or update a named light's active state, attachment, range and color.",
                "LIGHT_ANIMATION" => "Animate a named light's inner and outer range using rates and accelerations.",
                "OBJECT_ACTIVE_STATE" => "Set a scene node active or inactive.",
                "OBJECT_TRANSLATE_STATE" => "Set a node's translation, add a relative displacement, or place it at another node.",
                "OBJECT_SCALE_STATE" => "Set a node's XYZ scale immediately.",
                "OBJECT_ROTATE_STATE" => "Set a node's XYZ rotation or copy orientation from another node.",
                "OBJECT_MOTION" => "Move a node procedurally using velocity, acceleration, gravity, rotation or scale rates.",
                "OBJECT_MOTION_FROM_TO" => "Interpolate translation, rotation, scale or morph endpoints over time.",
                "OBJECT_MOTION_SI_SCRIPT" => "Play the named node's keyframe track from an SI script at the supplied frame rate.",
                "OBJECT_OPACITY_STATE" => "Set a node's alpha override flag and optionally its opacity.",
                "OBJECT_OPACITY_FROM_TO" => "Interpolate node opacity and optionally change alpha override at each endpoint.",
                "OBJECT_ADD_CHILD" => "Attach an existing child node to the specified parent.",
                "OBJECT_DELETE_CHILD" => "Detach the specified child from its parent.",
                "OBJECT_CYCLE_TEXTURE" => "Advance the node's texture-cycle frame, or reset it when RESET is present.",
                "OBJECT_CONNECTOR" => "Pose and stretch a connector object between its configured endpoints.",
                "CALL_OBJECT_CONNECTOR" => "Launch a named animation with connector endpoint inputs.",
                "CAMERA_STATE" => "Set the named camera's clipping distances and zoom factors.",
                "CAMERA_FROM_TO" => "Interpolate the named camera's horizontal and vertical zoom factors.",
                "CALL_SEQUENCE" => "Release a waiting ON_CALL sequence in this animation.",
                "STOP_SEQUENCE" => "Stop the named sequence in this animation.",
                "CALL_ANIMATION" => "Launch a named animation with optional placement, binding and child-lifetime controls.",
                "STOP_ANIMATION" => "Request that the named animation stop.",
                "RESET_ANIMATION" => "Reset the named animation through its stop and cleanup path.",
                "INVALIDATE_ANIMATION" => "Request that the named animation finish so its loops can exit.",
                "FOG_STATE" => "Change fog enable state, color, altitude band or distance range.",
                "LOOP" => "Repeat this sequence, limited by iteration count or elapsed time.",
                "IF" => "Begin a conditional branch using LOD, player range, random weight or node cover.",
                "ELSEIF" => "Test the next branch when previous branches in this conditional chain failed.",
                "ELSE" => "Run the fallback branch when no preceding branch matched.",
                "ENDIF" => "End the current conditional chain.",
                "CALLBACK" => "Call the engine/game event identified by VALUE through this animation's registered callback.",
                "FBFX_COLOR_FROM_TO" => "Interpolate a full-screen color overlay's red, green, blue and alpha values.",
                "FBFX_CSINWAVE_FROM_TO" => "Animate the radial cosine-wave screen overlay and its position or radius.",
                "ANIM_VERBOSE" => "Store an animation verbosity marker; the engine handler performs no action.",
                _ => throw new InvalidOperationException("Missing animation vocabulary description: " + name)
            };
            return new(name, summary, name == "ANIM_VERBOSE" ? "s" : "", name == "ANIM_VERBOSE" ? ["ON", "OFF"] : null,
                Notes: name switch
                {
                    "CALLBACK" => "VALUE is an integer event ID interpreted by the registered handler. Without a handler the event has no effect. START_TIME is supported. zStudio preview records the callback without executing gameplay code.",
                    "IF" or "ELSEIF" => "Only the first available condition is compiled, in this order: ANIMATION_LOD, PLAYER_RANGE, RANDOM_WEIGHT, NODE_UNDERCOVER.",
                    "LOOP" => "LOOP_COUNT takes precedence over LOOP_TIME. Without either, the loop repeats indefinitely. A bare EVENT_OFFSET field is ignored; put it inside START_TIME.",
                    "OBJECT_MOTION_FROM_TO" => "A channel is enabled when either endpoint is supplied; an absent endpoint is zero. Omitted or nonpositive RUN_TIME uses a 0.001-second interval in the compiler.",
                    "ANIM_VERBOSE" => "Use the scalar form ANIM_VERBOSE ( ON ). This marker has no effect; no nested VALUE or timing fields are suggested.",
                    _ => null
                }, Example: name == "CALLBACK" ? "CALLBACK ( START_TIME ( ANIMATION_OFFSET 0.5 ) VALUE ( 25 ) )" : null, Suggested: name != "ANIM_VERBOSE", UsageWarning: name == "ANIM_VERBOSE" ? "ANIM_VERBOSE has no effect during playback." : null, ValidateValues: name != "ANIM_VERBOSE");
        }
        string? reference = name switch
        {
            "SCRIPT_FILENAME" or "ANIMATION_DEFINITION_FILE" => "file",
            "ANIMATION_LIST" when prerequisite => "animation",
            "NAME" when parent is "CALL_ANIMATION" or "CALL_OBJECT_CONNECTOR" or "STOP_ANIMATION" or "RESET_ANIMATION" or "INVALIDATE_ANIMATION" => "animation",
            "BOUNCE_SEQUENCE" => "sequence", "NAME" when parent is "CALL_SEQUENCE" or "STOP_SEQUENCE" => "sequence",
            "NAME" when parent is "SOUND" or "SOUND_NODE" or "BOUNCE_SOUND" => "sound", "NAME" when parent == "EFFECT" => "effect", _ => null
        };
        var field = AnimationOperand(name, parent, prerequisite) with { Reference = reference };
        if (name == "ACTIVATION" && parent == "SEQUENCE_DEFINITION") return field with { Choices = ["ON_CALL"], Notes = "ON_CALL waits for CALL_SEQUENCE; other activation values are ignored. Without ON_CALL the sequence runs when its animation starts." };
        if (name == "START_TIME" && parent == "SEQUENCE_DEFINITION") return field with { Suggested = false, UsageWarning = "START_TIME is not used on a sequence. Put it on an event.", ValidateValues = false, Description = "Ignored when placed directly on a sequence.", Notes = "Put START_TIME on an event inside the sequence to delay that event." };
        return field;
    }

    private static LanguageField AnimationOperand(string name, string parent, bool prerequisite)
    {
        LanguageField Operand(string summary, string shape = "", string? arguments = null, string? notes = null, string? example = null, string[]? choices = null) =>
            new(name, summary, shape, choices, Arguments: arguments, Notes: notes, Example: example);
        return name switch
        {
            "ANIMATION_DEFINITIONS" => Operand("Container for animation search paths, shared gravity and the definition list."),
            "ANIMATION_LIST" when prerequisite => Operand("Animation names that must satisfy this prerequisite group.", "s? ...", "Zero or more animation names", "These reference existing animations; this is not a definition/include list."),
            "ANIMATION_LIST" => Operand("Ordered inline definitions and included definition files."),
            "ANIMATION_DEFINITION" => Operand("Define an animation bound to one or more named scene roots."),
            "SEQUENCE_DEFINITION" => Operand("An ordered event stream belonging to this animation.", notes: "Sequences run alongside one another. Within a sequence, an earlier event can delay the next event despite its START_TIME."),
            "RESET_STATE" => Operand("Cleanup event stream used by the animation's stop/reset lifecycle.", notes: "This is not a second normal playback sequence. RESET_TIME controls restoration and cleanup behavior."),
            "ANIMATION_DEFINITION_FILE" => Operand("Include definitions from a file resolved through the animation search path.", "s", "Definition filename"),
            "ANIMATION_PATH" => Operand("Set semicolon-separated folders searched for animation definitions and keyframe scripts.", "s", "Search folders in compiler order"),
            "ANIMATION_NAME" => Operand("Set the logical name used to call this animation.", "s", "Animation name", "Without this field, each NAME root supplies its own entry name."),
            "ANIMATION_ROOT_NAME" => Operand("Set the attachment/callback node for this animation entry.", "s", "Attachment node name", "Defaults to the bound NAME root. This affects activation callbacks and node lookup."),
            "NAME" when parent == "ANIMATION_DEFINITION" => Operand("Scene roots to which this definition is bound.", "s ...", "One or more root names or decimal-wildcard patterns", "Each matching root creates an entry. Each * matches one decimal digit."),
            "NAME" when parent == "SEQUENCE_DEFINITION" => Operand("Name this sequence for CALL_SEQUENCE and STOP_SEQUENCE.", "s ...", "Sequence name", "If repeated, the last NAME record wins; the first scalar of that record supplies the name."),
            "NAME" => Operand(parent switch { "SOUND" or "BOUNCE_SOUND" => "Sound sample name to play.", "SOUND_NODE" => "Sound sample and sound-node name.", "EFFECT" => "Effect template name from effects.zrd.", "LIGHT_STATE" or "LIGHT_ANIMATION" => "Name of this animation's light node.", "CALL_SEQUENCE" or "STOP_SEQUENCE" => "Sequence name within the current animation.", "CALL_ANIMATION" or "STOP_ANIMATION" or "RESET_ANIMATION" or "INVALIDATE_ANIMATION" or "CALL_OBJECT_CONNECTOR" => "Logical animation name to control.", _ => "Scene node affected by this event." }, "s", "Name"),
            "START_TIME" => Operand("Delay event eligibility until the selected clock reaches the threshold.", "s n", "Clock mode, offset in seconds", "ANIMATION_OFFSET uses animation elapsed time; SEQUENCE_OFFSET uses sequence elapsed time; EVENT_OFFSET uses time since the preceding event. Earlier events, waits and conditions can delay actual dispatch. Default: ANIMATION_OFFSET 0.", choices: ["ANIMATION_OFFSET", "SEQUENCE_OFFSET", "EVENT_OFFSET"]),
            "ACTIVATION" => Operand("Choose how the animation becomes eligible to start.", "s", "Activation mode", "Animation default: WEAPON_HIT. Hit modes also use HEALTH and reset state.", choices: AnimationSourceFields.Options["ACTIVATION"]),
            "HEALTH" => Operand("Set the hit-trigger countdown used by hit activation modes.", "n", "Damage threshold", "The compiler initializes the countdown to this value; reset/stop replenishes it. Default: 0."),
            "RESET_TIME" => Operand("Control the delay before stop cleanup and rest-pose restoration.", "n n?", "Delay in seconds, optional second numeric marker", "A negative delay preserves the final pose. Presence of a second value changes the load-time cleanup flag; its numeric value is not stored."),
            "EXECUTION_BY_RANGE" => Operand("Gate execution by distance from the local player to the callback node.", "n", "Maximum distance in world units", "The engine compares squared 3D distance. This is a runtime gate after activation."),
            "EXECUTION_BY_ZONE" => Operand("Enable the animation's zone execution gate.", notes: "Presence flag; its values are not read. Zone gating is not simulated by zStudio."),
            "EXECUTION_PRIORITY" => Operand("Choose the node-callback priority bucket.", "n", "Priority integer, 0–255", "Default: 4. Game systems and other animations can overwrite a node later in the same frame."),
            "SAVE_LOG" => Operand("Control whether this animation participates in saved animation logging.", "s", "ON or OFF", "OFF sets the no-save-log flag; otherwise logging remains enabled.", choices: ["ON", "OFF"]),
            "NETWORK_LOG" => Operand("Control whether this animation participates in network animation logging.", "s", "ON or OFF", "OFF sets the no-network-log flag; otherwise logging remains enabled.", choices: ["ON", "OFF"]),
            "ACTIVATION_PREREQUISITE" => Operand("Conditions checked before the animation can activate.", notes: "Use REQUIRED and OPTIONS groups, or the supported flat form. When either named group exists, only named groups are compiled."),
            "REQUIRED" => Operand("Prerequisites that must all be satisfied."),
            "OPTIONS" => Operand("Alternative prerequisites counted toward MINIMUM_TO_SATISFY."),
            "MINIMUM_TO_SATISFY" => Operand("Set the minimum number of optional prerequisites required.", "n", "Count integer, 0–255"),
            "OBJECT_ACTIVE_LIST" => Operand("List node paths whose final nodes must be active.", arguments: "Nested paths: ( root child ... )"),
            "RUN_TIME" => Operand("Set this event's running or interpolation duration.", "n", "Seconds", parent == "OBJECT_MOTION" ? "When absent, procedural motion has no time limit; other completion conditions may still stop it." : "Default: 0. Zero-time interpolation behavior depends on this event; it is not a universal delay."),
            "SCRIPT_FILENAME" => Operand("SI keyframe script containing the NAME node's track.", "s", "Script filename, normally .zan"),
            "SCRIPT_FRAME_RATE" => Operand("Convert keyframe frame numbers to playback time.", "n", "Frames per second", "Default: 30. Keyframe tracks remain read-only in this editor version."),
            "VALUE" => Operand("Integer event ID sent to the animation's registered game callback.", "n", "Event ID", "The shared compiler converts numeric source values to a signed 32-bit integer, truncating fractional values. Missing VALUE compiles as 0. Known ID suggestions describe Player::AsyncCommandCallback; other handlers can interpret the same ID differently."),
            "GRAVITY" when parent == "ANIMATION_DEFINITIONS" => Operand("Set shared gravity used by procedural motion events.", "n", "Gravity magnitude"),
            "GRAVITY" => Operand("Enable gravity for this procedural motion.", "s n?", "GLOBAL, or LOCAL followed by a magnitude", "LOCAL uses the following value; other source tokens use the shared animation gravity.", choices: ["GLOBAL", "LOCAL"]),
            "TRANSLATION" => Operand("Set the launch direction, initial speed and acceleration.", "n n n? n?", "Yaw degrees, pitch degrees, optional speed, optional acceleration", "Speed and acceleration default to zero; the first two values are angles, not XYZ coordinates."),
            "TRANSLATION_RANGE_MIN" or "TRANSLATION_RANGE_MAX" => Operand("Bound the random launch yaw, pitch, speed and acceleration.", "n n n n", "Yaw degrees, pitch degrees, speed, acceleration", "Both MIN and MAX records are needed to enable random launch parameters."),
            "XYZ_ROTATION" => Operand("Set angular velocity and optional angular acceleration.", "n n n n? n? n?", "XYZ degrees per second, optionally XYZ degrees per second squared", "The acceleration triple is used only when all six values are supplied."),
            "SCALE" => Operand("Set XYZ scale rates and optional scale accelerations.", "n n n n? n? n?", "XYZ rates, optionally XYZ accelerations", "The acceleration triple is used only when all six values are supplied."),
            "FORWARD_ROTATION" => Operand("Orient the moving object along its motion direction.", "x n n", "Ignored marker (usually TIME), pitch offset degrees, roll offset degrees", "The compiler reads the second and third values as angular offsets; the first value is not interpreted."),
            "BOUNCE_SEQUENCE" => Operand("Sequence to invoke when procedural motion hits a surface.", "s", "Sequence name"),
            "BOUNCE_SOUND" => Operand("Sound sample and volume-speed settings for a motion impact."),
            "FULL_VOLUME_VELOCITY" => Operand("Impact speed used as the full-volume reference for the bounce sound.", "n", "Speed"),
            "MORPH_FROM" or "MORPH_TO" => Operand("Morph interpolation endpoint.", "n", "Morph value"),
            "TRANSLATE_FROM" or "TRANSLATE_TO" => Operand("Translation interpolation endpoint.", "n n n", "X, Y, Z"),
            "ROTATE_FROM" or "ROTATE_TO" => Operand("Rotation interpolation endpoint, converted to radians by the compiler.", "n n n", "X, Y, Z angles in degrees"),
            "SCALE_FROM" or "SCALE_TO" => Operand("Scale interpolation endpoint.", "n n n", "X, Y, Z scale factors"),
            "OPACITY_FROM" or "OPACITY_TO" => Operand("Opacity interpolation endpoint and optional alpha-override switch.", "n s?", "Opacity, optional ON or OFF", "Without the switch, the current alpha override is retained."),
            "STATE" when parent == "OBJECT_ACTIVE_STATE" => Operand("Enable or disable the target node.", "s", "ACTIVE or INACTIVE", choices: ["ACTIVE", "INACTIVE"]),
            "STATE" when parent == "OBJECT_OPACITY_STATE" => Operand("Set alpha override, optionally supplying opacity.", "s n?", "ON or OFF, optional opacity", choices: ["ON", "OFF"]),
            "STATE" => Operand(parent == "OBJECT_ROTATE_STATE" ? "Absolute XYZ rotation in degrees." : parent == "OBJECT_SCALE_STATE" ? "Absolute XYZ scale factors." : "Absolute XYZ translation.", "n n n", "X, Y, Z"),
            "RELATIVE" => Operand("Add this XYZ displacement to the target's current translation.", "n n n", "X, Y, Z displacement", "Overrides STATE when both are present."),
            "AT_NODE_XYZ" => Operand("Copy orientation from the named node.", "s", "Node name", "Overrides the explicit rotation STATE."),
            "AT_NODE" => Operand("Use a named node as the event's positional reference.", parent == "CALL_ANIMATION" ? "s n? n? n? n? n? n?" : parent == "FBFX_CSINWAVE_FROM_TO" ? "s" : "s n? n? n?", parent == "CALL_ANIMATION" ? "Node, optional XYZ offset, optional XYZ rotation in degrees" : "Node, optional XYZ offset (where supported)", "Offsets are used only as complete triples."),
            "WITH_NODE" => Operand("Use this node as the launched animation's positional reference.", "s", "Position-reference node name", "Takes precedence over AT_NODE's positional reference when both are supplied. Use OPERAND_NODE to bind the animation's recipient."),
            "OPERAND_NODE" => Operand("Bind the launched animation to this recipient and supply its INPUT_NODE reference.", "s", "Recipient node name", "AT_NODE and WITH_NODE separately determine placement."),
            "LOCAL_NAME" => Operand("Retain the launched child under a local name for lifetime management.", "s", "Local child name", "Named children are stopped when their launcher is cleaned up."),
            "WAIT_FOR_COMPLETION" => Operand("Wait for this launched child animation to complete before advancing.", notes: "Presence flag. Retains the child instance even without LOCAL_NAME."),
            "PARENT_CHILD" => Operand("Identify the parent and child for an attach/detach operation.", "s s", "Parent node, child node"),
            "RESET" => Operand("Reset the texture-cycle frame instead of advancing it.", notes: "Presence flag; no values are needed."),
            "ACTIVE_STATE" => Operand("Enable or disable this sound or light node.", "s", "ACTIVE or INACTIVE", "Default: ACTIVE.", choices: ["ACTIVE", "INACTIVE"]),
            "COLOR" => Operand("Set RGB color components.", "n n n", "Red, green, blue"),
            "SATURATED" => Operand("Set the light's saturation flag.", "s", "TRUE or FALSE", "The differently spelled SATURATION field is ignored.", choices: ["TRUE", "FALSE"]),
            "RANGE" => Operand(parent == "LIGHT_ANIMATION" ? "Change the light's inner and outer ranges using velocity and acceleration." : parent == "FOG_STATE" ? "Set the near and far fog distances." : "Set the light's inner and outer ranges.", parent == "LIGHT_ANIMATION" ? "n n n n" : "n n", parent == "LIGHT_ANIMATION" ? "Inner rate, outer rate (distance per second), inner acceleration, outer acceleration (distance per second squared)" : "Near/inner distance, far/outer distance"),
            "TYPE" => Operand("Enable or disable fog.", "s", "OFF disables; other tokens enable", choices: ["OFF", "ON"]),
            "ALTITUDE" => Operand("Set the lower and upper fog altitude values.", "n n", "Lower altitude, upper altitude"),
            "NEAR_CLIP" or "FAR_CLIP" => Operand("Set this camera's clipping distance.", "n", "Distance"),
            "H_ZOOM" or "V_ZOOM" => Operand("Set this camera's zoom factor on the selected axis.", "n", "Zoom factor"),
            "H_ZOOM_FROM_TO" or "V_ZOOM_FROM_TO" => Operand("Interpolate this camera's zoom factor on the selected axis.", "n n", "Start factor, end factor"),
            "LOOP_COUNT" => Operand("Limit the loop's iteration count.", "n", "Count; negative means indefinitely", "Counts up to 65535 are representable; 65535 is the engine's indefinite sentinel. Takes precedence over LOOP_TIME."),
            "LOOP_TIME" => Operand("Limit looping by elapsed time.", "n", "Seconds", "Used only when LOOP_COUNT is absent."),
            "ANIMATION_LOD" => Operand("Branch according to the animation detail level.", "s", "HIGH, MEDIUM or LOW", choices: ["HIGH", "MEDIUM", "LOW"]),
            "PLAYER_RANGE" => Operand("Branch according to distance from the conditional reference position.", "n", "Range in world units"),
            "RANDOM_WEIGHT" => Operand("Supply the random branch weight.", "n", "Weight"),
            "NODE_UNDERCOVER" => Operand("Test whether a node is covered using the supplied vertical probe range.", "s n", "Node name, probe distance"),
            "FROM_NODE" or "TO_NODE" => Operand("Use this node as a connector endpoint.", "s", "Node name"),
            "FROM_NODE_POS" => Operand("Supply the source node's position as the connector input.", "s", "Node name"),
            "FROM_INPUT_NODE" or "TO_INPUT_NODE" => Operand("Use the corresponding node supplied by the connector caller.", notes: "Presence flag."),
            "FROM_INPUT_POS" or "TO_INPUT_POS" or "TO_INPUT_NODE_POS" => Operand("Use the corresponding position supplied by the connector caller.", notes: "Presence flag."),
            "FROM_POS" or "TO_POS" => Operand("Set the corresponding connector endpoint position.", "n n n", "X, Y, Z"),
            "FROM_T_START" or "FROM_T_END" or "TO_T_START" or "TO_T_END" or "TO_T" => Operand("Set the connector endpoint's interpolation parameter along the connecting segment.", "n", "Segment parameter", name == "TO_T" ? "Sets both target start and end to the same parameter." : "The endpoint changes between its start and end parameters over RUN_TIME."),
            "MAX_LENGTH" => Operand("Limit the connector's length.", "n", "Maximum length"),
            "FROM" or "TO" => Operand("Full-screen overlay color endpoint.", "n n n n", "Red, green, blue, alpha"),
            "SCREEN_POS_FROM" or "SCREEN_POS_TO" => Operand("Screen-space center of the radial overlay.", "n n", "X, Y", "The FROM field enables this channel; an omitted TO repeats FROM."),
            "SCREEN_RADIUS_FROM" or "SCREEN_RADIUS_TO" => Operand("Screen-space radius of the radial overlay.", "n", "Radius", "The FROM field enables this channel; an omitted TO repeats FROM."),
            "WORLD_RADIUS_FROM" or "WORLD_RADIUS_TO" => Operand("World-space radius used by the radial overlay.", "n", "Radius", "The FROM field enables this channel; an omitted TO repeats FROM."),
            "CSIN_FROM" or "CSIN_TO" => Operand("Cosine-wave overlay parameter endpoint.", "n n n", "Three wave parameters", "The FROM field enables this channel; an omitted TO repeats FROM. The precise artistic units of the three parameters are not established."),
            _ => throw new InvalidOperationException("Missing animation operand description: " + name)
        };
    }
}
