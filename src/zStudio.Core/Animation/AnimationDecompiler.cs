using System.Buffers.Binary;
using System.Numerics;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core.Animation;

/// <summary>
/// Turns a compiled animation entry back into its definition (<c>ANIMATION_DEFINITION</c>), the inverse of
/// <see cref="AnimationCompiler"/>: rates, directions and reference tables become the keywords that produce them, and
/// angles return to degrees. Values are chosen so that the definition compiles to the same fields; callers verify it.
/// </summary>
public static class AnimationDecompiler
{
    /// <summary>
    /// The definition items of <paramref name="entry"/> as a list node (the value of ANIMATION_DEFINITION).
    /// <paramref name="script"/> names the script file and frame rate of each keyframe event, whose keys live in a script.
    /// </summary>
    public static ZrdNode Definition(AnimationEntry entry, Func<AnimationEvent, (string File, float Rate)> script, CancellationToken token = default)
        => Definition(entry, script, new AnimationDefinitionBudget(token));

    internal static ZrdNode Definition(AnimationEntry entry, Func<AnimationEvent, (string File, float Rate)> script, AnimationDefinitionBudget budget)
    {
        // Every event needs at least its keyword and value array. Refuse large admitted binary streams before
        // constructing even the first candidate node; detailed charges below cover their remaining operands.
        long minimum = 1;
        foreach (var sequence in entry.AllSequences) { budget.Visit(); minimum += 2L * sequence.Events.Count; }
        budget.CheckNodes(minimum);
        Builder b = new(entry, script, budget);
        return b.Definition();
    }

    private sealed class Builder(AnimationEntry entry, Func<AnimationEvent, (string File, float Rate)> script, AnimationDefinitionBudget budget)
    {
        private readonly List<ZrdNode> items = [];

        public ZrdNode Definition()
        {
            List<ZrdNode> d = [];
            Key(d, "NAME", S(entry.RootName));
            if (entry.Name != entry.RootName) Key(d, "ANIMATION_NAME", S(entry.Name));
            if (entry.AttachName != entry.RootName) Key(d, "ANIMATION_ROOT_NAME", S(entry.AttachName));
            Key(d, "ACTIVATION", S(entry.Bytes[153] switch { 0 => "WEAPON_HIT", 1 => "COLLIDE_HIT", 2 => "WEAPON_OR_COLLIDE_HIT", 3 => "ON_CALL", 4 => "ON_STARTUP", var other => throw new InvalidDataException($"{entry.Name}: activation {other} has no keyword.") }));
            uint flags = entry.U32(148);
            if (entry.F32(172) != 0) Key(d, "HEALTH", F(entry.F32(172)));
            Key(d, "RESET_TIME", (flags & 0x20) != 0 ? [F(entry.F32(164))] : [F(entry.F32(164)), F(-1)]);
            if ((flags & 0x02) != 0) Key(d, "EXECUTION_BY_RANGE", F(Root(entry.F32(160))));
            if ((flags & 0x08) != 0) Key(d, "EXECUTION_BY_ZONE");
            if (entry.Bytes[154] != 4) Key(d, "EXECUTION_PRIORITY", I(entry.Bytes[154]));
            if ((flags & 0x1000) != 0) Key(d, "SAVE_LOG", S("OFF"));
            if ((flags & 0x0400) != 0) Key(d, "NETWORK_LOG", S("OFF"));
            if (entry.References[6].Count > 0) Key(d, "ACTIVATION_PREREQUISITE", Prerequisites());
            if (entry.Primary.Events.Count > 0) Key(d, "RESET_STATE", [.. Events(entry.Primary)]);
            foreach (var sequence in entry.Sequences)
            {
                List<ZrdNode> s = [];
                if (sequence.Name.Length > 0) Key(s, "NAME", S(sequence.Name));
                if (sequence.Bytes[32] == 3) Key(s, "ACTIVATION", S("ON_CALL"));
                s.AddRange(Events(sequence));
                Key(d, "SEQUENCE_DEFINITION", [.. s]);
            }
            return A([.. d]);
        }

        private ZrdNode[] Prerequisites()
        {
            var records = entry.References[6];
            List<ZrdNode> options = [], required = [];
            List<ZrdNode> animations = [], paths = []; List<ZrdNode> path = [];
            bool option = records.Count > 0 && records[0].I32(0) == 1;
            foreach (var r in records)
            {
                int mode = r.Bytes[4];
                if (mode == 1) animations.Add(S(r.Text(8, 32)));
                else { path.Add(S(r.Text(12, 28))); if (mode == 2) { paths.Add(A([.. path])); path = []; } }
            }
            List<ZrdNode> group = [];
            if (entry.Bytes[268] != 0) Key(group, "MINIMUM_TO_SATISFY", I(entry.Bytes[268]));
            if (animations.Count > 0) Key(group, "ANIMATION_LIST", [.. animations]);
            if (paths.Count > 0) Key(group, "OBJECT_ACTIVE_LIST", [.. paths]);
            if (!option && paths.Count == 0 && entry.Bytes[268] == 0) return [.. group];
            List<ZrdNode> result = [];
            Key(result, option ? "OPTIONS" : "REQUIRED", [.. group]);
            return [.. result];
        }

        // ------------------------------------------------------------ events

        private List<ZrdNode> Events(AnimationSequence sequence)
        {
            List<ZrdNode> list = [];
            foreach (var ev in sequence.Events) Key(list, Keyword(ev.Type), [.. Event(ev)]);
            return list;
        }

        private static string Keyword(byte type) => type switch
        {
            1 => "SOUND", 2 => "SOUND_NODE", 3 => "EFFECT", 4 => "LIGHT_STATE", 5 => "LIGHT_ANIMATION", 6 => "OBJECT_ACTIVE_STATE",
            7 => "OBJECT_TRANSLATE_STATE", 8 => "OBJECT_SCALE_STATE", 9 => "OBJECT_ROTATE_STATE", 10 => "OBJECT_MOTION", 11 => "OBJECT_MOTION_FROM_TO",
            12 => "OBJECT_MOTION_SI_SCRIPT", 13 => "OBJECT_OPACITY_STATE", 14 => "OBJECT_OPACITY_FROM_TO", 15 => "OBJECT_ADD_CHILD", 16 => "OBJECT_DELETE_CHILD",
            17 => "OBJECT_CYCLE_TEXTURE", 18 => "OBJECT_CONNECTOR", 19 => "CALL_OBJECT_CONNECTOR", 20 => "CAMERA_STATE", 21 => "CAMERA_FROM_TO",
            22 => "CALL_SEQUENCE", 23 => "STOP_SEQUENCE", 24 => "CALL_ANIMATION", 25 => "STOP_ANIMATION", 26 => "RESET_ANIMATION", 27 => "INVALIDATE_ANIMATION",
            28 => "FOG_STATE", 30 => "LOOP", 31 => "IF", 32 => "ELSE", 33 => "ELSEIF", 34 => "ENDIF", 35 => "CALLBACK", 36 => "FBFX_COLOR_FROM_TO",
            37 => "FBFX_CSINWAVE_FROM_TO", 39 => "ANIM_VERBOSE",
            _ => throw new InvalidDataException($"event type {type} has no keyword."),
        };

        private List<ZrdNode> Event(AnimationEvent e)
        {
            List<ZrdNode> p = [];
            if (e.StartMode != 1 || e.Threshold != 0)
                Key(p, "START_TIME", S(e.StartMode switch { 2 => "SEQUENCE_OFFSET", 3 => "EVENT_OFFSET", _ => "ANIMATION_OFFSET" }), F(e.Threshold));
            switch (e.Type)
            {
                case 1: Key(p, "NAME", S(Sample(e.I16(12)))); AtNode(p, e.I16(14), e.Vector(16)); break;
                case 2:
                    Key(p, "NAME", S(e.Text(12)));
                    if (e.I32(52) != 1) Key(p, "ACTIVE_STATE", S("INACTIVE"));
                    if ((e.U32(48) & 0x02) != 0) AtNode(p, e.I32(56), e.Vector(60));
                    break;
                case 3: Key(p, "NAME", S(Effect(e.I16(12)))); AtNode(p, e.I16(14), e.Vector(16)); break;
                case 4:
                    {
                        uint fields = e.U32(48);
                        Key(p, "NAME", S(e.Text(12)));
                        if (e.I32(52) != 1) Key(p, "ACTIVE_STATE", S("INACTIVE"));
                        if ((fields & 0x02) != 0) AtNode(p, e.I32(68), e.Vector(72), always: true);
                        if ((fields & 0x08) != 0) Key(p, "RANGE", F(e.F32(96)), F(e.F32(100)));
                        if ((fields & 0x10) != 0) Key(p, "COLOR", V(e.Vector(104)));
                        if ((fields & 0x100) != 0) Key(p, "SATURATED", S(e.I32(64) != 0 ? "TRUE" : "FALSE"));
                        break;
                    }
                case 5: Key(p, "NAME", S(e.Text(12))); Key(p, "RANGE", F(e.F32(48)), F(e.F32(52)), F(e.F32(56)), F(e.F32(60))); RunTime(p, e.F32(108)); break;
                case 6: Key(p, "NAME", S(Node(e.I16(16)))); Key(p, "STATE", S(e.I32(12) != 0 ? "ACTIVE" : "INACTIVE")); break;
                case 7:
                    Key(p, "NAME", S(Node(e.I16(28))));
                    if (e.I16(30) != 0) AtNode(p, e.I16(30), e.Vector(16));
                    else Key(p, (e.U32(12) & 1) != 0 ? "RELATIVE" : "STATE", V(e.Vector(16)));
                    break;
                case 8: Key(p, "NAME", S(Node(e.I16(24)))); Key(p, "STATE", V(e.Vector(12))); break;
                case 9:
                    Key(p, "NAME", S(Node(e.I16(28))));
                    if ((e.U32(12) & 2) != 0) Key(p, "AT_NODE_XYZ", S(Node(e.I16(30))));
                    else Key(p, "STATE", V(Degrees(e.Vector(16))));
                    break;
                case 10: Motion(e, p); break;
                case 11: FromTo(e, p); break;
                case 12:
                    {
                        var (file, rate) = script(e);
                        Key(p, "NAME", S(Node(e.I32(12)))); Key(p, "SCRIPT_FRAME_RATE", F(rate)); Key(p, "SCRIPT_FILENAME", S(file));
                        break;
                    }
                case 13:
                    Key(p, "NAME", S(Node(e.I16(20))));
                    Key(p, "STATE", e.I16(14) != 0 ? [S(e.I16(12) != 0 ? "ON" : "OFF"), F(e.F32(16))] : [S(e.I16(12) != 0 ? "ON" : "OFF")]);
                    break;
                case 14:
                    Key(p, "NAME", S(Node(e.I32(12))));
                    Key(p, "OPACITY_FROM", Opacity(e.F32(20), e.I16(16))); Key(p, "OPACITY_TO", Opacity(e.F32(24), e.I16(18)));
                    RunTime(p, e.F32(32));
                    break;
                case 15 or 16: Key(p, "PARENT_CHILD", S(Node(e.I16(12))), S(Node(e.I16(14)))); break;
                case 17: Key(p, "NAME", S(Node(e.I16(16)))); if ((e.U32(12) & 1) != 0) Key(p, "RESET"); break;
                case 18: Connector(e, p); break;
                case 19: CallConnector(e, p); break;
                case 20:
                    {
                        uint c = e.U32(12); Key(p, "NAME", S(Node(e.I32(16))));
                        if ((c & 1) != 0) Key(p, "NEAR_CLIP", F(e.F32(20)));
                        if ((c & 2) != 0) Key(p, "FAR_CLIP", F(e.F32(24)));
                        if ((c & 0x20) != 0) Key(p, "H_ZOOM", F(e.F32(40)));
                        if ((c & 0x40) != 0) Key(p, "V_ZOOM", F(e.F32(44)));
                        break;
                    }
                case 21:
                    {
                        uint c = e.U32(12); Key(p, "NAME", S(Node(e.I32(16))));
                        if ((c & 0x20) != 0) Key(p, "H_ZOOM_FROM_TO", F(e.F32(80)), F(e.F32(84)));
                        if ((c & 0x40) != 0) Key(p, "V_ZOOM_FROM_TO", F(e.F32(92)), F(e.F32(96)));
                        RunTime(p, e.F32(104));
                        break;
                    }
                case 22 or 23 or 25 or 26 or 27: Key(p, "NAME", S(e.Text(12))); break;
                case 24: CallAnimation(e, p); break;
                case 28:
                    {
                        uint f = e.U32(44);
                        Key(p, "TYPE", S(e.I32(48) != 0 ? "LINEAR" : "OFF"));
                        if ((f & 2) != 0) Key(p, "COLOR", V(e.Vector(52)));
                        if ((f & 4) != 0) Key(p, "ALTITUDE", F(e.F32(64)), F(e.F32(68)));
                        if ((f & 8) != 0) Key(p, "RANGE", F(e.F32(72)), F(e.F32(76)));
                        break;
                    }
                case 30:
                    if (e.U32(12) == 2) Key(p, "LOOP_TIME", F(e.F32(16)));
                    else Key(p, "LOOP_COUNT", I(e.I32(16) == 65535 ? -1 : e.I32(16)));
                    break;
                case 31 or 33:
                    switch (e.U32(12))
                    {
                        case 4: Key(p, "ANIMATION_LOD", S(e.I32(20) switch { 2 => "HIGH", 1 => "MEDIUM", _ => "LOW" })); break;
                        case 2: Key(p, "PLAYER_RANGE", F(Root(e.F32(20)))); break;
                        case 1: Key(p, "RANDOM_WEIGHT", F(e.F32(20))); break;
                        case 0x10: Key(p, "NODE_UNDERCOVER", S(Node(e.I32(16))), F(e.F32(20))); break;
                        default: throw new InvalidDataException($"condition {e.U32(12)} has no keyword.");
                    }
                    break;
                case 32 or 34: break;
                case 35: Key(p, "VALUE", I(e.I32(12))); break;
                case 36:
                    Key(p, "FROM", F(e.F32(12)), F(e.F32(24)), F(e.F32(36)), F(e.F32(48)));
                    Key(p, "TO", F(e.F32(16)), F(e.F32(28)), F(e.F32(40)), F(e.F32(52)));
                    RunTime(p, e.F32(60));
                    break;
                case 37: Wave(e, p); break;
                case 39: p.Add(S(e.I32(12) != 0 ? "ON" : "OFF")); break;
            }
            // START_TIME is written first above; the original files put it anywhere in the item.
            return p;
        }

        private void Motion(AnimationEvent e, List<ZrdNode> p)
        {
            uint flags = e.U32(12);
            Key(p, "NAME", S(Node(e.I32(16))));
            if ((flags & 1) != 0) Key(p, "GRAVITY", S("LOCAL"), F(e.F32(24)));
            if ((flags & 4) != 0)
            {
                var (yaw, pitch, speed, accel) = Launch(e.Vector(112), e.Vector(64), e.Vector(76));
                Key(p, "TRANSLATION", F(yaw), F(pitch), F(speed), F(accel));
            }
            if ((flags & 8) != 0)
            {
                Key(p, "TRANSLATION_RANGE_MIN", F(e.F32(32)), F(e.F32(40)), F(e.F32(48)), F(e.F32(56)));
                Key(p, "TRANSLATION_RANGE_MAX", F(e.F32(36)), F(e.F32(44)), F(e.F32(52)), F(e.F32(60)));
            }
            if ((flags & 0x20) != 0) Key(p, "XYZ_ROTATION", [.. V(Degrees(e.Vector(136))), .. V(Degrees(e.Vector(148)))]);
            if ((flags & 0x100) != 0) Key(p, "SCALE", [.. V(e.Vector(172)), .. V(e.Vector(184))]);
            if ((flags & 0x80) != 0) Key(p, "FORWARD_ROTATION", S("TIME"), F(Degrees(e.F32(124))), F(Degrees(e.F32(128))));
            if ((flags & 0x400) != 0) Key(p, "RUN_TIME", F(e.F32(248)));
            if ((flags & 0x800) != 0) Key(p, "BOUNCE_SEQUENCE", S(e.Text(208)));
            if ((flags & 0x1000) != 0)
            {
                List<ZrdNode> sound = []; Key(sound, "NAME", S(Sample(e.I16(242)))); Key(sound, "FULL_VOLUME_VELOCITY", F(e.F32(244)));
                Key(p, "BOUNCE_SOUND", [.. sound]);
            }
        }

        /// <summary>Yaw, pitch, speed and acceleration whose compiled direction and vectors match the stored ones.</summary>
        private static (float, float, float, float) Launch(Vector3 direction, Vector3 velocity, Vector3 acceleration)
        {
            float pitch = Nearest(direction.Y * 90f, p => (float)(p * (double)(1f / 90f)) == direction.Y);
            double horizontal = Math.Sqrt(direction.X * (double)direction.X + direction.Z * (double)direction.Z);
            float guess = horizontal < 1e-6 ? 0 : (float)(Math.Atan2(-direction.X, -direction.Z) * 180 / Math.PI);
            // Angles a full turn apart store different rounding in the vanishing components (180° is not −180°).
            float yaw = guess;
            foreach (float candidate in new[] { guess, guess + 360, guess - 360 })
            {
                float found = Nearest(candidate, y => Same(AnimationCompilerDirection(y, pitch), direction));
                if (Same(AnimationCompilerDirection(found, pitch), direction)) { yaw = found; break; }
            }
            var compiled = AnimationCompilerDirection(yaw, pitch);
            float Scale(Vector3 v) => Math.Abs(direction.Y) >= Math.Max(Math.Abs(direction.X), Math.Abs(direction.Z)) ? v.Y / direction.Y
                : Math.Abs(direction.X) >= Math.Abs(direction.Z) ? v.X / direction.X : v.Z / direction.Z;
            float speed = Nearest(Scale(velocity), s => Same(compiled * s, velocity)), accel = Nearest(Scale(acceleration), a => Same(compiled * a, acceleration));
            return (yaw, pitch, speed, accel);
        }
        /// <summary>Bit-for-bit equality, so signed zeros count.</summary>
        private static bool Same(Vector3 a, Vector3 b) =>
            BitConverter.SingleToInt32Bits(a.X) == BitConverter.SingleToInt32Bits(b.X) && BitConverter.SingleToInt32Bits(a.Y) == BitConverter.SingleToInt32Bits(b.Y) && BitConverter.SingleToInt32Bits(a.Z) == BitConverter.SingleToInt32Bits(b.Z);
        private static Vector3 AnimationCompilerDirection(float yaw, float pitch) => AnimationCompiler.LaunchDirection(yaw, pitch);


        private void FromTo(AnimationEvent e, List<ZrdNode> p)
        {
            uint c = e.U32(12); float time = e.F32(140);
            Key(p, "NAME", S(Node(e.I32(16))));
            if ((c & 8) != 0) { Key(p, "MORPH_FROM", F(e.F32(20))); Key(p, "MORPH_TO", F(e.F32(24))); }
            if ((c & 1) != 0) { Key(p, "TRANSLATE_FROM", V(e.Vector(32))); Key(p, "TRANSLATE_TO", V(e.Vector(44))); }
            if ((c & 2) != 0) { Key(p, "ROTATE_FROM", V(Degrees(e.Vector(68)))); Key(p, "ROTATE_TO", V(Degrees(e.Vector(80)))); }
            if ((c & 4) != 0) { Key(p, "SCALE_FROM", V(e.Vector(104))); Key(p, "SCALE_TO", V(e.Vector(116))); }
            // A missing RUN_TIME compiles as a millisecond.
            if (time != 0.001f) RunTime(p, time);
        }

        private void Connector(AnimationEvent e, List<ZrdNode> p)
        {
            uint f = e.U32(12);
            Key(p, "NAME", S(Node(e.I16(16))));
            if ((f & 0x01) != 0) Key(p, "FROM_NODE", S(Node(e.I16(18))));
            if ((f & 0x02) != 0) Key(p, "FROM_INPUT_NODE");
            if ((f & 0x08) != 0) Key(p, "FROM_POS", V(e.Vector(24)));
            if ((f & 0x10) != 0) Key(p, "FROM_INPUT_POS");
            if ((f & 0x20) != 0) Key(p, "TO_NODE", S(Node(e.I16(20))));
            if ((f & 0x40) != 0) Key(p, "TO_INPUT_NODE");
            if ((f & 0x100) != 0) Key(p, "TO_POS", V(e.Vector(36)));
            if ((f & 0x200) != 0) Key(p, "TO_INPUT_POS");
            if ((f & 0x800) != 0) { Key(p, "FROM_T_START", F(e.F32(48))); Key(p, "FROM_T_END", F(e.F32(52))); }
            if ((f & 0x1000) != 0) Key(p, "TO_T", F(e.F32(64)));
            if ((f & 0x2000) != 0) { Key(p, "TO_T_START", F(e.F32(64))); Key(p, "TO_T_END", F(e.F32(68))); }
            if (e.F32(80) != 0) RunTime(p, e.F32(80));
            if ((f & 0x8000) != 0) Key(p, "MAX_LENGTH", F(e.F32(84)));
        }

        private void CallConnector(AnimationEvent e, List<ZrdNode> p)
        {
            uint f = e.U32(12);
            Key(p, "NAME", S(e.Text(16)));
            if (e.I16(50) >= 0 && Child(e.I16(50)) is { Local.Length: > 0 } child) Key(p, "LOCAL_NAME", S(child.Local));
            if ((f & 0x01) != 0) Key(p, "FROM_NODE", S(Node(e.I16(52))));
            if ((f & 0x02) != 0) Key(p, "FROM_NODE_POS", S(Node(e.I16(52))));
            if ((f & 0x08) != 0) Key(p, "FROM_POS", V(e.Vector(56)));
            if ((f & 0x40) != 0) Key(p, "TO_NODE", S(Node(e.I16(54))));
            if ((f & 0x200) != 0) Key(p, "TO_INPUT_NODE_POS");
            if ((f & 0x400) != 0) Key(p, "TO_POS", V(e.Vector(68)));
        }

        private void CallAnimation(AnimationEvent e, List<ZrdNode> p)
        {
            int flags = e.I16(46);
            Key(p, "NAME", S(e.Text(12)));
            if ((flags & 1) != 0)
            {
                List<ZrdNode> at = [S(Node(e.I16(52)))];
                if ((flags & 2) != 0) at.AddRange(V(e.Vector(56)));
                if ((flags & 4) != 0) at.AddRange(V(Degrees(e.Vector(68))));
                Key(p, "AT_NODE", [.. at]);
            }
            if ((flags & 8) != 0) Key(p, "WITH_NODE", S(Node(e.I16(52))));
            if (e.I16(44) != 0) Key(p, "OPERAND_NODE", S(Node(e.I16(44))));
            if (e.I16(50) >= 0 && Child(e.I16(50)) is { Local.Length: > 0 } child) Key(p, "LOCAL_NAME", S(child.Local));
            if ((flags & 0x10) != 0) Key(p, "WAIT_FOR_COMPLETION");
        }

        private void Wave(AnimationEvent e, List<ZrdNode> p)
        {
            uint f = e.U32(12);
            if ((f & 0x02) != 0) Key(p, "AT_NODE", S(Node((short)(f >> 16))));
            if ((f & 0x01) != 0) { Key(p, "SCREEN_POS_FROM", F(e.F32(28)), F(e.F32(40))); Key(p, "SCREEN_POS_TO", F(e.F32(32)), F(e.F32(44))); }
            if ((f & 0x04) != 0) { Key(p, "SCREEN_RADIUS_FROM", F(e.F32(60))); Key(p, "SCREEN_RADIUS_TO", F(e.F32(64))); }
            if ((f & 0x08) != 0) { Key(p, "WORLD_RADIUS_FROM", F(e.F32(52))); Key(p, "WORLD_RADIUS_TO", F(e.F32(56))); }
            Key(p, "CSIN_FROM", F(e.F32(72)), F(e.F32(84)), F(e.F32(96)));
            Key(p, "CSIN_TO", F(e.F32(76)), F(e.F32(88)), F(e.F32(100)));
            RunTime(p, e.F32(108));
        }

        private void AtNode(List<ZrdNode> p, int node, Vector3 offset, bool always = false)
        {
            if (node == 0 && !always) return;
            List<ZrdNode> at = [S(Node(node))];
            if (offset != Vector3.Zero || always) at.AddRange(V(offset));
            Key(p, "AT_NODE", [.. at]);
        }
        private void RunTime(List<ZrdNode> p, float time) { if (time != 0) Key(p, "RUN_TIME", F(time)); }
        private ZrdNode[] Opacity(float value, short state) => state < 0 ? [F(value)] : [F(value), S(state != 0 ? "ON" : "OFF")];

        // ------------------------------------------------------------ references

        private string Node(int index) => index switch
        {
            -200 => "INPUT_NODE",
            > 0 when index < entry.References[1].Count => entry.References[1][index].Text(0, 36),
            _ => throw new InvalidDataException($"{entry.Name}: node reference {index} is not in its table."),
        };
        private string Sample(int index) => index > 0 && index < entry.References[4].Count ? entry.References[4][index].Text(0, 32) : throw new InvalidDataException($"{entry.Name}: sample reference {index} is not in its table.");
        private string Effect(int index) => index > 0 && index < entry.References[5].Count ? entry.References[5][index].Text(0, 32) : throw new InvalidDataException($"{entry.Name}: effect reference {index} is not in its table.");
        private (string Name, string Local)? Child(int index) => index < entry.References[7].Count ? (entry.References[7][index].Text(0, 32), entry.References[7][index].Text(32, 32)) : null;

        // ------------------------------------------------------------ values

        private static float Degrees(float radians) => Nearest((float)(radians * 180 / Math.PI), d => (float)(d * Math.PI / 180) == radians);
        private static Vector3 Degrees(Vector3 radians) => new(Degrees(radians.X), Degrees(radians.Y), Degrees(radians.Z));
        private static float Root(float square) => Nearest((float)Math.Sqrt(square), r => (float)((double)r * r) == square);

        /// <summary>The float nearest <paramref name="guess"/> (a whole or short decimal when one works) that satisfies <paramref name="ok"/>.</summary>
        private static float Nearest(float guess, Func<float, bool> ok)
        {
            foreach (double scale in new[] { 1.0, 10, 100, 1000, 10000 })
            {
                float round = (float)(Math.Round(guess * scale) / scale);
                if (ok(round)) return round;
            }
            if (ok(guess)) return guess;
            float up = guess, down = guess;
            for (int i = 0; i < 64; i++)
            {
                up = MathF.BitIncrement(up); down = MathF.BitDecrement(down);
                if (ok(up)) return up;
                if (ok(down)) return down;
            }
            return guess;
        }

        private ZrdNode S(string text) { budget.Node(text.Length); return new(Guid.NewGuid(), ZrdKind.String, 0, text, []); }
        private ZrdNode I(int value) { budget.Node(); return new(Guid.NewGuid(), ZrdKind.Int, unchecked((uint)value), "", []); }
        private ZrdNode F(float value) { budget.Node(); return new(Guid.NewGuid(), ZrdKind.Float, BitConverter.SingleToUInt32Bits(value), "", []); }
        private ZrdNode[] V(Vector3 v) => [F(v.X), F(v.Y), F(v.Z)];
        private ZrdNode A(params ZrdNode[] children) { budget.Node(); return new(Guid.NewGuid(), ZrdKind.Array, 0, "", children); }
        private void Key(List<ZrdNode> list, string key, params ZrdNode[] values) { list.Add(S(key)); list.Add(A(values)); }
    }
}
