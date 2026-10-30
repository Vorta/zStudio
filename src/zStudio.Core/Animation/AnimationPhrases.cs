using System.Globalization;
using System.Numerics;

namespace zStudio.Core.Animation;

/// <summary>The colour family an event row, lane or mark uses (design: state, motion, camera, sound, effect, call, release, stop, flow, game).</summary>
public enum AnimationEventFamily { State, Motion, Camera, Sound, Effect, Call, Release, Stop, Flow, Game }

/// <summary>One game callback value: the row's short text, the full meaning and what the preview does with it.</summary>
public sealed record AnimationCallbackMeaning(int Value, string Short, string Meaning, string Preview);

/// <summary>
/// The animation vocabulary's one phrase per event kind and per callback value (docs/agents/preview.md, "Animation
/// vocabulary"), from the compiled operands. Names come from the entry's own reference tables; nothing is resolved in a scene.
/// </summary>
public static class AnimationPhrases
{
    private static readonly string[] Keywords =
    [
        "", "SOUND", "SOUND_NODE", "EFFECT", "LIGHT_STATE", "LIGHT_ANIMATION", "OBJECT_ACTIVE_STATE", "OBJECT_TRANSLATE_STATE",
        "OBJECT_SCALE_STATE", "OBJECT_ROTATE_STATE", "OBJECT_MOTION", "OBJECT_MOTION_FROM_TO", "OBJECT_MOTION_SI_SCRIPT",
        "OBJECT_OPACITY_STATE", "OBJECT_OPACITY_FROM_TO", "OBJECT_ADD_CHILD", "OBJECT_DELETE_CHILD", "OBJECT_CYCLE_TEXTURE",
        "OBJECT_CONNECTOR", "CALL_OBJECT_CONNECTOR", "CAMERA_STATE", "CAMERA_FROM_TO", "CALL_SEQUENCE", "STOP_SEQUENCE",
        "CALL_ANIMATION", "STOP_ANIMATION", "RESET_ANIMATION", "INVALIDATE_ANIMATION", "FOG_STATE", "", "LOOP", "IF", "ELSE",
        "ELSEIF", "ENDIF", "CALLBACK", "FBFX_COLOR_FROM_TO", "FBFX_CSINWAVE_FROM_TO", "TEXT_MESSAGE", "ANIM_VERBOSE", "MARKER",
    ];

    /// <summary>The source keyword of an event type ("CALL_ANIMATION"); compiled-only kinds get the catalog's name.</summary>
    public static string Keyword(byte type) => type < Keywords.Length && Keywords[type].Length > 0 ? Keywords[type] : $"EVENT_0x{type:X2}";

    public static AnimationEventFamily Family(byte type) => type switch
    {
        1 or 2 => AnimationEventFamily.Sound,
        3 or 4 or 5 or 18 or 28 or 36 or 37 => AnimationEventFamily.Effect,
        10 or 11 or 12 => AnimationEventFamily.Motion,
        20 or 21 => AnimationEventFamily.Camera,
        19 or 24 => AnimationEventFamily.Call,
        22 => AnimationEventFamily.Release,
        23 or 25 or 26 or 27 => AnimationEventFamily.Stop,
        30 or 31 or 32 or 33 or 34 => AnimationEventFamily.Flow,
        35 or 38 or 39 or 40 => AnimationEventFamily.Game,
        _ => AnimationEventFamily.State,
    };

    private static readonly AnimationCallbackMeaning[] Callbacks =
    [
        new(1, "gives the camera back", "Rebinds the game camera to the player; re-aims the chase camera.", "traced"),
        new(2, "takes the camera", "Releases the game camera from the player (single player); HUD, reticle, top and chat lines off.", "traced"),
        new(10, "player takes the tank's pose", "Player position and heading from the tank root; normal control; top and chat lines on.", "moves the player reference"),
        new(11, "suspends the player", "Suspends the player vehicle (single player): no driving or physics, position frozen, engine sound off.", "freezes the player reference"),
        new(14, "takes the controls away", "Single player: HUD, reticle and mission timer off; pauses all turrets.", "traced"),
        new(15, "gives the controls back", "Single player: HUD, reticle centred, weapon name for 5 s, mission timer runs; resumes turrets.", "traced"),
        new(16, "stops the player vehicle", "Velocities and inputs zeroed.", "traced"),
        new(17, "moves the restart point", "Player restart point and pose to the tank root (teleports, elevators, m13 transporters).", "moves the player reference"),
        new(20, "skippable", "Makes the animation skippable: any key resets it (restores and cleans up; ends an opening).", "traced"),
        new(25, "empties nanites, destroys the player", "Empties the nanite reserve, then destroys the player.", "traced"),
        new(26, "destroys the player", "Damage = health + 1.", "traced"),
        new(27, "10 damage to the player", "Deals 10 damage to the player.", "traced"),
        new(99, "mission complete", "Saves state and queues the next mission's film (after the last: credits).", "traced"),
        new(911, "VTOL airdrop", "Drops the next VTOL pickup at the airdrop point if 20 units are clear.", "traced; drop-window analysis"),
        new(912, "spawns a pickup", "Spawns a Lock On Nuke Launcher at vwbus.", "traced"),
        new(913, "spawns a pickup", "Spawns a Nano-Canister at crbox.", "traced"),
        new(914, "spawns a pickup", "Spawns a Lock On Missile Launcher at drop.", "traced"),
    ];

    /// <summary>What a CALLBACK value does in the game; an unknown value does nothing.</summary>
    public static AnimationCallbackMeaning Callback(int value) =>
        Callbacks.FirstOrDefault(c => c.Value == value) ?? new(value, "does nothing", "The game ignores this value.", "traced");

    /// <summary>The threshold badge: "anim ≥ 23 s", "seq ≥ 1.5 s", "prev + 0.1 s"; none for the default animation clock 0.</summary>
    public static string? ThresholdBadge(byte clock, float threshold) => clock switch
    {
        1 when threshold == 0 => null,
        1 => "anim ≥ " + AnimationSeconds.Short(threshold),
        2 => "seq ≥ " + AnimationSeconds.Short(threshold),
        3 => "prev + " + AnimationSeconds.Short(threshold),
        _ => $"clock {clock} ≥ " + AnimationSeconds.Short(threshold),
    };

    /// <summary>The threshold in words: "may start 0.1 s after the previous event completes".</summary>
    public static string ThresholdWords(byte clock, float threshold) => clock switch
    {
        2 => $"may start once its sequence has run {AnimationSeconds.Short(threshold)}",
        3 => $"may start {AnimationSeconds.Short(threshold)} after the previous event completes",
        _ => threshold == 0 ? "may start at once" : $"may start once its animation has run {AnimationSeconds.Short(threshold)}",
    };

    /// <summary>The event's row phrase. <paramref name="script"/> is its keyframe script when the source names one.</summary>
    public static string Event(AnimationEntry entry, AnimationEvent ev, string? script = null)
    {
        if (ev.Bytes.Length < 12) return "Incomplete event";
        if (ev.Spec is not { } spec) return $"Unknown event 0x{ev.Type:X2}";
        if (ev.Bytes.Length < spec.Size) return $"{Keyword(ev.Type)} · incomplete record";
        string Node(int reference) => NodeName(entry, reference);
        switch (ev.Type)
        {
            case 1: return "Sound " + Quote(TableName(entry, 4, ev.I16(12))) + At(entry, ev.I16(14), ev.Vector(16));
            case 2:
                {
                    string at = (ev.U32(48) & 2) != 0 ? At(entry, ev.I32(56), ev.Vector(60)) : "";
                    return (ev.I32(52) == 1 ? "Sound " : "Stop sound ") + Quote(ev.Text(12)) + at;
                }
            case 3: return "Effect " + Quote(TableName(entry, 5, ev.I16(12))) + At(entry, ev.I16(14), ev.Vector(16));
            case 4: return "Light " + Quote(ev.Text(12)) + (ev.I32(52) == 1 ? " on" : " off");
            case 5: return "Animate light " + Quote(ev.Text(12)) + " · " + AnimationSeconds.Short(ev.F32(108));
            case 6: return (ev.I32(12) == 1 ? "Activate " : "Deactivate ") + Quote(Node(ev.I16(16)));
            case 7:
                {
                    string target = Quote(Node(ev.I16(28)));
                    if ((ev.U32(12) & 1) != 0) return $"Move {target} by {Vector(ev.Vector(16))}";
                    return ev.I16(30) != 0 ? $"Place {target} at {Quote(Node(ev.I16(30)))} {Vector(ev.Vector(16))}" : $"Place {target} at {Vector(ev.Vector(16))}";
                }
            case 8: return $"Scale {Quote(Node(ev.I16(24)))} to {Vector(ev.Vector(12))}";
            case 9:
                {
                    string target = Quote(Node(ev.I16(28))); int flags = ev.I32(12) & 7;
                    if (flags is not (0 or 1) && ev.I16(30) != 0) return $"Rotate {target} as {Quote(Node(ev.I16(30)))} + {Degrees(ev.Vector(16))}";
                    return $"Rotate {target} {(flags == 1 ? "by" : "to")} {Degrees(ev.Vector(16))}";
                }
            case 10:
                {
                    uint flags = ev.U32(12); List<string> parts = [];
                    if ((flags & 1) != 0) parts.Add("gravity");
                    if ((flags & 8) != 0) parts.Add("random launch");
                    else if ((flags & 4) != 0) parts.Add("launch");
                    if ((flags & 0x20) != 0) parts.Add("spin");
                    if ((flags & 0x100) != 0) parts.Add("scale");
                    if ((flags & 0x200) != 0) parts.Add("morph");
                    if ((flags & 0x400) != 0) parts.Add(AnimationSeconds.Short(ev.F32(ev.Version == 39 ? 328 : 248)));
                    if ((flags & 0x800) != 0) parts.Add("releases " + Quote(ev.Text(208)) + " on contact");
                    return "Motion " + Quote(Node(ev.I32(16))) + string.Concat(parts.Select(p => " · " + p));
                }
            case 11:
                {
                    uint channels = ev.U32(12); string target = Quote(Node(ev.I32(16))); string time = " · " + AnimationSeconds.Short(ev.F32(140));
                    if (channels == 8) return $"Morph {target} {Number(ev.F32(20))} → {Number(ev.F32(24))}{time}";
                    List<string> verbs = [];
                    if ((channels & 1) != 0) verbs.Add("Move");
                    if ((channels & 2) != 0) verbs.Add(verbs.Count == 0 ? "Turn" : "turn");
                    if ((channels & 4) != 0) verbs.Add(verbs.Count == 0 ? "Scale" : "scale");
                    if ((channels & 8) != 0) verbs.Add(verbs.Count == 0 ? "Morph" : "morph");
                    return (verbs.Count == 0 ? "Animate" : string.Join(" and ", verbs)) + " " + target + time;
                }
            case 12: return "Keyframes " + Quote(Node(ev.I32(12))) + (string.IsNullOrEmpty(script) ? "" : " · " + script);
            case 13:
                return "Set opacity " + Quote(Node(ev.I16(20))) + (ev.I16(14) == 1 ? " " + Number(ev.F32(16)) : "") + (ev.I16(12) == 1 ? " · override on" : " · override off");
            case 14: return $"Fade {Quote(Node(ev.I32(12)))} {Number(ev.F32(20))} → {Number(ev.F32(24))} · {AnimationSeconds.Short(ev.F32(32))}";
            case 15: return $"Attach {Quote(Node(ev.I16(14)))} to {Quote(Node(ev.I16(12)))}";
            case 16: return $"Detach {Quote(Node(ev.I16(14)))} from {Quote(Node(ev.I16(12)))}";
            case 17: return $"Texture frame {ev.I16(18)} on {Quote(Node(ev.I16(16)))}";
            case 18: return $"Beam {Quote(Node(ev.I16(16)))} · {AnimationSeconds.Short(ev.F32(80))}";
            case 19: return $"Call {Quote(ev.Text(16))} between points";
            case 20: return "Camera " + Quote(Node(ev.I32(16)));
            case 21: return $"Move camera {Quote(Node(ev.I32(16)))} · {AnimationSeconds.Short(ev.F32(104))}";
            case 22: return "Release " + Quote(ev.Text(12));
            case 23: return "Stop sequence " + Quote(ev.Text(12));
            case 24:
                {
                    int flags = (ushort)ev.I16(46); string text = "Call " + Quote(ev.Text(12));
                    if ((flags & 8) != 0 && ev.I16(52) != 0) text += " with " + Quote(Node(ev.I16(52)));
                    else if ((flags & 1) != 0 && ev.I16(52) != 0) text += " at " + Quote(Node(ev.I16(52)));
                    if (ev.I16(44) > 0) text += " on " + Quote(Node(ev.I16(44)));
                    if ((flags & 16) != 0) text += " and wait";
                    if (IsLocalChild(entry, ev)) text += " as local child";
                    return text;
                }
            case 25 or 26 or 27:
                {
                    string verb = ev.Type switch { 25 => "Stop animation ", 26 => "Reset animation ", _ => "Invalidate " };
                    return verb + Quote(ev.Text(12)) + (ev.Text(12) == entry.Name ? " · this animation" : "");
                }
            case 28: return (ev.U32(44) & 1) != 0 ? (ev.I32(48) != 0 ? "Fog on" : "Fog off") : "Fog";
            case 30: return "Loop " + LoopText(ev);
            case 31: return "If";
            case 32: return "Else";
            case 33: return "Else if";
            case 34: return "End if";
            case 35: { var meaning = Callback(ev.I32(12)); return $"Game callback {meaning.Value} · {meaning.Short}"; }
            case 36: return "Screen colour · " + AnimationSeconds.Short(ev.F32(60));
            case 37: return "Screen wave · " + AnimationSeconds.Short(ev.F32(108));
            case 38: return $"Text message {ev.I32(12)}";
            default: return "Marker";
        }
    }

    /// <summary>"×4", "forever" or "for 2 s" (LOOP_COUNT 65535 and a negative or absent LOOP_TIME loop forever).</summary>
    internal static string LoopText(AnimationEvent ev)
    {
        var (mode, count, seconds) = Loop(ev);
        return mode switch { "count" => $"×{count}", "time" => "for " + AnimationSeconds.Short(seconds!.Value), _ => "forever" };
    }
    internal static (string Mode, int? Count, double? Seconds) Loop(AnimationEvent ev)
    {
        if (ev.Bytes.Length < 20) return ("forever", null, null);
        uint stop = ev.U32(12);
        if ((stop & 1) != 0) { uint count = ev.U32(16) & 65535; return count == 65535 ? ("forever", null, null) : ("count", (int)count, null); }
        if ((stop & 2) != 0 && ev.F32(16) >= 0) return ("time", null, ev.F32(16));
        return ("forever", null, null);
    }

    /// <summary>A CALL_ANIMATION with LOCAL_NAME: its child record names it, so the launcher stops it when it stops.</summary>
    internal static bool IsLocalChild(AnimationEntry entry, AnimationEvent ev)
    {
        if (ev.Type != 24 || ev.Bytes.Length < 52) return false;
        int slot = ev.I16(50);
        return slot >= 0 && slot < entry.References[7].Count && entry.References[7][slot].Bytes.Length >= 68 && entry.References[7][slot].I32(64) == 1;
    }

    /// <summary>
    /// The nodes an event names, as its phrase does (its target, a position or parent node), by name; the bound root appears as
    /// <see cref="AnimationEntry.RootName"/>. What a selected row outlines in the view and how an object finds the rows naming it.
    /// </summary>
    public static IReadOnlyList<string> NodeNames(AnimationEntry entry, AnimationEvent ev)
    {
        ArgumentNullException.ThrowIfNull(entry); ArgumentNullException.ThrowIfNull(ev);
        if (ev.Bytes.Length < 12 || ev.Spec is not { } spec || ev.Bytes.Length < spec.Size) return [];
        int[] references = ev.Type switch
        {
            1 or 3 => [ev.I16(14)],
            2 => (ev.U32(48) & 2) != 0 ? [ev.I32(56)] : [],
            6 or 17 or 18 => [ev.I16(16)],
            7 or 9 => [ev.I16(28), ev.I16(30)],
            8 => [ev.I16(24)],
            10 or 11 or 20 or 21 => [ev.I32(16)],
            12 or 14 => [ev.I32(12)],
            13 => [ev.I16(20)],
            15 or 16 => [ev.I16(14), ev.I16(12)],
            24 => [ev.I16(52), ev.I16(44)],
            _ => [],
        };
        List<string> names = [];
        foreach (int reference in references)
        {
            if (reference is 0 or -200) continue;
            if (reference != -100 && (reference < 0 || reference >= entry.References[1].Count)) continue;
            string name = reference == -100 ? entry.RootName : entry.References[1][reference].Bytes.Length >= 32 ? entry.References[1][reference].Text(0, Math.Min(36, entry.References[1][reference].Bytes.Length)) : "";
            if (name.Length > 0 && !names.Contains(name, StringComparer.Ordinal)) names.Add(name);
        }
        return names;
    }

    /// <summary>A node reference by name: the bound root (−100), the input node (−200), or the entry's node-reference table.</summary>
    public static string NodeName(AnimationEntry entry, int reference) => reference switch
    {
        0 => "(none)",
        -100 => entry.RootName,
        -200 => "the input node",
        _ => TableName(entry, 1, reference),
    };
    internal static string TableName(AnimationEntry entry, int table, int index)
    {
        if (index <= 0 || index >= entry.References[table].Count) return $"missing reference {index}";
        var record = entry.References[table][index];
        return record.Bytes.Length >= 32 ? Safe(record.Text(0, Math.Min(36, record.Bytes.Length))) : $"incomplete reference {index}";
    }

    private static string At(AnimationEntry entry, int reference, Vector3 offset)
    {
        if (reference == 0) return offset == Vector3.Zero ? "" : " · at " + Vector(offset);
        return " · at " + NodeName(entry, reference) + (offset == Vector3.Zero ? "" : " " + Vector(offset));
    }
    internal static string Quote(string name) => Safe(name);
    internal static string Safe(string text) => JsonData.ShownText(text, 64);
    /// <summary>A number as the phrases write it: up to three decimals, a true minus sign.</summary>
    public static string Number(double value) => double.IsFinite(value)
        ? value.ToString("0.###", CultureInfo.InvariantCulture).Replace('-', '−') : "—";
    internal static string Vector(Vector3 value) => $"{Number(value.X)}, {Number(value.Y)}, {Number(value.Z)}";
    private static string Degrees(Vector3 radians) => Vector(radians * (float)(180 / Math.PI));

    /// <summary>
    /// Names compacted for a grouped row or a reference node: "rdrail_destroy01 … 08" (the shared prefix, backed off to the
    /// start of a number so the digits read whole).
    /// </summary>
    public static string Range(IReadOnlyList<string> names)
    {
        if (names.Count == 0) return "";
        string first = names[0], last = names[^1];
        if (names.Count == 1 || first == last) return first;
        int common = 0;
        while (common < first.Length && common < last.Length && first[common] == last[common]) common++;
        while (common > 0 && char.IsAsciiDigit(first[common - 1])) common--;
        return first + " … " + (common > 0 && common < last.Length ? last[common..] : last);
    }
}
