using System.Globalization;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Core.Animation;

public sealed partial class AnimationPreviewContext
{
    private void BindMaterialCycles()
    {
        for (int i = 0; i < Scene.Materials.Count; i++)
        {
            if (Scene.Materials[i]["cycle"] is not JsonObject cycle || cycle["texture_indices"] is not JsonArray frames) continue;
            var names = frames.Select(v => (int)JsonData.Integer(v, -1)).Where(t => t >= 0 && t < Scene.Textures.Count).Select(t => Scene.Textures[t].Text("name")).ToArray();
            if (names.Length > 0) MaterialCycles[i] = new(names, cycle.Float("speed"), cycle["looping"]?.GetValue<bool>() == true, cycle.Float("current_index"));
        }
    }
    private void BindEffectCycles(CancellationToken token) => BindEffectCycles(token, LookupWorkBudget.MaximumUnits);

    // Initialization owns this memo: Scene and Effects are public mutable state, so no result survives this call.
    // The observer is an internal deterministic cancellation/work seam; ordinary loading does not supply one.
    internal long BindEffectCycles(CancellationToken token, long maximumWork, Action<long>? reserved = null)
    {
        // zEffect::InitFromPath (0x460070) edits the first display material, shared
        // by other models (including fire_bft), not just spawned effect instances.
        LookupWorkBudget work = new(maximumWork, token);
        Dictionary<int, int?> materials = [];
        Dictionary<int, int[]> children = [];
        HashSet<int> visited = [];
        Stack<int> pending = new();
        Dictionary<int, TextureCycle> bindings = [];
        try
        {
            Reserve(0);
            foreach (var effect in Effects.Values)
            {
                Reserve(1); // Cache hits still observe cancellation.
                if (!materials.TryGetValue(effect.RootNode, out int? material))
                {
                    Reserve(64); // Memo entry, including missing/invalid first-model results.
                    material = FindMaterial(effect.RootNode);
                    materials.Add(effect.RootNode, material);
                }
                if (material is int index && effect.Textures.Length > 0)
                {
                    Reserve(64);
                    bindings[index] = new(effect.Textures, effect.Speed, effect.Loop);
                }
            }
            // Refusal or cancellation leaves saved/interpreter cycles intact. LoadAsync likewise cannot return a
            // partially initialized context. Dictionary overwrite order above retains the last applicable effect.
            Reserve(bindings.Count);
        }
        catch (InvalidDataException ex) when (work.Exhausted)
        { throw new InvalidDataException("Animation effect material discovery exceeds its work limit. Simplify repeated effect hierarchies or split the scene.", ex); }
        foreach (var (material, cycle) in bindings) MaterialCycles[material] = cycle;
        return work.UsedUnits;

        void Reserve(long units)
        {
            work.Reserve(units);
            reserved?.Invoke(work.UsedUnits);
            token.ThrowIfCancellationRequested();
        }
        int? FindMaterial(int root)
        {
            // A successful early match can leave pending edges; HashSet.Clear clears retained capacity, even after
            // a later tiny search. Account for both before reusing the operation's traversal storage.
            Reserve(1L + visited.EnsureCapacity(0) + pending.Count);
            visited.Clear(); pending.Clear(); pending.Push(root);
            while (pending.TryPop(out int index))
            {
                Reserve(2);
                if (index < 0 || index >= Scene.Nodes.Count || !visited.Add(index)) continue;
                // An invalid nonnegative model or empty model still stops at this node, as the original lookup did.
                if (Scene.Nodes[index].ModelIndex is >= 0) return FirstMaterial(index);
                if (!children.TryGetValue(index, out int[]? next))
                {
                    Reserve(64);
                    List<int> collected = [];
                    // Charge raw partition memberships before deduplication as well as retained children.
                    foreach (int child in SceneBuilder.Children(Scene.Nodes[index], work))
                    { Reserve(2); collected.Add(child); }
                    Reserve(collected.Count);
                    children.Add(index, next = collected.ToArray());
                }
                for (int i = next.Length - 1; i >= 0; i--)
                { Reserve(1); pending.Push(next[i]); }
            }
            return null;
        }
    }
    private int? FirstMaterial(int node)
    {
        if (node < 0 || node >= Scene.Nodes.Count || Scene.Nodes[node].ModelIndex is not int model || model < 0 || model >= Scene.Models.Count) return null;
        return Scene.Models[model].Polygons.FirstOrDefault()?.MaterialIndex;
    }
    private async Task LoadScriptCyclesAsync(string[] files, AssetResolver resolver, CancellationToken token)
    {
        Dictionary<string, ScriptContent> scripts = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in files.Where(p => FormatRegistry.Probe(p).Family == FormatFamily.Scripts))
        {
            var doc = await resolver.OpenCachedAsync(file, token).ConfigureAwait(false);
            foreach (var asset in doc.Assets) if (asset.Content is ScriptContent script) scripts.TryAdd(asset.Name.Replace('/', '\\'), script);
        }
        string mission = Path.GetFileName(Path.GetDirectoryName(World.Path)!);
        string entry = $"support\\tex_fx{mission}.gw";
        if (!scripts.ContainsKey(entry)) entry = "support\\tex_fx.gw";
        ReadTextureScript(entry, scripts, token);
    }
    /// <summary>
    /// Interpret only texture setup commands, with local node lookup and bounded source includes. Commands match as the
    /// retail interpreter matches them: case-sensitive prefixes (<c>source</c>; <c>FindNode</c>, <c>CycleTextureSetOn</c> and
    /// the rest in DispatchCoreCommand 0x4c20a0), and <c>Quit</c> exactly. Scripts are followed as a build follows them,
    /// <see cref="Worlds.WorldAssembler.MaximumScriptDepth"/> levels deep and <see cref="Worlds.WorldAssembler.MaximumInstructions"/>
    /// instructions in all (a script sourced repeatedly runs each time); scripts beyond that stop the reading with a
    /// diagnostic, so the preview says that the game may set up more cycles than it shows.
    /// </summary>
    public void ReadTextureScript(string entry, IReadOnlyDictionary<string, ScriptContent> scripts, CancellationToken token = default)
        => ReadTextureScript(entry, scripts, token, LookupWorkBudget.MaximumUnits);

    internal void ReadTextureScript(string entry, IReadOnlyDictionary<string, ScriptContent> scripts, CancellationToken token, long maximumLookupWork,
        long maximumOperandCharacters = ScriptOperandBudget.MaximumCharacters)
    {
        LookupWorkBudget lookupWork = new(maximumLookupWork, token);
        ScriptOperandBudget operandWork = new(maximumCharacters: maximumOperandCharacters);
        HashSet<int> lookupVisited = []; Stack<int> lookupPending = new(); Dictionary<int, int[]> lookupChildren = [];
        HashSet<string> active = new(StringComparer.OrdinalIgnoreCase); int node = -1, material = -1, count = 0, instructions = 0;
        float speed = 15; bool loop = false; List<string> maps = []; string[]? completedMaps = null; string? stopped = null;
        // Once complete, maps cannot change until SetOn starts another cycle. Repeated settings must not copy the whole
        // list for every instruction (65,536 maps times a million settings is otherwise hundreds of GB of allocation).
        void Publish() { if (material >= 0 && maps.Count == count && count > 0) MaterialCycles[material] = new(completedMaps ??= maps.ToArray(), speed, loop); }
        void Read(string path, bool normalize = false)
        {
            if (stopped != null) return;
            // Includes execute repeatedly, including missing targets. Charge normalization and all three possible
            // path hashes (active Add/Remove and script lookup) before copying or processing the full identity.
            operandWork.Inspect(path, token, normalize ? 4 : 3);
            if (normalize) path = path.Replace('/', '\\');
            if (active.Count > Worlds.WorldAssembler.MaximumScriptDepth) { stopped = $"{JsonData.ShownText(path, 64)} is sourced more than {Worlds.WorldAssembler.MaximumScriptDepth} levels deep"; return; }
            if (!active.Add(path)) return;
            try
            {
                if (!scripts.TryGetValue(path, out var script)) return;
                foreach (var args in script.Instructions)
                {
                    if (stopped != null) return;
                    token.ThrowIfCancellationRequested();
                    if (++instructions > Worlds.WorldAssembler.MaximumInstructions) { stopped = $"the scripts run more than {Worlds.WorldAssembler.MaximumInstructions:N0} instructions"; return; }
                    // Covers numeric parsing, command/name checks, maps and ignored extra operands as well as includes.
                    // The same allowance follows every recursive read and is reserved before dispatch does any work.
                    operandWork.Inspect(args, token);
                    if (args.Length == 0) continue;
                    string command = args[0], arg = args.Length > 1 ? args[1] : "";
                    bool Is(string name) => command.StartsWith(name, StringComparison.Ordinal);
                    if (Worlds.ScriptConditions.IsQuit(command)) return;
                    if (Worlds.ScriptConditions.IsSource(command)) Read(arg, normalize: true);
                    // The scripts run as the mission loads: the world file's highest slot of the name.
                    else if (Is("FindNode"))
                    {
                        // Index construction is bounded before its LINQ collections; every query reserves its full
                        // authored hash input before dictionary lookup. Repeated huge operands are not free hits.
                        lookupWork.Reserve(1L + arg.Length + (loadedNames == null ? 64L * Math.Min(LoadedCount, Scene.Nodes.Count) : 0));
                        node = LoadedNamed(arg) is { Count: > 0 } named ? named[0] : -1;
                        token.ThrowIfCancellationRequested();
                    }
                    else if (Is("FindSubNode")) node = node >= 0 ? FindSubBelow(node, arg, LoadedCount, lookupWork, lookupVisited, lookupPending, lookupChildren) : -1;
                    else if (Is("CycleTextureSetLooping")) { loop = arg.Equals("on", StringComparison.OrdinalIgnoreCase) || arg.Equals("true", StringComparison.OrdinalIgnoreCase); Publish(); }
                    else if (Is("CycleTextureSetMap")) { if (maps.Count < count) maps.Add(arg); Publish(); }
                    else if (Is("CycleTextureSetOn"))
                    {
                        material = FirstMaterial(node) ?? -1; maps = []; completedMaps = null; speed = 15; loop = false;
                        count = int.TryParse(arg, out int n) && n is > 0 and <= 65536 ? n : 0;
                    }
                    else if (Is("CycleTextureSetSpeed")) { if (float.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) && float.IsFinite(value)) speed = value; Publish(); }
                }
            }
            finally { active.Remove(path); }
        }
        try { Read(entry); }
        catch (InvalidDataException) when (lookupWork.Exhausted) { stopped = "repeated node lookups exceed the preview work limit"; }
        catch (InvalidDataException) when (operandWork.Exhausted) { stopped = "repeated script operands exceed the preview text work limit"; }
        if (stopped != null) Diagnostics.Add($"Texture effect scripts: {stopped}; the preview shows only the texture cycles set up before that, and the game may set up others.");
    }
}
