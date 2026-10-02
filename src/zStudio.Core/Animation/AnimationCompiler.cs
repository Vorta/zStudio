using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Core.Animation;

/// <summary>
/// Compiles a mission's animation definitions (<c>data/mN/zrdr/anim.zad</c> and the files it lists) and their keyframe
/// scripts into <c>anim.zbd</c> version 28, as the original tool did. Definitions bind to the world by name: a root or
/// referenced node that the world lacks would make the game reject the whole file, so the compiler reports it. Names
/// with <c>*</c> (one digit each) expand to every matching world node, in name order. The file carries no source stamps,
/// since the game rejects it when a stamped source's time differs.
/// </summary>
public sealed partial class AnimationCompiler
{
    /// <summary>
    /// The engine reads the entry count as a signed 16-bit value (LoadZbd sign-extends it, retail 0x45F18C), so a file
    /// holds at most 32,767 entries including the blank first one.
    /// </summary>
    public const int HeaderSize = 308, MaximumEntries = short.MaxValue;
    public sealed record Result(byte[] Bytes, AnimationPackage Package, IReadOnlyList<string> Warnings, IReadOnlyList<string> Inputs);

    private readonly AnimationDefinitionSet definitions;
    private readonly IReadOnlyCollection<string>? worldNodes;
    private readonly HashSet<string>? nodeSet, effectSet;
    private readonly List<string> warnings = [];
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private readonly CancellationToken token;
    private readonly long maximumBytes;
    private long bytes;
    /// <summary>Keyframe scripts by name and naming file: each is read and parsed once per compilation.</summary>
    private readonly Dictionary<(string Name, string From), Script?> scripts = [];
    private void Warn(string message) { if (seen.Add(message) && warnings.Count < 2000) warnings.Add(message); }

    private AnimationCompiler(AnimationDefinitionSet definitions, IReadOnlyCollection<string>? worldNodes, IReadOnlyCollection<string>? effects, long maximumBytes, CancellationToken token)
    {
        this.definitions = definitions; this.worldNodes = worldNodes; this.token = token; this.maximumBytes = maximumBytes;
        nodeSet = worldNodes == null ? null : new(worldNodes, StringComparer.Ordinal);
        effectSet = effects == null ? null : new(effects, StringComparer.Ordinal);
    }

    /// <summary>
    /// Compiles <paramref name="root"/> (a project path). <paramref name="worldNodes"/> are the names in the mission's
    /// world, used to expand wildcards and to check that every node the entries need exists; without them wildcards
    /// cannot expand and names are not checked.
    /// </summary>
    public static Result Compile(IProjectFiles files, string root, IReadOnlyCollection<string>? worldNodes, CancellationToken token = default)
        => Compile(files, root, worldNodes, null, token);

    /// <summary>
    /// Compiles <paramref name="root"/>, also checking effect templates against <paramref name="effects"/>, the names
    /// effects.zrd defines (see <see cref="EffectNames"/>): the game rejects the whole file when one does not resolve.
    /// </summary>
    public static Result Compile(IProjectFiles files, string root, IReadOnlyCollection<string>? worldNodes, IReadOnlyCollection<string>? effects, CancellationToken token = default)
        => Compile(files, root, worldNodes, effects, FormatRegistry.MaximumDocumentBytes, token);

    internal static Result Compile(IProjectFiles files, string root, IReadOnlyCollection<string>? worldNodes, IReadOnlyCollection<string>? effects, long maximumBytes, CancellationToken token)
    {
        var set = AnimationDefinitionSet.Load(files, root, token);
        AnimationCompiler compiler = new(set, worldNodes, effects, maximumBytes, token);
        foreach (var w in set.Warnings) compiler.Warn(w);
        var package = compiler.Build();
        byte[] bytes = AnimationWriter.Write(package, token);
        return new(bytes, AnimationPackage.Read(bytes, token), compiler.warnings, set.Files.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    /// <summary>
    /// Counts compiled bytes as they are built, so definitions that would expand past what a document can hold (pattern
    /// roots, repeated long keyframe streams) fail early instead of growing without bound.
    /// </summary>
    private void Grow(long count)
    {
        bytes += count;
        if (bytes > maximumBytes) throw new InvalidDataException($"The compiled animations would be larger than {maximumBytes:N0} bytes.");
    }

    /// <summary>
    /// The effect template names an effects.zrd tree defines: the NAME of each entry of its EFFECTS list, which
    /// FindTemplateIndexByName (retail 0x462280) compares exactly with an animation's effect names.
    /// </summary>
    public static IEnumerable<string> EffectNames(ZrdNode root)
    {
        while (root.Children.Count == 1 && root.Children[0].Kind == ZrdKind.Array) root = root.Children[0];
        var children = root.Children;
        for (int i = 0; i + 1 < children.Count; i++)
            if (children[i] is { Kind: ZrdKind.String, Text: "EFFECTS" } && children[i + 1].Kind == ZrdKind.Array)
                foreach (var effect in children[i + 1].Children.Where(c => c.Kind == ZrdKind.Array))
                    if (new AnimationItem("EFFECT", effect, "effects.zrd").TextOf("NAME") is { } name) yield return name;
    }

    /// <summary>
    /// A keyframe script read once: an original SI Animation Script, or zStudio's keyframe format (its tracks by object
    /// and its track without OBJECT lines).
    /// </summary>
    private sealed class Script
    {
        public string Path { get; }
        private readonly SiAnimationScript.Script? si;
        private readonly Dictionary<string, List<AnimationScript.Key>> objects = new(StringComparer.Ordinal);
        private readonly List<AnimationScript.Key>? loose;
        public Script(string path, byte[] bytes)
        {
            Path = path;
            if (SiAnimationScript.Recognize(bytes)) { si = SiAnimationScript.Parse(bytes, path); return; }
            var tracks = AnimationScript.Parse(bytes, path);
            foreach (var (name, keys) in tracks) if (name != null) objects[name] = keys;
            loose = tracks.Count == 1 && tracks[0].Object == null ? tracks[0].Keys : null;
        }
        /// <summary>Whether the script moves a node of this name (an SI script lists it; a keyframe-format script has its OBJECT section or no sections).</summary>
        public bool Moves(string name) => si?.Has(name) ?? (objects.ContainsKey(name) || loose != null);
        /// <summary>The node's keyframes at <paramref name="frameRate"/> frames per second.</summary>
        public List<AnimationKeyframe> Compile(string name, float frameRate, string source) => si != null
            ? SiAnimationScript.Compile(si, name, frameRate, source)
            : AnimationScript.Compile(objects.GetValueOrDefault(name) ?? loose!, frameRate, source);
    }
    private Script? ReadScript(string name, string from)
    {
        if (scripts.TryGetValue((name, from), out var cached)) return cached;
        Script? script = definitions.ReadScript(name, from) is { } file ? new(file.Path, file.Bytes) : null;
        scripts[(name, from)] = script;
        return script;
    }

    private AnimationPackage Build()
    {
        List<AnimationEntry> entries = [Placeholder()];
        foreach (var definition in definitions.Definitions)
        {
            token.ThrowIfCancellationRequested();
            foreach (var (root, bindings) in Roots(definition.Item))
            {
                // Definitions are shared between missions; one whose root the world lacks has no animation there.
                if (!NodeExists(root)) continue;
                if (entries.Count >= MaximumEntries) throw definition.Item.Error($"more than {MaximumEntries - 1} animations; the game reads at most {MaximumEntries} entries, including the blank first one.");
                try { entries.Add(new EntryBuilder(this, definition, root, bindings, entries.Count).Build()); }
                catch (InvalidDataException ex) when (!ex.Message.StartsWith(definition.File, StringComparison.Ordinal)) { throw definition.Item.Error($"{Name(definition.Item)}: {ex.Message}"); }
            }
        }
        // Prefix: signature, version, no source stamps, then the global state (entry count, default gravity).
        byte[] prefix = new byte[12 + 60];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, 0x08170616); BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(4), 28);
        BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(12 + 8), (uint)entries.Count << 16);
        BinaryPrimitives.WriteSingleLittleEndian(prefix.AsSpan(12 + 0x1C), definitions.Gravity);
        AnimationPackage package = new() { Prefix = prefix, Tail = [] };
        package.Entries.AddRange(entries);
        return package;
    }

    private static string Name(AnimationItem definition) => definition.TextOf("ANIMATION_NAME") ?? definition.TextOf("NAME") ?? "(unnamed)";

    /// <summary>The engine skips entry 0; the original compiler wrote it blank with the on-call activation mode.</summary>
    private static AnimationEntry Placeholder()
    {
        byte[] header = new byte[HeaderSize]; header[153] = 3;
        return new(header, 0, -1) { Primary = new(new byte[64]) };
    }

    // ---------------------------------------------------------------- names

    [GeneratedRegex(@"\*")] private static partial Regex Star();

    /// <summary>
    /// The roots a definition binds to. A NAME listing several nodes is one animation bound to the first the world has
    /// (the shipped destroy_turret lists eight turrets and binds to tur_101 in m1, tur_201 in m4); a pattern expands to
    /// every matching world node in name order, and the digits a root matched replace the stars in the definition's
    /// other names.
    /// </summary>
    private IEnumerable<(string Root, string Digits)> Roots(AnimationItem definition) => Roots(definition, worldNodes, Warn);
    internal static IEnumerable<(string Root, string Digits)> Roots(AnimationItem definition, IReadOnlyCollection<string>? worldNodes, Action<string> Warn)
    {
        var name = definition.Item("NAME") ?? throw definition.Error("an animation definition needs a NAME.");
        var names = name.Scalars.Select(v => v.Kind == ZrdKind.String ? v.Text : v.Value).ToList();
        if (names.Count > 1)
        {
            HashSet<string>? present = worldNodes == null ? null : new(worldNodes, StringComparer.Ordinal);
            if (names.FirstOrDefault(n => present == null || present.Contains(n)) is { } first) yield return (first, "");
            yield break;
        }
        foreach (var value in name.Scalars)
        {
            string pattern = value.Kind == ZrdKind.String ? value.Text : value.Value;
            if (!pattern.Contains('*')) { yield return (pattern, ""); continue; }
            if (worldNodes == null) { Warn($"{definition.Source}: {pattern} names world nodes by pattern; without the world it binds to nothing."); continue; }
            Regex match = new("^" + Star().Replace(Regex.Escape(pattern).Replace("\\*", "*"), "([0-9])") + "$", RegexOptions.CultureInvariant);
            // A pattern that matches nothing has no animation in this mission, as a missing root has none.
            var found = worldNodes.Distinct(StringComparer.Ordinal).Where(n => match.IsMatch(n)).Order(StringComparer.Ordinal).ToList();
            foreach (string node in found) yield return (node, string.Concat(match.Match(node).Groups.Values.Skip(1).Select(g => g.Value)));
        }
    }

    /// <summary>A pattern's stars replaced, in order, by the digits its root matched.</summary>
    internal static string Bind(string text, string digits)
    {
        if (digits.Length == 0 || !text.Contains('*')) return text;
        StringBuilder result = new(); int d = 0;
        foreach (char c in text) result.Append(c == '*' && d < digits.Length ? digits[d++] : c);
        return result.ToString();
    }

    internal bool NodeExists(string name) => nodeSet == null || nodeSet.Contains(name);
    internal bool EffectExists(string name) => effectSet == null || effectSet.Contains(name);

    /// <summary>
    /// Launch direction for yaw and pitch in degrees, forward −Z, as the shipped events store it: pitch is linear
    /// (straight up at 90°, with 1 − |pitch|/90 left for the horizontal) and yaw turns the horizontal part. The
    /// arithmetic follows the original's mixed precision, which every shipped direction and velocity reproduces.
    /// </summary>
    public static Vector3 LaunchDirection(float yawDegrees, float pitchDegrees)
    {
        double fraction = pitchDegrees * (double)(1f / 90f), horizontal = 1 - Math.Abs(fraction);
        float radians = yawDegrees * (float)(Math.PI / 180), sin = (float)Math.Sin(radians), cos = (float)Math.Cos(radians);
        return new((float)((0.0 - sin) * horizontal), (float)fraction, (float)((0.0 - cos) * horizontal));
    }
}
