using System.Text.Json.Nodes;
using zStudio.Core.Animation;

namespace zStudio.Core;

public static partial class MissionSceneLoader
{
    private sealed record AnimationUseSeed(int Root, AnimationEntry Entry, string Reason);

    /// <summary>
    /// Static recipient discovery. Shares runtime name binding; never runs events, chooses gameplay conditions,
    /// or treats a positional reference as the object being animated.
    /// </summary>
    private static IReadOnlyDictionary<int, MissionAnimationUses> CollectAnimationUses(
        AnimationPreviewContext context, GameScene loadedScene, AnimationBindingOperation work, IReadOnlyDictionary<int, int> initialRoots,
        IReadOnlyList<AnimationUseSeed> seeds, IReadOnlyList<MissionActor> actors, JsonNode? vehicles, JsonNode? weapons,
        MissionLayoutSelection layout, List<string> diagnostics, CancellationToken token)
    {
        Dictionary<int, MissionAnimationUses> result = [];
        Queue<(AnimationEntry Entry, int Root, AnimationBinding Binding)> pending = [];
        HashSet<(int Entry, int Root, AnimationBinding Binding)> scheduled = [];
        Dictionary<int, HashSet<int>> subtrees = [];
        Dictionary<string, int>? loadedNames = null;
        HashSet<string> reported = new(StringComparer.Ordinal);
        BoundedDiagnostics notes = new(diagnostics);
        // Initial roots are the binding phase's own results; they never depend on the allowance left after it.
        foreach (var entry in context.Package.Entries)
            if (initialRoots.TryGetValue(entry.Index, out int root)) Record(entry, root, "initialRoot", "Initial animation root");
        try
        {
            foreach (var entry in context.Package.Entries)
            {
                work.Reserve(1);
                if (initialRoots.TryGetValue(entry.Index, out int root)) Schedule(entry, root, context.Binding(entry, root, work));
            }
            foreach (var seed in seeds) Add(seed.Entry, seed.Root, "indirect", seed.Reason, AnimationBinding.Rebound);
            // An irregular vehicle, weapon or entry omits only its own associations, never the rest of discovery.
            Dictionary<string, JsonNode?> vehicleDefinitions = [];
            Guarded(() => layout.VehicleResource, () => vehicleDefinitions = Fields(vehicles));
            foreach (var actor in actors)
            {
                work.Reserve(64L + 8L * actor.Name.Length);
                Guarded(() => $"{layout.VehicleResource} '{JsonData.ShownText(VehicleTemplateName(actor.Name), 64)}'", () => Actor(actor));
            }
            List<(string Name, JsonNode? Definition)> ballistics = [];
            Guarded(() => "weapons.zrd", () =>
            {
                foreach (var (category, group) in Fields(weapons))
                    if (category == "BALLISTICS")
                        foreach (var (name, definition) in Fields(group)) { work.Reserve(16); ballistics.Add((name, definition)); }
            });
            foreach (var (name, definition) in ballistics)
                Guarded(() => $"weapons.zrd '{JsonData.ShownText(name, 64)}'", () => Weapon(definition));
            while (pending.TryDequeue(out var item))
            {
                work.Reserve(1);
                Guarded(() => $"#{item.Entry.Index} ({JsonData.ShownText(item.Entry.Name, 64)})", () => Recipients(item.Entry, item.Root, item.Binding));
            }

            void Actor(MissionActor actor)
            {
                if (actor.Pickup is { } pickup)
                {
                    Named($"pu{pickup.TypeIndex:000}", actor.Root, "Pickup collection");
                    return;
                }
                if (actor.PlacementSource == "Animation initialization") return;
                string template = VehicleTemplateName(actor.Name);
                if (!vehicleDefinitions.TryGetValue(template, out var definition)) return;
                var fields = Fields(definition);
                var common = Fields(fields.GetValueOrDefault("common_mode"));
                Named(FirstString(common.GetValueOrDefault("start_anims")), actor.Root, $"Vehicle startup, {layout.VehicleResource}", required: true);
                var death = Entry(actor.Name.Contains("net", StringComparison.Ordinal) ? "bft_00" : actor.Name) ?? Entry(template);
                if (death != null) Add(death, actor.Root, "indirect", "Vehicle destruction", AnimationBinding.Rebound);
                // These callbacks explicitly supply the actor root. regen_skin and shock_vehicle supply null instead.
                Named("shatter_vehicle", actor.Root, "Vehicle shatter destruction");
                Named("napalm_vehicle", actor.Root, "Vehicle burning destruction");
                Named("subt", actor.Root, "Vehicle submerged destruction");
                foreach (var (mode, modeData) in fields)
                {
                    if (mode == "common_mode") continue;
                    var modal = Fields(modeData);
                    foreach (string key in TransitionKeys)
                    {
                        if (modal.GetValueOrDefault(key)?["children"] is not JsonArray list) continue;
                        // PlayerLoadModalFxList retains at most two entries.
                        foreach (var item in list.Take(2))
                            if (item.Text("type") == "string") Named(item.Text("value"), actor.Root, $"Vehicle transition, {key}", required: true);
                    }
                    string master = FirstString(modal.GetValueOrDefault("mode"));
                    if (master == "track") Named("bft_exhaust_trail", actor.Root, "Vehicle exhaust");
                    if (master == "amphib") Named("boat_wake_trail", actor.Root, "Vehicle amphibious wake");
                    if (master == "sub") Named("bft_bubble1", actor.Root, "Vehicle submarine bubbles");
                }
                if (actor.Name.Contains("bft", StringComparison.Ordinal) || actor.Name.Contains("net", StringComparison.Ordinal))
                    Named("bft_exhaust_trail", actor.Root, "Vehicle exhaust");
            }
            // MODEL_ANIMATION binds the flyout's model copy. ANIMATION and ANIMATION_ATTACHED use a null
            // bound node and only position references. Describe the concrete model template, never the shooter.
            void Weapon(JsonNode? definition)
            {
                var fields = Fields(definition);
                var flyout = Fields(fields.GetValueOrDefault("FLYOUT"));
                if (flyout.ContainsKey("EFFECT")) return; // Retail EFFECT takes precedence over MODEL, even if empty.
                string name = FirstString(flyout.GetValueOrDefault("MODEL_ANIMATION"));
                string model = FirstString(flyout.GetValueOrDefault("MODEL"));
                if (name.Length == 0 || model.Length == 0) return;
                // Weapons are loaded before AIV/pickup instances. Later copies or no-copy renames cannot
                // steal this lookup. Like effect-template preparation, index the frozen original world.
                if (loadedNames == null)
                {
                    Dictionary<string, int> names = new(StringComparer.Ordinal);
                    foreach (var node in loadedScene.Nodes)
                    {
                        work.Reserve(64L + node.Name.Length);
                        if (node.Class != "none") names[node.Name] = node.Index;
                    }
                    loadedNames = names;
                }
                work.Reserve(1L + model.Length);
                if (loadedNames.TryGetValue(model, out int modelRoot)) Named(name, modelRoot, "Weapon flyout model template, weapons.zrd", required: true);
                else notes.Add($"Animation associations: weapon model '{JsonData.ShownText(model, 64)}' is unavailable.");
            }
            void Recipients(AnimationEntry entry, int root, AnimationBinding binding)
            {
                if (!subtrees.TryGetValue(root, out var descendants))
                {
                    var nodes = work.Descendants(root, context.Scene.Nodes.Count);
                    work.Reserve(32L * nodes.Count);
                    subtrees[root] = descendants = nodes.ToHashSet();
                }
                // The reasons leave out the root: one write reached from many roots is one association, not many.
                if ((entry.U32(148) & 0x40) != 0)
                    foreach (var tracked in entry.References[0])
                    {
                        work.Reserve(1);
                        int target = context.ResolveTrackedNode(entry, tracked.Text(0, 36), root, binding, work);
                        if (target >= 0 && !descendants.Contains(target))
                            Add(entry, target, "indirect", "Animation cleanup outside its bound root", null);
                    }
                foreach (var ev in entry.AllSequences.SelectMany(s => s.Events))
                {
                    work.Reserve(1);
                    if (ev.Spec == null || ev.Bytes.Length < ev.Spec.Size) continue;
                    // Only write operands. Sound/effect origins, connectors' endpoints and conditions read nodes.
                    int reference = ev.Type switch
                    {
                        6 => ev.I16(16), 7 or 9 => ev.I16(28), 8 => ev.I16(24),
                        10 or 11 or 20 or 21 => ev.I32(16), 12 or 14 => ev.I32(12),
                        13 => ev.I16(20), 15 or 16 => ev.I16(14), 17 or 18 => ev.I16(16),
                        _ => 0
                    };
                    int target = Resolve(reference);
                    if (target >= 0 && !descendants.Contains(target))
                        Add(entry, target, "indirect", "Animation target outside its bound root", null);
                    if (ev.Type is not (19 or 24)) continue;
                    var child = work.EntryLookup().ResolveChild(ev, token);
                    if (child == null) { notes.Add($"Animation associations: unresolved child of #{entry.Index} ({entry.Name})."); continue; }
                    // CALL_ANIMATION's OPERAND_NODE is the bound recipient; AT_NODE/WITH_NODE position it.
                    int operand = ev.Type == 24 && ev.I16(44) > 0 ? Resolve(ev.I16(44)) : -1;
                    if (operand >= 0) Add(child, operand, "indirect", $"Called by {entry.Name}", AnimationBinding.Rebound);
                    // Default-root calls are already seeded above, with their genuine initial binding.
                }
                int Resolve(int reference) => reference == 0 || reference == -200 ? -1 :
                    context.ResolveInstanceNode(entry, reference, root, binding, work);
            }
        }
        catch (InvalidDataException ex)
        { notes.Add($"Animation association discovery is incomplete: {ex.Message}"); }
        catch (IOException) when (work.Exhausted)
        { notes.Add($"Animation association discovery is incomplete: it reached the scene's binding allowance, so some indirect associations are not shown."); }
        return new System.Collections.ObjectModel.ReadOnlyDictionary<int, MissionAnimationUses>(result);

        // The label is made only on failure, and each is reported once (a definition many actors share fails for each).
        void Guarded(Func<string> what, Action discover)
        {
            try { discover(); }
            catch (InvalidDataException ex) when (!work.Exhausted)
            {
                string label = what();
                if (reported.Add(label)) notes.Add($"Animation association discovery is incomplete for {label}: {ex.Message}");
            }
        }
        AnimationEntry? Entry(string name)
        {
            work.Reserve(1L + name.Length);
            return name.Length == 0 ? null : work.EntryLookup().Find(name, token);
        }
        void Named(string name, int root, string reason, bool required = false)
        {
            if (name.Length == 0) return;
            var entry = Entry(name);
            if (entry != null) Add(entry, root, "indirect", reason, AnimationBinding.Rebound);
            else if (required) notes.Add($"Animation associations: {reason}: '{JsonData.ShownText(name, 64)}' is unavailable.");
        }
        void Add(AnimationEntry entry, int root, string origin, string reason, AnimationBinding? binding)
        {
            work.Reserve(256);
            if (Record(entry, root, origin, reason) && binding is { } kind) Schedule(entry, root, kind);
        }
        bool Record(AnimationEntry entry, int root, string origin, string reason)
        {
            if (root < 0 || root >= context.Scene.Nodes.Count || entry.Index == 0 || entry.Name.Length == 0) return false;
            var use = new MissionAnimationUse(entry.Index, entry.Name, origin, reason);
            var addition = new MissionAnimationUses([use], origin == "initialRoot", false, origin == "indirect");
            result[root] = result.GetValueOrDefault(root, MissionAnimationUses.Empty).Merge(addition);
            return true;
        }
        void Schedule(AnimationEntry entry, int root, AnimationBinding binding)
        {
            if (root >= 0 && root < context.Scene.Nodes.Count && entry.Index != 0 && entry.Name.Length != 0 && scheduled.Add((entry.Index, root, binding)))
                pending.Enqueue((entry, root, binding));
        }
        Dictionary<string, JsonNode?> Fields(JsonNode? node)
        {
            Dictionary<string, JsonNode?> fields = new(StringComparer.Ordinal);
            foreach (var (name, value) in Records(node))
            { work.Reserve(64L + name.Length); fields.TryAdd(name, value); }
            return fields;
        }
    }

    private static readonly string[] TransitionKeys =
        ["t2a_anims", "a2t_anims", "t2h_anims", "h2t_anims", "a2h_anims", "h2a_anims", "a2s_anims", "s2a_anims"];
}
