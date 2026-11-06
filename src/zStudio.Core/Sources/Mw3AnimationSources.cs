using System.Buffers.Binary;
using System.Text;
using zStudio.Core.Animation;
using zStudio.Core.Worlds;

namespace zStudio.Core.Sources;

/// <summary>Recovers named MW3 keyframe scripts from their native definitions and source stamps.</summary>
internal static partial class Mw3AnimationSources
{
    internal sealed record Mission(string Name, AnimationPackage Package, IReadOnlyCollection<string> WorldNodes);
    private sealed record Track(string Object, float Rate, IReadOnlyList<AnimationKeyframe> Frames, string Text);

    internal static IReadOnlyList<WorldSources.Output> Reconstruct(IReadOnlyList<Mission> missions, IProjectFiles files,
        List<string> notes, CancellationToken token, Action<string>? status = null)
    {
        Dictionary<string, Dictionary<string, Track>> scripts = new(StringComparer.OrdinalIgnoreCase);
        ReconstructionBudget budget = new(ReconstructionBudget.MaximumBytes);
        foreach (var mission in missions)
        {
            var set = AnimationDefinitionSet.Load(files, $"data/{mission.Name}/zrdr/anim.zad", GameVariant.MechWarrior3, token);
            var roots = new AnimationRoots(mission.WorldNodes, token, game: GameVariant.MechWarrior3);
            HashSet<string> nodes = new(mission.WorldNodes, StringComparer.Ordinal);
            var stamps = Stamps(mission.Package.Prefix, token);
            int index = 1;
            foreach (var definition in set.Definitions)
                foreach (var (root, digits) in roots.Resolve(definition.Item, notes.Add))
                {
                    token.ThrowIfCancellationRequested();
                    if (!nodes.Contains(root)) continue;
                    string name = AnimationCompiler.EntryName(definition.Item, root, digits, GameVariant.MechWarrior3);
                    if (index >= mission.Package.Entries.Count || mission.Package.Entries[index].Name != name || mission.Package.Entries[index].RootName != root)
                        throw new InvalidDataException($"{mission.Name}: the native definitions do not bind to compiled animation {index} ({name}); no substitute animation sources were written.");
                    var entry = mission.Package.Entries[index++];
                    foreach (var (item, ev) in AnimationSources.ScriptEvents(definition.Item, entry, token))
                    {
                        if (ev.I32(20) != 0) throw new InvalidDataException($"{mission.Name}: {entry.Name} uses cubic SI interpolation, whose source reconstruction is not implemented.");
                        string target = AnimationCompiler.BoundName(item.TextOf("NAME") ?? "", digits, GameVariant.MechWarrior3);
                        string file = AnimationCompiler.BoundName(item.TextOf("SCRIPT_FILENAME") ?? "", digits, GameVariant.MechWarrior3);
                        float rate = item.Item("SCRIPT_FRAME_RATE")?.Number() ?? 30;
                        string beside = $"{Path.GetDirectoryName(definition.File)!.Replace('\\', '/')}/{file}";
                        string[] candidates = stamps.Where(p => Path.GetFileName(p).Equals(file, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                        string path = candidates.Contains(beside, StringComparer.OrdinalIgnoreCase) ? beside : candidates.Length == 1 ? candidates[0]
                            : candidates.Length == 0 ? beside : throw new InvalidDataException($"{definition.File}: the source stamps give more than one path for {file}.");
                        SourceProject.RequireSource(path);
                        var sourceFrames = ev.Keyframes(token); budget.Retain(256L * sourceFrames.Count);
                        // The original 1.2 linear interpreter reads only base/rate operands. Its unused cubic slots
                        // contain compiler memory residue; native recompilation initializes them to zero.
                        var frames = sourceFrames.Select(f => f.ForVersion(28)).ToArray();
                        string text = AnimationScript.Decompile(frames, rate, token, SourceProject.MaximumSourceTextBytes, budget.Retain)
                            ?? throw new InvalidDataException($"{path}: {target}'s keyframes do not follow its recorded {rate}/s frame grid.");
                        if (!scripts.TryGetValue(path, out var tracks)) scripts.Add(path, tracks = new(StringComparer.Ordinal));
                        if (tracks.TryGetValue(target, out var previous))
                        {
                            if (previous.Rate != rate || previous.Text != text)
                                throw new InvalidDataException($"{path}: {target} has conflicting compiled tracks; reconstruction cannot choose one.");
                        }
                        else tracks.Add(target, new(target, rate, frames, text));
                    }
                }
            if (index != mission.Package.Entries.Count) throw new InvalidDataException($"{mission.Name}: the native definitions leave {mission.Package.Entries.Count - index} compiled animations unmatched.");
        }
        List<WorldSources.Output> outputs = [];
        foreach (var (path, tracks) in scripts.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested(); status?.Invoke(path);
            string text;
            try
            {
                text = SiScriptWriter.Write(tracks.Values.Select(t => new SiScriptWriter.Track(t.Object, t.Frames, t.Rate)).ToArray(), new(null), token, reserveText: budget.Retain);
            }
            catch (InvalidDataException ex)
            {
                text = AnimationScript.Write(tracks.Values.Select(t => (t.Object, t.Text)), token, budget.Retain);
                var parsed = AnimationScript.Parse(Encoding.Latin1.GetBytes(text), path, token);
                foreach (var track in tracks.Values)
                {
                    var keys = AnimationScript.Track(parsed, track.Object) ?? throw new InvalidDataException($"{path}: reconstructed script lost {track.Object}.");
                    var compiled = AnimationScript.Compile(keys, track.Rate, path, token);
                    if (compiled.Count != track.Frames.Count || !compiled.Zip(track.Frames).All(pair => SiScriptWriter.Same(pair.First, pair.Second)))
                        throw new InvalidDataException($"{path}: reconstructed script does not reproduce {track.Object}'s linear keyframes.");
                }
                notes.Add($"{path}: SI text could not reproduce the compiled linear operands ({ex.Message}); the verified zStudio keyframe syntax is used.");
            }
            outputs.Add(new(path, Encoding.Latin1.GetBytes(text)));
        }
        VerifyAndRepair(missions, files, outputs, notes, budget, token, status);
        return outputs;
    }

    private static List<string> Stamps(byte[] prefix, CancellationToken token)
    {
        int count = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(8));
        List<string> paths = [];
        for (int i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            var field = prefix.AsSpan(12 + i * 84, 80); int end = field.IndexOf((byte)0);
            string text = Encoding.Latin1.GetString(end < 0 ? field : field[..end]);
            if (text.EndsWith(".zan", StringComparison.OrdinalIgnoreCase) && WorldAssembler.ProjectPath(text) is { } path) paths.Add(path);
        }
        return paths;
    }
}
