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
        ReadTextureScript(entry, scripts);
    }
    /// <summary>Interpret only texture setup commands, with local node lookup and bounded source includes.</summary>
    public void ReadTextureScript(string entry, IReadOnlyDictionary<string, ScriptContent> scripts)
    {
        HashSet<string> active = new(StringComparer.OrdinalIgnoreCase); int node = -1, material = -1, count = 0;
        float speed = 15; bool loop = false; List<string> maps = [];
        void Publish() { if (material >= 0 && maps.Count == count && count > 0) MaterialCycles[material] = new(maps.ToArray(), speed, loop); }
        void Read(string path)
        {
            if (active.Count >= 32 || !active.Add(path)) return;
            try
            {
                if (!scripts.TryGetValue(path, out var script)) return;
                foreach (var args in script.Instructions)
                {
                    if (args.Length == 0) continue;
                    string arg = args.Length > 1 ? args[1] : "";
                    switch (args[0].ToLowerInvariant())
                    {
                        case "quit": return;
                        case "source": Read(arg.Replace('/', '\\')); break;
                        case "findnode": node = Scene.Nodes.FirstOrDefault(n => n.Name == arg)?.Index ?? -1; break;
                        case "findsubnode": node = node >= 0 ? FindBelow(node, arg) : -1; break;
                        case "cycletextureseton":
                            material = FirstMaterial(node) ?? -1; maps = []; speed = 15; loop = false;
                            count = int.TryParse(arg, out int n) && n is > 0 and <= 65536 ? n : 0; break;
                        case "cycletexturesetspeed": if (float.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) && float.IsFinite(value)) speed = value; Publish(); break;
                        case "cycletexturesetlooping": loop = arg.Equals("on", StringComparison.OrdinalIgnoreCase) || arg == "1"; Publish(); break;
                        case "cycletexturesetmap": if (maps.Count < count) maps.Add(arg); Publish(); break;
                    }
                }
            }
            finally { active.Remove(path); }
        }
        Read(entry);
    }
}
