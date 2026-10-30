using System.Globalization;
using System.Text.Json;
using zStudio.Core.Animation;
using zStudio.Core.Formats;
using zStudio.Core.Worlds;

namespace zStudio.Core.Sources;

/// <summary>Lossless, definition-scoped edits. Record addresses belong to one revision, never to a display name.</summary>
public static partial class SourceAnimationEdits
{
    public const int MaximumFields = 4096;
    public sealed record Field(string Id, string Key, string Label, string[] Values, string[] Kinds, string[] Choices,
        bool Present, bool Optional, string? ReadOnly);
    public sealed record Record(string Id, string Kind, string Name, IReadOnlyList<Field> Fields);
    public sealed record Definition(string File, int Ordinal, int Index, string Name, IReadOnlyList<Record> Records)
    {
        public IReadOnlyList<string> Missions { get; init; } = [];
    }
    public sealed record Change(string Field, IReadOnlyList<string>? Values);
    public sealed record Edit(string File, byte[] Content);

    private static readonly IReadOnlyDictionary<string, string> EventFields = AnimationSourceFields.EventFields;
    private static readonly string[] Settings = AnimationSourceFields.Settings;
    private static readonly IReadOnlyDictionary<string, string[]> Options = AnimationSourceFields.Options;

    public static Definition Read(IProjectFiles files, string root, int index, CancellationToken token = default)
    {
        var set = AnimationDefinitionSet.Load(files, root, token);
        if (index < 0 || index >= set.Definitions.Count) throw new InvalidDataException("The animation definition no longer exists.");
        var definition = set.Definitions[index];
        var bytes = files.Read(definition.File, token, ProjectReadLimits.Resource(SourceProject.MaximumSourceTextBytes));
        return Inspect(definition.File, definition.Ordinal, index, bytes, token).Description;
    }

    private sealed record Inspection(Definition Description, ZrdTextSyntax Syntax, Dictionary<string, (ZrdNode Parent, int KeyIndex, ZrdNode? Value)> Locations);
    public static Definition Read(SourceWorkspace workspace, string mission, int index, CancellationToken token = default)
    {
        var snapshot = new SourceBuilder.Snapshot(workspace.Root, workspace.Overlay(), inventoryToken: token);
        var model = Read(snapshot.Files(), SourceBuilder.AnimationRoot(mission), index, token);
        var result = model with { Missions = AffectedMissions(workspace.Root, snapshot.Files(), model.File, token) };
        snapshot.CheckUnchanged(token);
        return result;
    }

    private static string[] AffectedMissions(string root, IProjectFiles files, string file, CancellationToken token)
    {
        List<string> affected = [];
        int definitions = 0;
        foreach (string mission in SourceWorlds.Missions(root, token))
        {
            string entry = SourceBuilder.AnimationRoot(mission);
            if (!files.Exists(entry)) continue;
            var set = AnimationDefinitionSet.Load(files, entry, token);
            definitions += set.Definitions.Count;
            if (definitions > 262144) throw new InvalidDataException("Animation dependency discovery exceeds its definition allowance.");
            if (set.Files.Contains(file, StringComparer.OrdinalIgnoreCase)) affected.Add(mission);
        }
        return affected.ToArray();
    }

    /// <summary>Validate a shared source against every mission that includes it before accepting a write.</summary>
    public static void ValidateSharedEdit(SourceWorkspace workspace, Edit edit, CancellationToken token = default)
    {
        var overlay = new Dictionary<string, byte[]>(workspace.Overlay(), StringComparer.OrdinalIgnoreCase) { [edit.File] = edit.Content };
        var discovery = new SourceBuilder.Snapshot(workspace.Root, overlay, inventoryToken: token);
        string[] affected = AffectedMissions(workspace.Root, discovery.Files(), edit.File, token);
        foreach (string mission in affected)
        {
            token.ThrowIfCancellationRequested();
            var snapshot = new SourceBuilder.Snapshot(workspace.Root, overlay, inventoryToken: token);
            var world = snapshot.World(mission, token);
            var result = AnimationCompiler.Evaluate(snapshot.Files(), SourceBuilder.AnimationRoot(mission), world.World.Nodes.Select(n => n.Name).ToArray(), snapshot.Effects(mission, token), token: token);
            if (result.EngineRejection is { } rejection) throw new InvalidDataException($"{mission}: {rejection}");
            snapshot.CheckUnchanged(token);
        }
        discovery.CheckUnchanged(token);
    }
    private static Inspection Inspect(string file, int ordinal, int index, ReadOnlySpan<byte> bytes, CancellationToken token)
    {
        if (!ZrdText.LooksLikeText(bytes)) throw new InvalidDataException("Animation editing requires a text definition source.");
        var syntax = ZrdTextSyntax.Parse(bytes, token);
        ZrdNode root = syntax.Root;
        while (root.Children.Count == 1 && root.Children[0].Kind == ZrdKind.Array) root = root.Children[0];
        var definition = AnimationItem.Parse(root, file).Where(i => i.Key == "ANIMATION_DEFINITIONS")
            .SelectMany(i => i.All("ANIMATION_LIST")).SelectMany(i => i.All("ANIMATION_DEFINITION")).ElementAtOrDefault(ordinal)
            ?? throw new InvalidDataException("The source definition changed.");
        var body = definition.Values ?? throw new InvalidDataException("The definition has no body.");
        List<Record> records = []; Dictionary<string, (ZrdNode, int, ZrdNode?)> locations = [];
        int fieldCount = 0; long characters = 0;
        string Safe(string s) => s.Length <= 512 ? s : throw new InvalidDataException("An animation field exceeds the editor's text allowance.");
        List<Field> Fields(ZrdNode parent, string id, string allowed, bool settings = false, string? locked = null)
        {
            List<Field> fields = [];
            var keys = Pairs(parent).ToArray();
            foreach (var (key, at, value) in keys)
            {
                token.ThrowIfCancellationRequested();
                if (allowed == "ACTIVATION" && key is not ("NAME" or "ACTIVATION")) continue;
                if (key is "SEQUENCE_DEFINITION" or "RESET_STATE" or "ACTIVATION_PREREQUISITE" or "REQUIRED" or "OPTIONS" or "OBJECT_ACTIVE_LIST") continue;
                if (value?.Children.Any(c => c.Kind == ZrdKind.Array) == true)
                {
                    if (key == "BOUNCE_SOUND" && allowed.Split(' ').Contains(key))
                        fields.AddRange(Fields(value, id + "/" + at, "NAME FULL_VOLUME_VELOCITY", locked: locked).Select(f => f with { Label = "Bounce sound · " + f.Label }));
                    else fields.AddRange(Fields(value, id + "/" + at, "", locked: "This nested source field is not editable here.").Select(f => f with { Label = Caption(key) + " · " + f.Label }));
                    continue;
                }
                Add(key, at, value);
            }
            if (settings)
                foreach (string key in Settings.Where(k => !keys.Any(p => p.Key == k))) Add(key, -1, null);
            else if (allowed.Split(' ').Contains("START_TIME") && !keys.Any(p => p.Key == "START_TIME")) Add("START_TIME", -1, null);
            else if (allowed == "ACTIVATION" && !keys.Any(p => p.Key == "ACTIVATION")) Add("ACTIVATION", -1, null);
            foreach (string flag in new[] { "WAIT_FOR_COMPLETION", "RESET" })
                if (allowed.Split(' ').Contains(flag) && !keys.Any(p => p.Key == flag)) Add(flag, -1, null);
            return fields;

            void Add(string key, int at, ZrdNode? value)
            {
                if (++fieldCount > MaximumFields) throw new InvalidDataException("This definition exceeds the animation editor's field allowance.");
                string fieldId = id + "/" + (at < 0 ? "@" + key : at.ToString(CultureInfo.InvariantCulture));
                if (value?.Children.Count > 128) throw new InvalidDataException("An animation field exceeds 128 values.");
                characters += value?.Children.Sum(c => (long)c.Value.Length) ?? 0;
                if (characters > 131072) throw new InvalidDataException("This definition exceeds the animation editor's text allowance.");
                string[] values = value?.Children.Select(c => Safe(c.Kind == ZrdKind.String ? c.Text : c.Value)).ToArray() ?? [];
                string[] kinds = value?.Children.Select(c => c.Kind == ZrdKind.String ? "text" : key is "LOOP_COUNT" or "EXECUTION_PRIORITY" or "MINIMUM_TO_SATISFY" or "VALUE" or "FRAME" ? "integer" : "number").ToArray() ?? [];
                bool known = allowed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(key);
                string? reason = locked ?? (!known ? "This source field is not editable here." : keys.Count(k => k.Key == key) > 1 ? "Repeated field: edit the source to disambiguate it." : null);
                string[] choices = Options.GetValueOrDefault(key) ?? [];
                if (key == "ACTIVATION" && !settings) choices = ["ON_CALL"];
                if (key == "STATE" && allowed == "NAME STATE START_TIME" && values.Length == 1 && values[0] is "ACTIVE" or "INACTIVE") choices = ["ACTIVE", "INACTIVE"];
                if (at < 0)
                {
                    values = key switch { "START_TIME" => ["ANIMATION_OFFSET", "0"], "ACTIVATION" => [settings ? "WEAPON_HIT" : "ON_CALL"], "SAVE_LOG" or "NETWORK_LOG" => ["ON"], "EXECUTION_PRIORITY" => ["4"], "EXECUTION_BY_ZONE" or "WAIT_FOR_COMPLETION" or "RESET" => [], _ => ["0"] };
                    kinds = key switch { "START_TIME" => ["text", "number"], "ACTIVATION" or "SAVE_LOG" or "NETWORK_LOG" => ["text"], "EXECUTION_PRIORITY" => ["integer"], "EXECUTION_BY_ZONE" or "WAIT_FOR_COMPLETION" or "RESET" => [], _ => ["number"] };
                }
                locations.Add(fieldId, (parent, at, value));
                fields.Add(new(fieldId, Safe(key), Caption(key), values, kinds, choices, at >= 0, settings || key == "START_TIME" || allowed == "ACTIVATION" || key is "WAIT_FOR_COMPLETION" or "RESET", reason));
            }
        }
        records.Add(new("settings", "settings", "Activation", Fields(body, "settings", string.Join(' ', Settings), true)));
        int sequence = 0;
        foreach (var (key, at, value) in Pairs(body))
        {
            if (value == null) continue;
            if (key == "ACTIVATION_PREREQUISITE") { Prerequisites(value, "prerequisites/" + at); continue; }
            if (key is not ("SEQUENCE_DEFINITION" or "RESET_STATE")) continue;
            string id = "sequence/" + at;
            string name = key == "RESET_STATE" ? "Reset state" : new AnimationItem(key, value, file).All("NAME").LastOrDefault()?.Text() ?? $"Sequence {++sequence}";
            records.Add(new(id, "sequence", Safe(name), Fields(value, id, "ACTIVATION")));
            foreach (var (eventKey, eventAt, eventValue) in Pairs(value))
            {
                if (records.Count >= MaximumFields) throw new InvalidDataException("This definition exceeds the animation editor's record allowance.");
                if (eventKey is "NAME" or "ACTIVATION" or "START_TIME") continue;
                string eventId = id + "/event/" + eventAt;
                string? locked = EventFields.ContainsKey(eventKey) ? null : "The compiler does not support this event.";
                List<Field> fields = eventValue == null ? [] : Fields(eventValue, eventId, EventFields.GetValueOrDefault(eventKey, "") + " START_TIME", locked: locked);
                if (eventKey == "ANIM_VERBOSE" && eventValue is { Children.Count: 1 } && eventValue.Children[0].Kind == ZrdKind.String)
                {
                    string fieldId = eventId + "/value";
                    locations.Add(fieldId, (value, eventAt, eventValue));
                    fields = [new(fieldId, "VALUE", "Verbose logging", [eventValue.Children[0].Text], ["text"], ["ON", "OFF"], true, false, null)];
                }
                if (eventKey == "OBJECT_MOTION_SI_SCRIPT")
                    fields = fields.Select(f => f.Key == "START_TIME" ? f : f with { ReadOnly = "Keyframe tracks are read-only in this release." }).ToList();
                var timing = fields.FirstOrDefault(f => f.Key == "START_TIME");
                string clock = timing is { Present: true, Values.Length: >= 2 } ? timing.Values[0] switch { "SEQUENCE_OFFSET" => "Sequence", "EVENT_OFFSET" => "Previous event", _ => "Animation" } + " + " + timing.Values[1] + " s · " : "0 s · ";
                string target = fields.FirstOrDefault(f => f.Key == "NAME")?.Values.FirstOrDefault() ?? "";
                records.Add(new(eventId, Safe(eventKey), clock + Caption(eventKey) + (target.Length > 0 ? " · " + target : ""), fields));
            }
        }
        return new(new(file, ordinal, index, Safe(definition.TextOf("ANIMATION_NAME") ?? definition.TextOf("NAME") ?? "Animation"), records), syntax, locations);

        void Prerequisites(ZrdNode parent, string id, string group = "Activation prerequisites")
        {
            if (records.Count >= MaximumFields) throw new InvalidDataException("Too many prerequisite records.");
            records.Add(new(id, "prerequisites", group, Fields(parent, id, "MINIMUM_TO_SATISFY ANIMATION_LIST OBJECT_ACTIVE_LIST")));
            foreach (var (key, at, value) in Pairs(parent))
            {
                if (value != null && (key is "REQUIRED" or "OPTIONS")) Prerequisites(value, id + "/" + at, key == "REQUIRED" ? "Required conditions (all)" : "Optional conditions (minimum to satisfy)");
                else if (key == "OBJECT_ACTIVE_LIST" && value != null)
                {
                    List<Field> paths = [];
                    for (int n = 0; n < value.Children.Count; n++)
                    {
                        var path = value.Children[n];
                        if (path.Kind != ZrdKind.Array || path.Children.Any(c => c.Kind != ZrdKind.String)) continue;
                        if (++fieldCount > MaximumFields || path.Children.Count > 128) throw new InvalidDataException("Too many prerequisite path fields.");
                        string fieldId = id + "/" + at + "/path/" + n;
                        locations.Add(fieldId, (value, n, path));
                        paths.Add(new(fieldId, key, $"Active object path {n + 1}", path.Children.Select(c => Safe(c.Text)).ToArray(),
                            path.Children.Select(_ => "text").ToArray(), [], true, false, null));
                    }
                    records.Add(new(id + "/" + at, "prerequisites", group + " · active objects", paths));
                }
            }
        }
    }

    public static Edit Plan(IProjectFiles files, string animationRoot, int index, IReadOnlyList<Change> changes, CancellationToken token = default)
    {
        if (changes.Count is 0 or > 256) throw new InvalidDataException("Change between 1 and 256 animation fields at a time.");
        var set = AnimationDefinitionSet.Load(files, animationRoot, token);
        if (index < 0 || index >= set.Definitions.Count) throw new InvalidDataException("The animation definition changed.");
        var definition = set.Definitions[index];
        byte[] bytes = files.Read(definition.File, token, ProjectReadLimits.Resource(SourceProject.MaximumSourceTextBytes));
        var inspection = Inspect(definition.File, definition.Ordinal, index, bytes, token);
        var fields = inspection.Description.Records.SelectMany(r => r.Fields).ToDictionary(f => f.Id);
        Dictionary<Guid, ZrdNode> replacements = []; Dictionary<Guid, List<(int At, string Key, ZrdNode? Value)>> structural = [];
        HashSet<string> changed = [];
        foreach (var change in changes)
        {
            token.ThrowIfCancellationRequested();
            if (!changed.Add(change.Field) || !fields.TryGetValue(change.Field, out var field)) throw new InvalidDataException("An animation field is missing or changed twice.");
            if (field.ReadOnly != null) throw new InvalidDataException(field.ReadOnly);
            var (parent, at, stored) = inspection.Locations[field.Id];
            if (change.Values == null && !field.Optional) throw new InvalidDataException("This field cannot be removed.");
            // Names are trimmed (a name typed with spaces would never match the node or animation it names); numbers too.
            string[]? requested = change.Values == null ? null : Trimmed(field, change.Values);
            ZrdNode[]? values = requested == null ? null : Values(field, requested);
            if (values != null && stored != null)
            {
                if (stored.Children.Count != values.Length)
                {
                    // Preserve the syntax identities of unchanged list entries, including their attached comments.
                    int[,] matches = new int[stored.Children.Count + 1, values.Length + 1];
                    for (int i = stored.Children.Count - 1; i >= 0; i--)
                        for (int j = values.Length - 1; j >= 0; j--)
                            matches[i, j] = field.Values[i] == requested![j] ? 1 + matches[i + 1, j + 1] : Math.Max(matches[i + 1, j], matches[i, j + 1]);
                    int old = 0, next = 0;
                    while (old < stored.Children.Count && next < values.Length)
                    {
                        if (field.Values[old] == requested![next]) { values[next++] = stored.Children[old++]; }
                        else if (matches[old + 1, next] >= matches[old, next + 1]) old++; else next++;
                    }
                    replacements[stored.Id] = stored with { Children = values };
                }
                else for (int i = 0; i < values.Length; i++)
                    if (requested![i] != field.Values[i]) replacements[stored.Children[i].Id] = values[i] with { Id = stored.Children[i].Id };
            }
            else
            {
                if (!structural.TryGetValue(parent.Id, out var edits)) structural[parent.Id] = edits = [];
                edits.Add((at, field.Key, values == null ? null : (stored ?? ZrdNode.Create(ZrdKind.Array)) with { Children = values }));
            }
        }
        ZrdNode Apply(ZrdNode node)
        {
            if (replacements.TryGetValue(node.Id, out var scalar)) return scalar;
            if (node.Kind != ZrdKind.Array) return node;
            List<ZrdNode> children = node.Children.Select(Apply).ToList();
            if (structural.TryGetValue(node.Id, out var edits))
                foreach (var edit in edits.OrderByDescending(e => e.At))
                {
                    if (edit.At >= 0) { int count = edit.At + 1 < children.Count && children[edit.At + 1].Kind == ZrdKind.Array ? 2 : 1; children.RemoveRange(edit.At, count); }
                    if (edit.Value != null)
                    {
                        int at = edit.At < 0 ? children.Count : edit.At;
                        children.Insert(at, Text(edit.Key)); children.Insert(at + 1, edit.Value);
                    }
                }
            return node with { Children = children };
        }
        var (text, lossless) = inspection.Syntax.Rewrite(Apply(inspection.Syntax.Root), token);
        if (!lossless) throw new InvalidDataException("This change would rewrite unrelated source formatting; the source was left unchanged.");
        return new(definition.File, ZrdTextSyntax.Encode(text));
    }

    public static void ValidateChanges(Definition definition, IReadOnlyList<Change> changes)
    {
        if (changes.Count is 0 or > 256) throw new InvalidDataException("Change between 1 and 256 animation fields at a time.");
        var fields = definition.Records.SelectMany(r => r.Fields).ToDictionary(f => f.Id);
        HashSet<string> seen = [];
        long characters = 0;
        foreach (var change in changes)
        {
            characters += change.Values?.Sum(v => (long)v.Length) ?? 0;
            if (characters > 131072) throw new InvalidDataException("Animation changes exceed the text allowance.");
            if (!seen.Add(change.Field) || !fields.TryGetValue(change.Field, out var field)) throw new InvalidDataException("An animation field is missing or changed twice.");
            if (field.ReadOnly != null) throw new InvalidDataException(field.ReadOnly);
            if (change.Values == null) { if (!field.Optional) throw new InvalidDataException("This field cannot be removed."); }
            else _ = Values(field, Trimmed(field, change.Values));
        }
    }
    /// <summary>
    /// The requested values without surrounding white space; a name (a text value, every entry of a list) that is empty once
    /// trimmed is refused: the game would look for an animation or node of that name and never find it.
    /// </summary>
    public static string[] Trimmed(Field field, IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(field); ArgumentNullException.ThrowIfNull(values);
        bool list = field.Key is "ANIMATION_LIST" or "OBJECT_ACTIVE_LIST";
        var trimmed = values.Select(v => (v ?? throw new InvalidDataException($"{field.Label} has a missing value.")).Trim()).ToArray();
        for (int i = 0; i < trimmed.Length; i++)
            if (trimmed[i].Length == 0 && (list || i < field.Kinds.Length && field.Kinds[i] == "text"))
                throw new InvalidDataException($"{field.Label} needs a name: a blank one is not allowed.");
        return trimmed;
    }
    private static ZrdNode[] Values(Field field, IReadOnlyList<string> values)
    {
        bool list = field.Key is "ANIMATION_LIST" or "OBJECT_ACTIVE_LIST";
        if ((!list && values.Count != field.Kinds.Length) || values.Count > 128 || list && field.Key == "OBJECT_ACTIVE_LIST" && values.Count == 0 || values.Any(v => v.Length > 512)) throw new InvalidDataException($"{field.Label} needs {(list ? "1–128" : field.Kinds.Length.ToString(CultureInfo.InvariantCulture))} values, each at most 512 characters.");
        if (field.Choices.Length > 0 && (values.Count == 0 || !field.Choices.Contains(values[0]))) throw new InvalidDataException($"Choose a supported {field.Label.ToLowerInvariant()}.");
        return values.Select((value, i) => (list ? "text" : field.Kinds[i]) switch
        {
            "text" => Text(value),
            "integer" => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) ? ZrdNode.Create(ZrdKind.Int, number.ToString(CultureInfo.InvariantCulture)) : throw new InvalidDataException($"{field.Label} needs a whole number."),
            _ => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number) && float.IsFinite(number) ? ZrdNode.Create(ZrdKind.Float, number.ToString("R", CultureInfo.InvariantCulture)) : throw new InvalidDataException($"{field.Label} needs a finite number.")
        }).ToArray();
    }
    private static ZrdNode Text(string text) => ZrdNode.Create(ZrdKind.String, JsonSerializer.Serialize(text));
    public static string Caption(string key) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(key.Replace('_', ' ').ToLowerInvariant());
    private static IEnumerable<(string Key, int At, ZrdNode? Value)> Pairs(ZrdNode node)
    {
        for (int i = 0; i < node.Children.Count; i++)
            if (node.Children[i].Kind == ZrdKind.String)
            {
                int at = i; string key = node.Children[i].Text;
                ZrdNode? value = i + 1 < node.Children.Count && node.Children[i + 1].Kind == ZrdKind.Array ? node.Children[++i] : null;
                yield return (key, at, value);
            }
    }
}
