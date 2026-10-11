using System.Buffers.Binary;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Core.Sources;

/// <summary>Moves one existing release window by changing only its source CALL_ANIMATION start token.</summary>
public static class SourceVtolDropEdits
{
    /// <summary>Prepares source content on an existing isolated workspace transaction; does not accept or save it.</summary>
    public static IReadOnlyList<(string Relative, byte[] Content)> Prepare(SourceWorkspace preparedWorkspace,
        string mission, IReadOnlyList<string> worldNodes, AnimationPackage currentPackage, VtolDropMovePlan plan,
        CancellationToken token = default)
    {
        if (!preparedWorkspace.IsPrepared) throw new InvalidOperationException("Prepare the parachute edit on an isolated source workspace transaction.");
        return Prepare(new WorkspaceFiles(preparedWorkspace, token), SourceBuilder.AnimationRoot(mission), worldNodes, currentPackage, plan, token);
    }

    /// <summary>Compiles both source versions and admits only the selected threshold's four-byte delta.</summary>
    public static IReadOnlyList<(string Relative, byte[] Content)> Prepare(IProjectFiles files, string animationRoot,
        IReadOnlyList<string> worldNodes, AnimationPackage currentPackage, VtolDropMovePlan plan, CancellationToken token = default)
    {
        VtolDropWindows.ValidatePlan(currentPackage, plan, token);
        VtolDropWindows.RequireVerifiedMove(plan);
        if (plan.NewStart == plan.Window.Start) return [];
        // The compiler already bounds source reads, syntax expansion and output before allocation. Retain only two
        // compilations, and use the first output itself as the expected threshold-only result (no package clone).
        var before = AnimationCompiler.Compile(files, animationRoot, worldNodes, null, VtolDropWindows.MaximumPackageBytes,
            token, captureSourceBindings: true);
        if (before.EngineRejection is { } rejection) throw Refusal($"The animation sources cannot be built: {JsonData.ShownText(rejection)}");
        VerifyCurrent(currentPackage, before.Package, token);
        var binding = before.SourceBindings!.SingleOrDefault(b => b.EntryIndex == plan.Window.EntryIndex)
            ?? throw Refusal("The selected animation has no source definition.");
        if (before.SourceBindings!.Count(b => b.File.Equals(binding.File, StringComparison.OrdinalIgnoreCase)
            && b.DefinitionOrdinal == binding.DefinitionOrdinal) != 1)
            throw Refusal("This definition produces several animation entries; moving one would also change another flight.");

        var currentSequences = currentPackage.Entries[plan.Window.EntryIndex].AllSequences.ToList();
        int sequenceIndex = currentSequences.FindIndex(s => s.Id == plan.Window.SequenceId);
        if (sequenceIndex < 0) throw Refusal("The selected schedule changed.");
        var currentEvents = currentSequences[sequenceIndex].Events;
        int eventIndex = currentEvents.FindIndex(e => e.Id == plan.Window.CallEventId);
        if (eventIndex < 0) throw Refusal("The selected release call changed.");
        var compiledSequence = before.Package.Entries[plan.Window.EntryIndex].AllSequences.ElementAt(sequenceIndex);
        var compiledEvent = compiledSequence.Events[eventIndex];
        int callOrdinal = currentEvents.Take(eventIndex).Count(e => e.Type == 24);

        byte[] source = files.Read(binding.File, token, ProjectReadLimits.Resource(SourceProject.MaximumSourceTextBytes));
        if (!ZrdText.LooksLikeText(source)) throw Refusal("The animation definition must be text to edit its start-time token.");
        var syntax = ZrdTextSyntax.Parse(source, token);
        var root = syntax.Root;
        while (root.Children.Count == 1 && root.Children[0].Kind == ZrdKind.Array) root = root.Children[0];
        var definition = AnimationItem.Parse(root, binding.File).Where(i => i.Key == "ANIMATION_DEFINITIONS")
            .SelectMany(i => i.All("ANIMATION_LIST")).SelectMany(i => i.All("ANIMATION_DEFINITION"))
            .ElementAtOrDefault(binding.DefinitionOrdinal) ?? throw Refusal("The source definition changed.");
        var sourceSequence = sequenceIndex == 0 ? definition.Item("RESET_STATE")
            : definition.All("SEQUENCE_DEFINITION").ElementAtOrDefault(sequenceIndex - 1);
        // Every CALL_ANIMATION compiles to exactly one type-24 event; ignored settings and unknown items do not
        // participate. Pair only this event kind, avoiding a second copy of the compiler's event-type table.
        var sourceCalls = sourceSequence?.All("CALL_ANIMATION").ToList() ?? [];
        if (sourceCalls.Count != compiledSequence.Events.Count(e => e.Type == 24) || callOrdinal >= sourceCalls.Count)
            throw Refusal("The source calls no longer match the selected schedule.");
        var timing = sourceCalls[callOrdinal].Item("START_TIME");
        if (timing == null || timing.Scalars.Count < 2 || timing.Text() != "SEQUENCE_OFFSET"
            || timing.Number(1) != plan.Window.Start || compiledEvent.Type != 24 || compiledEvent.StartMode != 2)
            throw Refusal("The release call no longer has the expected sequence-relative start.");
        var scalar = timing.Scalars[1];
        byte[] changed = ZrdTextSyntax.Encode(syntax.ReplaceScalars(new Dictionary<Guid, ZrdNode>
        { [scalar.Id] = SourceResourceEdits.Float(plan.NewStart) }, token));
        var after = AnimationCompiler.Compile(new ChangedFiles(files, binding.File, changed), animationRoot, worldNodes, null,
            VtolDropWindows.MaximumPackageBytes, token);
        BinaryPrimitives.WriteSingleLittleEndian(before.Bytes.AsSpan(checked((int)compiledEvent.SourceOffset + 8), 4), plan.NewStart);
        if (!Same(before.Bytes, after.Bytes, token))
            throw Refusal("The source change also changes another animation value; this window cannot be moved independently.");
        VtolDropWindows.ValidatePlan(currentPackage, plan, token);
        return [(binding.File, changed)];
    }

    private static void VerifyCurrent(AnimationPackage current, AnimationPackage built, CancellationToken token)
    {
        if (current.Version != 28 || current.Entries.Count != built.Entries.Count || current.Prefix.Length < 60
            || !current.Prefix.AsSpan(current.Prefix.Length - 60 + 28, 4).SequenceEqual(built.Prefix.AsSpan(built.Prefix.Length - 60 + 28, 4)))
            throw Refusal("The animation sources no longer compile to the world's animation package; rebuild the world first.");
        for (int i = 0; i < current.Entries.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (AnimationComparer.Difference(current.Entries[i], built.Entries[i], token) is { } difference)
                throw Refusal($"The animation sources differ from entry {i}: {JsonData.ShownText(difference)}. Rebuild the world first.");
        }
    }

    private static bool Same(byte[] expected, byte[] actual, CancellationToken token)
    {
        if (expected.Length != actual.Length) return false;
        for (int offset = 0; offset < expected.Length; offset += 4096)
        {
            token.ThrowIfCancellationRequested();
            int count = Math.Min(4096, expected.Length - offset);
            if (!expected.AsSpan(offset, count).SequenceEqual(actual.AsSpan(offset, count))) return false;
        }
        return true;
    }

    private static InvalidDataException Refusal(string message) => new(message);

    private sealed class WorkspaceFiles(SourceWorkspace workspace, CancellationToken operationToken) : IProjectFiles
    {
        public bool Exists(string relative) => workspace.Exists(relative, operationToken);
        public byte[] Read(string relative, CancellationToken token, ProjectReadLimits limits) => workspace.Read(relative, token, limits)
            ?? throw new FileNotFoundException("An animation source is missing; rebuild the world.", relative);
    }

    private sealed class ChangedFiles(IProjectFiles files, string path, byte[] content) : IProjectFiles
    {
        public bool Exists(string relative) => relative.Equals(path, StringComparison.OrdinalIgnoreCase) || files.Exists(relative);
        public byte[] Read(string relative, CancellationToken token, ProjectReadLimits limits)
        {
            token.ThrowIfCancellationRequested();
            if (!relative.Equals(path, StringComparison.OrdinalIgnoreCase)) return files.Read(relative, token, limits);
            limits.Validate(content); return content;
        }
    }
}
