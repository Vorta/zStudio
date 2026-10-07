using System.Globalization;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Formats;

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
    private void BindEffectCycles()
    {
        // zEffect::InitFromPath (0x460070) edits the first display material, shared
        // by other models (including fire_bft), not just spawned effect instances.
        foreach (var effect in Effects.Values)
        {
            int node = Descendants(effect.RootNode).FirstOrDefault(n => Scene.Nodes[n].ModelIndex is >= 0, -1);
            if (FirstMaterial(node) is int material && effect.Textures.Length > 0)
                MaterialCycles[material] = new(effect.Textures, effect.Speed, effect.Loop);
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
    {
        HashSet<string> active = new(StringComparer.OrdinalIgnoreCase); int node = -1, material = -1, count = 0, instructions = 0;
        float speed = 15; bool loop = false; List<string> maps = []; string[]? completedMaps = null; string? stopped = null;
        // Once complete, maps cannot change until SetOn starts another cycle. Repeated settings must not copy the whole
        // list for every instruction (65,536 maps times a million settings is otherwise hundreds of GB of allocation).
        void Publish() { if (material >= 0 && maps.Count == count && count > 0) MaterialCycles[material] = new(completedMaps ??= maps.ToArray(), speed, loop); }
        void Read(string path)
        {
            if (stopped != null) return;
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
                    if (args.Length == 0) continue;
                    string command = args[0], arg = args.Length > 1 ? args[1] : "";
                    bool Is(string name) => command.StartsWith(name, StringComparison.Ordinal);
                    if (Worlds.ScriptConditions.IsQuit(command)) return;
                    if (Worlds.ScriptConditions.IsSource(command)) Read(arg.Replace('/', '\\'));
                    // The scripts run as the mission loads: the world file's highest slot of the name.
                    else if (Is("FindNode")) node = LoadedNamed(arg) is { Count: > 0 } named ? named[0] : -1;
                    else if (Is("FindSubNode")) node = node >= 0 ? FindSubBelow(node, arg, LoadedCount) : -1;
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
        Read(entry);
        if (stopped != null) Diagnostics.Add($"Texture effect scripts: {stopped}, more than a build follows; the preview shows only the texture cycles set up before that, and the game may set up others.");
    }
}
