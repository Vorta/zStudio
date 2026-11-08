using System.Globalization;
using zStudio.Core.Animation;

namespace zStudio.Core.Sources;

/// <summary>
/// A map's animation definitions as its list (<c>data/mN/zrdr/anim.zad</c>) reaches them, read-only: every listing of a
/// definition file (a file listed twice compiles twice; a listed file that is missing is skipped), and every definition in
/// compile order with what it binds to in this world. Text and lists are bounded here, before anything shows them.
/// </summary>
public sealed record SourceAnimationDefinitions(IReadOnlyList<SourceAnimationListing> Listings, IReadOnlyList<SourceAnimationDefinition> Definitions)
{
    /// <summary>Whether the binding is known: false when the animations did not build (<see cref="Rejection"/>).</summary>
    public bool BindingKnown { get; init; } = true;
    /// <summary>Why the game would reject the map's animations, when it would.</summary>
    public string? Rejection { get; init; }
    public static SourceAnimationDefinitions Empty { get; } = new([], []);

    /// <summary>At most this many names, sequences or scripts are kept per definition (the rest are counted).</summary>
    public const int MaximumListed = 32;
    internal const int MaximumText = 256;

    internal static SourceAnimationDefinitions Summarize(AnimationDefinitionSet set, IReadOnlyList<AnimationCompiler.SourceBinding>? bindings, string? rejection, CancellationToken token, GameVariant game = GameVariant.Recoil)
    {
        Dictionary<string, int> occurrences = new(StringComparer.OrdinalIgnoreCase);
        List<SourceAnimationListing> listings = [];
        for (int i = 0; i < set.Listings.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var listing = set.Listings[i];
            int occurrence = listing.Missing ? 0 : occurrences.GetValueOrDefault(listing.File);
            if (!listing.Missing) occurrences[listing.File] = occurrence + 1;
            listings.Add(new(i, Short(listing.File), listing.ListedBy == null ? null : Short(listing.ListedBy), listing.Missing, occurrence));
        }
        List<int>[] entries = [.. set.Definitions.Select(_ => new List<int>())];
        foreach (var binding in bindings ?? [])
            if (binding.Definition >= 0 && binding.Definition < entries.Length) entries[binding.Definition].Add(binding.EntryIndex);
        List<SourceAnimationDefinition> definitions = new(set.Definitions.Count);
        Dictionary<(string File, int Ordinal), string> ownerProofs = [];
        long proofWork = 0;
        foreach (var group in set.Definitions.GroupBy(d => d.File, StringComparer.Ordinal))
        {
            var siblings = group.DistinctBy(d => d.Ordinal).ToArray();
            var proofs = AnimationSourceAddressProof.Owners(siblings, token, count =>
            {
                if (count > AnimationCompiler.MaximumExpansionWork - proofWork) throw new InvalidDataException("Animation source ownership exceeds its summary allowance.");
                proofWork += count;
            });
            foreach (var sibling in siblings) ownerProofs.Add((sibling.File, sibling.Ordinal), proofs[sibling.Ordinal]);
        }
        for (int i = 0; i < set.Definitions.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            definitions.Add(Describe(i, set.Definitions[i], entries[i], game) with { AddressProof = ownerProofs[(set.Definitions[i].File, set.Definitions[i].Ordinal)] });
        }
        bool known = bindings != null && rejection == null;
        return new(listings, definitions) { BindingKnown = known, Rejection = rejection == null ? null : Short(rejection, 1024) };
    }

    private static SourceAnimationDefinition Describe(int index, AnimationDefinition definition, IReadOnlyList<int> entries, GameVariant game)
    {
        var item = definition.Item;
        string? Text(string key) { try { return item.TextOf(key) is { } text ? Short(text) : null; } catch (InvalidDataException) { return null; } }
        string? Number(string key, int operand = 0, bool absolute = false)
        {
            if (item.Item(key) is not { } setting) return null;
            try { double value = setting.Number(operand); return (absolute ? Math.Abs(value) : value).ToString("0.###", CultureInfo.InvariantCulture); }
            catch (InvalidDataException) { return setting.Scalars.Count > operand ? Short(setting.Scalars[operand].Text) : null; }
        }
        // NAME as written: one name, a list (the first the world has binds) or a pattern (each * is one digit).
        var names = item.Item("NAME")?.Scalars.Select(s => s.Text).ToArray() ?? [];
        string root = names.Length == 0 ? "" : string.Join(", ", names.Take(8).Select(n => Short(n, 64))) + (names.Length > 8 ? $", … {names.Length - 8} more" : "");
        string name = Text("ANIMATION_NAME") ?? (names.Length > 0 ? Short(names[0]) : "(unnamed)");
        string? execution = item.Item("EXECUTION_BY_RANGE") is { } range
            ? "By range · " + (game == GameVariant.MechWarrior3 && range.Scalars.Count > 1
                ? "at least " + (Number("EXECUTION_BY_RANGE", absolute: true) ?? "?") + " and less than " + (Number("EXECUTION_BY_RANGE", 1, absolute: true) ?? "?")
                : "less than " + (Number("EXECUTION_BY_RANGE", absolute: true) ?? "?")) + " from the attach node"
            : item.Has("EXECUTION_BY_ZONE") ? "By zone" : null;
        string? prerequisites = null;
        if (item.Item("ACTIVATION_PREREQUISITE") is { } prerequisite)
        {
            int required = prerequisite.Item("REQUIRED")?.Items.Count() ?? 0, options = prerequisite.Item("OPTIONS")?.Items.Count() ?? 0;
            prerequisites = required == 0 && options == 0 ? "Listed" : string.Join(" · ", new[] { required > 0 ? $"{required} required" : null, options > 0 ? $"{options} options" : null }.OfType<string>());
        }
        List<SourceAnimationSequence> sequences = [];
        int sequenceCount = 0;
        List<string> scripts = []; int scriptCount = 0;
        int cleanup = Events(item.Item("RESET_STATE")?.Items ?? []);
        foreach (var sequence in item.All("SEQUENCE_DEFINITION"))
        {
            sequenceCount++;
            if (sequences.Count < MaximumListed)
            {
                string label = sequence.All("NAME").LastOrDefault() is { Scalars.Count: > 0 } named ? Short(named.Text(), 64) : "";
                sequences.Add(new(label, Events(sequence.Items)));
            }
        }
        foreach (var ev in (item.Item("RESET_STATE")?.Items ?? []).Concat(item.All("SEQUENCE_DEFINITION").SelectMany(s => s.Items)))
            if (ev.Key == "OBJECT_MOTION_SI_SCRIPT" && ev.TextOf("SCRIPT_FILENAME") is { } script)
            {
                if (scripts.Contains(script, StringComparer.OrdinalIgnoreCase)) continue;
                scriptCount++;
                if (scripts.Count < MaximumListed) scripts.Add(Short(script));
            }
        return new(index, definition.Listing, Short(definition.File), definition.Ordinal, name, root, names.Any(n => n.Contains('*')), Text("ANIMATION_ROOT_NAME"),
            Text("ACTIVATION"), Number("HEALTH"), Number("RESET_TIME"), execution, item.Item("EXECUTION_PRIORITY") is { } priority && priority.Scalars.Count > 0 ? Short(priority.Text()) : null, prerequisites,
            sequenceCount, sequences, cleanup, scriptCount, scripts, entries);

        // The events of a sequence: its items other than its name and the settings a sequence may carry.
        static int Events(IEnumerable<AnimationItem> items) => items.Count(i => i.Key is not ("NAME" or "ACTIVATION" or "START_TIME"));
    }

    internal static string Short(string text, int maximum = MaximumText) => JsonData.ShownText(text, maximum);
}

/// <summary>One listing of a definition file: <see cref="Occurrence"/> counts the earlier listings of the same file.</summary>
public sealed record SourceAnimationListing(int Index, string File, string? ListedBy, bool Missing, int Occurrence);

/// <summary>
/// A definition in compile order: its file and position there, its listing, its name (ANIMATION_NAME, else NAME), NAME as
/// written (<see cref="Pattern"/> when a star stands for a digit), its attach node, its settings as written, its sequences
/// (at most <see cref="SourceAnimationDefinitions.MaximumListed"/>, <see cref="SequenceCount"/> in all), the keyframe
/// scripts it names, and the compiled entries it produced in this world (none when it binds to nothing here).
/// </summary>
public sealed record SourceAnimationDefinition(int Index, int Listing, string File, int Ordinal, string Name, string RootName, bool Pattern, string? Attach,
    string? Activation, string? Health, string? ResetTime, string? Execution, string? Priority, string? Prerequisites,
    int SequenceCount, IReadOnlyList<SourceAnimationSequence> Sequences, int CleanupEvents, int ScriptCount, IReadOnlyList<string> Scripts, IReadOnlyList<int> Entries)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string? AddressProof { get; init; }
}

public sealed record SourceAnimationSequence(string Name, int Events);
