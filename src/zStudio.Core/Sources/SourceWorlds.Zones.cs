using zStudio.Core.Gltf;

namespace zStudio.Core.Sources;

/// <summary>Selected model content cannot be read or decoded to prepare a source-world build.</summary>
public sealed class SourceModelBuildException(string message, Exception innerException) : IOException(message, innerException) { }

public static partial class SourceWorlds
{
    // An added physical file carries its own defaults and nested reference overrides. Choosing a file
    // never silently chooses the attributes of one placement of that file in another mission.
    private static void ValidateModelClosure(SourceWorkspace workspace, string model, CancellationToken token)
    {
        model = SourceWorkspace.Normalize(model);
        if (!workspace.Exists(model, token))
            throw new InvalidDataException($"The model {JsonData.ShownText(model)} does not exist.");
        long remaining = 128L << 20, work = 128L << 20;
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase) { model };
        Queue<string> pending = new([model]);
        void Reserve(long amount)
        {
            token.ThrowIfCancellationRequested();
            if (amount < 0 || amount > work)
                throw new InvalidDataException("Adding this model exceeds the dependency inspection work allowance.");
            work -= amount;
        }
        while (pending.TryDequeue(out string? path))
        {
            token.ThrowIfCancellationRequested();
            Reserve(path.Length + 1L);
            GltfDocument hierarchy;
            try
            {
                byte[] bytes = workspace.ReadModel(path, token, remaining, GltfDocument.MaximumJsonBytes)
                    ?? throw new InvalidDataException($"The model dependency {JsonData.ShownText(path)} is missing.");
                remaining -= bytes.Length;
                Reserve(bytes.Length);
                hierarchy = GltfDocument.ReadHierarchy(bytes, token);
            }
            catch (SourceFileChangedException) { throw; }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                throw new SourceModelBuildException($"The model {JsonData.ShownText(path)} cannot be built: {ex.Message}", ex);
            }
            foreach (string reference in SourceMissionModels.Dependencies(path, hierarchy, Reserve))
            {
                token.ThrowIfCancellationRequested();
                if (seen.Contains(reference)) continue;
                if (seen.Count >= SourceProject.MaximumFiles)
                    throw new InvalidDataException("The model's dependency closure exceeds the project file limit.");
                seen.Add(reference);
                pending.Enqueue(reference);
            }
        }
    }
}
