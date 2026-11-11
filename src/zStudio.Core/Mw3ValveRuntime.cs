using zStudio.Core.Formats;

namespace zStudio.Core;

public sealed record Mw3ValveTrace(long Sequence, float Time, string Valve, string Kind, int Value, string Detail, Guid? Record = null);
public sealed record Mw3ValveEffect(string Valve, string Kind, ZrdNode Arguments, Guid Record, float Time);
public sealed record Mw3ValveValue(string Name, int Value, string? Condition);
public sealed record Mw3ValveTimer(string Target, int Value, float Due);

/// <summary>Owned, bounded mission state. It never writes source trees or the accepted mission scene.</summary>
public sealed class Mw3ValveRuntime
{
    private sealed class Holder(string name)
    {
        public readonly string Name = name;
        public int Value;
        public string? Condition;
        public string[] Inputs = [];
        public readonly List<Reaction> Reactions = [];
    }
    private sealed class Reaction(Guid record, string kind, ZrdNode args, string? target = null)
    {
        public readonly Guid Record = record;
        public readonly string Kind = kind;
        public readonly ZrdNode Args = args;
        public readonly string? Target = target;
        public bool Consumed;
    }
    private readonly Dictionary<string, Holder> holders = new(StringComparer.Ordinal);
    private readonly List<Mw3ValveTimer> timers = [];
    private bool compositeTimers;
    private readonly Queue<Mw3ValveTrace> trace = new();
    private readonly List<Mw3ValveEffect> effects = [];
    private readonly HashSet<string> reading = new(StringComparer.Ordinal);
    private int work, reactionCount, callDepth, fireDepth;
    private long sequence;
    private readonly Func<string, ZrdNode, bool>? available;
    private readonly Action<Mw3ValveEffect>? immediate;
    public const int MaximumTimers = 32_768, MaximumTrace = 4096, MaximumWork = 100_000;
    public float Time { get; private set; }
    public bool Halted { get; private set; }
    public IReadOnlyList<Mw3ValveTimer> Timers => timers.AsReadOnly();
    public IReadOnlyList<Mw3ValveTrace> Trace => trace.ToArray();

    public Mw3ValveRuntime(Mw3ValveProgram program, Func<string, ZrdNode, bool>? available = null, Action<Mw3ValveEffect>? immediate = null)
    {
        this.available = available;
        this.immediate = immediate;
        work = MaximumWork;
        foreach (var d in program.Definitions)
        {
            var holder = Get(d.Name);
            if (d.Condition != null && d.Inputs.Count > 0)
            {
                if (holder.Condition != null) { Log(d.Name, "ignored", 0, "Conflicting compound definition; record ignored.", d.Record); continue; }
                holder.Condition = d.Condition; holder.Inputs = [.. d.Inputs];
                foreach (string input in holder.Inputs) Add(Get(input), new(d.Record, "update", Empty, holder.Name));
            }
            if (d.Initial is { } initial && holder.Condition == null) holder.Value = initial;
            foreach (var action in d.Actions)
            {
                string? target = action.Kind is "update" or "delayupdate" ? Text(action.Arguments, 0) : null;
                if (target != null) _ = Get(target);
                Add(holder, new(d.Record, action.Kind, action.Arguments, target));
            }
        }
        foreach (var holder in holders.Values) _ = Read(holder);
    }
    private static readonly ZrdNode Empty = ZrdNode.Create(ZrdKind.Array);
    private void Add(Holder holder, Reaction reaction)
    {
        if (++reactionCount > Mw3ValveProgram.MaximumReactions) throw new InvalidDataException("Valve preview reaction limit exceeded.");
        holder.Reactions.Add(reaction);
    }
    private Holder Get(string name)
    {
        if (name.Length > 4096) throw new InvalidDataException("Valve name exceeds the preview's 4,096-character limit.");
        if (holders.TryGetValue(name, out var holder)) return holder;
        if (holders.Count == Mw3ValveProgram.MaximumValves) throw new InvalidDataException("Valve preview name limit exceeded.");
        holder = new(name); holders.Add(name, holder); return holder;
    }
    public int Value(string name) => Run(() => Read(Get(name)));
    public void Register(IEnumerable<string> names) => Run(() =>
    {
        int count = 0;
        foreach (string name in names)
        {
            if (++count > Mw3ValveProgram.MaximumReactions) throw new InvalidDataException("Too many valve references for preview.");
            _ = Get(name);
        }
        return 0;
    });
    public IReadOnlyList<Mw3ValveValue> Values() => Run(() => (IReadOnlyList<Mw3ValveValue>)holders.Values.Select(h => new Mw3ValveValue(h.Name, Read(h), h.Condition)).ToArray());
    public void Set(string name, int value) => Run(() => { SetCore(Get(name), value); return 0; });
    public void Update(string name) => Run(() => { Fire(Get(name)); return 0; });
    internal void SetClock(float now) => Run(() =>
    {
        if (!float.IsFinite(now) || now < Time) throw new ArgumentException("Advance valve time monotonically to a finite value.");
        Time = now; return 0;
    });
    public void Tick(float now) => Run(() =>
    {
        if (!float.IsFinite(now) || now < Time) throw new ArgumentException("Advance valve time monotonically to a finite value.");
        Time = now;
        bool lone = timers.Count == 1 && !compositeTimers;
        int visited = 0;
        while (visited < timers.Count)
        {
            Spend();
            var timer = timers[visited];
            if (!(timer.Due < now)) { visited++; continue; }
            timers.RemoveAt(visited);
            SetCore(Get(timer.Target), timer.Value);
            if (lone)
            {
                if (timers.Count > 0) Log(timer.Target, "timer quirk", timer.Value, "Retail lone-timer completion drops timers created by its reactions.");
                timers.Clear(); compositeTimers = false; break;
            }
        }
        if (timers.Count == 0) compositeTimers = false;
        return 0;
    });
    public IReadOnlyList<Mw3ValveEffect> DrainEffects()
    {
        var result = effects.ToArray(); effects.Clear(); return result;
    }
    private T Run<T>(Func<T> action)
    {
        if (Halted) throw new InvalidOperationException("Valve simulation halted; reset it before continuing.");
        if (callDepth++ == 0) work = MaximumWork;
        try { return action(); }
        catch (InvalidDataException)
        {
            Halted = true; effects.Clear(); throw;
        }
        finally { --callDepth; }
    }
    private void Spend()
    {
        if (--work < 0) throw new InvalidDataException("Valve simulation work limit reached; check immediate reaction cycles.");
    }
    private int Read(Holder holder)
    {
        Spend();
        if (holder.Condition == null) return holder.Value;
        if (reading.Count >= 128) throw new InvalidDataException("Compound valve depth limit exceeded.");
        if (!reading.Add(holder.Name)) throw new InvalidDataException("Cyclic compound valve condition.");
        try
        {
            bool zero = false, nonzero = false;
            foreach (string input in holder.Inputs) { int value = Read(Get(input)); zero |= value == 0; nonzero |= value != 0; }
            return holder.Condition switch { "any_nonzero" => nonzero ? 1 : 0, "any_zero" => zero ? 1 : 0, "all_nonzero" => zero ? 0 : 1, _ => nonzero ? 0 : 1 };
        }
        finally { reading.Remove(holder.Name); }
    }
    private void SetCore(Holder holder, int value)
    {
        Spend(); int old = Read(holder);
        if (old == value) return;
        if (holder.Condition == null) holder.Value = value;
        Log(holder.Name, "set", value, holder.Condition == null ? $"Previous value {old}." : "Computed holder ignores assignment; reactions still fire.");
        Fire(holder);
    }
    private void Fire(Holder holder)
    {
        Spend();
        if (fireDepth >= 128) throw new InvalidDataException("Valve reaction depth limit exceeded; check immediate cycles.");
        fireDepth++;
        try
        {
            foreach (var reaction in holder.Reactions)
            {
                Spend();
                int value = Read(holder);
                if (reaction.Kind == "update") { if (reaction.Target != null) Fire(Get(reaction.Target)); continue; }
                if (reaction.Kind == "delayupdate")
                {
                    if (reaction.Target == null) continue;
                    float due = Time + Number(reaction.Args, 1);
                    if (!float.IsFinite(due)) throw new InvalidDataException("Valve timer is not finite.");
                    if (timers.Count == MaximumTimers) throw new InvalidDataException("Valve timer limit reached.");
                    if (timers.Count > 0) compositeTimers = true;
                    timers.Add(new(reaction.Target, value, due));
                    Log(holder.Name, "timer", value, FormattableString.Invariant($"sets {MissionAiValves.Short(reaction.Target)} to {value} at {due:0.###} s"), reaction.Record); continue;
                }
                int triggerIndex = reaction.Kind switch
                {
                    "sound" or "sound_once" or "animation" or "animation_once" or "destroy" => 1,
                    "teleport" => 3, "missionreset" or "missionabort" or "missioncomplete" => 0, _ => 2
                };
                if (Integer(reaction.Args, triggerIndex) != value || reaction.Consumed) continue;
                if (available?.Invoke(reaction.Kind, reaction.Args) == false)
                { Log(holder.Name, "unavailable", value, reaction.Kind + ": target or resource was not found.", reaction.Record); continue; }
                if (reaction.Kind.EndsWith("_once", StringComparison.Ordinal)) reaction.Consumed = true;
                if (effects.Count == MaximumWork) throw new InvalidDataException("Valve effect limit reached.");
                var effect = new Mw3ValveEffect(holder.Name, reaction.Kind, reaction.Args, reaction.Record, Time);
                effects.Add(effect);
                Log(holder.Name, reaction.Kind, value, reaction.Kind is "missionreset" or "missioncomplete" ? "No retail gameplay effect." : Text(reaction.Args, 0) ?? "", reaction.Record);
                immediate?.Invoke(effect);
            }
        }
        finally { fireDepth--; }
    }
    private void Log(string name, string kind, int value, string detail, Guid? record = null)
    {
        if (trace.Count == MaximumTrace) trace.Dequeue();
        trace.Enqueue(new(++sequence, Time, MissionAiValves.Short(name), kind, value, MissionAiValves.Short(detail, 512), record));
    }
    public static string? Text(ZrdNode args, int index) => args.Children.ElementAtOrDefault(index) is { Kind: ZrdKind.String } n ? n.Text : null;
    public static int Integer(ZrdNode args, int index) => args.Children.ElementAtOrDefault(index) is { Kind: ZrdKind.Int or ZrdKind.Float } n ? unchecked((int)n.Bits) : 0;
    public static float Number(ZrdNode args, int index) => args.Children.ElementAtOrDefault(index) is { } n
        ? n.Kind is ZrdKind.Float or ZrdKind.Int ? BitConverter.UInt32BitsToSingle(n.Bits) : 0 : 0;
    // zReader get_vec3/get_float_value convert integer operands; delayupdate stores the raw word instead.
    public static float Scalar(ZrdNode args, int index) => args.Children.ElementAtOrDefault(index) is { Kind: ZrdKind.Int } n
        ? unchecked((int)n.Bits) : Number(args, index);
}
