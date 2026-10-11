using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Terrain;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Core.Sources;

public static partial class SourceBlender
{
    /// <summary>Bounded, workspace-backed discovery of the missions that load an edited model, without assembling worlds.</summary>
    private sealed class TextureModelUses(SourceWorkspace workspace, string model, CancellationToken token)
    {
        private readonly SourceScriptInspection inspection = new(workspace, token);
        private readonly ScriptTraceBudget traceBudget = new();
        private readonly Dictionary<string, bool> missions = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IReadOnlyList<IReadOnlyList<string>>?> scripts = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TerrainRecipe?> recipes = new(StringComparer.OrdinalIgnoreCase);
        private long recipeBytes;

        internal bool InMission(string mission)
        {
            token.ThrowIfCancellationRequested();
            if (missions.TryGetValue(mission, out bool known)) return known;
            var bindings = inspection.MissionModels(mission);
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
                return bindings.Exists(path, inspection.ModelExists);
            }
            bool Holds(string path, int depth)
            {
                inspection.ReserveWork(path.Length + 1L);
                if (!Exists(path)) return false;
                string geometry = bindings.Geometry(path);
                if (geometry.Equals(model, StringComparison.OrdinalIgnoreCase)) return true;
                if (holds.TryGetValue(path, out bool cached)) return cached;
                holds[path] = false;
                if (depth >= GltfDocument.MaximumDepth) throw new IOException("The other mission's model references are too deep to inspect safely.");
                if (inspection.ReadModel(geometry) is not { } bytes) return false;
                inspection.ReserveWork(bytes.LongLength);
                var hierarchy = GltfDocument.ReadHierarchy(bytes, token);
                foreach (string next in bindings.Dependencies(path, hierarchy, inspection.ReserveWork))
                    if (Holds(next, depth + 1)) return holds[path] = true;
                // Terrain markers in a loaded database take surfaces from their recipe, including transparent groups.
                if (depth == 0)
                    foreach (var root in hierarchy.Roots)
                        if (TerrainUses(root, geometry, 0)) return holds[path] = true;
                return false;
            }
            bool TerrainUses(GltfNode node, string from, int depth)
            {
                inspection.ReserveWork(1);
                if (depth >= GltfDocument.MaximumDepth) throw new IOException("The other mission's terrain groups are too deep to inspect safely.");
                if (Text(node.Extras?[WorldGltf.Key]?["terrain"]) is { } uri)
                {
                    inspection.ReserveWork(4L * (from.Length + uri.Length + 1));
                    string path = WorldAssembler.Relative(from, uri);
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
                            if (bindings.Geometry(logical).Equals(model, StringComparison.OrdinalIgnoreCase)) return true;
                        }
                    return false;
                }
                if (WorldGltf.IsTransparentTerrainGroup(node, from))
                    foreach (var child in node.Children)
                        if (TerrainUses(child, from, depth + 1)) return true;
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
