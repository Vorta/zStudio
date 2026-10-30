using System.Globalization;
using zStudio.Core;
using zStudio.Core.Animation;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

/// <summary>
/// How a definition starts (design/world-tree-animations.md §4), first match wins: the mission's opening or a saved game's
/// load (startanims.zrd), on startup, on damage, called by another animation of the map, started by the game on the map's
/// units, or none of these. Never "unused": the game starts animations by name that no static scan sees.
/// </summary>
internal enum AnimationStartKind { Opening, Load, Startup, Damage, Called, Game, None }

/// <summary>The bindings of one calling definition that call a definition ("rdrail_destroy0*", 8).</summary>
internal sealed record AnimationCallerGroup(int Definition, string Name, int Bindings);

/// <summary>
/// How a definition starts: its kind, the detail line ("Called by rdrail_destroy0* (8)"), its activation as compiled
/// (ON_CALL, ON_STARTUP, WEAPON_HIT, COLLIDE_HIT, WEAPON_OR_COLLIDE_HIT), its HEALTH, and the calls of the map that reach it.
/// <see cref="Known"/> is false when the map's animations did not build: only the activation is known then.
/// </summary>
internal sealed record AnimationStart(AnimationStartKind Kind, string Detail, string WideDetail, string Activation, double? Health,
    int CallerCount, IReadOnlyList<AnimationCallerGroup> Callers, bool Known);

/// <summary>
/// What a definition acts on, as the grey text after its name says it (§5): kind object, objects, template, playerTank, units or
/// none, the text, and how many (bindings, or units the game starts it on). <see cref="Quiet"/>: "template" when the template
/// is named as the definition, drawn quieter.
/// </summary>
internal sealed record AnimationTarget(string Kind, string Text, int Count, bool Quiet);

/// <summary>One definition's row facts: its start, target, the earlier definition of the same name, and what a search matches.</summary>
internal sealed record AnimationDefinitionSummary(int Definition, AnimationStart Start, AnimationTarget? Target, int? DuplicateOf, bool Frameable, string SearchText)
{
    /// <summary>The fields a search matches, in the order a result names them.</summary>
    internal IReadOnlyList<(string Field, string Text)> SearchFields { get; init; } = [];
}

/// <summary>
/// The World tree's Animations rows for one built map (§13): per definition how it starts, what it acts on, a repeated name
/// and the search text; per bound object the definitions binding it. Built once per shown map, off the UI thread, from the
/// graph index (callers, the opening), the object uses (game-started units) and the definitions' bound rows. Immutable.
/// </summary>
internal sealed class AnimationTreeSummary
{
    internal sealed record Inputs(SourceAnimationDefinitions Definitions, AnimationPackage? Package, AnimationSourceProvenance? Provenance,
        IReadOnlyList<string> NewGameStart, IReadOnlyList<string> LoadGameStart, string Map, IReadOnlyDictionary<int, int[]> DefinitionRoots,
        IReadOnlyDictionary<int, MissionAnimationUses> Uses, Func<int, bool> IsTreeRow, Func<int, string> DisplayName, int? PlayerTank,
        AnimationGraphIndex? Index = null);

    internal IReadOnlyDictionary<int, AnimationDefinitionSummary> Definitions { get; }
    /// <summary>The definitions binding each bound row (its <see cref="SourceMapSections.DefinitionRoots"/> inverse).</summary>
    internal IReadOnlyDictionary<int, int[]> DefinitionsByRoot { get; }
    /// <summary>The graph index the summary read (the package's), for Animation mode to reuse.</summary>
    internal AnimationGraphIndex? Index { get; }
    internal string Map { get; }
    private AnimationTreeSummary(IReadOnlyDictionary<int, AnimationDefinitionSummary> definitions, IReadOnlyDictionary<int, int[]> byRoot, AnimationGraphIndex? index, string map)
    { Definitions = definitions; DefinitionsByRoot = byRoot; Index = index; Map = map; }

    // ---------------------------------------------------------------- words

    /// <summary>The group a kind falls in (Group by start): the opening and a saved game's load are both a mission start.</summary>
    internal static AnimationStartKind Group(AnimationStartKind kind) => kind == AnimationStartKind.Load ? AnimationStartKind.Opening : kind;
    internal static readonly AnimationStartKind[] Groups = [AnimationStartKind.Opening, AnimationStartKind.Startup, AnimationStartKind.Damage, AnimationStartKind.Called, AnimationStartKind.Game, AnimationStartKind.None];
    internal static string Label(AnimationStartKind kind) => kind switch
    {
        AnimationStartKind.Opening or AnimationStartKind.Load => "Mission start", AnimationStartKind.Startup => "On startup", AnimationStartKind.Damage => "On damage",
        AnimationStartKind.Called => "Called", AnimationStartKind.Game => "Started by the game", _ => "No caller found",
    };
    /// <summary>MCP's name of a kind (start.kind) and of a group (definitionGroup start).</summary>
    internal static string Key(AnimationStartKind kind) => kind switch
    {
        AnimationStartKind.Opening => "opening", AnimationStartKind.Load => "load", AnimationStartKind.Startup => "startup", AnimationStartKind.Damage => "damage",
        AnimationStartKind.Called => "called", AnimationStartKind.Game => "game", _ => "none",
    };
    /// <summary>The rule of a kind, for its group row's tooltip and the legend.</summary>
    internal static string Rule(AnimationStartKind kind) => kind switch
    {
        AnimationStartKind.Opening or AnimationStartKind.Load => "Listed in the mission's startanims.zrd: NEW_GAME_START (the opening) or LOAD_GAME_START (a saved game's load).",
        AnimationStartKind.Startup => "ACTIVATION ( ON_STARTUP ): it starts when the mission loads.",
        AnimationStartKind.Damage => "Hit activation: WEAPON_HIT (the default), COLLIDE_HIT or WEAPON_OR_COLLIDE_HIT, after its HEALTH in damage.",
        AnimationStartKind.Called => "ON_CALL, and a call of this map's animations reaches it.",
        AnimationStartKind.Game => "ON_CALL, nothing calls it, and the game starts it on this map's units: pickup collection, vehicle startup, destruction and mode changes, turret destruction, trails, weapon flyouts.",
        _ => "ON_CALL with none of the above. The game may still start it by name at run time (effects, weapon impacts).",
    };
    /// <summary>The words a search for a kind matches ("damage", "startup", "no caller").</summary>
    internal static string Words(AnimationStartKind kind) => kind switch
    {
        AnimationStartKind.Opening => "mission start opening", AnimationStartKind.Load => "mission start load", AnimationStartKind.Startup => "on startup",
        AnimationStartKind.Damage => "on damage hit", AnimationStartKind.Called => "called", AnimationStartKind.Game => "started by the game", _ => "no caller found",
    };
    /// <summary>The kind's glyph (PreviewIcon kind).</summary>
    internal static string Glyph(AnimationStartKind kind) => kind switch
    {
        AnimationStartKind.Opening or AnimationStartKind.Load => "StartOpening", AnimationStartKind.Startup => "StartStartup", AnimationStartKind.Damage => "StartDamage",
        AnimationStartKind.Called => "StartCalled", AnimationStartKind.Game => "StartGame", _ => "StartNone",
    };
    /// <summary>The kind's colour (Animation mode's palette, so the tree and the mode agree); a call whose callers are unknown is tertiary.</summary>
    internal static System.Windows.Media.Brush Brush(AnimationStartKind kind, bool known = true)
    {
        var palette = AnimationGraphPalette.For(!OutlinerKinds.LightTheme);
        if (!known && kind == AnimationStartKind.Called) return palette.T3;
        return kind switch
        {
            AnimationStartKind.Opening or AnimationStartKind.Load => palette.Camera, AnimationStartKind.Startup => palette.Release, AnimationStartKind.Damage => palette.Stop,
            AnimationStartKind.Called => palette.Call, AnimationStartKind.Game => palette.State, _ => palette.T4,
        };
    }

    // ---------------------------------------------------------------- building

    /// <summary>A use the game makes of a unit (not a call or a target of another animation): pickups, vehicles, turrets, weapon flyouts.</summary>
    private static string? GameNoun(string reason) =>
        reason.StartsWith("Pickup collection", StringComparison.Ordinal) ? "pickup" : reason.StartsWith("Vehicle", StringComparison.Ordinal) ? "vehicle"
        : reason.StartsWith("Turret", StringComparison.Ordinal) ? "turret" : reason.StartsWith("Weapon flyout", StringComparison.Ordinal) ? "weapon flyout" : null;
    /// <summary>When the game starts it, in words, by the use's reason.</summary>
    private static string? GamePhrase(string reason)
    {
        (string Prefix, string Phrase)[] phrases =
        [
            ("Pickup collection", "when a pickup is collected"), ("Vehicle transition", "on the tank's mode changes"), ("Vehicle startup", "when a vehicle starts"),
            ("Vehicle destruction", "when a vehicle is destroyed"), ("Vehicle shatter", "when a unit shatters"), ("Vehicle burning", "when a unit burns"),
            ("Vehicle submerged", "when a unit sinks"), ("Vehicle exhaust", "exhaust trails"), ("Vehicle amphibious", "amphibious wakes"),
            ("Vehicle submarine", "submarine bubbles"), ("Turret firing", "when a turret fires"), ("Turret burning", "when a unit burns"),
            ("Turret destruction", "when a turret is destroyed"), ("Weapon flyout", "weapon flyout model"),
        ];
        foreach (var (prefix, phrase) in phrases) if (reason.StartsWith(prefix, StringComparison.Ordinal)) return phrase;
        return null;
    }

    internal static AnimationTreeSummary Create(Inputs inputs, CancellationToken token = default)
    {
        var definitions = inputs.Definitions.Definitions;
        var package = inputs.Package;
        bool known = inputs.Definitions.BindingKnown && package != null;
        var index = !known ? null : inputs.Index?.Package == package ? inputs.Index
            : AnimationGraphIndex.Create(package!, inputs.Provenance, AnimationGraphOptions.For(inputs.Definitions, inputs.NewGameStart, inputs.Map), token);
        Dictionary<int, int> entryDefinition = [];
        foreach (var definition in definitions)
            foreach (int entry in definition.Entries) entryDefinition.TryAdd(entry, definition.Index);
        HashSet<int> loads = [];
        if (index != null) foreach (string name in inputs.LoadGameStart) if (index.First(name) is int first) loads.Add(first);
        // The game's uses on this map's units, by definition: the units (rows) and the reasons.
        Dictionary<int, (HashSet<int> Nodes, List<string> Reasons)> game = [];
        foreach (var (node, uses) in inputs.Uses)
        {
            token.ThrowIfCancellationRequested();
            foreach (var use in uses.Uses)
                if (use.Origin == "indirect" && GameNoun(use.Reason) != null && entryDefinition.TryGetValue(use.EntryIndex, out int owner))
                {
                    if (!game.TryGetValue(owner, out var found)) game[owner] = found = ([], []);
                    found.Nodes.Add(node); found.Reasons.Add(use.Reason);
                }
        }
        Dictionary<string, int> firstNamed = new(StringComparer.Ordinal);
        Dictionary<int, List<int>> byRoot = [];
        Dictionary<int, AnimationDefinitionSummary> rows = [];
        var byIndex = definitions.ToDictionary(d => d.Index);
        foreach (var definition in definitions)
        {
            token.ThrowIfCancellationRequested();
            int[] roots = inputs.DefinitionRoots.GetValueOrDefault(definition.Index) ?? [];
            foreach (int root in roots) { if (!byRoot.TryGetValue(root, out var list)) byRoot[root] = list = []; list.Add(definition.Index); }
            int[] entries = package == null ? [] : [.. definition.Entries.Where(e => e >= 0 && e < package.Entries.Count)];
            game.TryGetValue(definition.Index, out var played);
            var start = Start(definition, entries, played.Reasons);
            var target = Target(definition, entries, roots, start, played.Nodes);
            // The player tank's own destruction reads as the tank's.
            if (start.Kind == AnimationStartKind.Game && target?.Kind == "playerTank")
                start = start with { Detail = start.Detail.Replace("when a vehicle is destroyed", "when the tank is destroyed", StringComparison.Ordinal),
                    WideDetail = start.WideDetail.Replace("when a vehicle is destroyed", "when the tank is destroyed", StringComparison.Ordinal) };
            int? duplicate = firstNamed.TryGetValue(definition.Name, out int earlier) ? earlier : null;
            if (duplicate == null) firstNamed[definition.Name] = definition.Index;
            List<(string, string)> fields = [("name", definition.Name), ("rootName", definition.RootName)];
            if (target != null) fields.Add(("target", target.Text));
            fields.Add(("file", System.IO.Path.GetFileName(definition.File)));
            foreach (int entry in entries.Take(256)) fields.Add(("binding", package!.Entries[entry].Name + " " + package.Entries[entry].RootName));
            fields.Add(("start", Words(start.Kind)));
            rows[definition.Index] = new(definition.Index, start, target, duplicate, known && roots.Any(inputs.IsTreeRow), string.Join(" ", fields.Select(f => f.Item2)))
            { SearchFields = fields };
        }
        return new(rows, byRoot.ToDictionary(p => p.Key, p => p.Value.ToArray()), index, inputs.Map);

        AnimationStart Start(SourceAnimationDefinition definition, int[] entries, List<string>? reasons)
        {
            // As compiled (the entry's activation byte and HEALTH), else as written.
            string activation; double? health;
            if (entries.Length > 0 && package!.Entries[entries[0]] is { Bytes.Length: >= 176 } compiled)
            {
                activation = compiled.Bytes[153] switch { 1 => "COLLIDE_HIT", 2 => "WEAPON_OR_COLLIDE_HIT", 3 => "ON_CALL", 4 => "ON_STARTUP", _ => "WEAPON_HIT" };
                health = compiled.F32(172);
            }
            else
            {
                activation = definition.Activation is "ON_CALL" or "ON_STARTUP" or "COLLIDE_HIT" or "WEAPON_OR_COLLIDE_HIT" ? definition.Activation : "WEAPON_HIT";
                health = double.TryParse(definition.Health, NumberStyles.Float, CultureInfo.InvariantCulture, out double written) ? written : null;
            }
            List<AnimationCallerGroup> groups = [];
            int callers = 0;
            if (index != null)
            {
                HashSet<int> calling = [];
                foreach (int entry in entries)
                    for (int? offset = 0; offset is int at;)
                    {
                        var page = index.Callers(entry, at, AnimationGraph.MaximumPageSize);
                        foreach (var caller in page.Rows)
                            if (caller.Relation is AnimationGraphRelation.Call or AnimationGraphRelation.CallWait or AnimationGraphRelation.LocalChild) calling.Add(caller.Caller);
                        offset = page.NextOffset;
                    }
                callers = calling.Count;
                foreach (int caller in calling.Order())
                {
                    int owner = entryDefinition.GetValueOrDefault(caller, -1);
                    int at = groups.FindIndex(g => g.Definition == owner && (owner >= 0 || g.Name == package!.Entries[caller].Name));
                    if (at >= 0) groups[at] = groups[at] with { Bindings = groups[at].Bindings + 1 };
                    else groups.Add(new(owner, owner >= 0 && byIndex.TryGetValue(owner, out var named) ? named.Name : package!.Entries[caller].Name, 1));
                }
            }
            var kind = index != null && entries.Any(index.StartsNewGame) ? AnimationStartKind.Opening
                : entries.Any(loads.Contains) ? AnimationStartKind.Load
                : activation == "ON_STARTUP" ? AnimationStartKind.Startup
                : activation != "ON_CALL" ? AnimationStartKind.Damage
                : callers > 0 || !known ? AnimationStartKind.Called
                : reasons is { Count: > 0 } ? AnimationStartKind.Game : AnimationStartKind.None;
            string detail, wide;
            switch (kind)
            {
                case AnimationStartKind.Opening: detail = "Opening · new game start"; wide = "opening"; break;
                case AnimationStartKind.Load: detail = "Saved-game load (LOAD_GAME_START)"; wide = "saved-game load"; break;
                case AnimationStartKind.Startup:
                    string range = definition.Execution is { } execution && execution.StartsWith("By range · ", StringComparison.Ordinal) ? " · runs while the player is within " + execution[11..] : "";
                    detail = "On startup" + range; wide = "on startup" + range; break;
                case AnimationStartKind.Damage:
                    string what = activation switch { "COLLIDE_HIT" => "collision", "WEAPON_OR_COLLIDE_HIT" => "weapon or collision", _ => "weapon" };
                    detail = health is > 0 ? $"After {Number(health.Value)} {what} damage" : $"On the first {(what == "collision" ? "collision" : "hit")}";
                    wide = char.ToLowerInvariant(detail[0]) + detail[1..]; break;
                case AnimationStartKind.Called when !known:
                    detail = "When called (callers unknown: the animations did not build)"; wide = "when called"; break;
                case AnimationStartKind.Called:
                    // Names are not unique: definitions sharing one read as one name with all their calling bindings.
                    var named = groups.GroupBy(g => g.Name, StringComparer.Ordinal).Select(g => (Name: g.Key, Bindings: g.Sum(x => x.Bindings))).ToArray();
                    string names = string.Join(", ", named.Take(2).Select(g => g.Bindings > 1 ? $"{g.Name} ({g.Bindings})" : g.Name)) + (named.Length > 2 ? $" and {named.Length - 2} more" : "");
                    detail = "Called by " + names; wide = "← " + names; break;
                case AnimationStartKind.Game:
                    var phrases = reasons!.Select(GamePhrase).OfType<string>().Distinct().Take(2).ToArray();
                    wide = phrases.Length == 0 ? "started by the game" : string.Join(", ", phrases);
                    detail = "Started by the game" + (phrases.Length == 0 ? "" : " · " + wide); break;
                default: detail = $"When called · nothing in {inputs.Map} calls it"; wide = $"nothing in {inputs.Map} calls it"; break;
            }
            return new(kind, detail, wide, activation, health, callers, groups, known);
        }

        AnimationTarget? Target(SourceAnimationDefinition definition, int[] entries, int[] roots, AnimationStart start, HashSet<int>? units)
        {
            if (!known) return null;
            if (roots.Length == 0) return new("none", "not in this map", 0, false);
            if (start.Kind == AnimationStartKind.Game && units is { Count: > 0 })
            {
                if (inputs.PlayerTank is int tank && units.All(u => u == tank)) return new("playerTank", "player tank", 1, false);
                var nouns = game[definition.Index].Reasons.Select(GameNoun).OfType<string>().Distinct().ToArray();
                if (nouns is ["weapon flyout"]) return new("units", "weapon flyout", units.Count, false);
                nouns = [.. nouns.Where(n => n != "weapon flyout")];
                string noun = nouns.Length == 1 ? nouns[0] : "unit";
                return new("units", $"{units.Count.ToString("N0", CultureInfo.CurrentCulture)} {noun}{(units.Count == 1 ? "" : "s")}", units.Count, false);
            }
            if (inputs.PlayerTank is int player && roots.All(r => r == player)) return new("playerTank", "player tank", 1, false);
            // Bound only to nodes no World tree row lists: effect, enemy and pickup templates.
            if (!roots.Any(inputs.IsTreeRow))
            {
                string name = inputs.DisplayName(roots[0]);
                return name == definition.Name ? new("template", "template", roots.Length, true) : new("template", name, roots.Length, false);
            }
            if (entries.Length > 1) return new("objects", Run([.. entries.Select(e => package!.Entries[e].RootName)]), entries.Length, false);
            string bound = entries.Length == 1 ? package!.Entries[entries[0]].RootName : definition.RootName;
            return new("object", bound == definition.Name ? "" : bound, 1, false);
        }
    }

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// Several roots as a run: a common prefix and the first–last number when the numbers are contiguous ("rdrail01–08"),
    /// "brl_01 … 15" when they are not, the name when all are the same, else the first two and "…".
    /// </summary>
    internal static string Run(IReadOnlyList<string> roots)
    {
        if (roots.Count == 0) return "";
        if (roots.Count == 1 || roots.All(r => r == roots[0])) return roots[0];
        var parts = roots.Select(r => System.Text.RegularExpressions.Regex.Match(r, @"^(.*?)(\d+)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)).ToArray();
        if (parts.All(p => p.Success) && parts.Select(p => p.Groups[1].Value).Distinct(StringComparer.Ordinal).Count() == 1
            && parts.All(p => p.Groups[2].Value.Length <= 9))
        {
            var numbers = parts.Select(p => (Text: p.Groups[2].Value, Value: int.Parse(p.Groups[2].Value, CultureInfo.InvariantCulture))).ToArray();
            var lowest = numbers.MinBy(n => n.Value); var highest = numbers.MaxBy(n => n.Value);
            bool contiguous = numbers.Select(n => n.Value).Distinct().Count() == numbers.Length && highest.Value - lowest.Value == numbers.Length - 1;
            return parts[0].Groups[1].Value + lowest.Text + (contiguous ? "–" : " … ") + highest.Text;
        }
        return roots[0] + ", " + roots[1] + (roots.Count > 2 ? " …" : "");
    }
}
