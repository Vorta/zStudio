using System.Buffers.Binary;
using System.Numerics;

namespace zStudio.Core.Animation;

public sealed partial class AnimationCompiler
{
    internal static Vector3 Mw3LaunchDirection(float yawDegrees, float pitchDegrees)
    {
        float fraction = pitchDegrees / 90f, horizontal = 1f - Math.Abs(fraction), radians = (float)(yawDegrees * (Math.PI / 180));
        return new(-(float)Math.Sin(radians) * horizontal, fraction, -(float)Math.Cos(radians) * horizontal);
    }
    private sealed partial class EntryBuilder
    {
        private void CompleteMw3Event(AnimationItem key, E e)
        {
            if (e.Bytes[0] == 4)
            {
                // The native record inserts subdivide/static switches before the basis node.
                e.Bytes.AsSpan(68, 56).CopyTo(e.Bytes.AsSpan(76)); e.Bytes.AsSpan(68, 8).Clear();
                uint fields = BinaryPrimitives.ReadUInt32LittleEndian(e.Bytes.AsSpan(48));
                Switch("SUBDIVIDE", 68, 0x200); Switch("STATIC", 72, 0x400);
                Switch("DIRECTIONAL", 60, 0x80);
                if (key.Item("TYPE") is { } type) e.Int(56, type.Text() == "POINT" ? 1 : 0);
                if (key.Item("AMBIENT") is { } ambient) { e.Float(124, ambient.Number()); fields |= 0x20; }
                if (key.Item("DIFFUSE") is { } diffuse) { e.Float(128, diffuse.Number()); fields |= 0x40; }
                e.UInt(48, fields);
                void Switch(string name, int offset, uint bit)
                {
                    if (key.Item(name) is not { } item) return;
                    e.Int(offset, item.Text() is "TRUE" or "ON" ? 1 : 0); fields |= bit;
                }
            }
            else if (e.Bytes[0] == 10)
            {
                e.Bytes.AsSpan(248, 4).CopyTo(e.Bytes.AsSpan(328)); e.Bytes.AsSpan(248, 80).Clear();
                e.Short(280, -1); e.Short(320, -1);
                uint flags = BinaryPrimitives.ReadUInt32LittleEndian(e.Bytes.AsSpan(12));
                Collision("WATER", 248, 280, 282, 284, 0x800, 0x1000);
                Collision("LAVA", 288, 320, 322, 324, 0x800, 0x1000);
                if (key.Item("IMPACT_FORCE")?.Text() == "ON") flags |= 2;
                if (key.Item("GRAVITY")?.Text() == "COMPLEX") flags |= 0x2000;
                if (key.Item("GRAVITY") is { } gravity && gravity.Scalars.Any(s => s.Kind == Formats.ZrdKind.String && s.Text == "NO_ALTITUDE")) flags |= 0x4000;
                if (key.Item("FORWARD_ROTATION")?.Text() == "DISTANCE") flags = (flags & ~0x80u) | 0x40;
                e.UInt(12, flags);
                void Collision(string medium, int name, int cache, int sample, int volume, uint sequenceBit, uint soundBit)
                {
                    if (key.Item("BOUNCE_SEQUENCE_" + medium) is { } sequence)
                    { e.Text(name, Bound(sequence.Text()), 32); e.Short(cache, -1); flags |= sequenceBit; }
                    if (key.Item("BOUNCE_SOUND_" + medium) is { } sound)
                    {
                        e.Short(sample, Sample(sound.TextOf("NAME") ?? throw sound.Error("Collision sound needs NAME.")));
                        if (sound.Item("FULL_VOLUME_VELOCITY") is { } speed) e.Float(volume, speed.Number());
                        flags |= soundBit;
                    }
                }
            }
        }

        private AnimationEvent Mw3Event(AnimationItem key)
        {
            byte type = key.Key == "DETONATE_WEAPON" ? (byte)41 : (byte)42;
            E e = new(new byte[type == 41 ? 36 : 592]); e.Bytes[0] = type; e.Bytes[1] = 1; e.Int(4, e.Bytes.Length);
            if (key.Item("START_TIME") is { } timing) { var (mode, threshold) = Start(timing); e.Bytes[1] = mode; e.Float(8, threshold); }
            if (type == 41)
            {
                e.Text(12, Bound(Req(key, "WEAPON")), 10);
                if (key.Item("AT_NODE") is { } at) { e.Short(22, Node(at.Text())); e.Vector(24, Offset(at, 1)); }
                return new(e.Bytes) { Version = 39 };
            }
            string puffer = Bound(Req(key, "NAME")); e.Text(12, puffer, 32); e.Int(44, Puffer(puffer));
            uint fields = 0; e.Int(52, -1);
            if (key.Item("AT_NODE") is { } basis)
            {
                e.Int(52, Node(basis.Text())); fields |= 2;
                if (basis.Scalars.Count > 1) { e.Vector(60, Offset(basis, 1)); fields |= 1; }
            }
            if (key.Item("ACTIVE_STATE") is { } active) { e.Int(56, active.Text() == "ACTIVE" ? 1 : 0); fields |= 4; }
            Vector("TRANSLATION", 60, 1); Vector("LOCAL_VELOCITY", 72, 8); Vector("WORLD_VELOCITY", 84, 0x10);
            Vector("MIN_RANDOM_VELOCITY", 96, 0x20); Vector("MAX_RANDOM_VELOCITY", 108, 0x40); Vector("WORLD_ACCELERATION", 120, 0x10000);
            if (key.Item("TIME_INTERVAL") is { } time) { e.Int(132, 0); e.Float(136, time.Number()); fields |= 0x100; }
            if (key.Item("DISTANCE_INTERVAL") is { } distance) { e.Int(132, 1); e.Float(136, distance.Number()); fields |= 0x80; }
            Range("SIZE_RANGE", 140, 0x200); Range("LIFETIME_RANGE", 148, 0x400); Range("START_AGE_RANGE", 156, 0x8000);
            Scalar("DEVIATION_DISTANCE", 164, 0x800); Range("FADE_IN_RANGE", 168, 0x80000); Range("FADE_RANGE", 176, 0x1000);
            Scalar("FRICTION", 184, 0x20000); Scalar("WIND_FACTOR", 188, 0x100000);
            if (key.Item("PARTICLES_PER_EMISSION") is { } count) { e.Int(192, count.Integer()); fields |= 0x200000; }
            if (key.Item("TEXTURES") is { } textures)
            {
                if (textures.Scalars.Count > 6) throw textures.Error("At most six puffer textures.");
                for (int i = 0; i < textures.Scalars.Count; i++) e.Text(204 + 36 * i, Bound(textures.Text(i)), 32);
                fields |= 0x4000;
            }
            if (key.Item("GROWTH_FACTOR") is { } growth)
            { e.Int(540, 2); e.Float(548, 1); e.Float(552, 1); e.Float(556, growth.Number()); fields |= 0x2000; }
            e.UInt(48, fields);
            return new(e.Bytes) { Version = 39 };
            void Vector(string name, int offset, uint bit) { if (key.Item(name) is { } item) { e.Vector(offset, Vec(item, 0)); fields |= bit; } }
            void Scalar(string name, int offset, uint bit) { if (key.Item(name) is { } item) { e.Float(offset, item.Number()); fields |= bit; } }
            void Range(string name, int offset, uint bit) { if (key.Item(name) is { } item) { e.Float(offset, item.Number()); e.Float(offset + 4, item.Number(1)); fields |= bit; } }
        }

        private int Puffer(string name)
        {
            if (puffers.Count == 0) puffers.Add(new byte[44]);
            for (int i = 1; i < puffers.Count; i++) if (Name(puffers[i], 36) == name) return i;
            if (puffers.Count == 255) throw item.Error("More than 254 puffer references.");
            byte[] record = new byte[44]; Text(record, 0, name, 36, "puffer"); puffers.Add(record); return puffers.Count - 1;
        }
    }
}
