using System.Globalization;
using System.Numerics;
using System.Text;

namespace Recoil.Zbd.Core.Animation;

/// <summary>
/// A keyframe script (<c>.zan</c>) for <c>OBJECT_MOTION_SI_SCRIPT</c>. The original scripts (exported from Softimage
/// scenes) did not survive, so this text format is zStudio's. A script holds a track per object of its scene, and the
/// definition's NAME picks the track. Each line of a track is a key at a frame of the definition's
/// <c>SCRIPT_FRAME_RATE</c>; the channels it lists start a segment that runs to the next key, and the last key's frame
/// ends the track.
/// </summary>
/// <remarks>
/// <code>
/// OBJECT copter01
/// FRAME 0 POSITION 1429.22 56.43 3107.5 ROTATION 0.929 -0.128 -0.319 0.136 SCALE 1 1 1
/// FRAME 5 POSITION 1440.95 53.52 3092.22 VELOCITY 31.1 -8.3 -48.8 ROTATION 0.941 -0.121 -0.279 0.148
/// FRAME 2599
/// </code>
/// A script without OBJECT lines is one track that any node may use. Frames may go back: the engine plays a reversed
/// segment as authored (the shipped m3puloop.zan steps from frame 45 back to 40).
/// POSITION and SCALE are XYZ, ROTATION a quaternion (W X Y Z). A rate may follow its channel: VELOCITY (units per
/// second), SPIN (rotation vector, half-angle radians per second) and GROWTH (scale per second). An omitted rate moves
/// the channel to its value at the next key that lists it, reached at that key's time (or holds it if none does); a key
/// that lists it again at the same frame is a cut.
/// Reconstructed scripts list every rate, so they compile to the shipped keyframes exactly. <c>#</c> starts a comment.
/// </remarks>
public static class AnimationScript
{
    public const string Extension = ".zan";
    /// <summary>Keys in one object track; a file can contain several independently bounded tracks.</summary>
    public const int MaximumKeys = 65_536;
    /// <summary>At most 64 MiB of conservatively estimated decoded keys (256 bytes each) in one file.</summary>
    public const int MaximumTotalKeys = 4 * MaximumKeys;

    public sealed class Key
    {
        public int Frame { get; set; }
        public Vector3? Position, Scale; public Quaternion? Rotation;
        public Vector3? Velocity, Growth, Spin;
    }

    /// <summary>A script's tracks by object name (null for a script without OBJECT lines), in file order.</summary>
    public static List<(string? Object, List<Key> Keys)> Parse(ReadOnlySpan<byte> bytes, string source, CancellationToken token = default)
        => Parse(bytes, source, token, null);

    internal static List<(string? Object, List<Key> Keys)> Parse(ReadOnlySpan<byte> bytes, string source, CancellationToken token, Action? reserveKey)
    {
        token.ThrowIfCancellationRequested();
        if (bytes.Length > Sources.SourceProject.MaximumSourceTextBytes)
            throw new InvalidDataException($"{JsonData.ShownText(source)} exceeds the {Sources.SourceProject.MaximumSourceTextBytes:N0}-byte keyframe source limit.");
        List<(string? Object, List<Key> Keys)> tracks = [];
        HashSet<string> objects = new(StringComparer.Ordinal);
        List<Key>? keys = null; int total = 0;
        string text = Encoding.Latin1.GetString(bytes);
        int lineNumber = 0;
        foreach (string raw in text.Split('\n'))
        {
            token.ThrowIfCancellationRequested();
            lineNumber++;
            string line = raw; int hash = line.IndexOf('#'); if (hash >= 0) line = line[..hash];
            var tokens = line.Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) continue;
            if (tokens[0].Equals("OBJECT", StringComparison.OrdinalIgnoreCase))
            {
                if (tokens.Length != 2) throw Error("OBJECT is followed by one object name.");
                if (tracks.Count > 0 && tracks[0].Object == null) throw Error("keys before the first OBJECT line belong to no object.");
                if (!objects.Add(tokens[1])) throw Error($"object {tokens[1]} has two tracks.");
                Close(); keys = []; tracks.Add((tokens[1], keys));
                continue;
            }
            if (keys == null) { keys = []; tracks.Add((null, keys)); }
            if (keys.Count >= MaximumKeys) throw Error($"more than {MaximumKeys} keys in one track.");
            if (++total > MaximumTotalKeys) throw Error($"more than {MaximumTotalKeys} keys across the script's tracks.");
            if (!tokens[0].Equals("FRAME", StringComparison.OrdinalIgnoreCase) || tokens.Length < 2 || !int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int frame) || frame < 0)
                throw Error("a key starts with FRAME and a frame number of 0 or more.");
            reserveKey?.Invoke(); // The compiler's shared retained-key allowance, before creating the next key.
            Key key = new() { Frame = frame };
            for (int i = 2; i < tokens.Length;)
            {
                string channel = tokens[i].ToUpperInvariant(); int count = channel == "ROTATION" ? 4 : 3;
                if (channel is not ("POSITION" or "ROTATION" or "SCALE" or "VELOCITY" or "SPIN" or "GROWTH")) throw Error($"unknown channel {tokens[i]}.");
                float[] v = new float[count];
                for (int j = 0; j < count; j++)
                    if (i + 1 + j >= tokens.Length || !float.TryParse(tokens[i + 1 + j], NumberStyles.Float, CultureInfo.InvariantCulture, out v[j]) || !float.IsFinite(v[j]))
                        throw Error($"{channel} needs {count} finite numbers.");
                switch (channel)
                {
                    case "POSITION": key.Position = new(v[0], v[1], v[2]); break;
                    case "SCALE": key.Scale = new(v[0], v[1], v[2]); break;
                    case "ROTATION":
                        // The engine turns the quaternion into a matrix (0x45AE90); a zero one collapses the node.
                        if (v[0] * (double)v[0] + v[1] * (double)v[1] + v[2] * (double)v[2] + v[3] * (double)v[3] < 1e-10) throw Error("ROTATION needs a quaternion that is not zero.");
                        key.Rotation = new(v[1], v[2], v[3], v[0]); break;
                    case "VELOCITY": key.Velocity = new(v[0], v[1], v[2]); break;
                    case "GROWTH": key.Growth = new(v[0], v[1], v[2]); break;
                    case "SPIN": key.Spin = new(v[0], v[1], v[2]); break;
                }
                i += 1 + count;
            }
            if (key.Velocity.HasValue && !key.Position.HasValue || key.Growth.HasValue && !key.Scale.HasValue || key.Spin.HasValue && !key.Rotation.HasValue)
                throw Error("a rate follows the channel it belongs to.");
            keys.Add(key);
        }
        Close();
        if (tracks.Count == 0) throw new InvalidDataException($"{source}: the script has no keys.");
        return tracks;
        InvalidDataException Error(string message) => new($"{source}, line {lineNumber}: {message}");
        void Close()
        {
            if (tracks.Count == 0) return;
            var (name, last) = tracks[^1];
            string what = name == null ? source : $"{source}, object {name}";
            if (last.Count < 2) throw new InvalidDataException($"{what}: a track needs at least two keys (its last key ends the motion).");
            if (last.Take(last.Count - 1).All(k => k.Position == null && k.Rotation == null && k.Scale == null)) throw new InvalidDataException($"{what}: no key moves anything.");
        }
    }

    /// <summary>The track for <paramref name="name"/>: its OBJECT section, or the only track of a script without sections.</summary>
    public static List<Key>? Track(IReadOnlyList<(string? Object, List<Key> Keys)> tracks, string name) =>
        tracks.FirstOrDefault(t => t.Object == name).Keys ?? (tracks.Count == 1 && tracks[0].Object == null ? tracks[0].Keys : null);

    /// <summary>The keyframes of a script at <paramref name="frameRate"/> frames per second, as the original compiler wrote them.</summary>
    public static List<AnimationKeyframe> Compile(IReadOnlyList<Key> keys, float frameRate, string source, CancellationToken token = default)
    {
        if (!(frameRate > 0) || !float.IsFinite(frameRate)) throw new InvalidDataException($"{source}: SCRIPT_FRAME_RATE must be above 0.");
        List<AnimationKeyframe> frames = [];
        float step = FrameStep(frameRate);
        for (int i = 0; i + 1 < keys.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var key = keys[i];
            float time = keys[i].Frame * step, end = keys[i + 1].Frame * step;
            if (!float.IsFinite(time) || !float.IsFinite(end)) throw new InvalidDataException($"{source}: frame {keys[i + 1].Frame} is beyond single-precision time at {frameRate} frames per second.");
            int flags = (key.Position.HasValue ? 1 : 0) | (key.Rotation.HasValue ? 2 : 0) | (key.Scale.HasValue ? 4 : 0);
            if (flags == 0) continue;
            var frame = AnimationKeyframe.Create(flags); frame.Start = time; frame.End = end;
            if (key.Position is { } p)
            {
                var rate = key.Velocity ?? Rate(i, k => k.Position, p);
                int o = frame.ChannelOffset(0); frame.SetVector(o, p); frame.SetFloat(o + 12, 0); frame.SetVector(o + 16, rate);
            }
            if (key.Rotation is { } q)
            {
                var spin = key.Spin ?? SpinTo(i, q);
                int o = frame.ChannelOffset(1); frame.SetFloat(o, q.W); frame.SetFloat(o + 4, q.X); frame.SetFloat(o + 8, q.Y); frame.SetFloat(o + 12, q.Z); frame.SetVector(o + 16, spin);
            }
            if (key.Scale is { } s)
            {
                var rate = key.Growth ?? Rate(i, k => k.Scale, s);
                int o = frame.ChannelOffset(2); frame.SetVector(o, s); frame.SetFloat(o + 12, 0); frame.SetVector(o + 16, rate);
            }
            frames.Add(frame);
        }
        return frames;

        // The rate that reaches the channel's value at the next key listing it (float operands, double arithmetic). A
        // key listing it again at the same frame is a cut: the zero-length segment holds its value (the engine samples
        // it at time 0), so its rate is zero rather than a division by zero.
        Vector3 Rate(int index, Func<Key, Vector3?> channel, Vector3 from)
        {
            for (int j = index + 1; j < keys.Count; j++)
                if (channel(keys[j]) is { } to)
                {
                    double seconds = (keys[j].Frame - keys[index].Frame) / (double)frameRate;
                    return seconds == 0 ? Vector3.Zero : Finite(new((float)((to.X - (double)from.X) / seconds), (float)((to.Y - (double)from.Y) / seconds), (float)((to.Z - (double)from.Z) / seconds)), j);
                }
            return Vector3.Zero;
        }
        Vector3 SpinTo(int index, Quaternion from)
        {
            for (int j = index + 1; j < keys.Count; j++)
                if (keys[j].Rotation is { } to)
                {
                    double seconds = (keys[j].Frame - keys[index].Frame) / (double)frameRate;
                    return seconds == 0 ? Vector3.Zero : Finite((Vector3)(Log(to, from) / seconds), j);
                }
            return Vector3.Zero;
        }
        Vector3 Finite(Vector3 rate, int to) => float.IsFinite(rate.X) && float.IsFinite(rate.Y) && float.IsFinite(rate.Z) ? rate
            : throw new InvalidDataException($"{source}: reaching the value at frame {keys[to].Frame} needs a rate beyond single precision.");
    }

    /// <summary>
    /// A frame's time is its number times the single-precision frame length, as the shipped streams show: frame 2595 at
    /// 15 per second starts at 173.00002 (2595 × 0.06666667).
    /// </summary>
    public static float FrameStep(float frameRate) => 1f / frameRate;

    /// <summary>The spin (half-angle rotation vector) turning <paramref name="from"/> into <paramref name="to"/>: to = exp(spin)·from.</summary>
    private static DoubleVector Log(Quaternion to, Quaternion from)
    {
        double fw = from.W, fx = from.X, fy = from.Y, fz = from.Z, tw = to.W, tx = to.X, ty = to.Y, tz = to.Z;
        // delta = to · conj(from)
        double w = tw * fw + tx * fx + ty * fy + tz * fz;
        double x = -tw * fx + tx * fw - ty * fz + tz * fy;
        double y = -tw * fy + tx * fz + ty * fw - tz * fx;
        double z = -tw * fz - tx * fy + ty * fx + tz * fw;
        if (w < 0) { w = -w; x = -x; y = -y; z = -z; }
        double sine = Math.Sqrt(x * x + y * y + z * z);
        if (sine < 1e-12) return new(0, 0, 0);
        double half = Math.Atan2(sine, w);
        return new(x / sine * half, y / sine * half, z / sine * half);
    }
    private readonly record struct DoubleVector(double X, double Y, double Z)
    {
        public static DoubleVector operator /(DoubleVector v, double d) => new(v.X / d, v.Y / d, v.Z / d);
        public static explicit operator Vector3(DoubleVector v) => new((float)v.X, (float)v.Y, (float)v.Z);
    }

    /// <summary>Whether an OBJECT line can hold <paramref name="name"/>: one Latin-1 token without comment marks.</summary>
    public static bool IsObjectName(string name) => name.Length > 0 && !name.Any(c => c is ' ' or '\t' or '\r' or '\n' or '#' or '\0' || c > 255);

    /// <summary>A script file of object tracks (each from <see cref="Decompile"/>), in the given order.</summary>
    public static string Write(IEnumerable<(string Object, string Track)> tracks, CancellationToken token = default)
    {
        StringBuilder text = new("# RECOIL keyframe script, reconstructed by zStudio: a track per OBJECT, keys as FRAME n and\n# channels with their rates per second.\n");
        int total = 0;
        foreach (var (name, track) in tracks)
        {
            token.ThrowIfCancellationRequested();
            if (!IsObjectName(name)) throw new InvalidDataException($"'{name}' cannot name a script track.");
            if (8L + name.Length + track.Length > Sources.SourceProject.MaximumSourceTextBytes - text.Length)
                throw new InvalidDataException($"The keyframe script exceeds the {Sources.SourceProject.MaximumSourceTextBytes:N0}-byte source limit.");
            int keys = 0;
            foreach (var raw in track.AsSpan().EnumerateLines())
            {
                token.ThrowIfCancellationRequested();
                var line = raw.TrimStart();
                if (!line.StartsWith("FRAME", StringComparison.OrdinalIgnoreCase) || line.Length > 5 && !char.IsWhiteSpace(line[5])) continue;
                if (++keys > MaximumKeys) throw new InvalidDataException($"A keyframe track exceeds {MaximumKeys:N0} keys.");
                if (++total > MaximumTotalKeys) throw new InvalidDataException($"The keyframe script exceeds {MaximumTotalKeys:N0} keys across its tracks.");
            }
            text.Append("OBJECT ").Append(name).Append('\n').Append(track);
        }
        string result = text.ToString();
        _ = Parse(Encoding.Latin1.GetBytes(result), "reconstructed keyframe script", token);
        return result;
    }

    /// <summary>
    /// A track for compiled keyframes at <paramref name="frameRate"/>: a key per keyframe start with its channels and
    /// rates, and a final key at the last end. Null when the stream is not on the frame grid (it cannot be a script).
    /// </summary>
    public static string? Decompile(IReadOnlyList<AnimationKeyframe> frames, float frameRate)
    {
        if (frames.Count == 0 || !(frameRate > 0)) return null;
        StringBuilder text = new();
        float step = FrameStep(frameRate);
        int? Frame(float time) { int f = (int)Math.Round(time / (double)step); return f >= 0 && f * step == time ? f : null; }
        int? previous = null;
        for (int i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            if (Frame(f.Start) is not int frame || Frame(f.End) is not int next) return null;
            // A segment that does not start where the last ended follows a key without channels, which holds everything.
            if (previous is int end && end != frame) text.Append("FRAME ").Append(end.ToString(CultureInfo.InvariantCulture)).Append('\n');
            text.Append("FRAME ").Append(frame.ToString(CultureInfo.InvariantCulture));
            if (f.ChannelOffset(0) is int p and >= 0) Channel("POSITION", [f.F32(p), f.F32(p + 4), f.F32(p + 8)], "VELOCITY", f.Vector(p + 16));
            if (f.ChannelOffset(1) is int r and >= 0) Channel("ROTATION", [f.F32(r), f.F32(r + 4), f.F32(r + 8), f.F32(r + 12)], "SPIN", f.Vector(r + 16));
            if (f.ChannelOffset(2) is int s and >= 0) Channel("SCALE", [f.F32(s), f.F32(s + 4), f.F32(s + 8)], "GROWTH", f.Vector(s + 16));
            text.Append('\n');
            previous = next;
        }
        text.Append("FRAME ").Append(previous!.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');
        return text.ToString();

        void Channel(string name, float[] values, string rateName, Vector3 rate)
        {
            text.Append(' ').Append(name);
            foreach (float v in values) text.Append(' ').Append(Number(v));
            text.Append(' ').Append(rateName).Append(' ').Append(Number(rate.X)).Append(' ').Append(Number(rate.Y)).Append(' ').Append(Number(rate.Z));
        }
    }
    private static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);
}
