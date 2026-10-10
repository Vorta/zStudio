using System.Globalization;
using System.Numerics;
using System.Text;

namespace Recoil.Zbd.Core.Animation;

/// <summary>
/// Writes compiled keyframes as the SI Animation Script they were compiled from, in the original layout: every
/// frame the compiler kept (and grid frames where every object stood still), every object in every frame block, and
/// the Softimage DKit messages the exporter interleaved (only for scripts of the shipped files). Keyed values are the ones the stored floats came from;
/// rotations are the Euler angles that reproduce the stored quaternion (<see cref="SiRotationSolver"/>); where a
/// channel stops before the next key lists it, its end value is solved from the stored rate. Values between keys
/// that the compiler absorbed are not recorded anywhere: a held channel takes the next key's value right after it
/// stops, which the shipped fragments of the original texts show. The text is compiled again before it is returned
/// and refused unless it reproduces every keyframe bit for bit.
/// </summary>
internal static class SiScriptWriter
{
    private const long Micro = 1_000_000;

    /// <summary>One object's compiled keyframes and the frame rate they were compiled at.</summary>
    public sealed record Track(string Object, IReadOnlyList<AnimationKeyframe> Frames, float FrameRate);

    /// <summary>
    /// The Softimage version in the DKit warning (null: no DKit messages, as in scripts that did not come from the
    /// shipped files), and whether the messages precede each frame (or follow it).
    /// </summary>
    public sealed record Layout(string? Version, bool WarningsBeforeFrames = true);

    private enum Channel { Scale, Rotation, Position }
    private enum Pin : byte { None, Key, End }
    /// <summary>"-0.000000", which reads back as negative zero; every other value is its number of millionths.</summary>
    private const long NegativeZero = long.MinValue;
    private static double Value(long micros) => micros == NegativeZero ? -0.0 : micros / 1e6;
    private static double Millionths(long micros) => micros == NegativeZero ? 0 : micros;

    private sealed class Key
    {
        public int Frame;
        public float[]? Position, Velocity, Scale, Growth;
        public SiMath.Quat? Rotation; public (float X, float Y, float Z) Spin;
        public bool Has(Channel c) => c switch { Channel.Position => Position != null, Channel.Rotation => Rotation != null, _ => Scale != null };
        public bool Bare => Position == null && Rotation == null && Scale == null;
    }

    /// <summary>
    /// The script text, or an <see cref="InvalidDataException"/> saying why the keyframes have none (or why finding it
    /// would take more than <paramref name="evaluations"/> rotation candidates, or the text would be larger than
    /// <paramref name="maximumBytes"/>, the most a project reads).
    /// </summary>
    public static string Write(IReadOnlyList<Track> tracks, Layout layout, CancellationToken token, long evaluations = 2_000_000_000,
        int maximumBytes = Sources.SourceProject.MaximumSourceTextBytes, Action<long>? reserveText = null)
    {
        token.ThrowIfCancellationRequested();
        if (tracks.Count == 0) throw new InvalidDataException("no tracks.");
        foreach (var track in tracks)
        {
            token.ThrowIfCancellationRequested();
            CheckName(track.Object);
        }
        var keys = tracks.Select(t => Keys(t, token)).ToList();
        var frames = Sequence(keys, token);
        FrameIndex frameIndex = new(frames, token);
        var ranges = keys.Select((k, i) => frameIndex.Range(k.Select(key => key.Frame), tracks[i].Object)).ToArray();
        // Before any rotation is searched for: each object is written in the frames from its first key to its last, in at
        // least 133 bytes besides its name, and its keyed positions and scales need six-decimal texts.
        long poses = 0, smallest = 0;
        for (int i = 0; i < tracks.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            long span = ranges[i].Count;
            poses += span; smallest += span * (133 + tracks[i].Object.Length);
            foreach (var key in keys[i])
            {
                token.ThrowIfCancellationRequested();
                if (key.Position != null) Micros(key.Position, tracks[i], key.Frame);
                if (key.Scale != null) Micros(key.Scale, tracks[i], key.Frame);
            }
        }
        if (poses > SiAnimationScript.MaximumPoses) throw new InvalidDataException($"the script would hold more than {SiAnimationScript.MaximumPoses} object poses.");
        if (smallest > maximumBytes) throw TooLarge(maximumBytes);
        List<(string Object, int Start, long[]?[] Values)> objects = [];
        SiRotationSolver.Budget budget = new(evaluations);
        for (int i = 0; i < tracks.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var (start, count) = ranges[i];
            objects.Add((tracks[i].Object, start, Values(tracks[i], keys[i], frames.GetRange(start, count), budget, token)));
        }
        string text = Text(objects, frames, layout, token, maximumBytes, reserveText);
        Verify(text, tracks, token);
        return text;

        static InvalidDataException TooLarge(int maximumBytes) => new($"the script would be larger than {maximumBytes:N0} bytes, the most a project reads.");
    }

    /// <summary>Greedy subsequence matching without rescanning every global frame for each sparse object.
    /// Frame values may repeat or run backwards; positions always advance, just as Values does.</summary>
    internal sealed class FrameIndex
    {
        private readonly Dictionary<int, List<int>> positions = [];
        private readonly CancellationToken token;
        internal FrameIndex(IReadOnlyList<int> frames, CancellationToken token = default)
        {
            this.token = token;
            token.ThrowIfCancellationRequested();
            for (int i = 0; i < frames.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                int frame = frames[i];
                token.ThrowIfCancellationRequested();
                if (!positions.TryGetValue(frame, out var list)) positions[frame] = list = [];
                list.Add(i);
            }
        }
        internal (int Start, int Count) Range(IEnumerable<int> keys, string name)
        {
            token.ThrowIfCancellationRequested();
            int first = -1, last = -1;
            foreach (int key in keys)
            {
                token.ThrowIfCancellationRequested();
                if (!positions.TryGetValue(key, out var list)) throw Missing();
                int at = list.BinarySearch(last + 1); if (at < 0) at = ~at;
                if (at >= list.Count) throw Missing();
                last = list[at]; if (first < 0) first = last;
            }
            if (first < 0) throw Missing();
            return (first, last - first + 1);
            InvalidDataException Missing() => new($"{JsonData.ShownText(name)}'s keys do not follow the script's frames.");
        }
    }

    /// <summary>The keys of a compiled stream: a key at each segment start (with its channels and rates), a bare key
    /// where a segment does not start at the previous end (a gap), and a bare key at the last end.</summary>
    private static List<Key> Keys(Track track, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (track.Frames.Count == 0) throw new InvalidDataException($"{JsonData.ShownText(track.Object)} has no keyframes.");
        List<Key> keys = []; int? previous = null;
        foreach (var f in track.Frames)
        {
            token.ThrowIfCancellationRequested();
            int start = FrameOf(f.Start, track), end = FrameOf(f.End, track);
            if (previous is int p && p != start) keys.Add(new() { Frame = p });
            Key key = new() { Frame = start };
            if ((f.Flags & ~7) != 0 || (f.Flags & 7) == 0) throw new InvalidDataException($"{JsonData.ShownText(track.Object)} has a keyframe without channels.");
            if (f.ChannelOffset(0) is int o0 and >= 0) { key.Position = Floats(f, o0, 3); key.Velocity = Floats(f, o0 + 16, 3); }
            if (f.ChannelOffset(1) is int o1 and >= 0) { key.Rotation = new(f.F32(o1), f.F32(o1 + 4), f.F32(o1 + 8), f.F32(o1 + 12)); key.Spin = (f.F32(o1 + 16), f.F32(o1 + 20), f.F32(o1 + 24)); }
            if (f.ChannelOffset(2) is int o2 and >= 0) { key.Scale = Floats(f, o2, 3); key.Growth = Floats(f, o2 + 16, 3); }
            keys.Add(key);
            previous = end;
        }
        keys.Add(new() { Frame = previous!.Value });
        // A script holds one pose per frame: two keys at one frame (a cut) cannot be written.
        for (int i = 1; i < keys.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (keys[i].Frame == keys[i - 1].Frame) throw new InvalidDataException($"{JsonData.ShownText(track.Object)} has two keys at frame {keys[i].Frame}, which a script frame cannot hold.");
        }
        return keys;

        static float[] Floats(AnimationKeyframe f, int offset, int count) => [.. Enumerable.Range(0, count).Select(i => f.F32(offset + 4 * i))];
    }

    private static int FrameOf(float time, Track track)
    {
        double frame = Math.Round(time / SiMath.SecondsPerFrame(track.FrameRate));
        if (!(frame >= 0 && frame < int.MaxValue) || SiMath.Bits(SiMath.KeyTime((int)frame, track.FrameRate)) != SiMath.Bits(time))
            throw new InvalidDataException($"{JsonData.ShownText(track.Object)} has a keyframe at {time.ToString("R", CultureInfo.InvariantCulture)} s, which is not on the {track.FrameRate.ToString("R", CultureInfo.InvariantCulture)}/s frame grid.");
        return (int)frame;
    }

    /// <summary>
    /// The script's frames: a track that steps back defines them; otherwise every object's key frames, plus frames on
    /// the script's most common key spacing where every object is inside a gap (the exporter wrote every step there).
    /// </summary>
    private static List<int> Sequence(List<List<Key>> tracks, CancellationToken token)
    {
        foreach (var keys in tracks)
            for (int i = 1; i < keys.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                if (keys[i].Frame < keys[i - 1].Frame)
                {
                    if (keys.Count > SiAnimationScript.MaximumFrames) Bounded(null);
                    List<int> backwards = new(keys.Count);
                    foreach (var key in keys)
                    {
                        token.ThrowIfCancellationRequested();
                        backwards.Add(key.Frame);
                    }
                    return backwards;
                }
            }
        HashSet<int> distinct = [];
        foreach (var track in tracks)
            foreach (var key in track)
            {
                token.ThrowIfCancellationRequested();
                if (!distinct.Contains(key.Frame) && distinct.Count >= SiAnimationScript.MaximumFrames) Bounded(null);
                distinct.Add(key.Frame);
            }
        var keyed = distinct.Order().ToList();
        // The most common spacing, the first seen among equals.
        Dictionary<int, int> counts = []; List<int> order = [];
        for (int i = 1; i < keyed.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            int d = keyed[i] - keyed[i - 1];
            if (counts.TryAdd(d, 1)) order.Add(d); else counts[d]++;
        }
        int step = order.Count == 0 ? 1 : order.OrderByDescending(d => counts[d]).First();
        if (keyed.Take(keyed.Count - 1).Any(f => f % step != 0)) return keyed;
        // A change of each track's moving/still state at its keys. Only changes at the current frame matter;
        // scanning every sparse track at every global frame would take tracks × frames work.
        Dictionary<int, int> changes = [];
        foreach (var track in tracks)
        {
            bool moving = false;
            foreach (var key in track)
            {
                token.ThrowIfCancellationRequested();
                bool next = !key.Bare;
                if (next != moving) changes[key.Frame] = changes.GetValueOrDefault(key.Frame) + (next ? 1 : -1);
                moving = next;
            }
        }
        int active = 0;
        List<int> all = [];
        for (int i = 0; i < keyed.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            all.Add(keyed[i]);
            if (i + 1 == keyed.Count) break;
            int a = keyed[i], b = keyed[i + 1];
            active += changes.GetValueOrDefault(a);
            if (active != 0) continue;
            long between = ((long)b - a - 1) / step;
            if (all.Count + between > SiAnimationScript.MaximumFrames) Bounded(null);
            for (long f = (long)a + step; f < b; f += step)
            {
                token.ThrowIfCancellationRequested();
                all.Add((int)f);
            }
        }
        return all;

        static List<int> Bounded(List<int>? frames) => frames != null && frames.Count <= SiAnimationScript.MaximumFrames ? frames
            : throw new InvalidDataException($"the script would have more than {SiAnimationScript.MaximumFrames} frames.");
    }

    /// <summary>The object's scaling, rotation and translation (millionths) at each script frame, or null outside its frames.</summary>
    private static long[]?[] Values(Track track, List<Key> keys, List<int> frames, SiRotationSolver.Budget budget, CancellationToken token)
    {
        // Rotation keys in key order, each solved near the previous one.
        Dictionary<int, SiRotationSolver.Triple> rotations = [];
        SiRotationSolver.Triple? previous = null;
        for (int i = 0; i < keys.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (keys[i].Rotation is { } q)
            {
                var t = SiRotationSolver.Key(q, previous, budget, token)
                    ?? throw new InvalidDataException(budget.Exhausted ? $"finding {JsonData.ShownText(track.Object)}'s rotation angles took too long" : $"{JsonData.ShownText(track.Object)}'s rotation at frame {keys[i].Frame} has no six-decimal Euler angles that compile to it.");
                rotations[i] = t; previous = t;
            }
        }
        int n = frames.Count;
        long[]?[] values = new long[]?[n * 3];
        // Per frame and channel: whether a key fixes the value there (Key), a solved end value does (End), or neither.
        var pinned = new Pin[n * 3];
        Dictionary<Channel, long[]> current = [];
        Dictionary<(int Key, Channel Channel), long[]> ends = [];
        int ki = -1, first = -1, last = -1;
        for (int pos = 0; pos < n; pos++)
        {
            token.ThrowIfCancellationRequested();
            if (ki + 1 < keys.Count && keys[ki + 1].Frame == frames[pos])
            {
                ki++;
                if (first < 0) first = pos;
                last = pos;
                var k = keys[ki];
                foreach (Channel c in Enum.GetValues<Channel>())
                {
                    if (k.Has(c)) { current[c] = c == Channel.Rotation ? Triple(rotations[ki]) : Micros(c == Channel.Position ? k.Position! : k.Scale!, track, k.Frame); pinned[pos * 3 + (int)c] = Pin.Key; }
                    else if (ends.Remove((ki - 1, c), out var end)) { current[c] = end; pinned[pos * 3 + (int)c] = Pin.End; }
                }
                // A channel whose segment ends at a key that does not list it stops at the value its rate reaches.
                if (!k.Bare && ki + 1 < keys.Count)
                {
                    var next = keys[ki + 1];
                    foreach (Channel c in Enum.GetValues<Channel>())
                        if (k.Has(c) && !next.Has(c))
                        {
                            token.ThrowIfCancellationRequested();
                            ends[(ki, c)] = c == Channel.Rotation
                                ? Triple(SiRotationSolver.End(rotations[ki], k.Spin, k.Frame, next.Frame, track.FrameRate, budget, token)
                                    ?? throw new InvalidDataException(budget.Exhausted ? $"finding {JsonData.ShownText(track.Object)}'s rotation angles took too long" : $"{JsonData.ShownText(track.Object)}'s rotation from frame {k.Frame} has no six-decimal end angles that reproduce its spin."))
                                : VectorEnd(c == Channel.Position ? k.Position! : k.Scale!, c == Channel.Position ? k.Velocity! : k.Growth!, k.Frame, next.Frame, track, token);
                        }
                }
            }
            for (int c = 0; c < 3; c++) values[pos * 3 + c] = current.GetValueOrDefault((Channel)c);
        }
        if (ki != keys.Count - 1) throw new InvalidDataException($"{JsonData.ShownText(track.Object)}'s keys do not follow the script's frames.");
        Hold(values, pinned, n, token);
        // Frames outside the object's own are not its frames: the compiler would read them as keys.
        for (int pos = 0; pos < n; pos++)
        {
            token.ThrowIfCancellationRequested();
            if (pos < first || pos > last) for (int c = 0; c < 3; c++) values[pos * 3 + c] = null;
        }
        return values;
    }

    /// <summary>Held stretches: between a pinned value and the next key that lists the channel, the held frames take the
    /// key's value right away (one step below the compiler's threshold), or an even spread where one step would be keyed.</summary>
    private static void Hold(long[]?[] values, Pin[] pinned, int n, CancellationToken token)
    {
        foreach (Channel c in Enum.GetValues<Channel>())
        {
            token.ThrowIfCancellationRequested();
            var pins = Enumerable.Range(0, n).Where(p => pinned[p * 3 + (int)c] != Pin.None).ToList();
            for (int i = 1; i < pins.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                int i0 = pins[i - 1], i1 = pins[i];
                if (i1 - i0 < 2 || pinned[i1 * 3 + (int)c] != Pin.Key) continue;
                var v0 = values[i0 * 3 + (int)c]; var v1 = values[i1 * 3 + (int)c];
                if (v0 == null || v1 == null || v0.SequenceEqual(v1)) continue;
                if (Absorbed(c, v0, v1))
                {
                    for (int p = i0 + 1; p < i1; p++)
                    {
                        token.ThrowIfCancellationRequested();
                        values[p * 3 + (int)c] = v1;
                    }
                    continue;
                }
                for (int step = 1; step < i1 - i0; step++)
                {
                    token.ThrowIfCancellationRequested();
                    double w = step / (double)(i1 - i0);
                    values[(i0 + step) * 3 + (int)c] = c == Channel.Rotation ? Slerp(v0, v1, w) : [.. v0.Zip(v1, (a, b) => (long)Math.Round(Millionths(a) + (Millionths(b) - Millionths(a)) * w, MidpointRounding.ToEven))];
                }
            }
        }
    }

    private static bool Absorbed(Channel c, long[] a, long[] b)
    {
        if (c == Channel.Rotation) return SiMath.RotationAngle(Rotation(a), Rotation(b)) / 2 <= SiMath.Threshold;
        for (int i = 0; i < 3; i++) if (Math.Abs((double)(float)Value(a[i]) - (float)Value(b[i])) > SiMath.Threshold) return false;
        return true;
    }

    private static SiMath.Quat Rotation(long[] t) => SiMath.CompileRotation(t[0] / 1e6, t[1] / 1e6, t[2] / 1e6);

    /// <summary>Angles a fraction <paramref name="w"/> of the way between two triples (normalised quaternion interpolation), near the first's form.</summary>
    private static long[] Slerp(long[] t0, long[] t1, double w)
    {
        var a = SiMath.ExactQuaternion(t0[0] / 1e6, t0[1] / 1e6, t0[2] / 1e6); var b = SiMath.ExactQuaternion(t1[0] / 1e6, t1[1] / 1e6, t1[2] / 1e6);
        if (a.W * b.W + a.X * b.X + a.Y * b.Y + a.Z * b.Z < 0) b = (-b.W, -b.X, -b.Y, -b.Z);
        (double W, double X, double Y, double Z) m = (a.W + (b.W - a.W) * w, a.X + (b.X - a.X) * w, a.Y + (b.Y - a.Y) * w, a.Z + (b.Z - a.Z) * w);
        double norm = Math.Sqrt(m.W * m.W + m.X * m.X + m.Y * m.Y + m.Z * m.Z);
        var e = SiMath.Euler(m.W / norm, m.X / norm, m.Y / norm, m.Z / norm);
        (double A, double B, double G)[] branches = [e, (e.A > 0 ? e.A - Math.PI : e.A + Math.PI, (e.B > 0 ? Math.PI : -Math.PI) - e.B, e.G > 0 ? e.G - Math.PI : e.G + Math.PI)];
        var best = (from br in branches from ka in new[] { 0, -1, 1 } from kb in new[] { 0, -1, 1 } from kg in new[] { 0, -1, 1 }
                    let f = (A: br.A + ka * 2 * Math.PI, B: br.B + kb * 2 * Math.PI, G: br.G + kg * 2 * Math.PI)
                    orderby Math.Abs(f.A - t0[0] / 1e6) + Math.Abs(f.B - t0[1] / 1e6) + Math.Abs(f.G - t0[2] / 1e6)
                    select f).First();
        return [SiRotationSolver.Round(best.A), SiRotationSolver.Round(best.B), SiRotationSolver.Round(best.G)];
    }

    /// <summary>Six-decimal values whose floats give the stored rate from the segment's start.</summary>
    private static long[] VectorEnd(float[] start, float[] rate, int from, int to, Track track, CancellationToken token)
    {
        double rho = SiMath.InverseDuration(from, to, track.FrameRate);
        long[] result = new long[3];
        for (int i = 0; i < 3; i++)
        {
            token.ThrowIfCancellationRequested();
            float p0 = start[i]; uint target = SiMath.Bits(rate[i]);
            bool Fits(float p1) => float.IsFinite(p1) && SiMath.Bits(SiMath.ComponentRate(p0, p1, rho)) == target;
            if (Fits(p0)) { result[i] = Micros([p0], track, from)[0]; continue; }
            float estimate = (float)(p0 + rate[i] / rho);
            long? found = null;
            // Floats around the estimate first (the value the stored rate came from is usually among them)...
            int bits = BitConverter.SingleToInt32Bits(estimate);
            for (int k = 0; k <= 400 && found == null; k++)
                foreach (int d in k == 0 ? [0] : new[] { -k, k })
                {
                    token.ThrowIfCancellationRequested();
                    float p1 = BitConverter.Int32BitsToSingle(bits + d);
                    if (Fits(p1) && Text(p1) is { } m) { found = m == NegativeZero && Fits(0f) ? 0 : m; break; }
                }
            // ...then six-decimal values around it, where a float step is far finer than a millionth (an end near zero
            // after a move from far away): the rate's own float step spans many of them, so the roundest one is taken
            // (whole units first, then tenths, and so on), each nearest the estimate.
            if (found == null && float.IsFinite(estimate) && Math.Abs(estimate) < 9e12)
            {
                double step = Math.Max(MathF.BitIncrement(Math.Abs(p0)) - Math.Abs(p0), MathF.BitIncrement(Math.Abs(estimate)) - Math.Abs(estimate));
                long window = Math.Min(1_000_000, 64 + (long)Math.Ceiling(step * 1e6)), centre = SiRotationSolver.Round(estimate);
                for (long grain = Micro; grain >= 1 && found == null; grain /= 10)
                {
                    long first = (long)Math.Round(centre / (double)grain) * grain;
                    for (long k = 0; k <= 2 * (window / grain + 1) && found == null; k++)
                    {
                        token.ThrowIfCancellationRequested();
                        long m = first + grain * (k == 0 ? 0 : (k + 1) / 2 * (k % 2 == 1 ? -1 : 1));
                        if (Math.Abs(m - centre) <= window && Fits((float)(m / 1e6))) found = m;
                    }
                }
            }
            result[i] = found ?? throw new InvalidDataException($"{JsonData.ShownText(track.Object)} stops at frame {to} with a rate no six-decimal value reaches.");
        }
        return result;
    }

    private static long[] Micros(float[] v, Track track, int frame) =>
        [.. v.Select(x => Text(x) ?? throw new InvalidDataException($"{JsonData.ShownText(track.Object)}'s value {x.ToString("R", CultureInfo.InvariantCulture)} at frame {frame} has no six-decimal text that reads back as it."))];

    private static long[] Triple(SiRotationSolver.Triple t) => [t.A, t.B, t.G];

    /// <summary>
    /// The six-decimal value (in millionths) printf("%f") gives for <paramref name="value"/>, when it reads back as the
    /// same float. Negative zero is written "-0.000000" (<see cref="NegativeZero"/>), which reads back as itself.
    /// </summary>
    private static long? Text(float value)
    {
        if (!float.IsFinite(value) || Math.Abs(value) >= 9e12) return null;
        if (value == 0) return float.IsNegative(value) ? NegativeZero : 0;
        long m = ExactMicros(value);
        return SiMath.Bits((float)double.Parse(Format(m), CultureInfo.InvariantCulture)) == SiMath.Bits(value) ? m : null;
    }

    /// <summary>The exact binary value times a million, rounded half to even (as a correctly rounded printf does).</summary>
    private static long ExactMicros(double v)
    {
        if (v == 0) return 0;
        long bits = BitConverter.DoubleToInt64Bits(v);
        bool negative = bits < 0; int exponent = (int)((bits >> 52) & 0x7FF); long mantissa = bits & 0xFFFFFFFFFFFFFL;
        if (exponent == 0) exponent = 1; else mantissa |= 1L << 52;
        exponent -= 1075;
        BigInteger n = new BigInteger(mantissa) * Micro, q;
        if (exponent >= 0) q = n << exponent;
        else
        {
            BigInteger d = BigInteger.One << -exponent;
            q = BigInteger.DivRem(n, d, out var r);
            var twice = r * 2;
            if (twice > d || twice == d && !q.IsEven) q += 1;
        }
        return (long)(negative ? -q : q);
    }

    public static string Format(long micros)
    {
        if (micros == NegativeZero) return "-0.000000";
        long magnitude = Math.Abs(micros);
        return (micros < 0 ? "-" : "") + (magnitude / Micro).ToString(CultureInfo.InvariantCulture) + "." + (magnitude % Micro).ToString("D6", CultureInfo.InvariantCulture);
    }

    internal static string Text(IReadOnlyList<(string Object, int Start, long[]?[] Values)> objects, List<int> frames, Layout layout,
        CancellationToken token = default, int maximumBytes = Sources.SourceProject.MaximumSourceTextBytes, Action<long>? reserveText = null)
    {
        token.ThrowIfCancellationRequested();
        Dictionary<int, List<int>> starts = [], ends = [];
        for (int i = 0; i < objects.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            CheckName(objects[i].Object);
            var (_, start, values) = objects[i];
            if (values.Length == 0) continue;
            Add(starts, start, i);
            Add(ends, checked(start + values.Length / 3), i);
        }
        return AnimationScriptText.Build(text =>
        {
            text.Append(SiAnimationScript.Header).Append("\r\nFRAMES: ").Append(frames.Count)
                .Append("\r\nOBJECTS: ").Append(objects.Count).Append("\r\n");
            SortedSet<int> active = [];
            for (int pos = 0; pos < frames.Count; pos++)
            {
                token.ThrowIfCancellationRequested();
                if (ends.TryGetValue(pos, out var ending)) foreach (int i in ending) active.Remove(i);
                if (starts.TryGetValue(pos, out var starting)) foreach (int i in starting) active.Add(i);
                if (layout.WarningsBeforeFrames) Warning(text);
                text.Append("Frame: ").Append(frames[pos] + 1L).Append("\r\n");
                foreach (int i in active)
                {
                    token.ThrowIfCancellationRequested();
                    var (name, start, values) = objects[i];
                    int at = (pos - start) * 3;
                    if (at < 0 || at >= values.Length || values[at] is not { } s || values[at + 1] is not { } r || values[at + 2] is not { } t) continue;
                    text.Append("Object: ").Append(name).Append("\r\n");
                    text.Append("Scaling:     ").Append(Triple3(s)).Append("\r\nRotation:    ").Append(Triple3(r)).Append("\r\nTranslation: ").Append(Triple3(t)).Append("\r\n");
                }
                if (!layout.WarningsBeforeFrames) Warning(text);
            }
            return true;
        }, token, maximumBytes, reserveText)!;

        void Warning(AnimationScriptText text)
        {
            if (layout.Version != null) text.Append("Warning, file version ").Append(layout.Version)
                .Append(" is later than DKit release version 3\r\nAttempt to read: An error may occur...\r\n");
        }

        static void Add(Dictionary<int, List<int>> events, int frame, int index)
        {
            if (!events.TryGetValue(frame, out var indices)) events[frame] = indices = [];
            indices.Add(index);
        }

        static string Triple3(long[] v) => $"{Format(v[0])} {Format(v[1])} {Format(v[2])}";
    }

    private static void CheckName(string name)
    {
        if (name.Length > AnimationScriptLexing.MaximumTokenBytes)
            throw new InvalidDataException($"Object {JsonData.ShownText(name)} exceeds the {AnimationScriptLexing.MaximumTokenBytes:N0}-byte keyframe token limit.");
    }

    /// <summary>Compiles the text again and refuses it unless every keyframe comes back bit for bit (pad floats aside).</summary>
    private static void Verify(string text, IReadOnlyList<Track> tracks, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var script = SiAnimationScript.Parse(Encoding.Latin1.GetBytes(text), "script", token);
        AnimationCompileWork work = new(token);
        foreach (var track in tracks)
        {
            token.ThrowIfCancellationRequested();
            var compiled = SiAnimationScript.Compile(script, track.Object, track.FrameRate, "script", token, work);
            if (compiled.Count != track.Frames.Count || !compiled.Zip(track.Frames).All(p => Same(p.First, p.Second)))
                throw new InvalidDataException($"{JsonData.ShownText(track.Object)}'s script text does not compile back to its keyframes exactly.");
        }
    }

    internal static bool Same(AnimationKeyframe a, AnimationKeyframe b)
    {
        if (a.Flags != b.Flags || a.Bytes.Length != b.Bytes.Length) return false;
        for (int o = 0; o < a.Bytes.Length; o += 4)
        {
            if (Pad(a, o)) continue;
            if (BitConverter.ToUInt32(a.Bytes, o) != BitConverter.ToUInt32(b.Bytes, o)) return false;
        }
        return true;

        // The unused fourth base float of position and scale holds leftover memory in shipped files.
        static bool Pad(AnimationKeyframe f, int o) => f.ChannelOffset(0) is int p and >= 0 && o == p + 12 || f.ChannelOffset(2) is int s and >= 0 && o == s + 12;
    }
}
