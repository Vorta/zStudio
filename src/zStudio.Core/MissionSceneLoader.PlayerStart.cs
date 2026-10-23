using zStudio.Core.Animation;

namespace zStudio.Core;

/// <summary>
/// The mission's opening takes over the player's start (docs/agents/engine-evidence.md, player start): an entry of its
/// startup chain takes the controls and gives them back from the pose of the tank's node. <paramref name="Opening"/> is the
/// entry that takes the controls; <paramref name="SetsPose"/> whether the chain also moves or turns the tank's root, which
/// then decides where the player starts (m2–m6).
/// </summary>
public sealed record MissionStartControl(string Opening, bool SetsPose);

public static partial class MissionSceneLoader
{
    // Player::AsyncCommandCallback (0x42bb30): 14 takes the single player's controls, 15 gives them back, 10 takes the
    // player's pose from the tank's root node (SyncLocalPoseFromRootNode).
    private const int TakeControls = 14, ReleaseControls = 15, SyncPoseFromNode = 10;

    /// <summary>
    /// Reads the startup chain (the NEW_GAME_START entries and the entries they call, by CALL_ANIMATION, at their bound or
    /// operand node) for the opening's hold on the player: some entry sends <see cref="TakeControls"/>, and some sequence
    /// sends both <see cref="ReleaseControls"/> and <see cref="SyncPoseFromNode"/>. Null when the chain does not.
    /// </summary>
    private static MissionStartControl? PlayerStartControl(AnimationPreviewContext context, AnimationBindingOperation work, IReadOnlyList<string> startup,
        int player, CancellationToken token)
    {
        var lookup = work.EntryLookup();
        Queue<(AnimationEntry Entry, int Root, AnimationBinding Binding)> pending = [];
        HashSet<(int Entry, int Root, AnimationBinding Binding)> seen = [];
        foreach (string name in startup)
        {
            work.Reserve(1L + name.Length);
            if (lookup.Find(name, token) is { } entry && work.Root(entry) is >= 0 and int root) Enqueue(entry, root, context.Binding(entry, root, work));
        }
        string? opening = null; bool releases = false, setsPose = false;
        while (pending.TryDequeue(out var item))
        {
            token.ThrowIfCancellationRequested();
            var (entry, root, binding) = item;
            foreach (var sequence in entry.AllSequences)
            {
                bool gives = false, syncs = false;
                foreach (var ev in sequence.Events)
                {
                    work.Reserve(1);
                    if (ev.Spec == null || ev.Bytes.Length < ev.Spec.Size) continue;
                    if (ev.Type == 35)
                    {
                        int value = ev.I32(12);
                        if (value == TakeControls) opening ??= entry.Name;
                        gives |= value == ReleaseControls; syncs |= value == SyncPoseFromNode;
                        continue;
                    }
                    // Translate, rotate and the motions write the node they name (the operands CollectAnimationUses reads).
                    int reference = ev.Type switch { 7 or 9 => ev.I16(28), 10 or 11 => ev.I32(16), 12 => ev.I32(12), _ => 0 };
                    if (reference != 0 && context.ResolveInstanceNode(entry, reference, root, binding, work) == player) setsPose = true;
                    if (ev.Type != 24 || lookup.ResolveChild(ev, token) is not { } child) continue;
                    // CALL_ANIMATION's OPERAND_NODE rebinds the child there; otherwise it runs at its own bound root.
                    int operand = ev.I16(44) > 0 ? context.ResolveInstanceNode(entry, ev.I16(44), root, binding, work) : -1;
                    if (operand >= 0) Enqueue(child, operand, AnimationBinding.Rebound);
                    else if (work.Root(child) is >= 0 and int childRoot) Enqueue(child, childRoot, context.Binding(child, childRoot, work));
                }
                releases |= gives && syncs;
            }
        }
        return opening != null && releases ? new(opening, setsPose) : null;

        void Enqueue(AnimationEntry entry, int root, AnimationBinding binding)
        {
            work.Reserve(16);
            if (seen.Add((entry.Index, root, binding))) pending.Enqueue((entry, root, binding));
        }
    }
}
