using zStudio.Core.Gltf;
using zStudio.Core.Terrain;
using zStudio.Core.Worlds;

namespace zStudio.Core.Sources;

public static partial class SourceBlender
{
    public static IReadOnlyList<string> MissionsUsingModel(SourceWorkspace workspace, string model, CancellationToken token = default)
        => MissionsUsingModel(workspace, model, token, new(workspace, token));

    internal static IReadOnlyList<string> MissionsUsingModel(SourceWorkspace workspace, string model, CancellationToken token, SourceScriptInspection inspection)
    {
        TextureModelUses uses = new(workspace, model, token, inspection);
        return SourceWorlds.Missions(workspace.Root, token).Where(uses.InMission).ToArray();
    }
    /// <summary>Bounded, workspace-backed discovery of the missions that load an edited model, without assembling worlds.</summary>
    private sealed class TextureModelUses(SourceWorkspace workspace, string model, CancellationToken token, SourceScriptInspection? sharedInspection = null)
    {
        private readonly SourceScriptInspection inspection = sharedInspection ?? new(workspace, token);
        private readonly ScriptTraceBudget traceBudget = new();
        private readonly Dictionary<string, bool> missions = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IReadOnlyList<IReadOnlyList<string>>?> scripts = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TerrainRecipe?> recipes = new(StringComparer.OrdinalIgnoreCase);
        private long recipeBytes;

        internal bool InMission(string mission)
        {
            token.ThrowIfCancellationRequested();
            if (missions.TryGetValue(mission, out bool known)) return known;
            Dictionary<string, bool> holds = new(StringComparer.OrdinalIgnoreCase);
            bool found = false;
            try
            {
                ScriptTrace.Visit(Script, mission + ".gs", step =>
                {
                    inspection.ReserveWork(1);
                    if (!found && step is { Command: "LoadGameGen", Args: [var file, _, ..] }
                        && WorldAssembler.ResolveModel(file, step.ModelDirectories, Exists) is { } loaded)
                        found = Holds(loaded, 0);
                }, token, budget: traceBudget);
            }
            // As with other mission inspection, scripts or models the build cannot read cannot build this model.
            // Capacity and access failures remain failures, rather than silently making an incomplete inspection succeed.
            catch (InvalidDataException) when (!traceBudget.Exhausted) { found = false; }
            return missions[mission] = found;

            bool Exists(string path)
            {
                inspection.ReserveWork(path.Length + 1L);
                return inspection.ModelExists(path);
            }
            bool Holds(string path, int depth)
            {
                inspection.ReserveWork(path.Length + 1L);
                if (!Exists(path)) return false;
                string geometry = path;
                if (geometry.Equals(model, StringComparison.OrdinalIgnoreCase)) return true;
                if (holds.TryGetValue(path, out bool cached)) return cached;
                holds[path] = false;
                if (depth >= GltfDocument.MaximumDepth) throw new IOException("The other mission's model references are too deep to inspect safely.");
                if (inspection.InspectModel(geometry) is not { } graph) return false;
                foreach (string next in graph.References)
                    if (Holds(next, depth + 1)) return holds[path] = true;
                // Terrain markers in a loaded database take surfaces from their recipe, including transparent groups.
                if (depth == 0)
                    foreach (string recipe in graph.TerrainRecipes)
                        if (TerrainUses(recipe)) return holds[path] = true;
                return false;
            }
            bool TerrainUses(string path)
            {
                inspection.ReserveWork(path.Length + 1L);
                if (!recipes.TryGetValue(path, out var recipe))
                {
                    const long maximumBytes = 64L * 1024 * 1024;
                    byte[]? bytes;
                    try { bytes = workspace.Read(path, token, Math.Min(TerrainRecipe.MaximumBytes, maximumBytes - recipeBytes)); }
                    catch (InvalidDataException ex) { throw new IOException("The other missions' terrain recipes exceed the texture inspection input allowance.", ex); }
                    if (bytes != null) { recipeBytes += bytes.Length; inspection.ReserveWork(bytes.Length); }
                    recipes[path] = recipe = bytes == null ? null : TerrainRecipe.Parse(bytes, path);
                }
                if (recipe != null)
                    foreach (var surface in recipe.Surfaces)
                    {
                        inspection.ReserveWork(4L * (path.Length + surface.Model.Length + 1));
                        string logical = SourceTerrain.SurfaceFile(path, surface);
                        if (logical.Equals(model, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                return false;
            }
        }

        private IReadOnlyList<IReadOnlyList<string>>? Script(string name)
        {
            string path = $"{SourceProject.GameGenFolder}/{name.Replace('\\', '/')}";
            inspection.ReserveWork(path.Length + 1L);
            if (scripts.TryGetValue(path, out var cached)) return cached;
            var parsed = inspection.Read(path);
            return scripts[path] = parsed == null ? null : [.. parsed.Lines.Where(line => line.IsInstruction).Select(line => line.Tokens)];
        }
    }
}
