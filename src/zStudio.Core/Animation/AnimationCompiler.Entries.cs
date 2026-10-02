using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core.Animation;

public sealed partial class AnimationCompiler
{
    private const short InputNode = -200;

    /// <summary>Compiles one definition bound to one root into an entry.</summary>
    private sealed class EntryBuilder(AnimationCompiler compiler, AnimationDefinition definition, string root, string digits, int index)
    {
        private readonly AnimationItem item = definition.Item;
        // Reference tables in file order: tracked nodes, node references, lights, sound nodes, samples, effect
        // templates, activation prerequisites, child animations. A table's first record is a reserved blank.
        private readonly List<byte[]>[] tables = Enumerable.Range(0, 8).Select(_ => new List<byte[]>()).ToArray();
        private static readonly int[] NameSizes = [36, 36, 36, 36, 32, 32, 0, 32];
        private byte minimumPrerequisites;

        private string Bound(string text) => Bind(text, digits);

        public AnimationEntry Build()
        {
            byte[] header = new byte[HeaderSize];
            string name = Bound(item.TextOf("ANIMATION_NAME") ?? root);
            string attach = Bound(item.TextOf("ANIMATION_ROOT_NAME") ?? root);
            Text(header, 0, name, 32, "ANIMATION_NAME"); Text(header, 32, root, 32, "NAME"); Text(header, 68, attach, 32, "ANIMATION_ROOT_NAME");
            if (!compiler.NodeExists(root)) compiler.Warn($"{definition.File}: {name} is bound to {root}, which the world lacks; the game rejects the animation file.");
            if (!compiler.NodeExists(attach)) compiler.Warn($"{definition.File}: {name} is attached to {attach}, which the world lacks; the game rejects the animation file.");

            uint flags = 0; byte activation = 0, priority = 4; float range = 0, reset = 0, health = 0; bool secondReset = false;
            foreach (var key in item.Items)
                switch (key.Key)
                {
                    case "NAME" or "ANIMATION_NAME" or "ANIMATION_ROOT_NAME" or "SEQUENCE_DEFINITION" or "RESET_STATE": break;
                    case "ACTIVATION":
                        activation = key.Text() switch
                        {
                            "WEAPON_HIT" => 0, "COLLIDE_HIT" => 1, "WEAPON_OR_COLLIDE_HIT" => 2, "ON_CALL" => 3, "ON_STARTUP" => 4,
                            var other => throw key.Error($"unknown ACTIVATION {other}."),
                        };
                        break;
                    case "HEALTH": health = key.Number(); break;
                    case "RESET_TIME": reset = key.Number(); secondReset = key.Scalars.Count > 1; break;
                    case "EXECUTION_BY_RANGE": flags |= 0x02; range = key.Number(); break;
                    case "EXECUTION_BY_ZONE": flags |= 0x08; break;
                    case "EXECUTION_PRIORITY": priority = Byte(key, key.Integer()); break;
                    case "SAVE_LOG": if (key.Text() == "OFF") flags |= 0x1000; break;
                    case "NETWORK_LOG": if (key.Text() == "OFF") flags |= 0x0400; break;
                    case "ACTIVATION_PREREQUISITE": Prerequisites(key); break;
                    default: compiler.Warn($"{definition.File}: {name}: {key.Key} is not an animation setting and was ignored."); break;
                }
            if (!secondReset) flags |= 0x20;
            if (Events(item).Any(e => e.Key == "CALLBACK")) flags |= 0x10;
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(148), flags);
            header[153] = activation; header[154] = priority; header[155] = 2;
            BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(160), (float)((double)range * range));
            BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(164), reset);
            BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(172), health);

            AnimationEntry entry = new(header, index, -1);
            entry.Primary = Sequence("RESET_SEQUENCE", item.Item("RESET_STATE")?.Items ?? []);
            foreach (var sequence in item.All("SEQUENCE_DEFINITION"))
                entry.Sequences.Add(Sequence(sequence.TextOf("NAME") ?? "", sequence.Items, sequence));
            if (entry.Sequences.Count > 255) throw item.Error("more than 255 sequences.");
            // The loader creates the entry's lights and sound nodes by name before it resolves node references, so
            // those names need not be in the world; any other missing node makes the game reject the file.
            HashSet<string> created = new(tables[2].Skip(1).Concat(tables[3].Skip(1)).Select(r => Name(r, 36)), StringComparer.Ordinal);
            foreach (var record in tables[0].Skip(1).Concat(tables[1].Skip(1)))
            {
                string node = Name(record, 36);
                if (!created.Contains(node) && !compiler.NodeExists(node)) compiler.Warn($"{definition.File}: {name} names node {node}, which the world lacks; the game rejects the animation file.");
            }
            // LoadZbd also rejects the file when an effect template is not in effects.zrd (retail 0x45F899).
            foreach (var record in tables[5].Skip(1))
            {
                string effect = Name(record, 32);
                if (!compiler.EffectExists(effect)) compiler.Warn($"{definition.File}: {name} spawns effect {effect}, which effects.zrd does not define; the game rejects the animation file.");
            }
            for (int t = 0; t < 8; t++)
                foreach (var record in tables[t]) entry.References[t].Add(new(record));
            header[268] = minimumPrerequisites;
            compiler.Grow(HeaderSize + entry.References.Sum(t => t.Sum(r => (long)r.Bytes.Length)) + 64L * (1 + entry.Sequences.Count));
            return entry;
        }

        /// <summary>A setting stored in one byte of the header.</summary>
        private static byte Byte(AnimationItem key, int value) => value is >= 0 and <= 255 ? (byte)value : throw key.Error($"{key.Key} must be from 0 to 255, not {value}.");

        /// <summary>Every event item of the definition: its reset state and sequences.</summary>
        private static IEnumerable<AnimationItem> Events(AnimationItem definition) =>
            (definition.Item("RESET_STATE")?.Items ?? []).Concat(definition.All("SEQUENCE_DEFINITION").SelectMany(s => s.Items));

        // ------------------------------------------------------------ tables

        private static readonly string[] TableNames = ["tracked node", "node reference", "light", "sound node", "sample", "effect template", "activation prerequisite", "child animation"];
        private int Reference(int table, string name, int size)
        {
            if (tables[table].Count == 0) tables[table].Add(new byte[size]);
            for (int i = 1; i < tables[table].Count; i++) if (Name(tables[table][i], NameSizes[table]) == name) return i;
            byte[] record = new byte[size]; Text(record, 0, name, NameSizes[table], "name");
            return Append(table, record);
        }
        /// <summary>Appends a record; the entry header counts each table in one unsigned byte (LoadZbd, retail 0x45F25E).</summary>
        private int Append(int table, byte[] record)
        {
            // Tables with a reserved blank first record hold 254 references; prerequisites and child animations 255.
            int reserved = table is 6 or 7 ? 0 : 1;
            if (tables[table].Count >= 255) throw item.Error($"more than {255 - reserved} {TableNames[table]} references.");
            tables[table].Add(record); return tables[table].Count - 1;
        }
        private static string Name(byte[] record, int size) { int end = Array.IndexOf(record, (byte)0, 0, size); return Encoding.Latin1.GetString(record, 0, end < 0 ? size : end); }
        private short Node(string name)
        {
            name = Bound(name);
            if (name == "INPUT_NODE") return InputNode;
            return checked((short)Reference(1, name, 40));
        }
        private void Track(string name) { name = Bound(name); if (name != "INPUT_NODE") Reference(0, name, 96); }
        private short Light(string name) => checked((short)Reference(2, Bound(name), 44));
        private short Sound(string name) => checked((short)Reference(3, Bound(name), 44));
        private short Sample(string name) => checked((short)Reference(4, Bound(name), 36));
        private short Effect(string name) => checked((short)Reference(5, Bound(name), 36));
        private short Child(string animation, string local)
        {
            byte[] record = new byte[72]; Text(record, 0, Bound(animation), 32, "NAME");
            // A named (local) child is stopped when its launcher is cleaned up; a waited-for child ends by itself.
            if (local.Length > 0) { Text(record, 32, Bound(local), 32, "LOCAL_NAME"); BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(64), 1); }
            return (short)Append(7, record);
        }

        private void Prerequisites(AnimationItem key)
        {
            // Records: whether the entry is one of several options, the kind (1 an animation name, 3 a node on a path,
            // 2 the node that ends it) and the name; a node's name starts four bytes in, as the loader reads it.
            void Add(int mode, string target, bool option)
            {
                byte[] record = new byte[48]; BinaryPrimitives.WriteInt32LittleEndian(record, option ? 1 : 0); record[4] = (byte)mode;
                if (mode == 1) Text(record, 8, target, 32, "prerequisite"); else Text(record, 12, target, 28, "prerequisite");
                Append(6, record);
            }
            void Group(AnimationItem group, bool required)
            {
                foreach (var part in group.Items)
                    switch (part.Key)
                    {
                        case "MINIMUM_TO_SATISFY": minimumPrerequisites = Byte(part, part.Integer()); break;
                        case "ANIMATION_LIST": foreach (var value in part.Scalars) Add(1, Bound(value.Text), required); break;
                        case "OBJECT_ACTIVE_LIST":
                            foreach (var path in part.Values!.Children.Where(c => c.Kind == ZrdKind.Array))
                            {
                                var names = path.Children.Select(c => Bound(c.Text)).ToArray();
                                // A path names a node and the parts below it; the last part ends the search.
                                for (int i = 0; i < names.Length; i++) Add(i == names.Length - 1 ? 2 : 3, names[i], required);
                            }
                            break;
                        default: compiler.Warn($"{definition.File}: ACTIVATION_PREREQUISITE {part.Key} was ignored."); break;
                    }
            }
            if (key.Has("OPTIONS") || key.Has("REQUIRED"))
            {
                foreach (var group in key.All("REQUIRED")) Group(group, false);
                foreach (var group in key.All("OPTIONS")) Group(group, true);
            }
            else Group(key, false);
        }

        // ------------------------------------------------------------ sequences

        private AnimationSequence Sequence(string name, IEnumerable<AnimationItem> items, AnimationItem? owner = null)
        {
            byte[] header = new byte[64]; if (name.Length > 0) Text(header, 0, Bound(name), 32, "sequence NAME");
            AnimationSequence sequence = new(header);
            foreach (var key in items)
            {
                compiler.token.ThrowIfCancellationRequested();
                switch (key.Key)
                {
                    case "NAME": continue;
                    case "ACTIVATION": if (key.Text() == "ON_CALL") { header[32] = 3; header[33] = 3; } else compiler.Warn($"{definition.File}: sequence {name}: ACTIVATION {key.Text()} was ignored."); continue;
                    // The original compiler ignored a START_TIME given to a whole sequence; every shipped one is unused.
                    case "START_TIME": continue;
                }
                if (Event(key) is { } ev) { compiler.Grow(ev.Bytes.Length); sequence.Events.Add(ev); }
            }
            return sequence;
        }

        private static (byte Mode, float Threshold) Start(AnimationItem key) => (key.Text() switch
        {
            "ANIMATION_OFFSET" => (byte)1, "SEQUENCE_OFFSET" => (byte)2, "EVENT_OFFSET" => (byte)3,
            var other => throw key.Error($"unknown START_TIME {other}."),
        }, key.Number(1));

        private AnimationEvent? Event(AnimationItem key)
        {
            (byte Type, int Size)? spec = key.Key switch
            {
                "SOUND" => (1, 28), "SOUND_NODE" => (2, 72), "EFFECT" => (3, 28), "LIGHT_STATE" => (4, 124), "LIGHT_ANIMATION" => (5, 112),
                "OBJECT_ACTIVE_STATE" => (6, 20), "OBJECT_TRANSLATE_STATE" => (7, 32), "OBJECT_SCALE_STATE" => (8, 28), "OBJECT_ROTATE_STATE" => (9, 32),
                "OBJECT_MOTION" => (10, 252), "OBJECT_MOTION_FROM_TO" => (11, 144), "OBJECT_MOTION_SI_SCRIPT" => (12, 32),
                "OBJECT_OPACITY_STATE" => (13, 24), "OBJECT_OPACITY_FROM_TO" => (14, 36), "OBJECT_ADD_CHILD" => (15, 16), "OBJECT_DELETE_CHILD" => (16, 16),
                "OBJECT_CYCLE_TEXTURE" => (17, 20), "OBJECT_CONNECTOR" => (18, 88), "CALL_OBJECT_CONNECTOR" => (19, 80), "CAMERA_STATE" => (20, 48),
                "CAMERA_FROM_TO" => (21, 108), "CALL_SEQUENCE" => (22, 48), "STOP_SEQUENCE" => (23, 48), "CALL_ANIMATION" => (24, 80),
                "STOP_ANIMATION" => (25, 48), "RESET_ANIMATION" => (26, 48), "INVALIDATE_ANIMATION" => (27, 48), "FOG_STATE" => (28, 80),
                "LOOP" => (30, 20), "IF" => (31, 24), "ELSE" => (32, 12), "ELSEIF" => (33, 24), "ENDIF" => (34, 12), "CALLBACK" => (35, 16),
                "FBFX_COLOR_FROM_TO" => (36, 64), "FBFX_CSINWAVE_FROM_TO" => (37, 112), "ANIM_VERBOSE" => (39, 16),
                _ => null,
            };
            if (spec == null) { compiler.Warn($"{key.Source}: {key.Key} is not an animation event and was ignored."); return null; }
            var (type, size) = spec.Value;
            E e = new(new byte[size]);
            e.Bytes[0] = type; e.Bytes[1] = 1; e.Int(4, size);
            if (key.Item("START_TIME") is { } timing) { var (mode, threshold) = Start(timing); e.Bytes[1] = mode; e.Float(8, threshold); }
            switch (type)
            {
                case 1:
                    e.Short(12, Sample(Req(key, "NAME")));
                    if (key.Item("AT_NODE") is { } at1) { e.Short(14, Node(at1.Text())); e.Vector(16, Offset(at1, 1)); }
                    break;
                case 2:
                    {
                        string sound = Bound(Req(key, "NAME")); e.Text(12, sound, 32); e.Int(44, Sound(sound));
                        uint fields = 0; e.Int(52, 1);
                        if (key.Item("ACTIVE_STATE") is { } active) e.Int(52, active.Text() == "ACTIVE" ? 1 : 0);
                        if (key.Item("AT_NODE") is { } at2) { fields |= 0x02; e.Int(56, Node(at2.Text())); e.Vector(60, Offset(at2, 1)); }
                        e.UInt(48, fields);
                        break;
                    }
                case 3:
                    e.Short(12, Effect(Req(key, "NAME")));
                    if (key.Item("AT_NODE") is { } at3) { e.Short(14, Node(at3.Text())); e.Vector(16, Offset(at3, 1)); }
                    break;
                case 4:
                    {
                        string light = Bound(Req(key, "NAME")); e.Text(12, light, 32); e.Int(44, Light(light));
                        uint fields = 0; e.Int(52, 1); e.Int(56, 1);
                        if (key.Item("ACTIVE_STATE") is { } active) e.Int(52, active.Text() == "ACTIVE" ? 1 : 0);
                        if (key.Item("AT_NODE") is { } at4) { fields |= 0x02; e.Int(68, Node(at4.Text())); e.Vector(72, Offset(at4, 1)); }
                        if (key.Item("RANGE") is { } range) { fields |= 0x08; e.Float(96, range.Number(0)); e.Float(100, range.Number(1)); }
                        if (key.Item("COLOR") is { } color) { fields |= 0x10; e.Vector(104, Vec(color, 0)); }
                        // The original compiler knew only SATURATED; the misspelled SATURATION in some shipped files had no effect.
                        if (key.Item("SATURATED") is { } saturated) { fields |= 0x100; e.Int(64, saturated.Text() == "TRUE" ? 1 : 0); }
                        e.UInt(48, fields);
                        break;
                    }
                case 5:
                    {
                        string light = Bound(Req(key, "NAME")); e.Text(12, light, 32); e.Int(44, Light(light));
                        if (key.Item("RANGE") is { } range) for (int i = 0; i < 4; i++) e.Float(48 + i * 4, range.Number(i));
                        e.Float(108, Time(key));
                        break;
                    }
                case 6:
                    {
                        string target = Req(key, "NAME"); e.Int(12, Req(key, "STATE") == "ACTIVE" ? 1 : 0);
                        Track(target); e.Short(16, Node(target));
                        break;
                    }
                case 7:
                    {
                        string target = Req(key, "NAME"); Track(target); e.Short(28, Node(target));
                        if (key.Item("STATE") is { } state) e.Vector(16, Vec(state, 0));
                        if (key.Item("RELATIVE") is { } relative) { e.UInt(12, 1); e.Vector(16, Vec(relative, 0)); }
                        if (key.Item("AT_NODE") is { } at7) { e.Short(30, Node(at7.Text())); e.Vector(16, Offset(at7, 1)); }
                        break;
                    }
                case 8:
                    { string target = Req(key, "NAME"); Track(target); e.Vector(12, Vec(key.Item("STATE") ?? throw key.Error("OBJECT_SCALE_STATE needs a STATE."), 0)); e.Short(24, Node(target)); break; }
                case 9:
                    {
                        string target = Req(key, "NAME"); Track(target); e.Short(28, Node(target));
                        if (key.Item("STATE") is { } state) e.Vector(16, Radians(Vec(state, 0)));
                        if (key.Item("AT_NODE_XYZ") is { } at9) { e.UInt(12, 2); e.Short(30, Node(at9.Text())); }
                        break;
                    }
                case 10: Motion(key, e); break;
                case 11: FromTo(key, e); break;
                case 12: return Script(key, e);
                case 13:
                    {
                        string target = Req(key, "NAME"); Track(target);
                        var state = key.Item("STATE") ?? throw key.Error("OBJECT_OPACITY_STATE needs a STATE.");
                        e.Short(12, (short)(state.Text() == "ON" ? 1 : 0));
                        if (state.Scalars.Count > 1) { e.Short(14, 1); e.Float(16, state.Number(1)); }
                        e.Short(20, Node(target));
                        break;
                    }
                case 14:
                    {
                        string target = Req(key, "NAME"); Track(target); e.Int(12, Node(target));
                        var from = key.Item("OPACITY_FROM"); var to = key.Item("OPACITY_TO");
                        float start = from?.Number(0) ?? 0, end = to?.Number(0) ?? 0, time = Time(key);
                        // ON or OFF sets the alpha override at that end; without either it is left as it is (−1).
                        e.Short(16, Override(from)); e.Short(18, Override(to));
                        e.Float(20, start); e.Float(24, end); e.Float(28, Rate(start, end, time)); e.Float(32, time);
                        break;
                    }
                case 15 or 16:
                    {
                        var pair = key.Item("PARENT_CHILD") ?? throw key.Error($"{key.Key} needs PARENT_CHILD.");
                        e.Short(12, Node(pair.Text(0))); e.Short(14, Node(pair.Text(1)));
                        break;
                    }
                case 17:
                    {
                        string target = Req(key, "NAME"); Track(target);
                        if (key.Has("RESET")) e.UInt(12, 1);
                        e.Short(16, Node(target));
                        break;
                    }
                case 18: Connector(key, e); break;
                case 19: CallConnector(key, e); break;
                case 20: Camera(key, e); break;
                case 21: CameraFromTo(key, e); break;
                case 22 or 23: e.Text(12, Bound(Req(key, "NAME")), 32); e.Int(44, -1); break;
                case 24: CallAnimation(key, e); break;
                case 25 or 26 or 27: e.Text(12, Bound(Req(key, "NAME")), 32); break;
                case 28: Fog(key, e); break;
                case 30:
                    {
                        // A bare EVENT_OFFSET in a LOOP (not in START_TIME) had no effect in the original compiler.
                        // The engine compares the iteration count as 16 bits, 65535 (−1) meaning forever (retail 0x45C337).
                        if (key.Item("LOOP_COUNT") is { } count)
                        {
                            e.UInt(12, 1); int n = count.Integer();
                            if (n > 65535) throw count.Error($"LOOP_COUNT must be at most 65535 (or negative to loop forever), not {n}.");
                            e.Int(16, n < 0 ? 65535 : n);
                        }
                        else if (key.Item("LOOP_TIME") is { } time) { e.UInt(12, 2); e.Float(16, time.Number()); }
                        else { e.UInt(12, 1); e.Int(16, 65535); }
                        break;
                    }
                case 31 or 33: Condition(key, e); break;
                case 35: e.Int(12, key.Item("VALUE")?.Integer() ?? 0); break;
                case 36:
                    {
                        var from = key.Item("FROM") ?? throw key.Error("FBFX_COLOR_FROM_TO needs FROM."); var to = key.Item("TO") ?? throw key.Error("FBFX_COLOR_FROM_TO needs TO.");
                        float time = Time(key);
                        for (int c = 0; c < 4; c++) Triple(e, 12 + c * 12, from.Number(c), to.Number(c), time);
                        e.Float(60, time);
                        break;
                    }
                case 37: Wave(key, e); break;
                case 39: e.Int(12, key.Scalars.Count > 0 && key.Text() == "ON" ? 1 : 0); break;
            }
            return new AnimationEvent(e.Bytes) { Version = 28 };
        }

        // ------------------------------------------------------------ event details

        private void Motion(AnimationItem key, E e)
        {
            string target = Req(key, "NAME"); Track(target); e.Int(16, Node(target));
            uint flags = 0;
            if (key.Item("GRAVITY") is { } gravity)
            {
                flags |= 1;
                e.Float(24, gravity.Text() == "LOCAL" ? gravity.Number(1) : compiler.definitions.Gravity);
            }
            if (key.Item("TRANSLATION") is { } translation)
            {
                flags |= 4;
                float yaw = translation.Number(0), pitch = translation.Number(1);
                float speed = translation.Scalars.Count > 2 ? translation.Number(2) : 0, accel = translation.Scalars.Count > 3 ? translation.Number(3) : 0;
                var direction = LaunchDirection(yaw, pitch);
                e.Vector(64, direction * speed); e.Vector(76, direction * accel); e.Vector(112, direction);
            }
            if (key.Item("TRANSLATION_RANGE_MIN") is { } min && key.Item("TRANSLATION_RANGE_MAX") is { } max)
            {
                flags |= 0x18;
                for (int i = 0; i < 4; i++) { e.Float(32 + i * 8, min.Number(i)); e.Float(36 + i * 8, max.Number(i)); }
            }
            if (key.Item("XYZ_ROTATION") is { } spin)
            {
                flags |= 0x20;
                e.Vector(136, Radians(Vec(spin, 0)));
                if (spin.Scalars.Count >= 6) e.Vector(148, Radians(Vec(spin, 3)));
            }
            if (key.Item("SCALE") is { } scale)
            {
                flags |= 0x100;
                e.Vector(172, Vec(scale, 0));
                if (scale.Scalars.Count >= 6) e.Vector(184, Vec(scale, 3));
            }
            if (key.Item("RUN_TIME") is { } run) { flags |= 0x400; e.Float(248, run.Number()); }
            if (key.Item("BOUNCE_SEQUENCE") is { } bounce) { flags |= 0x800; e.Text(208, Bound(bounce.Text()), 32); e.Short(240, -1); }
            if (key.Item("BOUNCE_SOUND") is { } sound)
            {
                flags |= 0x1000;
                e.Short(242, Sample(sound.TextOf("NAME") ?? throw sound.Error("BOUNCE_SOUND needs a NAME.")));
                if (sound.Item("FULL_VOLUME_VELOCITY") is { } velocity) e.Float(244, velocity.Number());
            }
            if (key.Item("FORWARD_ROTATION") is { } forward)
            {
                flags |= 0x80;
                e.Float(124, Radians(forward.Number(1))); e.Float(128, Radians(forward.Number(2)));
            }
            e.UInt(12, flags);
        }



        private void FromTo(AnimationItem key, E e)
        {
            string target = Req(key, "NAME"); Track(target); e.Int(16, Node(target));
            // Without RUN_TIME the change is (nearly) immediate: the shipped rates divide by a millisecond.
            float time = Time(key), span = time > 0 ? time : 0.001f; uint channels = 0;
            // A channel is animated when either end is given; a missing end is zero.
            if (key.Item("MORPH_FROM") is { } morphFrom | key.Item("MORPH_TO") is { } morphTo)
            {
                channels |= 8; float from = key.Item("MORPH_FROM")?.Number() ?? 0, to = key.Item("MORPH_TO")?.Number() ?? 0;
                e.Float(20, from); e.Float(24, to); e.Float(28, Rate(from, to, span));
            }
            if (Pair("TRANSLATE_FROM", "TRANSLATE_TO") is var (tf, tt)) { channels |= 1; Channel(32, tf, tt); }
            if (Pair("ROTATE_FROM", "ROTATE_TO") is var (rf, rt)) { channels |= 2; Channel(68, Radians(rf), Radians(rt)); }
            if (Pair("SCALE_FROM", "SCALE_TO") is var (sf, st)) { channels |= 4; Channel(104, sf, st); }
            e.UInt(12, channels); e.Float(140, span);
            (Vector3, Vector3)? Pair(string from, string to)
            {
                var a = key.Item(from); var b = key.Item(to);
                return a == null && b == null ? null : (a == null ? Vector3.Zero : Vec(a, 0), b == null ? Vector3.Zero : Vec(b, 0));
            }
            void Channel(int offset, Vector3 from, Vector3 to)
            {
                e.Vector(offset, from); e.Vector(offset + 12, to);
                e.Vector(offset + 24, new(Rate(from.X, to.X, span), Rate(from.Y, to.Y, span), Rate(from.Z, to.Z, span)));
            }
        }

        private AnimationEvent Script(AnimationItem key, E e)
        {
            string target = Req(key, "NAME"); Track(target); e.Int(12, Node(target));
            string file = Bound(Req(key, "SCRIPT_FILENAME")); float rate = key.Item("SCRIPT_FRAME_RATE")?.Number() ?? 30;
            var script = compiler.ReadScript(file, key.Source) ?? throw key.Error($"keyframe script {file} was not found in the animation path.");
            if (!script.Moves(Bound(target))) throw key.Error($"keyframe script {file} has no track for {Bound(target)}.");
            var frames = script.Compile(Bound(target), rate, $"{script.Path}, object {Bound(target)}");
            var ev = new AnimationEvent(e.Bytes) { Version = 28 }.WithKeyframes(frames);
            ev.SetInt(16, frames.Count);
            return ev;
        }

        private void Connector(AnimationItem key, E e)
        {
            string beam = Req(key, "NAME"); Track(beam); e.Short(16, Node(beam));
            uint flags = 0; e.Float(64, 1); e.Float(68, 1);
            foreach (var part in key.Items)
                switch (part.Key)
                {
                    case "NAME" or "START_TIME": break;
                    case "FROM_NODE": flags |= 0x01; e.Short(18, Node(part.Text())); break;
                    case "FROM_INPUT_NODE": flags |= 0x02; break;
                    case "FROM_POS": flags |= 0x08; e.Vector(24, Vec(part, 0)); break;
                    case "FROM_INPUT_POS": flags |= 0x10; break;
                    case "TO_NODE": flags |= 0x20; e.Short(20, Node(part.Text())); break;
                    case "TO_INPUT_NODE": flags |= 0x40; break;
                    case "TO_POS": flags |= 0x100; e.Vector(36, Vec(part, 0)); break;
                    case "TO_INPUT_POS": flags |= 0x200; break;
                    case "FROM_T_START": flags |= 0x800; e.Float(48, part.Number()); break;
                    case "FROM_T_END": flags |= 0x800; e.Float(52, part.Number()); break;
                    case "TO_T_START": flags |= 0x2000; e.Float(64, part.Number()); break;
                    case "TO_T_END": flags |= 0x2000; e.Float(68, part.Number()); break;
                    case "TO_T": flags |= 0x1000; e.Float(64, part.Number()); e.Float(68, part.Number()); break;
                    case "RUN_TIME": e.Float(80, part.Number()); break;
                    case "MAX_LENGTH": flags |= 0x8000; e.Float(84, part.Number()); break;
                    default: compiler.Warn($"{key.Source}: OBJECT_CONNECTOR {part.Key} was ignored."); break;
                }
            float time = e.Get(80);
            if (time > 0) { e.Float(56, Rate(e.Get(48), e.Get(52), time)); e.Float(72, Rate(e.Get(64), e.Get(68), time)); }
            e.UInt(12, flags);
        }

        private void CallConnector(AnimationItem key, E e)
        {
            string animation = Req(key, "NAME"); e.Text(16, Bound(animation), 32); e.Short(50, -1);
            uint flags = 0;
            foreach (var part in key.Items)
                switch (part.Key)
                {
                    case "NAME" or "START_TIME": break;
                    case "LOCAL_NAME": e.Short(50, Child(animation, part.Text())); break;
                    case "FROM_NODE": flags |= 0x01; e.Short(52, Node(part.Text())); break;
                    case "FROM_NODE_POS": flags |= 0x02; e.Short(52, Node(part.Text())); break;
                    case "FROM_POS": flags |= 0x08; e.Vector(56, Vec(part, 0)); break;
                    case "TO_NODE": flags |= 0x40; e.Short(54, Node(part.Text())); break;
                    case "TO_INPUT_NODE_POS": flags |= 0x200; break;
                    case "TO_POS": flags |= 0x400; e.Vector(68, Vec(part, 0)); break;
                    default: compiler.Warn($"{key.Source}: CALL_OBJECT_CONNECTOR {part.Key} was ignored."); break;
                }
            e.UInt(12, flags);
        }

        private void Camera(AnimationItem key, E e)
        {
            Track(Req(key, "NAME")); e.Int(16, Node(Req(key, "NAME"))); uint channels = 0;
            if (key.Item("NEAR_CLIP") is { } near) { channels |= 1; e.Float(20, near.Number()); }
            if (key.Item("FAR_CLIP") is { } far) { channels |= 2; e.Float(24, far.Number()); }
            // Zoom factors are stored as given, in the viewport fields.
            if (key.Item("H_ZOOM") is { } h) { channels |= 0x20; e.Float(40, h.Number()); }
            if (key.Item("V_ZOOM") is { } v) { channels |= 0x40; e.Float(44, v.Number()); }
            e.UInt(12, channels);
        }
        private void CameraFromTo(AnimationItem key, E e)
        {
            Track(Req(key, "NAME")); e.Int(16, Node(Req(key, "NAME"))); uint channels = 0; float time = Time(key);
            if (key.Item("H_ZOOM_FROM_TO") is { } h) { channels |= 0x20; Triple(e, 80, h.Number(0), h.Number(1), time); }
            if (key.Item("V_ZOOM_FROM_TO") is { } v) { channels |= 0x40; Triple(e, 92, v.Number(0), v.Number(1), time); }
            e.UInt(12, channels); e.Float(104, time);
        }

        private void CallAnimation(AnimationItem key, E e)
        {
            string animation = Req(key, "NAME"); e.Text(12, Bound(animation), 32); e.Short(50, -1);
            ushort flags = 0;
            if (key.Item("AT_NODE") is { } at)
            {
                flags |= 1; e.Short(52, Node(at.Text()));
                if (at.Scalars.Count >= 4) { flags |= 2; e.Vector(56, Vec(at, 1)); }
                if (at.Scalars.Count >= 7) { flags |= 4; e.Vector(68, Radians(Vec(at, 4))); }
            }
            if (key.Item("WITH_NODE") is { } with) { flags |= 8; e.Short(52, Node(with.Text())); }
            if (key.Item("OPERAND_NODE") is { } operand) e.Short(44, Node(operand.Text()));
            // A local name or a wait keeps the child instance, so the launcher can address it.
            if (key.Item("LOCAL_NAME") is { } local) e.Short(50, Child(animation, local.Text()));
            else if (key.Has("WAIT_FOR_COMPLETION")) e.Short(50, Child(animation, ""));
            if (key.Has("WAIT_FOR_COMPLETION")) flags |= 0x10;
            e.Short(46, (short)flags);
        }

        private void Fog(AnimationItem key, E e)
        {
            e.Text(12, "default_fog_name", 32); uint fields = 0;
            if (key.Item("TYPE") is { } type) e.Int(48, type.Text() == "OFF" ? 0 : 1);
            if (key.Item("COLOR") is { } color) { fields |= 2; e.Vector(52, Vec(color, 0)); }
            if (key.Item("ALTITUDE") is { } altitude) { fields |= 4; e.Float(64, altitude.Number(0)); e.Float(68, altitude.Number(1)); }
            if (key.Item("RANGE") is { } range) { fields |= 8; e.Float(72, range.Number(0)); e.Float(76, range.Number(1)); }
            e.UInt(44, fields);
        }

        private void Condition(AnimationItem key, E e)
        {
            if (key.Item("ANIMATION_LOD") is { } lod) { e.UInt(12, 4); e.Int(20, lod.Text() switch { "HIGH" => 2, "MEDIUM" => 1, "LOW" => 0, var other => throw lod.Error($"unknown ANIMATION_LOD {other}.") }); }
            else if (key.Item("PLAYER_RANGE") is { } range) { e.UInt(12, 2); float r = range.Number(); e.Float(20, (float)((double)r * r)); }
            else if (key.Item("RANDOM_WEIGHT") is { } weight) { e.UInt(12, 1); e.Float(20, weight.Number()); }
            else if (key.Item("NODE_UNDERCOVER") is { } cover) { e.UInt(12, 0x10); e.Int(16, Node(cover.Text(0))); e.Float(20, cover.Number(1)); }
            else throw key.Error($"{key.Key} needs a condition.");
        }

        private void Wave(AnimationItem key, E e)
        {
            float time = Time(key); uint flags = 0;
            if (key.Item("AT_NODE") is { } at) { flags |= 0x02; flags |= (uint)(ushort)Node(at.Text()) << 16; }
            if (key.Item("SCREEN_POS_FROM") is { } posFrom)
            {
                flags |= 0x01; var posTo = key.Item("SCREEN_POS_TO") ?? posFrom;
                Triple(e, 28, posFrom.Number(0), posTo.Number(0), time); Triple(e, 40, posFrom.Number(1), posTo.Number(1), time);
            }
            if (key.Item("SCREEN_RADIUS_FROM") is { } radiusFrom) { flags |= 0x04; Triple(e, 60, radiusFrom.Number(), (key.Item("SCREEN_RADIUS_TO") ?? radiusFrom).Number(), time); }
            if (key.Item("WORLD_RADIUS_FROM") is { } worldFrom) { flags |= 0x08; e.Float(52, worldFrom.Number()); e.Float(56, (key.Item("WORLD_RADIUS_TO") ?? worldFrom).Number()); }
            if (key.Item("CSIN_FROM") is { } csinFrom)
            {
                var csinTo = key.Item("CSIN_TO") ?? csinFrom;
                for (int i = 0; i < 3; i++) Triple(e, 72 + i * 12, csinFrom.Number(i), csinTo.Number(i), time);
            }
            e.UInt(12, flags); e.Float(108, time);
        }

        // ------------------------------------------------------------ helpers

        private static short Override(AnimationItem? end) => end == null || end.Scalars.Count < 2 ? (short)-1 : (short)(end.Text(1) switch { "ON" => 1, "OFF" => 0, var other => throw end.Error($"unknown opacity override {other}.") });
        private string Req(AnimationItem key, string name) => key.TextOf(name) ?? throw key.Error($"{key.Key} needs {name}.");
        private static float Time(AnimationItem key) => key.Item("RUN_TIME")?.Number() ?? 0;
        /// <summary>A rate as the original compiler computed it: float operands, double arithmetic.</summary>
        private static float Rate(float from, float to, float time) => time == 0 ? 0 : (float)(((double)to - from) / time);
        private static void Triple(E e, int offset, float from, float to, float time) { e.Float(offset, from); e.Float(offset + 4, to); e.Float(offset + 8, Rate(from, to, time)); }
        private static Vector3 Vec(AnimationItem key, int start) => new(key.Number(start), key.Number(start + 1), key.Number(start + 2));
        private static Vector3 Offset(AnimationItem at, int start) => at.Scalars.Count >= start + 3 ? Vec(at, start) : Vector3.Zero;
        /// <summary>Degrees to radians in double precision, as every shipped angle except the launch yaw.</summary>
        private static float Radians(float degrees) => (float)(degrees * Math.PI / 180);
        private static Vector3 Radians(Vector3 degrees) => new(Radians(degrees.X), Radians(degrees.Y), Radians(degrees.Z));

        private static void Text(byte[] target, int offset, string text, int size, string what)
        {
            if (text.Length >= size || text.Any(c => c > 255 || c == 0)) throw new InvalidDataException($"{what} '{text}' needs 1–{size - 1} Latin-1 characters.");
            Encoding.Latin1.GetBytes(text, target.AsSpan(offset, text.Length));
        }

        private sealed class E(byte[] bytes)
        {
            public byte[] Bytes { get; } = bytes;
            public void Int(int o, int v) => BinaryPrimitives.WriteInt32LittleEndian(Bytes.AsSpan(o), v);
            public void UInt(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(o), v);
            public void Short(int o, short v) => BinaryPrimitives.WriteInt16LittleEndian(Bytes.AsSpan(o), v);
            public void Float(int o, float v) => BinaryPrimitives.WriteSingleLittleEndian(Bytes.AsSpan(o), v);
            public float Get(int o) => BinaryPrimitives.ReadSingleLittleEndian(Bytes.AsSpan(o));
            public void Vector(int o, Vector3 v) { Float(o, v.X); Float(o + 4, v.Y); Float(o + 8, v.Z); }
            public void Text(int o, string text, int size) => EntryBuilder.Text(Bytes, o, text, size, "name");
        }
    }
}
