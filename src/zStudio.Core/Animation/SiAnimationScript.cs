using System.Globalization;
using System.Numerics;
using System.Text;

namespace Recoil.Zbd.Core.Animation;

/// <summary>
/// RECOIL's original keyframe scripts: Softimage "SI Animation Script" text exported per scene, compiled by a lost tool
/// into the transform keyframes of <c>OBJECT_MOTION_SI_SCRIPT</c>. The compiler here reproduces every shipped keyframe
/// bit for bit (<see cref="SiMath"/>).
/// </summary>
/// <remarks>
/// <code>
/// SI Animation Script
/// FRAMES: 2
/// OBJECTS: 1
/// Frame: 1
/// Object: copter01
/// Scaling:     1.000000 1.000000 1.000000
/// Rotation:    0.304886 -0.111792 -0.811317
/// Translation: 570.880615 48.131424 168.158310
/// Frame: 6
/// ...
/// </code>
/// Each frame block lists every object of the scene: scaling, rotation (radians about X, Y and Z, applied Z then Y then
/// X) and translation. Frame n is frame n − 1 of the definition's <c>SCRIPT_FRAME_RATE</c>. The exporter also wrote the
/// Softimage DKit messages (<c>Warning, file version …</c>, <c>Attempt to read: …</c>) between frames; they, the header
/// lines, blank lines and <c>#</c> comments are not keys. A frame repeated immediately adds nothing.
/// </remarks>
public static class SiAnimationScript
{
    public const string Header = "SI Animation Script";
    public const int MaximumFrames = 65_536;
    public const int MaximumPoses = 1_048_576;

    /// <summary>One object's pose in a frame block, as the text gives it (each value parsed to double).</summary>
    public sealed record Pose(double[] Scaling, double[] Rotation, double[] Translation);
    /// <summary>A frame block: its label and the poses it lists, in text order.</summary>
    public sealed record Frame(int Label, IReadOnlyList<(string Object, Pose Pose)> Poses);
    /// <summary>A parsed script: its frame blocks in text order and the objects they name, in first-appearance order.</summary>
    public sealed record Script(IReadOnlyList<Frame> Frames, IReadOnlyList<string> Objects)
    {
        private Dictionary<string, List<(int Label, Pose Pose)>>? tracks;
        public bool Has(string name) => Track(name) != null;
        /// <summary>The object's poses in text order, indexed once for all the events that use the script.</summary>
        internal List<(int Label, Pose Pose)>? Track(string name)
        {
            if (tracks == null)
            {
                Dictionary<string, List<(int, Pose)>> built = new(StringComparer.Ordinal);
                foreach (var frame in Frames)
                    foreach (var (obj, pose) in frame.Poses)
                    {
                        if (!built.TryGetValue(obj, out var list)) built[obj] = list = [];
                        list.Add((frame.Label, pose));
                    }
                tracks = built;
            }
            return tracks.GetValueOrDefault(name);
        }
    }

    /// <summary>Whether <paramref name="bytes"/> is an SI Animation Script: its first line that is not blank is the header.</summary>
    public static bool Recognize(ReadOnlySpan<byte> bytes)
    {
        int at = 0;
        while (at < bytes.Length)
        {
            int end = bytes[at..].IndexOf((byte)'\n'); end = end < 0 ? bytes.Length : at + end;
            var line = Encoding.Latin1.GetString(bytes[at..end]).Trim();
            if (line.Length > 0) return line == Header;
            at = end + 1;
        }
        return false;
    }

    public static Script Parse(ReadOnlySpan<byte> bytes, string source)
    {
        List<Frame> frames = []; List<string> objects = []; HashSet<string> known = new(StringComparer.Ordinal);
        List<(string, Pose)>? poses = null; HashSet<string>? inFrame = null;
        string? current = null; double[]? s = null, r = null, t = null;
        int lineNumber = 0, total = 0;
        foreach (string raw in Encoding.Latin1.GetString(bytes).Split('\n'))
        {
            lineNumber++;
            string line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] == '#' || line == Header || line.StartsWith("Warning,", StringComparison.Ordinal) || line.StartsWith("Attempt to read", StringComparison.Ordinal)) continue;
            int colon = line.IndexOf(':');
            if (colon <= 0) throw Error("expected a 'Name: value' line.");
            string key = line[..colon], value = line[(colon + 1)..].Trim();
            switch (key)
            {
                case "FRAMES" or "OBJECTS":
                    if (frames.Count > 0) throw Error($"{key} belongs to the header.");
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _)) throw Error($"{key} needs a count.");
                    break;
                case "Frame":
                    Close();
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int label) || label < 1) throw Error("a frame label is a number of 1 or more.");
                    if (frames.Count >= MaximumFrames) throw Error($"more than {MaximumFrames} frames.");
                    poses = []; inFrame = new(StringComparer.Ordinal); frames.Add(new(label, poses));
                    break;
                case "Object":
                    if (poses == null) throw Error("an object belongs to a frame.");
                    Close();
                    if (value.Length == 0 || value.Any(char.IsWhiteSpace)) throw Error("an object name is one word.");
                    if (!inFrame!.Add(value)) throw Error($"object {value} appears twice in the frame.");
                    if (++total > MaximumPoses) throw Error($"more than {MaximumPoses} object poses.");
                    if (known.Add(value)) objects.Add(value);
                    current = value;
                    break;
                case "Scaling" or "Rotation" or "Translation":
                    if (current == null) throw Error($"{key} belongs to an object.");
                    var numbers = value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                    double[] v = new double[3];
                    if (numbers.Length != 3 || !numbers.Select((n, i) => double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]) && double.IsFinite(v[i]) && float.IsFinite((float)v[i])).All(ok => ok))
                        throw Error($"{key} needs three finite numbers.");
                    if ((key == "Scaling" ? s : key == "Rotation" ? r : t) != null) throw Error($"{key} is given twice for {current}.");
                    if (key == "Scaling") s = v; else if (key == "Rotation") r = v; else t = v;
                    break;
                default:
                    throw Error($"unknown line '{key}'.");
            }
        }
        Close();
        if (frames.Count == 0) throw new InvalidDataException($"{source}: the script has no frames.");
        return new(frames, objects);

        InvalidDataException Error(string message) => new($"{source}, line {lineNumber}: {message}");
        void Close()
        {
            if (current == null) return;
            if (s == null || r == null || t == null) throw Error($"object {current} needs Scaling, Rotation and Translation.");
            poses!.Add((current, new Pose(s, r, t)));
            current = null; s = r = t = null;
        }
    }

    /// <summary>The keyframes the original compiler made of <paramref name="name"/>'s motion at <paramref name="frameRate"/> frames per second.</summary>
    public static List<AnimationKeyframe> Compile(Script script, string name, float frameRate, string source)
    {
        if (!(frameRate > 0) || !float.IsFinite(frameRate)) throw new InvalidDataException($"{source}: SCRIPT_FRAME_RATE must be above 0.");
        // The object's frames; a label repeated immediately adds nothing.
        List<(int Frame, Vector3 Position, SiMath.Quat Rotation, Vector3 Scale)> keys = [];
        int? previous = null;
        foreach (var (label, pose) in script.Track(name) ?? [])
        {
            if (previous == label) continue;
            previous = label;
            keys.Add((label - 1, Vector(pose.Translation), SiMath.CompileRotation(pose.Rotation[0], pose.Rotation[1], pose.Rotation[2]), Vector(pose.Scaling)));
        }
        if (keys.Count < 2) throw new InvalidDataException($"{source}: {name} needs at least two frames (the last ends the motion).");
        List<AnimationKeyframe> frames = [];
        for (int i = 0; i + 1 < keys.Count; i++)
        {
            var (f0, p0, q0, s0) = keys[i]; var (f1, p1, q1, s1) = keys[i + 1];
            bool ends = i == 0 || i == keys.Count - 2;
            bool position = ends || Changed(p0, p1), rotation = ends || SiMath.RotationAngle(q0, q1) / 2 > SiMath.Threshold, scale = ends || Changed(s0, s1);
            int flags = (position ? 1 : 0) | (rotation ? 2 : 0) | (scale ? 4 : 0);
            if (flags == 0) continue;
            float start = SiMath.KeyTime(f0, frameRate), end = SiMath.KeyTime(f1, frameRate);
            if (!float.IsFinite(start) || !float.IsFinite(end)) throw new InvalidDataException($"{source}: frame {f1 + 1} is beyond single-precision time at {frameRate} frames per second.");
            double rho = SiMath.InverseDuration(f0, f1, frameRate);
            var frame = AnimationKeyframe.Create(flags); frame.Start = start; frame.End = end;
            if (position) Vec(frame.ChannelOffset(0), p0, Rate(p0, p1));
            if (rotation)
            {
                var spin = SiMath.Spin(q0, q1, f0, f1, frameRate);
                int o = frame.ChannelOffset(1);
                frame.SetFloat(o, q0.W); frame.SetFloat(o + 4, q0.X); frame.SetFloat(o + 8, q0.Y); frame.SetFloat(o + 12, q0.Z);
                frame.SetVector(o + 16, Finite(new(spin.X, spin.Y, spin.Z), f1));
            }
            if (scale) Vec(frame.ChannelOffset(2), s0, Rate(s0, s1));
            frames.Add(frame);

            Vector3 Rate(Vector3 from, Vector3 to) =>
                Finite(new(SiMath.ComponentRate(from.X, to.X, rho), SiMath.ComponentRate(from.Y, to.Y, rho), SiMath.ComponentRate(from.Z, to.Z, rho)), f1);
            void Vec(int o, Vector3 value, Vector3 rate) { frame.SetVector(o, value); frame.SetFloat(o + 12, 0); frame.SetVector(o + 16, rate); }
        }
        return frames;

        static Vector3 Vector(double[] v) => new((float)v[0], (float)v[1], (float)v[2]);
        static bool Changed(Vector3 a, Vector3 b) =>
            Math.Abs((double)a.X - b.X) > SiMath.Threshold || Math.Abs((double)a.Y - b.Y) > SiMath.Threshold || Math.Abs((double)a.Z - b.Z) > SiMath.Threshold;
        Vector3 Finite(Vector3 rate, int to) => float.IsFinite(rate.X) && float.IsFinite(rate.Y) && float.IsFinite(rate.Z) ? rate
            : throw new InvalidDataException($"{source}: reaching {name}'s pose at frame {to + 1} needs a rate beyond single precision.");
    }
}
