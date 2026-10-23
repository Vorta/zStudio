using zStudio.Core.Sources;

namespace zStudio.Core.Animation;

public sealed partial class AnimationPreviewContext
{
    public static AnimationPreviewContext FromSources(SourceSceneSnapshot source, MissionDifficulty difficulty, CancellationToken token = default)
    {
        var package = source.Animations ?? throw new InvalidDataException("This mission has no animation definitions.");
        var context = new AnimationPreviewContext { World = source, Package = Freeze(package), Mission = source.MissionScene(difficulty, token) };
        context.Diagnostics.AddRange(context.Mission.Diagnostics);
        if (source.Resources.TryGetValue("effects.zrd", out var effects) && effects.Root != null) context.ReadEffects(effects.Root, token);
        PreviewResourceBudget sounds = new();
        if (source.Resources.TryGetValue("sounds.zrd", out var definitions) && definitions.Root != null)
            foreach (var alias in ReadSoundAliases(definitions.Root, token))
            {
                token.ThrowIfCancellationRequested();
                if (context.Sounds.ContainsKey(alias.Name) || !source.Sounds.TryGetValue(alias.File, out var bytes)) continue;
                sounds.Member(bytes.Length, token);
                context.Sounds.Add(alias.Name, new(alias.Name, alias.File, alias.Loop, bytes));
            }
        foreach (var (name, bytes) in source.Sounds)
        {
            token.ThrowIfCancellationRequested();
            string key = Path.GetFileNameWithoutExtension(name);
            if (context.Sounds.ContainsKey(key)) continue;
            sounds.Member(bytes.Length, token); context.Sounds.Add(key, new(name, name, false, bytes));
        }
        context.BindMaterialCycles();
        string script = $"support\\tex_fx{source.Mission}.gw";
        if (!source.TextureScripts.ContainsKey(script)) script = "support\\tex_fx.gw";
        context.ReadTextureScript(script, source.TextureScripts, token);
        context.BindEffectCycles(token);
        context.Terrain = new AnimationTerrain(context.Scene, token);
        return context;
    }
}
