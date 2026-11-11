using System.Numerics;
using zStudio.Core.Animation;
using zStudio.Core.Formats;

namespace zStudio.Core;

public sealed record Mw3ValveActorState(string Name, bool? PoweredDown = null, bool Destroyed = false,
    int? Team = null, int? Strategy = null, Vector3? Position = null, float? Yaw = null)
{
    public IReadOnlyDictionary<string, bool> Switches { get; init; } = new Dictionary<string, bool>();
}

/// <summary>An animation that sets a valve through callbacks 42–49: the node-name valve its entry binds and the values its events set.</summary>
public sealed record Mw3ValveCallback(string Valve, int Entry, string Animation, IReadOnlyList<int> Values);

/// <summary>A single preview clock joins native reactions, animation callbacks and owned actor overrides.</summary>
public sealed class Mw3ValveSimulation
{
    public Guid Id { get; } = Guid.NewGuid();
    public Mw3ValveRuntime Runtime { get; }
    public AnimationPreviewContext Context { get; }
    private readonly AnimationPlayer player;
    private readonly Dictionary<(int Entry, int Root), string?> bindings = [];
    private readonly Dictionary<string, Mw3ValveActorState> actors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ZrdNode> targets = new(StringComparer.Ordinal);
    private readonly List<AnimationSoundCue> sounds = [];
    private bool animationFrontierPending;
    private CancellationToken operationToken;
    private long soundId;
    public IReadOnlyDictionary<string, Mw3ValveActorState> Actors => actors;
    public double Time => player.Time;
    public bool Aborted { get; private set; }
    public bool Faulted { get; private set; }
    public AnimationFrame CurrentFrame { get; private set; }

    public Mw3ValveSimulation(AnimationPreviewContext context, Mw3ValveProgram program, CancellationToken token = default, int lod = 0)
    {
        token.ThrowIfCancellationRequested();
        operationToken = token;
        Context = context; Runtime = new(program, Available, ApplyImmediateEffect);
        player = new(context, 0, 1, false, token) { LodLevel = Math.Max(0, lod) };
        player.NativeValveCallback = Callback;
        player.NativeValveStarted = Bind;
        foreach (var actor in context.Mission?.Actors ?? []) actors.TryAdd(actor.Name, new(actor.Name));
        foreach (var resource in context.Mission?.AiNetworks.ValveSources ?? [])
        {
            token.ThrowIfCancellationRequested();
            Runtime.Register(MissionAiValves.Records(resource.Member, resource.Root, token).SelectMany(r => MissionAiValves.References(r, token)).Select(r => r.Name));
            if (!resource.Member.Equals("targets.zrd", StringComparison.OrdinalIgnoreCase)) continue;
            var fields = MissionAiValves.Fields(resource.Root).Children;
            for (int i = 0; i + 1 < fields.Count; i += 2)
            {
                if (fields[i].Kind != ZrdKind.String || fields[i + 1].Kind != ZrdKind.Array) continue;
                string name = fields[i].Text;
                if (!context.Scene.Nodes.Any(n => n.Name == name)) continue;
                targets.TryAdd(name, fields[i + 1]); actors.TryAdd(name, new(name));
            }
        }
        player.StartValveLoadAnimations(token);
        foreach (string name in context.Mission?.StartupAnimations ?? [])
            if (context.Package.Entries.FirstOrDefault(e => e.Index > 0 && e.Name == name) is { } entry) player.StartValveMissionStartup(entry.Index, token);
        CurrentFrame = player.FlushValveFrontier(token);
        ApplyEffects(token); CurrentFrame = Frame(token); operationToken = default;
    }
    private bool Available(string kind, ZrdNode args)
    {
        string? name = Mw3ValveRuntime.Text(args, 0);
        return kind switch
        {
            "sound" or "sound_once" => name != null && Context.Sounds.ContainsKey(name),
            "animation" or "animation_once" => Context.Package.Entries.Any(e => e.Index > 0 && e.Name == name),
            "missionreset" or "missionabort" or "missioncomplete" => true,
            "teleport" => Context.Mission?.Actors.Any(a => a.Name == name) == true,
            _ => name != null && actors.ContainsKey(name)
        };
    }
    private void Mutate(Action action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Faulted) throw new InvalidOperationException("Valve preview was interrupted; reset it before continuing.");
        var previous = operationToken; operationToken = token; player.BeginValveOperation();
        try { action(); token.ThrowIfCancellationRequested(); }
        catch { Faulted = true; throw; }
        finally { operationToken = previous; }
    }
    public void Set(string name, int value, CancellationToken token = default) => Mutate(() => { Runtime.Set(name, value); ApplyEffects(token); CurrentFrame = Frame(token); }, token);
    public void Update(string name, CancellationToken token = default) => Mutate(() => { Runtime.Update(name); ApplyEffects(token); CurrentFrame = Frame(token); }, token);
    public void Traverse(AiNetwork network, AiNode node, int slot, CancellationToken token = default)
    {
        var link = node.PreviewLinks.FirstOrDefault(l => l.Slot == slot);
        if (link?.Target == null) throw new InvalidDataException("This outgoing slot has no uniquely resolved target.");
        var route = Mw3ValveRoutes.Evaluate(network, node, slot, Runtime);
        if (!route.Passable) throw new InvalidDataException(string.Join(" ", route.Reasons));
        Mutate(() =>
        {
            foreach (var assignment in route.Assignments) Runtime.Set(assignment.Valve, assignment.Value);
            ApplyEffects(token); CurrentFrame = Frame(token);
        }, token);
    }
    /// <summary>
    /// The time of the first tick after <paramref name="now"/> on which a timer due at <paramref name="due"/> runs: the game
    /// compares in single precision and runs a timer only once its time is strictly behind the clock. A timer due later
    /// is still waiting then.
    /// </summary>
    public static double TimerTime(double now, float due)
    {
        const long last = 3600 * 60;
        long tick = Math.Min(last, (long)Math.Round(now / AnimationPlayer.StepSeconds) + 1);
        if (float.IsFinite(due)) tick = Math.Clamp((long)Math.Floor(due / AnimationPlayer.StepSeconds), tick, last);
        while (tick < last && !(due < (float)(tick * AnimationPlayer.StepSeconds))) tick++;
        return tick * AnimationPlayer.StepSeconds;
    }
    public AnimationFrame Advance(double seconds, CancellationToken token = default)
    {
        if (!double.IsFinite(seconds) || seconds < Time || seconds > 3600) throw new ArgumentException("Advance between the current time and one hour; Reset returns to zero.");
        long target = (long)Math.Floor(seconds / AnimationPlayer.StepSeconds + 1e-7);
        Mutate(() =>
        {
            while (player.Time / AnimationPlayer.StepSeconds + .5 < target)
            {
                token.ThrowIfCancellationRequested();
                player.ValveClock = player.Time + AnimationPlayer.StepSeconds;
                try
                {
                    Runtime.SetClock((float)player.Time);
                    CurrentFrame = player.Step(token);
                    ApplyEffects(token);
                    Runtime.Tick((float)player.Time);
                    ApplyEffects(token);
                }
                finally { player.ValveClock = null; }
            }
            CurrentFrame = Frame(token);
        }, token);
        return CurrentFrame;
    }
    private bool Callback(int entryIndex, int root, int message)
    {
        if (message is < 42 or > 49) return false;
        Bind(entryIndex, root);
        string? name = bindings[(entryIndex, root)];
        if (name != null) Runtime.Set(name, message & 1);
        return name != null;
    }
    private void Bind(int entryIndex, int root)
    {
        var key = (entryIndex, root);
        if (!bindings.TryGetValue(key, out var name))
        {
            name = entryIndex > 0 ? CallbackValve(Context.Package.Entries[entryIndex], Context.Scene, root, player.ValveParent) : null;
            bindings.Add(key, name);
            if (name != null) Runtime.Register([name]);
        }
    }
    /// <summary>
    /// The node-name valve an entry's callbacks 42–49 bind when it is loaded on <paramref name="root"/>: the first such event
    /// of its normal sequences whose zero to three uniquely parented ancestors exist names it. Null without one.
    /// </summary>
    internal static string? CallbackValve(AnimationEntry entry, GameScene scene, int root, Func<int, int> parent)
    {
        if (entry.Bytes[152] == 5 || (entry.U32(148) & 0x10) == 0) return null;
        foreach (var ev in entry.Sequences.SelectMany(s => s.Events).Where(e => e.Type == 35 && e.I32(12) is >= 42 and <= 49))
        {
            int node = root, up = (ev.I32(12) - 42) / 2;
            while (up > 0 && node >= 0 && node < scene.Nodes.Count && parent(node) is >= 0 and var above) { node = above; up--; }
            if (up == 0 && node >= 0 && node < scene.Nodes.Count) return scene.Nodes[node].Name;
        }
        return null;
    }
    /// <summary>
    /// The valves the map's loaded animations set through callbacks 42–49, read from the entries' events and the roots they
    /// bind to in the starting scene (no animation runs). The valve test binds the same way as each animation is loaded.
    /// </summary>
    public static IReadOnlyList<Mw3ValveCallback> CallbackValves(AnimationPackage package, MissionSceneContext mission, CancellationToken token = default)
    {
        List<Mw3ValveCallback> result = [];
        var scene = mission.Scene;
        foreach (var entry in package.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.Index <= 0 || !mission.AnimationEntryRoots.TryGetValue(entry.Index, out int root) || root < 0 || root >= scene.Nodes.Count) continue;
            if (CallbackValve(entry, scene, root, node => scene.Nodes[node].Parents is [var parent] ? parent : -1) is not { } valve) continue;
            int[] values = [.. entry.Sequences.SelectMany(s => s.Events).Where(e => e.Type == 35 && e.I32(12) is >= 42 and <= 49).Select(e => e.I32(12) & 1).Distinct().Order()];
            result.Add(new(valve, entry.Index, entry.Name, values));
            if (result.Count == Mw3ValveMap.MaximumLinks) break;
        }
        return result;
    }
    private void ApplyEffects(CancellationToken token)
    {
        int remaining = 10_000;
        while (true)
        {
            var effects = Runtime.DrainEffects(); if (effects.Count == 0) break;
            bool animation = animationFrontierPending; animationFrontierPending = false;
            foreach (var effect in effects)
            {
                token.ThrowIfCancellationRequested();
                if (--remaining < 0) throw new InvalidDataException("Valve/animation reaction frontier limit exceeded.");
                string? name = Mw3ValveRuntime.Text(effect.Arguments, 0);
                if (effect.Kind is "animation" or "animation_once")
                {
                    // Activation (and its synchronous cleanup) already ran in reaction order.
                }
                else if (effect.Kind is "sound" or "sound_once")
                {
                    if (name != null && Context.Sounds.TryGetValue(name, out var sound) && sounds.Count < 1024)
                        sounds.Add(new(--soundId, name, false, sound.Loop, effect.Time, Vector3.Zero));
                }
                else if (effect.Kind == "missionabort") Aborted = true;
            }
            if (animation) CurrentFrame = player.FlushValveFrontier(token);
        }
    }
    // Actor reactions execute within the valve frontier: a death's valve writes precede later update reactions.
    private void ApplyImmediateEffect(Mw3ValveEffect effect)
    {
        string? name = Mw3ValveRuntime.Text(effect.Arguments, 0);
        if (effect.Kind is "animation" or "animation_once") { StartAnimation(name); return; }
        if (name == null || !actors.TryGetValue(name, out var actor)) return;
        switch (effect.Kind)
        {
            case "shutdown": actor = actor with { PoweredDown = Mw3ValveRuntime.Integer(effect.Arguments, 1) != 0 }; break;
            case "destroy":
                if (actor.Destroyed) return;
                actors[name] = actor with { Destroyed = true };
                if (targets.TryGetValue(name, out var target) && Mw3ValveProgram.Find(target, "health_below") is { } thresholds)
                    foreach (int i in Enumerable.Range(0, thresholds.Children.Count / 3).Select(i => i * 3)
                        .OrderByDescending(i => Mw3ValveRuntime.Number(thresholds, i + 2)))
                        if (thresholds.Children[i].Kind == ZrdKind.String) Runtime.Set(thresholds.Children[i].Text, Mw3ValveRuntime.Integer(thresholds, i + 1));
                if (targets.TryGetValue(name, out var destruction) && Mw3ValveProgram.Find(destruction, "destroy_anim") is { } destroyArguments)
                    StartAnimation(Mw3ValveRuntime.Text(destroyArguments, 0));
                return;
            case "setteam": actor = actor with { Team = Mw3ValveRuntime.Integer(effect.Arguments, 1) }; break;
            case "attack_strategy": actor = actor with { Strategy = Mw3ValveRuntime.Integer(effect.Arguments, 1) }; break;
            case "scan_aim": case "scan_hit": case "scan_time": case "canleave":
                actor = actor with { Switches = new Dictionary<string, bool>(actor.Switches) { [effect.Kind] = Mw3ValveRuntime.Integer(effect.Arguments, 1) != 0 } }; break;
            case "teleport":
                if (effect.Arguments.Children.ElementAtOrDefault(1) is { Children.Count: 3 } xyz)
                {
                    actor = actor with { Position = new(Mw3ValveRuntime.Scalar(xyz, 0), Mw3ValveRuntime.Scalar(xyz, 1), Mw3ValveRuntime.Scalar(xyz, 2)), Yaw = Mw3ValveRuntime.Scalar(effect.Arguments, 2) };
                    if (Context.Mission?.Actors.FirstOrDefault(a => a.Name == name) is { } placed) player.TeleportValveActor(placed.Root, actor.Position.Value, actor.Yaw.Value, operationToken);
                }
                break;
        }
        actors[name] = actor;
    }
    private void StartAnimation(string? name)
    {
        if (Context.Package.Entries.FirstOrDefault(e => e.Index > 0 && e.Name == name) is { } entry)
            animationFrontierPending |= player.StartValveAnimation(entry.Index, operationToken);
    }
    private AnimationFrame Frame(CancellationToken token)
    {
        var frame = player.Frame(token);
        sounds.RemoveAll(s => !Context.Sounds.TryGetValue(s.Name, out var sound) || !s.Persistent && Time - s.StartedAt >= sound.Duration);
        var poses = frame.Nodes.ToList(); var rendered = poses.Select(p => p.SourceNode).ToHashSet();
        foreach (var actor in Context.Mission?.Actors ?? [])
        {
            if (!actors.TryGetValue(actor.Name, out var state) || state.Position == null) continue;
            var descendants = player.ValveActorNodes(actor.Root, token);
            foreach (var pose in player.ValveActorPoses(actor.Root, descendants, token)) if (rendered.Add(pose.SourceNode)) poses.Add(pose);
        }
        return frame with { Nodes = poses, Sounds = frame.Sounds.Concat(sounds).ToArray(), ActiveSounds = frame.ActiveSounds.Concat(sounds).ToArray() };
    }
}
