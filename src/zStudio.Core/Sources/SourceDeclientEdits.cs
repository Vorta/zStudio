using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Core.Sources;

/// <summary>Checks an exclusive crater-mode edit against every mission that reads its shared model source.</summary>
internal static class SourceDeclientEdits
{
    internal static void Validate(SourceWorkspace workspace, string model, SourceEditPlan plan,
        SourceScriptInspection inspection, CancellationToken token)
    {
        // Flag edits cannot change loads or reference links. Discover consumers from the current sources,
        // then assemble only those consumers with the proposed files. Neither stage changes the workspace.
        ScriptTraceBudget budget = new();
        Dictionary<string, IReadOnlyList<IReadOnlyList<string>>?> scripts = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, bool> reaches = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> active = new(StringComparer.OrdinalIgnoreCase);
        SourceMissionModels bindings = new(null, token);
        ProposedFiles files = new(workspace, plan, token);
        int checkedWorlds = 0;
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
            }, token, budget: budget);
            if (!affected) continue;
            if (++checkedWorlds > 256)
                throw new InvalidDataException("The shared crater-mode edit reaches more than 256 missions. Give the model independent source ownership before editing its crater mode.");
            try { _ = new WorldAssembler(files, token).Assemble(Path.GetFileName(world)); }
            catch (Exception error) when ((error is InvalidDataException or IOException) && error is not SourceFileChangedException)
            {
                throw new InvalidDataException($"The crater-mode edit cannot be applied: {world} also reads {model} and cannot build with the proposed mode. Resolve its model and script crater settings first. {error.Message}", error);
            }
        }

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
            if (path.Equals(model, StringComparison.OrdinalIgnoreCase)) return true;
            if (reaches.TryGetValue(path, out bool found)) return found;
            if (depth >= GltfDocument.MaximumDepth || !active.Add(path))
                throw new InvalidDataException("The shared model reference hierarchy is cyclic or too deep to check the crater-mode edit.");
            try
            {
                if (inspection.ReadModel(path) is not { } bytes) return reaches[path] = false;
                inspection.ReserveWork(bytes.LongLength);
                var hierarchy = GltfDocument.ReadHierarchy(bytes, token);
                foreach (string child in bindings.Dependencies(path, hierarchy, inspection.ReserveWork))
                    if (Reaches(child, depth + 1)) return reaches[path] = true;
                return reaches[path] = false;
            }
            finally { active.Remove(path); }
        }
    }

    /// <summary>Bounded, read-only proposed inputs; all dependencies are recorded by the caller's prepared workspace.</summary>
    private sealed class ProposedFiles(SourceWorkspace workspace, SourceEditPlan plan, CancellationToken token) : IProjectFiles
    {
        private readonly Dictionary<string, byte[]> changed = plan.Changes.ToDictionary(c => c.Relative, c => c.Content, StringComparer.OrdinalIgnoreCase);
        private long remainingBytes = 512L << 20;
        private int remainingProbes = 1_000_000;
        public bool Exists(string relative)
        {
            Probe();
            bool present = workspace.Exists(relative, token);
            return changed.ContainsKey(relative) || present;
        }
        public byte[] Read(string relative, CancellationToken cancellation, ProjectReadLimits limits)
        {
            Probe(); cancellation.ThrowIfCancellationRequested();
            limits = limits.WithMaximum(remainingBytes);
            byte[] content;
            if (changed.TryGetValue(relative, out var proposed))
            {
                limits.Validate(proposed);
                content = proposed; // Planning already read this file through the workspace before producing its replacement.
            }
            else content = workspace.Read(relative, cancellation, limits)
                ?? throw new InvalidDataException($"The crater-mode check could not read {relative}.");
            remainingBytes -= content.LongLength;
            return content;
        }
        private void Probe()
        {
            token.ThrowIfCancellationRequested();
            if (--remainingProbes < 0)
                throw new InvalidDataException("Checking the shared crater-mode edit exceeds its aggregate file-probe allowance. Reduce its mission scope before retrying.");
        }
    }
}
