using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using zStudio.Core.Formats;

namespace zStudio.Core.Animation;

public sealed partial class AnimationCompiler
{
    private readonly Dictionary<AnimationItem, Dictionary<(int Sequence, int Event), string>> sourceAddressProofs = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(string File, int Ordinal), string> sourceOwnerProofs = [];

    private string SourceOwnerProof(AnimationDefinition definition)
    {
        if (!sourceOwnerProofs.TryGetValue((definition.File, definition.Ordinal), out string? proof))
        {
            var siblings = definitions.Definitions.Where(d => d.File == definition.File).DistinctBy(d => d.Ordinal).ToArray();
            var owners = AnimationSourceAddressProof.Owners(siblings, token, ReserveSyntax);
            foreach (var sibling in siblings) sourceOwnerProofs[(sibling.File, sibling.Ordinal)] = owners[sibling.Ordinal];
            proof = sourceOwnerProofs[(definition.File, definition.Ordinal)];
        }
        return proof;
    }

    /// <summary>
    /// A source record's syntax indices only address one revision. Guard their ownership with the surrounding key layout:
    /// optional settings can otherwise move a different sequence or event into precisely the same indices. Scalar operand
    /// edits leave unique ownership alone. Repeated, indistinguishable siblings need their ordered semantic contents too:
    /// changing those operands conservatively marks them, while timing edits remain safe. A changed layout is deliberately
    /// not remapped by ordinal or name; scenarios keep those items marked until their original layout returns.
    /// </summary>
    private string? SourceAddressProof(AnimationItem definition, AnimationItem? sequence, AnimationItem? ev = null)
    {
        if (sequence == null || sequence.SyntaxIndex < 0 || ev is { SyntaxIndex: < 0 }) return null;
        if (!sourceAddressProofs.TryGetValue(definition, out var proofs))
            sourceAddressProofs.Add(definition, proofs = AnimationSourceAddressProof.Build(definition, token, ReserveSyntax));
        return proofs.GetValueOrDefault((sequence.SyntaxIndex, ev?.SyntaxIndex ?? -1));
    }
}

/// <summary>The shared source codec's ownership proofs, reused by compilation and source-card inspection.</summary>
internal static class AnimationSourceAddressProof
{
    internal static Dictionary<int, string> Owners(IReadOnlyList<AnimationDefinition> definitions, CancellationToken token, Action<long> reserve)
    {
        Dictionary<string, List<AnimationDefinition>> anchors = new(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            token.ThrowIfCancellationRequested(); reserve(384);
            using var anchor = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var item in definition.Item.Items)
                if (item.Key is "NAME" or "ANIMATION_NAME" or "ANIMATION_ROOT_NAME") AppendContent(anchor, item, true, true, token);
            string key = Convert.ToHexString(anchor.GetHashAndReset());
            if (!anchors.TryGetValue(key, out var siblings)) anchors.Add(key, siblings = []);
            siblings.Add(definition);
        }
        Dictionary<int, string> proofs = [];
        foreach (var (anchor, siblings) in anchors)
            foreach (var definition in siblings)
            {
                reserve(256);
                using var proof = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                AppendText(proof, definition.File);
                AppendText(proof, anchor);
                // Duplicate owner anchors cannot distinguish a reordered sibling. Their entire contents, including
                // timing, must match before a start or retained binding is allowed to run again.
                if (siblings.Count > 1) AppendContent(proof, definition.Item, true, true, token);
                proofs.Add(definition.Ordinal, Convert.ToHexString(proof.GetHashAndReset()));
            }
        return proofs;
    }

    internal static Dictionary<(int Sequence, int Event), string> Build(AnimationItem definition, CancellationToken token, Action<long> reserve)
    {
        reserve(128);
        Dictionary<(int Sequence, int Event), string> proofs = [];
        using var layout = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var item in definition.Items)
        {
            token.ThrowIfCancellationRequested();
            AppendItem(layout, item);
            // Sequence names are ownership anchors as well as layout: exchanging named sibling sequences must not
            // turn a switch in one into a switch in the other. Renaming a sequence conservatively marks its events.
            if (item.Key == "SEQUENCE_DEFINITION")
                foreach (var name in item.All("NAME"))
                    foreach (var scalar in name.Scalars) AppendText(layout, scalar.Text);
        }
        byte[] definitionLayout = layout.GetHashAndReset();
        Dictionary<string, List<AnimationItem>> namedSequences = new(StringComparer.Ordinal);
        foreach (var item in definition.Items)
        {
            if (item.Key != "SEQUENCE_DEFINITION") continue;
            reserve(128);
            string name = SequenceName(item);
            if (!namedSequences.TryGetValue(name, out var siblings)) namedSequences.Add(name, siblings = []);
            siblings.Add(item);
        }
        foreach (var item in definition.Items)
        {
            if (item.Key is not ("SEQUENCE_DEFINITION" or "RESET_STATE")) continue;
            Dictionary<string, List<AnimationItem>> eventShapes = new(StringComparer.Ordinal);
            Dictionary<int, string> shapeOf = [];
            foreach (var child in item.Items)
            {
                if (child.Key is "NAME" or "ACTIVATION" or "START_TIME") continue;
                reserve(384);
                using var shape = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    AppendContent(shape, child, false, false, token);
                string key = Convert.ToHexString(shape.GetHashAndReset());
                shapeOf.Add(child.SyntaxIndex, key);
                if (!eventShapes.TryGetValue(key, out var siblings)) eventShapes.Add(key, siblings = []);
                siblings.Add(child);
            }
            Dictionary<int, byte[]> ambiguous = [];
            Dictionary<string, List<AnimationItem>> equalContents = new(StringComparer.Ordinal);
            foreach (var group in eventShapes.Where(p => p.Value.Count > 1))
            {
                foreach (var sibling in group.Value)
                {
                    reserve(128);
                    using var contents = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    AppendContent(contents, sibling, true, false, token);
                    byte[] content = contents.GetHashAndReset();
                    ambiguous.Add(sibling.SyntaxIndex, content);
                    string identity = Convert.ToHexString(content);
                    if (!equalContents.TryGetValue(identity, out var equal)) equalContents.Add(identity, equal = []);
                    equal.Add(sibling);
                }
            }
            Dictionary<int, (string Clock, int Rank, int Ties)> timingOrder = [];
            foreach (var group in equalContents.Values.Where(g => g.Count > 1))
            {
                reserve(128L * group.Count);
                var ordered = group.Select(e => (Event: e, Timing: Timing(e)))
                    .OrderBy(e => e.Timing.Clock, StringComparer.Ordinal).ThenBy(e => e.Timing.Seconds).ToArray();
                int rank = 0;
                for (int first = 0; first < ordered.Length;)
                {
                    int end = first + 1;
                    while (end < ordered.Length && ordered[end].Timing == ordered[first].Timing) end++;
                    if (first == 0 || ordered[first - 1].Timing.Clock != ordered[first].Timing.Clock) rank = 0;
                    for (int n = first; n < end; n++) timingOrder.Add(ordered[n].Event.SyntaxIndex, (ordered[n].Timing.Clock, rank, end - first));
                    rank += end - first; first = end;
                }
            }
            HashSet<int> contextual = [];
            foreach (var group in equalContents.Values.Where(g => g.Count > 1))
            {
                reserve(64L * group.Count);
                foreach (var sibling in group) contextual.Add(sibling.SyntaxIndex);
            }
            byte[]? sequenceContext = null;
            if (contextual.Count > 0)
            {
                reserve(128);
                using var context = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                foreach (var child in item.Items)
                {
                    if (child.Key is "NAME" or "ACTIVATION" or "START_TIME") continue;
                    // Equal event contents cannot distinguish their surrounding blocks. Hash the entire ordered context
                    // once, including suffixes: a leading IF (or call) also belongs to what follows it in that block.
                    // Window calls/stops use their order witness so verified moves preserve ownership. Other timings
                    // remain exact; exchanging timed callbacks or motion must not exchange the call they lead to.
                    AppendContent(context, child, true, child.Key is not ("CALL_ANIMATION" or "STOP_ANIMATION"), token);
                    if (timingOrder.TryGetValue(child.SyntaxIndex, out var order))
                    {
                        AppendText(context, order.Clock); AppendOrder(context, order.Rank, order.Ties);
                    }
                }
                sequenceContext = context.GetHashAndReset();
            }
            using var prefix = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            prefix.AppendData(definitionLayout);
            AppendItem(prefix, item);
            if (item.Key == "SEQUENCE_DEFINITION"
                && namedSequences[SequenceName(item)] is { Count: > 1 })
            {
                // A repeated name cannot establish which sibling survived an external reorder.
                AppendContent(prefix, item, true, true, token);
            }
            reserve(256);
            proofs.Add((item.SyntaxIndex, -1), Convert.ToHexString(prefix.GetCurrentHash()));
            foreach (var child in item.Items)
            {
                token.ThrowIfCancellationRequested();
                AppendItem(prefix, child);
                if (child.Key is "NAME" or "ACTIVATION" or "START_TIME") continue;
                // GetCurrentHash leaves the prefix intact: removing a later event does not invalidate earlier ones.
                reserve(256);
                using var proof = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                proof.AppendData(prefix.GetCurrentHash());
                string shape = shapeOf[child.SyntaxIndex]; AppendText(proof, shape);
                if (ambiguous.TryGetValue(child.SyntaxIndex, out var contents))
                {
                    proof.AppendData(contents);
                    if (contextual.Contains(child.SyntaxIndex)) proof.AppendData(sequenceContext!);
                }
                if (timingOrder.TryGetValue(child.SyntaxIndex, out var order))
                {
                    // Identical sibling calls need an order witness too: exchanging only their thresholds is otherwise
                    // indistinguishable from moving them. Verified window moves retain this order; crossings mark safely.
                    AppendText(proof, order.Clock); AppendOrder(proof, order.Rank, order.Ties);
                }
                proofs.Add((item.SyntaxIndex, child.SyntaxIndex), Convert.ToHexString(proof.GetHashAndReset()));
            }
        }
        return proofs;

        static (string Clock, float Seconds) Timing(AnimationItem item)
        {
            if (item.Item("START_TIME") is not { } timing) return ("ANIMATION_OFFSET", 0);
            if (timing.Scalars is not [var clock, var seconds, ..]) return ("unavailable", float.NaN);
            return (clock.Text, seconds.Kind switch
            {
                ZrdKind.Int => unchecked((int)seconds.Bits),
                ZrdKind.Float => BitConverter.UInt32BitsToSingle(seconds.Bits),
                _ => float.NaN,
            });
        }
        static void AppendOrder(IncrementalHash hash, int rank, int ties)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, rank);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], ties);
            hash.AppendData(bytes);
        }
        static string SequenceName(AnimationItem item) => item.All("NAME").LastOrDefault() is { Scalars.Count: > 0 } name ? name.Text() : "";
    }

    private static void AppendContent(IncrementalHash hash, AnimationItem item, bool values, bool timing, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Timing is explicitly editable without changing event ownership, even for repeated call windows.
        if (!timing && item.Key == "START_TIME") return;
        AppendText(hash, item.Key);
        if (item.Values == null) { AppendText(hash, "bare"); return; }
        AppendNode(hash, item.Values, values, timing, token);
    }
    private static void AppendNode(IncrementalHash hash, ZrdNode node, bool values, bool timing, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (node.Kind == ZrdKind.Array)
        {
            AppendText(hash, "array");
            for (int i = 0; i < node.Children.Count; i++)
            {
                var child = node.Children[i];
                if (child.Kind == ZrdKind.String && i + 1 < node.Children.Count && node.Children[i + 1].Kind == ZrdKind.Array)
                {
                    var contents = node.Children[++i];
                    if (!timing && child.Text == "START_TIME") continue;
                    AppendText(hash, "key"); AppendText(hash, child.Text);
                    AppendNode(hash, contents, values, timing, token);
                }
                else AppendNode(hash, child, values, timing, token);
            }
            AppendText(hash, "end");
        }
        else
        {
            AppendText(hash, node.Kind == ZrdKind.String ? "text" : "number");
            if (values)
            {
                if (node.Kind == ZrdKind.String) AppendText(hash, node.Text);
                else AppendNumber(hash, node.Kind, node.Bits);
            }
        }
    }
    private static void AppendNumber(IncrementalHash hash, ZrdKind kind, uint bits)
    {
        Span<byte> value = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(value, (int)kind);
        BinaryPrimitives.WriteUInt32LittleEndian(value[4..], bits);
        hash.AppendData(value);
    }
    private static void AppendItem(IncrementalHash hash, AnimationItem item)
    {
        Span<byte> address = stackalloc byte[5];
        BinaryPrimitives.WriteInt32LittleEndian(address, item.SyntaxIndex);
        address[4] = item.Values == null ? (byte)0 : (byte)1;
        hash.AppendData(address); AppendText(hash, item.Key);
    }
    private static void AppendText(IncrementalHash hash, string value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, value.Length);
        hash.AppendData(length);
        // The source codec's Latin-1 text is retained already; hash its UTF-16 code units without a second text copy.
        hash.AppendData(MemoryMarshal.AsBytes(value.AsSpan()));
    }
}
