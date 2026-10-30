using System.Diagnostics;
using System.Numerics;

namespace zStudio.Core.Animation;

public sealed record AnimationPathPoint(double Time, Vector3 Position, bool Break);
public sealed record AnimationPath(string Id, int Entry, long Instance, int Node, string Name, IReadOnlyList<AnimationPathPoint> Points)
{
    public string Kind { get; init; } = "object";
}
public sealed record AnimationPaths(double Seconds, string Status, IReadOnlyList<AnimationPath> Paths, IReadOnlyList<AnimationTrace> Events, IReadOnlyList<string> Diagnostics)
{
    /// <summary>Where procedural motions touched the ground ("contact") and where a contact played its impact sound ("impact"), oldest first.</summary>
    public IReadOnlyList<AnimationPathMarker> Markers { get; init; } = [];
}
/// <summary>A ground contact or impact sound of a procedural motion during path analysis, at the node's world position (with the preview height).</summary>
public sealed record AnimationPathMarker(double Time, Vector3 Position, string Kind, long Instance, int Node)
{
    /// <summary>The impact sound's name.</summary>
    public string? Sound { get; init; }
}

public sealed partial class AnimationPlayer
{
    private HashSet<Node>? pathDiscontinuities;
    private List<AnimationPathMarker>? pathMarkers;
    private const int MaximumPathMarkers = 4096;
    /// <summary>Path analysis records where a procedural motion touches the ground and where its contact plays an impact sound.</summary>
    private void MarkPath(Instance instance, Node node, string kind, string? sound = null)
    {
        if (pathMarkers == null || pathMarkers.Count >= MaximumPathMarkers) return;
        pathMarkers.Add(new(Time, World(instance, node).Translation + Vector3.UnitY * PreviewHeight, kind, instance.Id, node.Source) { Sound = sound });
    }
    private void ObservePathDiscontinuity(Instance instance, AnimationEvent ev, bool starting)
    {
        if (pathDiscontinuities == null || !starting) return;
        int? reference = ev.Type switch { 7 or 9 => ev.I16(28), 8 => ev.I16(24), 11 => ev.I32(16), 12 => ev.I32(12), 15 or 16 => ev.I16(14), _ => null };
        if (reference is int target && NodeRef(instance, target) is { } node) pathDiscontinuities.Add(node);
    }
    private void ObserveKeyframeCut(Node node, IReadOnlyList<AnimationKeyframe> frames, int index, float elapsed, float remaining)
    {
        if (pathDiscontinuities == null || index == 0 || frames[index].Start <= elapsed - remaining || frames[index].Start > elapsed) return;
        var current = frames[index];
        for (int channel = 0; channel < 3; channel++)
        {
            int at = current.ChannelOffset(channel);
            if (at < 0) continue;
            for (int prior = index - 1; prior >= 0; prior--)
            {
                var previous = frames[prior]; int before = previous.ChannelOffset(channel);
                if (before < 0) continue;
                float time = previous.End - Math.Max(previous.Start, prior == 0 ? 0 : frames[prior - 1].End);
                bool cut;
                if (channel == 1)
                {
                    var end = Quaternion.Normalize(Quaternion.Multiply(AnimationMath.FromRotationVector(previous.Vector(before + 16) * time), AnimationMath.ReadQuaternion(previous, before)));
                    var start = Quaternion.Normalize(AnimationMath.ReadQuaternion(current, at));
                    cut = MathF.Abs(Quaternion.Dot(end, start)) < .99999f;
                }
                else cut = Vector3.DistanceSquared(previous.Vector(before) + previous.Vector(before + 16) * time, current.Vector(at)) > .0001f;
                if (cut) pathDiscontinuities.Add(node);
                break;
            }
        }
    }
    /// <summary>Analyzes this private player's selected scenario. The caller must never pass its visible playback player.</summary>
    public AnimationPaths AnalyzePaths(double seconds, CancellationToken token = default)
    {
        if (!double.IsFinite(seconds) || seconds is <= 0 or > 3600) throw new ArgumentOutOfRangeException(nameof(seconds));
        const int maximumPoints = 216001, maximumTracks = 128;
        measuringDuration = true;
        pathDiscontinuities = [];
        pathMarkers = [];
        Dictionary<(long Instance, int Node), (Instance Instance, Node Node, List<AnimationPathPoint> Points)> tracks = [];
        HashSet<long> discovered = [];
        var watch = Stopwatch.StartNew();
        int retained = 0; string status = "Preview range";
        int ticks = (int)Math.Ceiling(seconds * 60);
        for (int step = 0; step <= ticks; step++)
        {
            token.ThrowIfCancellationRequested();
            pathDiscontinuities.Clear();
            if (step > 0) { cues.Clear(); Tick(token); }
            using (BeginBinding(token))
                foreach (var instance in instances)
                {
                    if (!discovered.Add(instance.Id)) continue;
                    foreach (var ev in instance.Entry.AllSequences.SelectMany(s => s.Events))
                    {
                        int offset = ev.Type switch { 12 => 12, 10 or 11 => 16, 7 or 9 => 28, 8 => 24, _ => -1 };
                        if (offset < 0) continue;
                        int reference = ev.Type is 7 or 8 or 9 ? ev.I16(offset) : ev.I32(offset);
                        if (NodeRef(instance, reference) is not { } node || tracks.ContainsKey((instance.Id, node.Source))) continue;
                        if (tracks.Count >= maximumTracks) throw new InvalidDataException("Animation paths exceed 128 motion targets; select a narrower program.");
                        tracks.Add((instance.Id, node.Source), (instance, node, []));
                    }
                }
            var work = new AnimationBindingOperation(context, token);
            var ancestry = BeginFrameAncestry(work);
            try
            {
                foreach (var (_, track) in tracks)
                {
                    if (retained >= maximumPoints) { status = "Partial: path sample limit"; break; }
                    Vector3 position = ancestry.Get(track.Instance, track.Node).World.Translation;
                    position.Y += PreviewHeight;
                    if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)) throw new InvalidDataException("An animated target has a non-finite position.");
                    bool discontinuity = track.Points.Count == 0;
                    Node? ancestor = track.Node; HashSet<int> visited = [];
                    while (ancestor != null && visited.Add(ancestor.Source))
                    {
                        discontinuity |= pathDiscontinuities.Contains(ancestor);
                        ancestor = ParentNode(track.Instance, ancestor.Parent);
                    }
                    // Keep the stationary endpoint once; later movement still gets its true start time.
                    if (!discontinuity && track.Points.Count > 1 && track.Points[^1].Position == position && track.Points[^2].Position == position)
                        track.Points[^1] = new(Time, position, false);
                    else { track.Points.Add(new(Time, position, discontinuity)); retained++; }
                }
            }
            finally { EndFrameAncestry(ancestry); }
            if (retained >= maximumPoints) break;
            if (IsDependencyBlocked) { status = "Partial: awaiting excluded dependency"; break; }
            if (unavailableDuration || instances.Any(i => i.Sequences.Any(s => s.State == 4))) { status = "Partial: an event or resource is unavailable"; break; }
            if (IsComplete) { status = instances.Any(i => i.Sequences.Any(s => s.State == 3)) ? "Open-ended: waiting for a sequence call" : "Finite"; break; }
            if (step > 0 && step % 60 == 0 && watch.Elapsed > TimeSpan.FromSeconds(15)) { status = "Partial: analysis work limit"; break; }
        }
        if (status == "Preview range" && instances.Any(i => i.Sequences.Any(s => s.ObservedInfiniteLoop))) status = "Looping · preview range";
        if (HasBlockedDependencies && !status.StartsWith("Partial:", StringComparison.Ordinal)) status = "Partial: an excluded dependency remains blocked · " + status;
        else if (hasExcludedCalls && !status.StartsWith("Partial:", StringComparison.Ordinal)) status += " · excluded child calls omitted";
        var paths = tracks.Select(t => new AnimationPath($"{t.Key.Instance}:{t.Key.Node}", t.Value.Instance.Entry.Index,
            t.Key.Instance, t.Key.Node, t.Key.Node >= 0 && t.Key.Node < context.Scene.Nodes.Count ? context.Scene.Nodes[t.Key.Node].Name : "Motion target", Trim(t.Value.Points))
            { Kind = context.Scene.Nodes[t.Key.Node].Class == "camera" ? "camera" : "object" }).ToArray();
        pathDiscontinuities = null;
        var markers = pathMarkers.ToArray(); pathMarkers = null;
        return new(Time, status, paths, trace.Take(4096).ToArray(), notes.Order(StringComparer.Ordinal).Take(256).ToArray()) { Markers = markers };
        static IReadOnlyList<AnimationPathPoint> Trim(List<AnimationPathPoint> points)
        {
            while (points.Count > 1 && !points[^1].Break && points[^1].Position == points[^2].Position) points.RemoveAt(points.Count - 1);
            return points;
        }
    }
}
