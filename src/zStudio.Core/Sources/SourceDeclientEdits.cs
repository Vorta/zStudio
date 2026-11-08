using zStudio.Core.Gltf;
using zStudio.Core.Worlds;

namespace zStudio.Core.Sources;

/// <summary>Checks an exclusive crater-mode edit against every mission that reads its shared model source.</summary>
internal static class SourceDeclientEdits
{
    internal static void Validate(SourceWorkspace workspace, string model, SourceEditPlan plan,
        SourceScriptInspection inspection, CancellationToken token)
        => ValidateModels(workspace, [model], plan, inspection, "crater-mode edit", token);

    internal static void ValidateModels(SourceWorkspace workspace, IReadOnlyCollection<string> models, SourceEditPlan plan,
        SourceScriptInspection inspection, string description, CancellationToken token)
    {
        var changedModels = models.ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Scripts do not change here. Any edited reference link is in changedModels,
        // so its current consumers also cover newly reached dependencies. Assemble
        // those consumers with the proposed files without changing the workspace.
        ScriptTraceBudget budget = new();
        Dictionary<string, IReadOnlyList<IReadOnlyList<string>>?> scripts = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, bool> reaches = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> active = new(StringComparer.OrdinalIgnoreCase);
        var overlay = new Dictionary<string, byte[]>(workspace.Overlay(), StringComparer.OrdinalIgnoreCase);
        foreach (var change in plan.Changes) overlay[change.Relative] = change.Content;
        var snapshot = new SourceBuilder.Snapshot(workspace.Root, overlay, inventoryToken: token);
        bool libraryAffected = false;
        if (snapshot.Game == GameVariant.MechWarrior3)
        {
            var libraries = SourceGameProfile.LibraryModels(workspace.Root, snapshot, token);
            libraryAffected = libraries.Any(path => Reaches(path, 0));
            if (libraryAffected) _ = Mw3LibrarySources.EvaluateModels(libraries, snapshot, token);
        }
        int checkedWorlds = 0;
        SourceEmbeddedZones? nativeWorlds = snapshot.Game == GameVariant.MechWarrior3 ? new(workspace, token) : null;
        foreach (string world in inspection.Worlds().Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            bool affected = false;
            ScriptTrace.Visit(Script, Path.GetFileName(world), step =>
            {
                inspection.ReserveWork(1);
                if (step is { Command: "LoadGameGen", Args: [var name, ..] }
                    && WorldAssembler.ResolveModel(name, step.ModelDirectories, inspection.ModelExists) is { } loaded
                    && Reaches(loaded, 0)) affected = true;
            }, token, budget: budget, game: snapshot.Game);
            // Incomplete native development scripts can load shared parts after their missing master.
            // Their resolved part loads do not make them buildable campaign consumers.
            if (nativeWorlds != null && !nativeWorlds.HasAuthoredWorld(Path.GetFileNameWithoutExtension(world))) continue;
            if (!affected && !libraryAffected) continue;
            if (++checkedWorlds > 256)
                throw new InvalidDataException($"The {description} reaches more than 256 missions. Give the model independent source ownership before editing it.");
            try
            {
                _ = snapshot.World(Path.GetFileNameWithoutExtension(world), token);
            }
            catch (Exception error) when ((error is InvalidDataException or IOException) && error is not SourceFileChangedException)
            {
                throw new InvalidDataException($"The {description} cannot be applied: {world} reads an edited source and cannot build with the proposed files. Resolve its model and script settings first. {error.Message}", error);
            }
        }
        snapshot.TrackValidation(workspace, plan.Changes.ToDictionary(c => c.Relative, c => c.Content, StringComparer.OrdinalIgnoreCase), token);
        snapshot.CheckUnchanged(token);

        IReadOnlyList<IReadOnlyList<string>>? Script(string name)
        {
            string path = SourceWorkspace.Normalize($"{SourceProject.GameGenFolder}/{name.Replace('\\', '/')}");
            if (scripts.TryGetValue(path, out var lines)) return lines;
            var syntax = inspection.Read(path);
            return scripts[path] = syntax == null ? null : [.. syntax.Lines.Where(l => l.IsInstruction).Select(l => l.Tokens)];
        }
        bool Reaches(string path, int depth)
        {
            inspection.ReserveWork(path.Length + 1L);
            if (changedModels.Contains(path)) return true;
            if (reaches.TryGetValue(path, out bool found)) return found;
            if (depth >= GltfDocument.MaximumDepth || !active.Add(path))
                throw new InvalidDataException("The shared model reference hierarchy is cyclic or too deep to check the crater-mode edit.");
            try
            {
                if (inspection.InspectModel(path) is not { } graph) return reaches[path] = false;
                foreach (string child in graph.References)
                    if (Reaches(child, depth + 1)) return reaches[path] = true;
                return reaches[path] = false;
            }
            finally { active.Remove(path); }
        }
    }

}
