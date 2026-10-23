using System.Numerics;
using System.Text.Json.Nodes;
using zStudio.Core.Animation;

namespace zStudio.Core;

public static partial class MissionSceneLoader
{
    /// <summary>
    /// The local player's tank after the starting frontier, as the game draws it once settled (docs/agents/preview.md, player
    /// tank): the requested master type as the end of the vehicle resource's mode changes from track, run on this tank only
    /// (<see cref="AnimationPlayer.RunVehicleModeChange"/>), then one primary pair and one secondary weapon under its gun
    /// (<c>LoadWeaponBanksAndSelectDefaults</c> 0x438ba0) with the turret doors collapsed (<c>TickAltGunRuntimeState</c>
    /// 0x439ba0). Only flags and poses of the tank's existing nodes change, so identities and indexes stand.
    /// </summary>
    /// <param name="requested">The choice to draw; null draws the game's start of the mission (<see cref="MissionPlayerTank.Defaults"/>).</param>
    /// <param name="mission">The mission number, which the weapons' thresholds compare with; null when the world names none.</param>
    private static MissionPlayerTank PreparePlayerTank(GameScene scene, MissionActor player, PlayerTankState? requested, AnimationPreviewContext? context,
        AnimationBindingOperation? bindings, JsonNode? vehicles, JsonNode? weapons, MissionLayoutSelection selection, int? mission, HashSet<int> positioned,
        MissionInitializationTransforms dependencies, List<string> notes, MissionPlacementBudget budget, CancellationToken token)
    {
        if (requested is { IsValid: false })
        {
            notes.Add("Player tank: the requested type or weapons are unknown; the preview draws the mission's start.");
            requested = null;
        }
        int root = player.Root;
        string template = VehicleTemplateName(player.Name);
        // The vehicle resource's definition of the tank's template: its common data and its mode blocks in file order.
        List<(string Name, Dictionary<string, JsonNode?> Fields)> modes = [];
        Dictionary<string, JsonNode?> common = new(StringComparer.Ordinal);
        try
        {
            JsonNode? definition = null;
            foreach (var (name, value) in Records(vehicles)) { budget.Name(name); if (name == template) { definition = value; break; } }
            foreach (var (name, value) in Records(definition))
            {
                budget.Name(name);
                if (name == "common_mode") { if (common.Count == 0) common = Fields(value); continue; }
                var fields = Fields(value);
                if (fields.ContainsKey("mode")) modes.Add((name, fields));
            }
        }
        catch (InvalidDataException ex) when (!budget.Exhausted)
        { notes.Add($"Player tank: {selection.VehicleResource} '{JsonData.ShownText(template, 64)}' cannot be read: {ex.Message}"); }

        // weapons.zrd: each weapon's gun model (NAME, else its key) and whether sub mode forbids it (BEAM, CATCHES_FIRE).
        Dictionary<string, (string Model, bool Beam, bool CatchesFire)> catalog = new(StringComparer.Ordinal);
        try
        {
            foreach (var (category, group) in Records(weapons))
            {
                budget.Name(category);
                if (category != "BALLISTICS") continue;
                foreach (var (key, value) in Records(group))
                {
                    budget.Name(key);
                    var fields = Fields(value);
                    string model = fields.TryGetValue("NAME", out var named) && FirstString(named) is { Length: > 0 } text ? text : key;
                    bool catchesFire = fields.TryGetValue("CATCHES_FIRE", out var fire) && fire?["children"] is JsonArray { Count: > 0 } flag && (JsonData.Integer(flag[0]?["value"]) & 1) != 0;
                    catalog.TryAdd(key, (model, fields.ContainsKey("BEAM"), catchesFire));
                }
                break;
            }
        }
        catch (InvalidDataException ex) when (!budget.Exhausted) { notes.Add($"Player tank: weapons.zrd cannot be read: {ex.Message}"); }

        // The weapon list in file order (key, mission threshold, dual mount), as LoadMasterCommonDataFromNode (0x422170) reads it.
        List<(string Key, long Threshold, bool Dual)> specs = [];
        if (common.GetValueOrDefault("weapons")?["children"] is JsonArray list)
            foreach (var item in list)
            {
                budget.Take();
                if (item?["children"] is not JsonArray spec || spec.Count == 0 || spec[0].Text("type") != "string") continue;
                string key = spec[0].Text("value"); budget.Name(key);
                specs.Add((key, spec.Count > 1 ? JsonData.Integer(spec[1]?["value"]) : 0, spec.Count > 2 && (JsonData.Integer(spec[2]?["value"]) & 1) != 0));
            }
        // CacheGunHardpointsAndDetachDisplays (0x4390d0) and the doors (InitStateFromNameAndMasterCommonData 0x420d10) are
        // found with FindSubNodeByName; the weapons under the gun with FindNodeRecursiveByName (docs/agents/engine-evidence.md, vehicles).
        int gun = FindSub(root, "gun"), doorLeft = FindSub(root, "doorleft"), doorRight = FindSub(root, "doorright");
        List<PlayerTankWeaponModel> models = [];
        Dictionary<PlayerTankWeapon, (string Key, long Threshold, bool Dual)> slots = [];
        foreach (var spec in specs)
            if (spec.Key.Length > 6 && PlayerTankWeapon.At(spec.Key[4] - '0', spec.Key[6] - '0') is { } slot) slots[slot] = spec;
        foreach (var weapon in PlayerTankWeapon.Catalog)
        {
            if (!slots.TryGetValue(weapon, out var spec))
            {
                models.Add(new(weapon, false, ModelName(weapon.Key), [], false, Allowed(weapon.Key)));
                continue;
            }
            string model = ModelName(spec.Key);
            string[] nodes = spec.Dual ? [model + "_L", model + "_R"] : [model];
            models.Add(new(weapon, true, model, nodes, gun >= 0 && nodes.Length > 0 && nodes.All(n => FindBelow(gun, n) >= 0), Allowed(spec.Key)));
        }
        bool Allowed(string key) => !catalog.TryGetValue(key, out var entry) || !entry.Beam && !entry.CatchesFire;
        // The gun model: the weapon's NAME, else its key (also for a key weapons.zrd lacks).
        string ModelName(string key) => catalog.TryGetValue(key, out var entry) ? entry.Model : key;

        // The game's start of the mission (LoadWeaponBanksAndSelectDefaults 0x438ba0): of the listed weapons the mission allows
        // (CheckMissionWeaponAvailability 0x43ca90: threshold not 0 and at most the mission), the first is the secondary and
        // the second the primary. A list whose picks are not a secondary and a primary falls back to its first of each.
        var allowed = specs.Where(s => s.Threshold != 0 && (mission is not int number || s.Threshold <= number))
            .Select(s => s.Key.Length > 6 ? PlayerTankWeapon.At(s.Key[4] - '0', s.Key[6] - '0') : null).ToArray();
        string startSecondary = allowed.ElementAtOrDefault(0) is { IsPrimary: false } first ? first.Id
            : allowed.FirstOrDefault(w => w is { IsPrimary: false })?.Id ?? PlayerTankState.Default.Secondary;
        string startPrimary = allowed.ElementAtOrDefault(1) is { IsPrimary: true } second ? second.Id
            : allowed.FirstOrDefault(w => w is { IsPrimary: true })?.Id ?? PlayerTankState.Default.Primary;
        PlayerTankState defaults = new(PlayerTankType.Tracked, startPrimary, startSecondary);
        requested ??= defaults;

        // Which types the map's data offers: the current (track) block's lists and the entries they start.
        var lookup = context == null ? null : bindings!.EntryLookup();
        var track = modes.Count > 0 && Mode(modes[0].Fields) == "tr" ? modes[0] : default;
        var amphib = modes.FirstOrDefault(m => Mode(m.Fields) == "am");
        List<PlayerTankTypeChoice> types = [new(PlayerTankType.Tracked, true, [], [], null)];
        Dictionary<PlayerTankType, IReadOnlyList<IReadOnlyList<AnimationEntry>>> chains = [];
        Offer(PlayerTankType.Amphibious, "am", (track, "t2a_anims"));
        Offer(PlayerTankType.Hover, "ho", (track, "t2h_anims"));
        Offer(PlayerTankType.Sub, "su", (track, "t2a_anims"), (amphib, "a2s_anims"));

        var type = requested.Type;
        bool withdrawn = false; string? failure = null;
        if (types.First(t => t.Type == type) is { Available: false })
        {
            withdrawn = type != PlayerTankType.Tracked;
            if (withdrawn) notes.Add($"Player tank: this map's data has no transition to {type.ToString().ToLowerInvariant()}; the preview shows it tracked.");
            type = PlayerTankType.Tracked;
        }
        if (type != PlayerTankType.Tracked)
        {
            failure = AnimationPlayer.RunVehicleModeChange(context!, chains[type], root, positioned, token);
            if (failure != null)
            {
                notes.Add($"Player tank: {type.ToString().ToLowerInvariant()} could not be shown: {failure} The preview shows it tracked.");
                type = PlayerTankType.Tracked;
            }
        }

        // In sub the game switches away from a secondary it forbids (TransitionToMasterTypeSub 0x42b2a0 calls
        // AutoSwitchToNextUsableAltWeapon 0x43c660); the preview owns every listed weapon with ammo.
        var secondary = PlayerTankWeapon.Find(requested.Secondary)!;
        bool substituted = false;
        if (type == PlayerTankType.Sub && !Model(secondary).AllowedInSub)
        {
            var next = Substitute(secondary);
            substituted = next != secondary; secondary = next;
        }
        var primary = PlayerTankWeapon.Find(requested.Primary)!;

        // Every listed weapon's model is switched off, then the primary pair and the secondary on.
        List<string> drawn = [];
        if (gun >= 0)
        {
            foreach (var spec in specs)
                foreach (string name in spec.Dual ? [ModelName(spec.Key) + "_L", ModelName(spec.Key) + "_R"] : new[] { ModelName(spec.Key) })
                    if (FindBelow(gun, name) is >= 0 and int node) SetActive(node, false);
            foreach (var weapon in new[] { primary, secondary })
                foreach (string name in Model(weapon).Nodes)
                    if (FindBelow(gun, name) is >= 0 and int node) { SetActive(node, true); drawn.Add(name); }
            // A raised secondary rests at its stored position at full scale, and the turret doors stay collapsed (scale X 0.001).
            if (Model(secondary) is { Listed: true, Nodes: [var single] } && FindBelow(gun, single) is >= 0 and int raised)
            {
                Settle(raised, Vector3.One);
                foreach (int door in new[] { doorLeft, doorRight })
                    if (door >= 0) Settle(door, new(0.001f, 1, 1));
            }
        }
        return new(player, template, requested, requested with { Type = type, Secondary = secondary.Id })
        {
            Defaults = defaults, Types = types, Weapons = models, Drawn = drawn, HasGun = gun >= 0, Failure = failure, TypeWithdrawn = withdrawn,
            SecondarySubstituted = substituted, HiddenAtStart = (scene.Nodes[root].Metadata.UInt("flags") & 4) == 0,
        };

        PlayerTankWeaponModel Model(PlayerTankWeapon weapon) => models.First(m => m.Weapon == weapon);
        // AutoSwitchToNextUsableAltWeapon: the other side of the bank, else the nearest lower bank, else the nearest higher one
        // with a usable weapon. Another bank is selected on its remembered side (HandleAltWeaponBankSelectInput 0x439260): the
        // preview takes the first, as the load sets it; that switch ignores beams and refuses CATCHES_FIRE in sub.
        PlayerTankWeapon Substitute(PlayerTankWeapon current)
        {
            bool Usable(int bank, int side) => PlayerTankWeapon.At(bank, side) is { } w && Model(w) is { Listed: true, AllowedInSub: true };
            if (Usable(current.Bank, 1 - current.Side)) return PlayerTankWeapon.At(current.Bank, 1 - current.Side)!;
            foreach (int bank in Enumerable.Range(2, current.Bank - 2).Reverse().Concat(Enumerable.Range(current.Bank + 1, Math.Max(0, 9 - current.Bank))))
            {
                if (!Usable(bank, 0) && !Usable(bank, 1)) continue;
                var first = PlayerTankWeapon.At(bank, 0)!;
                bool burning = slots.TryGetValue(first, out var spec) && catalog.TryGetValue(spec.Key, out var fire) && fire.CatchesFire;
                return Model(first).Listed && !burning ? first : current;
            }
            return current;
        }
        void Offer(PlayerTankType offered, string mode, params ((string Name, Dictionary<string, JsonNode?> Fields) Block, string Key)[] lists)
        {
            List<string> transitions = [], missing = []; string? problem = null;
            List<IReadOnlyList<AnimationEntry>> chain = [];
            if (context == null) problem = "the map has no animations";
            else if (track.Fields == null) problem = modes.Count == 0 ? $"{template} has no mode blocks" : $"{template} does not start in track mode";
            else if (!modes.Any(m => Mode(m.Fields) == mode)) problem = $"{template} has no {offered.ToString().ToLowerInvariant()} mode block";
            else
                foreach (var (block, key) in lists)
                {
                    if (block.Fields == null) { problem = $"{template} has no amphibious mode block"; break; }
                    // PlayerLoadModalFxList keeps at most two names; a name the animations lack starts nothing.
                    var names = block.Fields.GetValueOrDefault(key)?["children"] is JsonArray items
                        ? items.Where(i => i.Text("type") == "string").Take(2).Select(i => i.Text("value")).ToArray() : [];
                    if (names.Length == 0) { problem = $"{block.Name} has no {key}"; break; }
                    List<AnimationEntry> entries = [];
                    foreach (string name in names)
                    {
                        budget.Name(name); transitions.Add(name);
                        // ActivateRuntime refuses entries stored in states 2 and 4-6.
                        if (lookup!.Find(name, token) is { } entry && entry.Bytes[152] is not (2 or 4 or 5 or 6)) entries.Add(entry);
                        else missing.Add(name);
                    }
                    chain.Add(entries);
                }
            if (problem == null && missing.Count == 0) chains[offered] = chain;
            types.Add(new(offered, problem == null && missing.Count == 0, transitions, missing, problem));
        }
        static string Mode(Dictionary<string, JsonNode?> fields) => FirstString(fields.GetValueOrDefault("mode")) is { Length: >= 2 } text ? text[..2] : "";
        Dictionary<string, JsonNode?> Fields(JsonNode? value)
        {
            Dictionary<string, JsonNode?> fields = new(StringComparer.Ordinal);
            foreach (var (name, field) in Records(value)) { budget.Name(name); fields.TryAdd(name, field); }
            return fields;
        }
        void SetActive(int node, bool active)
        {
            var metadata = scene.Nodes[node].Metadata;
            metadata["flags"] = active ? metadata.UInt("flags") | 4u : metadata.UInt("flags") & ~4u;
        }
        // gwObject3DSetScale and gwObject3DSetPosition: the matrix is rebuilt from the stored rotation, this scale and the translation.
        void Settle(int node, Vector3 scale)
        {
            var current = scene.Nodes[node];
            if (current.Class != "object3d") return;
            var local = SceneBuilder.LocalTransform(current);
            var rotation = current.Data["rotate"] is { } euler ? new Vector3(euler.Float("x"), euler.Float("y"), euler.Float("z")) : Vector3.Zero;
            var pose = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(AnimationMath.FromEuler(rotation)) * Matrix4x4.CreateTranslation(local.Translation);
            if (Near(pose, local)) return;
            SetPose(scene, node, pose); dependencies.Writes.Add(node);
        }
        static bool Near(Matrix4x4 a, Matrix4x4 b)
        {
            for (int row = 0; row < 4; row++)
                for (int column = 0; column < 4; column++)
                    if (MathF.Abs(a[row, column] - b[row, column]) > 1e-5f) return false;
            return true;
        }
        // FindSubNodeByName: the node, then its children last to first, each before its own children.
        int FindSub(int start, string name) => Walk(start, name, lastFirst: true);
        // FindNodeRecursiveByName (0x447bc0): the node, then its children first to last, depth first.
        int FindBelow(int start, string name) => Walk(start, name, lastFirst: false);
        int Walk(int start, string name, bool lastFirst)
        {
            HashSet<int> seen = []; Stack<int> pending = new([start]);
            while (pending.TryPop(out int index))
            {
                budget.Take();
                if (index < 0 || index >= scene.Nodes.Count || !seen.Add(index)) continue;
                var node = scene.Nodes[index];
                budget.Take(1L + Math.Min(node.Name.Length, name.Length));
                if (node.Name == name) return index;
                var children = SceneBuilder.Children(node).ToArray();
                budget.Take(children.Length);
                if (lastFirst) foreach (int child in children) pending.Push(child);
                else for (int i = children.Length - 1; i >= 0; i--) pending.Push(children[i]);
            }
            return -1;
        }
    }
}
