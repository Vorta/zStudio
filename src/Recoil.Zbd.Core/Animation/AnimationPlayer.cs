using System.Numerics;
using System.Text.Json.Nodes;

namespace Recoil.Zbd.Core.Animation;

public sealed record AnimationNodePose(long Id, int SourceNode, int Model, Matrix4x4 Transform, bool Visible, float Opacity, int Variant, float Morph, double CycleTime, string? Texture = null);
public sealed record AnimationSoundCue(long Id, string Name, bool Stop, bool Persistent, double StartedAt, Vector3 Position)
{
    public float Gain { get; init; } = 1;
}
public sealed record AnimationLight(long Id, Vector3 Position, Vector3 Color, float Range, float Intensity, bool Active);
/// <summary>World-space preview eye/target; horizontal field of view in degrees.</summary>
public sealed record AnimationCamera(Vector3 Position, Vector3 Target, float FieldOfView)
{
    public int SourceNode { get; init; } = -1;
    public string Name { get; init; } = "";
}
public sealed record AnimationFog(bool Enabled, Vector3 Color, float Start, float End);
public sealed record AnimationTrace(long Instance, Guid Sequence, Guid Event, string Name, double Start, double? End, string Status);
public sealed record AnimationSequenceStatus(long Instance, Guid Sequence, string Name, int EventIndex, string State, int Iteration);
public sealed record AnimationFrame(double Time, IReadOnlyList<AnimationNodePose> Nodes, IReadOnlyList<AnimationLight> Lights,
    IReadOnlyList<AnimationSoundCue> Sounds, IReadOnlyList<AnimationSoundCue> ActiveSounds, AnimationCamera? Camera, AnimationFog? Fog,
    Vector4 ScreenColor, Vector4 ScreenWave, IReadOnlyList<AnimationTrace> Trace, IReadOnlyList<AnimationSequenceStatus> Sequences, IReadOnlyList<string> Diagnostics);

/// <summary>Retail event-clock semantics on a deterministic, device-independent 60 Hz clock.</summary>
public sealed partial class AnimationPlayer
{
    public const double StepSeconds = 1.0 / 60;
    public const int MaximumInstances = 256;
    private readonly AnimationPreviewContext context;
    private readonly int entryIndex;
    private readonly bool resetPhase;
    public int LodLevel { get; set; }
    private List<Instance> instances = [];
    private Dictionary<int, Node> sharedNodes = [];
    private bool initializingScene;
    private List<Effect> effects = [];
    private List<AnimationTrace> trace = [];
    private HashSet<string> notes = [];
    private Dictionary<long, AnimationLight> lights = [];
    private Dictionary<long, AnimationSoundCue> activeSounds = [];
    private readonly List<AnimationSoundCue> cues = [];
    private readonly SortedDictionary<long, Checkpoint> checkpoints = [];
    private long ticks, nextId;
    private uint randomState;
    private int randomIndex;
    private readonly float[] randomTable = new float[200];
    private Vector4 screenColor, screenWave;
    private AnimationFog? fog;
    private int dispatchBudget;
    public int Seed { get; }
    public int EffectLevel { get; init; } = 2;
    public bool? ConditionOverride { get; init; }
    public Vector3? ReferencePosition { get; init; }
    public Vector3? ActivationStart { get; init; }
    /// <summary>Preview-only mesh contact with the infinite world Y=0 plane.</summary>
    public bool GroundPlaneEnabled { get; init; }
    private float previewHeight;
    /// <summary>Preview-only world-Y offset, in game units. Authored coordinates remain unchanged.</summary>
    public float PreviewHeight
    {
        get => previewHeight;
        init
        {
            if (!float.IsFinite(value) || value is < 0 or > 100000)
                throw new ArgumentOutOfRangeException(nameof(value), "Preview height must be between 0 and 100000 game units.");
            previewHeight = value;
        }
    }
    public double Time => ticks * StepSeconds;
    public bool IsComplete => instances.All(i => i.Finished) && effects.Count == 0;

    public AnimationPlayer(AnimationPreviewContext context, int entryIndex, int seed = 1, bool resetPhase = false)
    {
        this.context = context; this.entryIndex = entryIndex; this.resetPhase = resetPhase; Seed = seed; Reset();
    }
    public void Reset()
    {
        ticks = nextId = 0; measuredEnd = 0; unavailableDuration = false; randomIndex = 0; randomState = unchecked((uint)Seed);
        for (int i = 0; i < randomTable.Length; i++) { randomState = unchecked(randomState * 214013 + 2531011); randomTable[i] = ((randomState >> 16) & 32767) / 32767f; }
        instances = []; sharedNodes = []; effects = []; trace = []; notes = [.. context.Diagnostics]; lights = []; activeSounds = []; cues.Clear(); checkpoints.Clear();
        screenColor = screenWave = Vector4.Zero; fog = null;
        AddInstance(context.Package.Entries[entryIndex], null, null, resetPhase);
        checkpoints[0] = CaptureCheckpoint();
    }
    public AnimationFrame Step(CancellationToken token = default) { cues.Clear(); Tick(token); return Frame(); }
    public AnimationFrame AdvanceTo(double seconds, bool seeking = false, CancellationToken token = default)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > 3600) throw new InvalidDataException("Preview time must be between zero and one hour.");
        long target = (long)Math.Floor(seconds / StepSeconds + 1e-7); cues.Clear();
        if (target < ticks)
        {
            var closest = checkpoints.LastOrDefault(p => p.Key <= target);
            if (closest.Value == null) Reset(); else Restore(closest.Value);
        }
        while (ticks < target) { token.ThrowIfCancellationRequested(); Tick(token); }
        if (seeking) cues.Clear();
        return Frame();
    }
    private void Tick(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ticks++; dispatchBudget = 10000; screenColor = screenWave = Vector4.Zero;
        foreach (var instance in instances.ToArray())
        {
            if (instance.Finished) continue;
            instance.Elapsed += (float)StepSeconds;
            if (instance.StopDelay >= 0)
            {
                instance.StopDelay -= (float)StepSeconds;
                if (instance.StopDelay <= 0) BeginCleanup(instance);
                else continue;
            }
            foreach (var sequence in instance.Sequences.ToArray()) if (sequence.State is 0 or 1) Run(instance, sequence);
            if (instance.Sequences.All(s => s.State is not (0 or 1)))
            {
                if (instance.Cleanup || instance.Entry.F32(164) < 0) Finish(instance);
                else { instance.StopDelay = instance.Entry.F32(164); if (instance.StopDelay == 0) BeginCleanup(instance); }
            }
        }
        // Landed debris remains above the solid preview plane during subsequent
        // shrink/sink stages and concurrent transform events, without new impacts.
        ConstrainSettledGround();
        foreach (var effect in effects)
        {
            effect.Age += (float)StepSeconds;
            effect.Scale = Math.Max(.01f, effect.Scale + (effect.Age < .3f ? 6 : -3.75f) * (float)StepSeconds);
        }
        effects.RemoveAll(e => e.Age >= 1);
        // Keep live one-shots in snapshots too: unmuting or resuming must not lose
        // a sound just because its event fired in an earlier presentation frame.
        foreach (var (id, cue) in activeSounds.ToArray())
            if (!cue.Persistent && Time - cue.StartedAt >= context.Sounds[cue.Name].Duration)
                activeSounds.Remove(id);
        // Keep completed root geometry as the final pose, but release nested instances.
        instances.RemoveAll(i => i.Finished && i.Id != 1 && !instances.Any(p => p.Children.Values.Contains(i.Id)));
        if (!measuringDuration && ticks % 60 == 0)
        {
            checkpoints[ticks] = CaptureCheckpoint(); int maximum = instances.Sum(i => i.Nodes.Count) > 10000 ? 2 : 8;
            while (checkpoints.Count > maximum + 1) checkpoints.Remove(checkpoints.Keys.First(k => k != 0));
        }
    }
    private void Run(Instance instance, Sequence state, float step = (float)StepSeconds)
    {
        float remaining = step;
        if (state.State == 1 || state.Cursor > 0) { state.Elapsed += remaining; state.EventElapsed += remaining; }
        while (state.State is 0 or 1)
        {
            if (--dispatchBudget <= 0) { Block(state, "Event dispatch limit reached; check zero-time loops."); return; }
            if (state.Cursor >= state.Data.Events.Count) { state.State = state.Data.IsEditable ? 2 : 4; unavailableDuration |= state.State == 4; return; }
            var ev = state.Data.Events[state.Cursor];
            if (ev.Spec == null || ev.Bytes.Length < ev.Spec.Size || ev.StartMode is < 1 or > 3 || !float.IsFinite(ev.Threshold))
            { Block(state, $"{ev.Name}: unverified or malformed event; this sequence is paused."); return; }
            bool starting = state.State == 0;
            if (starting)
            {
                float clock = ev.StartMode switch { 1 => instance.Elapsed, 2 => state.Elapsed, _ => state.EventElapsed };
                if (clock < ev.Threshold) return;
                state.EventElapsed = remaining; if (state.Cursor == 0) state.Elapsed = remaining;
                state.Work = ev.Clone(); state.Child = -1; state.KeyOffset = 0; state.KeyTime = 0;
                if (trace.Count >= 20000) trace.RemoveRange(0, 10000);
                trace.Add(new(instance.Id, state.Data.Id, ev.Id, ev.Name, Math.Max(0, Time - remaining), null, ev.Spec.Support));
                if (ev.Spec.Support != "Engine-based") notes.Add(ev.Spec.Support);
            }
            int result;
            try { result = Execute(instance, state, state.Work!, starting, ref remaining); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or ArithmeticException or IndexOutOfRangeException)
            { Block(state, $"{ev.Name}: {ex.Message}"); return; }
            if (result == 0) return; // Loop/reset explicitly changed the cursor and clocks.
            state.State = result;
            if (result != 2) return;
            int tr = trace.FindLastIndex(t => t.Instance == instance.Id && t.Event == ev.Id && t.End == null);
            if (tr >= 0) trace[tr] = trace[tr] with { End = Time - remaining };
            measuredEnd = Math.Max(measuredEnd, Time - remaining);
            state.Cursor++; state.EventElapsed = 0; state.Work = null;
            if (state.Cursor < state.Data.Events.Count) state.State = 0;
            else if (state.Data.ResetMode == 3) { state.Reset(); return; }
        }
    }
    private void Block(Sequence sequence, string message) { sequence.State = 4; unavailableDuration = true; notes.Add(sequence.Data.Name + ": " + message); }
    private float RandomUnit() { float result = randomTable[randomIndex]; randomIndex = (randomIndex + 1) % randomTable.Length; return result; }

    private Instance? AddInstance(AnimationEntry entry, Vector3? position, int? boundRoot, bool primary = false)
    {
        if (instances.Count + effects.Count >= MaximumInstances) { notes.Add("Preview instance limit reached (256). A looping emitter may be producing too many children."); return null; }
        int root = boundRoot ?? context.ResolveRoot(entry);
        if (root < 0) { unavailableDuration = true; notes.Add($"Unresolved animation root: {entry.RootName}"); return null; }
        if (initializingScene && !primary && entry.References[6].Count > 0)
        { notes.Add($"{entry.Name}: initialization activation prerequisites are unavailable."); return null; }
        bool shared = IsWorldNode(root) && ((entry.U32(148) & 0x8000) == 0 || boundRoot.HasValue);
        if (!initializingScene && shared && instances.Any(i => i.Entry.Index == entry.Index && i.Root == root && !i.Finished)) return null;
        Instance instance = new() { Id = ++nextId, Entry = entry, Root = root, Cleanup = primary, Shared = shared };
        var sourceNodes = context.Descendants(root).ToHashSet();
        for (int r = 1; r < entry.References[1].Count; r++)
        {
            int node = context.ResolveNode(entry, r, root);
            // Per-instance mission resets must not fall back to another turret's
            // identically named component when this instance lacks that part.
            if (initializingScene && boundRoot.HasValue && !sourceNodes.Contains(node)) continue;
            if (node >= 0) sourceNodes.UnionWith(context.Descendants(node));
        }
        foreach (int index in sourceNodes)
        {
            if (shared) { instance.Nodes[index] = SharedNode(index); continue; }
            var source = context.Scene.Nodes[index]; int parent = source.Parents.FirstOrDefault(p => sourceNodes.Contains(p), -1);
            var matrix = parent < 0 ? context.WorldTransform(index) : SceneBuilder.LocalTransform(source);
            var node = new Node { Source = index, Parent = parent, Exact = matrix, Position = matrix.Translation, Scale = Vec(source.Data["scale"], Vector3.One), Euler = Vec(source.Data["rotate"]), Active = source.Class != "object3d" || (source.Metadata.UInt("flags") & 4) != 0, Alpha = source.Data.Float("opacity", 1), AlphaEnabled = (source.Data.UInt("flags") & 2) != 0 };
            node.Rotation = AnimationMath.FromEuler(node.Euler);
            if ((parent < 0 || source.Class == "camera") && Matrix4x4.Decompose(matrix, out var scale, out var rotation, out _))
            { node.Scale = scale; node.Rotation = rotation; node.Euler = AnimationMath.ToEuler(rotation); }
            if (source.Class == "camera") node.Fov = source.Data.Float("fov_h_base", MathF.PI / 3);
            instance.Nodes[index] = node;
            node.RenderId = (instance.Id << 32) | (uint)index;
        }
        if (instance.Nodes.TryGetValue(root, out var rootNode))
        {
            if (!initializingScene) rootNode.Active = true;
            // Explicitly inspecting an unplaced actor's destruction/effect still
            // needs a local preview. World controllers never enable dormant actors.
            if (!initializingScene && instance.Id == 1 && rootNode.PendingPlacement && !entry.Sequences.SelectMany(s => s.Events).Any(e =>
                e.Spec != null && e.Bytes.Length >= e.Spec.Size && (e.Type == 12 && context.ResolveNode(entry,e.I32(12),root) == root || e.Type == 7 && context.ResolveNode(entry,e.I16(28),root) == root)))
            { rootNode.PendingPlacement = false; notes.Add("The selected actor has no recovered starting position; this individual preview uses its stored pose."); }
            if (position is Vector3 p) { Position(rootNode, p); rootNode.Parent = -1; }
        }
        instance.SavedNodes = instance.Nodes.ToDictionary(p => p.Key, p => p.Value.Clone());
        instance.Sequences = (primary ? new[] { entry.Primary } : entry.Sequences.ToArray()).Select(s => new Sequence(s)).ToList();
        instances.Add(instance);
        if (!initializingScene && entry.References[6].Count > 0) notes.Add("Manual preview activation bypasses game activation prerequisites.");
        return instance;
    }
    private Node? NodeRef(Instance instance, int reference)
    {
        if (reference == 0) return null;
        int index = reference is -100 or -200 ? instance.Root : context.ResolveNode(instance.Entry, reference, instance.Root);
        if (reference > 0 && reference < instance.Entry.References[1].Count)
        {
            string name = instance.Entry.References[1][reference].Text(0, 36);
            int local = name == instance.Entry.RootName ? instance.Root : context.FindBelow(instance.Root, name); if (local >= 0) index = local;
        }
        if (instance.Nodes.TryGetValue(index, out var node)) return node;
        notes.Add($"{instance.Entry.Name}: unresolved node reference {reference}."); return null;
    }
    private Matrix4x4 World(Instance instance, Node node)
    {
        Matrix4x4 matrix = node.Local; HashSet<int> seen = [node.Source]; int parent = node.Parent;
        while (parent >= 0 && seen.Add(parent) && ParentNode(instance, parent) is { } p) { matrix *= p.Local; parent = p.Parent; }
        return matrix;
    }
    private bool Visible(Instance instance, Node node)
    {
        if (!node.Active || node.PendingPlacement) return false; HashSet<int> seen = [node.Source]; int parent = node.Parent;
        int child = node.Source;
        while (parent >= 0 && seen.Add(parent) && ParentNode(instance, parent) is { } p)
        { if (!p.Active || p.PendingPlacement || !context.Lods.Includes(parent, child, LodLevel)) return false; child = parent; parent = p.Parent; }
        return true;
    }
    private float Opacity(Instance instance, Node node)
    {
        // Despite the historical SetLitFlag name, bit 2 pushes an alpha override
        // for the subtree. Retail RenderTraverse 0x44B300; Camera.c render stacks.
        HashSet<int> seen = [];
        while (seen.Add(node.Source))
        {
            if (node.AlphaEnabled) return Math.Clamp(node.Alpha, 0, 1);
            if (ParentNode(instance, node.Parent) is not { } parent) break;
            node = parent;
        }
        return 1;
    }
    private void BeginCleanup(Instance instance)
    {
        if (instance.Cleanup) { Finish(instance); return; }
        instance.StopDelay = -1; instance.Cleanup = true;
        if ((instance.Entry.U32(148) & 0x40) != 0)
            foreach (var tracked in instance.Entry.References[0])
            {
                int index = context.FindBelow(instance.Root, tracked.Text(0,32));
                if (instance.Nodes.TryGetValue(index,out var node) && instance.SavedNodes.TryGetValue(index,out var saved))
                {
                    node.Active = saved.Active; node.Position = saved.Position; node.Euler = saved.Euler; node.Scale = saved.Scale;
                    node.Rotation = saved.Rotation; node.Exact = saved.Exact; node.Changed = saved.Changed; node.Morph = 0;
                    node.PendingPlacement = saved.PendingPlacement; node.PositionWritten = saved.PositionWritten;
                    node.GroundSupported = saved.GroundSupported;
                }
            }
        instance.Sequences = [new Sequence(instance.Entry.Primary)];
        if (instance.Entry.Primary.Events.Count == 0) { measuredEnd = Math.Max(measuredEnd, Time - StepSeconds); Finish(instance); }
    }
    private void Finish(Instance instance)
    {
        instance.Finished = true;
        foreach (long key in activeSounds.Keys.Where(k => k >> 32 == instance.Id).ToArray()) { cues.Add(activeSounds[key] with { Stop = true }); activeSounds.Remove(key); }
        foreach (long key in lights.Keys.Where(k => k >> 32 == instance.Id).ToArray()) lights.Remove(key);
    }
    public AnimationFrame Frame()
    {
        List<AnimationNodePose> poses = []; AnimationCamera? camera = null; HashSet<long> rendered = [];
        foreach (var instance in instances) foreach (var (index, node) in instance.Nodes)
        {
            var source = context.Scene.Nodes[index];
            if (source.ModelIndex is >= 0 and int model && rendered.Add(node.RenderId))
                poses.Add(new(node.RenderId, index, model, World(instance, node), Visible(instance, node), Opacity(instance, node), node.Variant, node.Morph, Math.Max(0, Time - node.CycleStart)));
            if (source.Class == "camera" && node.Changed)
            {
                // Camera.c 0x44ABF0 takes the eye from the world matrix and
                // forward from its negative Z basis, not from a stored target.
                var transform = World(instance, node);
                var forward = Vector3.TransformNormal(-Vector3.UnitZ, transform);
                if (float.IsFinite(forward.LengthSquared()) && forward.LengthSquared() > 1e-12f && float.IsFinite(node.Fov))
                    camera = new(transform.Translation, transform.Translation + Vector3.Normalize(forward), node.Fov * (180 / MathF.PI)) { SourceNode = index, Name = source.Name };
                else notes.Add($"Camera {source.Name} #{index}: invalid transform or field of view; follow pose is unavailable.");
            }
        }
        foreach (var effect in effects)
        {
            string? texture = null; var template = effect.Template;
            if (template.Textures.Length > 0)
            {
                int frame = (int)Math.Floor(effect.Age * Math.Abs(template.Speed));
                frame = template.Loop ? frame % template.Textures.Length : Math.Min(frame, template.Textures.Length - 1);
                texture = template.Textures[frame];
            }
            Matrix4x4.Invert(context.WorldTransform(template.RootNode), out var rootInverse);
            foreach (int index in context.Descendants(template.RootNode))
            {
                if (context.Scene.Nodes[index].ModelIndex is not int model || model < 0) continue;
                Matrix4x4 transform = context.WorldTransform(index) * rootInverse * Matrix4x4.CreateScale(effect.Scale) * Matrix4x4.CreateTranslation(effect.Position);
                bool visible = true; int child = index; HashSet<int> visited = [];
                while (child != template.RootNode && visited.Add(child) && context.Scene.Nodes[child].Parents.FirstOrDefault(-1) is >= 0 and int parent)
                { if (!context.Lods.Includes(parent, child, LodLevel)) { visible = false; break; } child = parent; }
                poses.Add(new((effect.Id << 32) | (uint)index, index, model, transform, visible, 1, 0, 0, effect.Age, texture));
            }
        }
        // Keep simulation/event coordinates in the authored space. Apply the preview
        // origin once at the frame boundary, including nested effects and spatial audio.
        // GroundBottom measures against the same origin, so contact stays at world Y=0.
        Vector3 origin = new(0, PreviewHeight, 0);
        if (PreviewHeight != 0)
        {
            for (int i = 0; i < poses.Count; i++)
            {
                var transform = poses[i].Transform; transform.Translation += origin;
                poses[i] = poses[i] with { Transform = transform };
            }
            if (camera != null) camera = camera with { Position = camera.Position + origin, Target = camera.Target + origin };
        }
        return new(Time, poses, lights.Values.Select(l => l with { Position = l.Position + origin }).ToArray(),
            cues.Select(c => c with { Position = c.Position + origin }).ToArray(), activeSounds.Values.Select(c => c with { Position = c.Position + origin }).ToArray(), camera, fog, screenColor, screenWave, trace.ToArray(),
            instances.SelectMany(i => i.Sequences.Select(s => new AnimationSequenceStatus(i.Id, s.Data.Id, s.Data.Name, s.Cursor, s.State switch { 0 => "Waiting for threshold", 1 => "Running", 2 => "Complete", 3 => "Waiting for release", _ => "Unavailable" }, s.Iteration))).ToArray(), notes.Order(StringComparer.Ordinal).ToArray());
    }
    private static Vector3 Vec(JsonNode? value, Vector3 fallback = default) => value == null ? fallback : new(value.Float("x", fallback.X), value.Float("y", fallback.Y), value.Float("z", fallback.Z));
    private sealed class Node
    {
        public int Source, Parent, Variant = -1;
        public long RenderId;
        public Matrix4x4 Exact;
        public Vector3 Position, Euler, Scale;
        public Quaternion Rotation;
        public float Alpha = 1, Morph, Fov = MathF.PI / 3;
        public bool Active, Changed, AlphaEnabled, PendingPlacement, PositionWritten, GroundSupported;
        public double CycleStart;
        public Matrix4x4 Local => Changed ? Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Position) : Exact;
        public Node Clone() => (Node)MemberwiseClone();
    }
    private sealed class Sequence(AnimationSequence data)
    {
        public AnimationSequence Data = data;
        public int Cursor, State = data.ResetMode, Iteration, KeyOffset;
        public float Elapsed, EventElapsed, LoopElapsed, KeyTime;
        public bool ObservedInfiniteLoop;
        public long Child = -1;
        public AnimationEvent? Work;
        public Sequence Clone() { var copy = (Sequence)MemberwiseClone(); copy.Work = Work?.Clone(); return copy; }
        public void Reset() { Cursor = 0; State = Data.ResetMode; Elapsed = EventElapsed = 0; Work = null; }
    }
    private sealed class Instance
    {
        public required AnimationEntry Entry;
        public long Id;
        public int Root;
        public float Elapsed, StopDelay = -1;
        public bool Cleanup, Finished, FinishRequested, Shared;
        public Dictionary<int, Node> Nodes = [], SavedNodes = [];
        public List<Sequence> Sequences = [];
        public Dictionary<int, long> Children = [];
        public Instance Clone(Func<Node, Node>? cloneNode = null)
        {
            var copy = (Instance)MemberwiseClone(); copy.Nodes = Nodes.ToDictionary(p => p.Key, p => cloneNode?.Invoke(p.Value) ?? p.Value.Clone());
            copy.SavedNodes = SavedNodes; copy.Sequences = Sequences.Select(s => s.Clone()).ToList(); copy.Children = new(Children); return copy;
        }
    }
    private sealed class Effect
    {
        public required AnimationEffectTemplate Template;
        public long Id;
        public Vector3 Position;
        public float Age, Scale = 5;
        public Effect Clone() => (Effect)MemberwiseClone();
    }
    private sealed record Checkpoint(long Ticks, long NextId, int RandomIndex, List<Instance> Instances, Dictionary<int, Node> SharedNodes, List<Effect> Effects, List<AnimationTrace> Trace,
        HashSet<string> Notes, Dictionary<long, AnimationLight> Lights, Dictionary<long, AnimationSoundCue> Sounds, Vector4 Color, Vector4 Wave, AnimationFog? Fog);
    private Checkpoint CaptureCheckpoint()
    {
        Dictionary<Node, Node> copies = [];
        Node Copy(Node n) { if (!copies.TryGetValue(n, out var copy)) copies[n] = copy = n.Clone(); return copy; }
        return new(ticks, nextId, randomIndex, instances.Select(i => i.Clone(Copy)).ToList(), sharedNodes.ToDictionary(p => p.Key, p => Copy(p.Value)), effects.Select(e => e.Clone()).ToList(), [.. trace], [.. notes], new(lights), new(activeSounds), screenColor, screenWave, fog);
    }
    private void Restore(Checkpoint c)
    {
        Dictionary<Node, Node> copies = [];
        Node Copy(Node n) { if (!copies.TryGetValue(n, out var copy)) copies[n] = copy = n.Clone(); return copy; }
        ticks = c.Ticks; nextId = c.NextId; randomIndex = c.RandomIndex; instances = c.Instances.Select(i => i.Clone(Copy)).ToList(); sharedNodes = c.SharedNodes.ToDictionary(p => p.Key, p => Copy(p.Value)); effects = c.Effects.Select(e => e.Clone()).ToList();
        trace = [.. c.Trace]; notes = [.. c.Notes]; lights = new(c.Lights); activeSounds = new(c.Sounds); screenColor = c.Color; screenWave = c.Wave; fog = c.Fog;
    }
}

public static class AnimationMath
{
    public static Quaternion FromEuler(Vector3 radians) => Quaternion.CreateFromYawPitchRoll(radians.Y, radians.X, radians.Z);
    public static Quaternion FromRotationVector(Vector3 vector)
    {
        // Retail 0x475B80 uses sin(length), cos(length), not half the length.
        float angle = vector.Length(); return angle < 1e-8f ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(vector / angle, angle * 2);
    }
    public static Vector3 ToEuler(Quaternion q)
    {
        var m = Matrix4x4.CreateFromQuaternion(q);
        float pitch = MathF.Asin(Math.Clamp(-m.M32, -1, 1));
        return new(pitch, MathF.Atan2(m.M31, m.M33), MathF.Atan2(m.M12, m.M22));
    }
    public static Quaternion ReadQuaternion(AnimationRecord record, int offset) => new(record.F32(offset + 4), record.F32(offset + 8), record.F32(offset + 12), record.F32(offset));
}
