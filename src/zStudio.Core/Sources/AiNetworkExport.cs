using zStudio.Core.Formats;

namespace zStudio.Core.Sources;

/// <summary>
/// Writes an AI network's file (<c>net_&lt;n&gt;.zrd</c>) for the game an export is for (docs/agents/engine-evidence.md#net_nnzrd).
/// Recoil 1.x and reRECOIL v1 read only version 105: the names <c>net_01</c> to <c>net_99</c>, the nodes <c>node_00</c> to
/// <c>node_98</c> and three link slots per node. reRECOIL v2 reads version 105 and version 200 alike, with any network and node
/// number and any number of link slots. A network within version 105's limits is written as version 105, with three link
/// slots per node, for both targets: the shipped networks build byte for byte as before. A network past those limits is
/// written as version 200, which marks it for reRECOIL v2 (Recoil 1.x drops a network of another version); only target v2
/// writes it, and target v1 refuses it as it refuses a world that version 15 cannot store.
/// </summary>
internal static class AiNetworkExport
{
    /// <summary>The version of the networks that Recoil 1.x and reRECOIL v1 read.</summary>
    internal const int Version = 105;
    /// <summary>The version of a network past version 105's limits, which only reRECOIL v2 reads.</summary>
    internal const int WideVersion = 200;
    /// <summary>The link slots of a node in version 105.</summary>
    internal const int Version105Slots = 3;

    /// <summary>
    /// The tree to write for the network <paramref name="member"/> (its parsed source <paramref name="root"/>): for
    /// <paramref name="target"/> v2 a network past version 105's limits gets version 200 and keeps its link slots as authored;
    /// any other RECOIL network gets version 105 (a file without a version tag keeps none) and three link slots per node,
    /// empty slots (-1) added or empty slots past the third removed. A file of another version (MechWarrior 3's 106, or one the
    /// game drops), a file without node tags and a file that is not a list of tags and values are written as they are.
    /// <paramref name="problems"/> receives what keeps the game from using the network (see <see cref="Problems"/>).
    /// </summary>
    /// <exception cref="PastLimitsException">The network is past version 105's limits and <paramref name="target"/> is v1.</exception>
    internal static ZrdNode Prepare(string member, ZrdNode root, ExportTarget target, CancellationToken token, ICollection<string>? problems = null)
    {
        if (root.Kind != ZrdKind.Array) return root;
        // A source file holds one list; the game reads the tags of the root's list (as MissionAiNetworks.Decode does).
        bool wrapped = root.Children.Count == 1 && root.Children[0].Kind == ZrdKind.Array;
        var list = wrapped ? root.Children[0] : root;
        var items = list.Children;
        int versionAt = -1;
        List<int> nodeAt = [];
        for (int i = 0; i < items.Count; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            if (items[i].Kind != ZrdKind.String) continue;
            if (items[i].Text == "version") { if (versionAt < 0) versionAt = i; }
            else if (MissionAiNetworks.TryNodeIndex(items[i].Text, out _)) nodeAt.Add(i);
        }
        // Only a RECOIL network (version 105 or 200, or no version tag) is written for the target.
        int? stored = null;
        if (versionAt >= 0)
        {
            if (versionAt + 1 >= items.Count || items[versionAt + 1] is not { Kind: ZrdKind.Array, Children: [{ Kind: ZrdKind.Int } value] }
                || unchecked((int)value.Bits) is not (Version or WideVersion)) return root;
            stored = unchecked((int)value.Bits);
        }
        if (problems != null) foreach (string problem in Problems(items, nodeAt, token)) problems.Add(problem);
        if (nodeAt.Count == 0) return root;
        var past = PastVersion105(member, items, nodeAt, token);
        if (past.Count > 0 && target == ExportTarget.V1) throw new PastLimitsException(past);
        ZrdNode[]? edited = null;
        ZrdNode[] Edited() => edited ??= [.. items];
        int written = past.Count > 0 ? WideVersion : Version;
        if (stored is { } version && version != written)
            Edited()[versionAt + 1] = items[versionAt + 1] with { Children = [Integer(written)] };
        else if (stored == null && past.Count > 0)
            edited = [String("version"), ZrdNode.Create(ZrdKind.Array) with { Children = [Integer(WideVersion)] }, .. items];
        // Version 105 stores three link slots per node: the links PastVersion105 found all fit in the first three.
        if (past.Count == 0)
            foreach (int at in nodeAt)
            {
                token.ThrowIfCancellationRequested();
                if (at + 1 >= items.Count || LinkSlots(items[at + 1]) is not { } slots || slots.Children.Count == Version105Slots) continue;
                ZrdNode[] three = [.. slots.Children.Take(Version105Slots), .. Enumerable.Range(0, Math.Max(0, Version105Slots - slots.Children.Count)).Select(_ => Integer(-1))];
                var node = items[at + 1];
                Edited()[at + 1] = node with { Children = [node.Children[0], node.Children[1], slots with { Children = three }] };
            }
        if (edited == null) return root;
        var result = list with { Children = edited };
        return wrapped ? root with { Children = [result] } : result;
    }

    /// <summary>
    /// What a network uses past version 105, in words (empty when it fits): a member name Recoil 1.x does not read, node tags
    /// it does not read (<c>node_99</c>, <c>node_5</c>), and links in a fourth or later slot. At most a few of each are named.
    /// </summary>
    internal static IReadOnlyList<string> PastVersion105(string member, IReadOnlyList<ZrdNode> items, IReadOnlyList<int> nodeAt, CancellationToken token)
    {
        List<string> past = [];
        if (!MissionAiNetworks.RecoilReadsName(member))
            past.Add(MissionAiNetworks.TryNetworkNumber(member, out int number) && number > 99 ? $"network number {number}" : "a file name with more than two digits");
        List<string> keys = [], linked = [];
        int keyCount = 0, linkedCount = 0;
        foreach (int at in nodeAt)
        {
            token.ThrowIfCancellationRequested();
            string key = items[at].Text;
            if (!MissionAiNetworks.RecoilReadsNode(key) && keyCount++ < 3) keys.Add(JsonData.ShownText(key, 32));
            if (at + 1 < items.Count && LinkSlots(items[at + 1]) is { } slots && LastLink(slots) >= Version105Slots && linkedCount++ < 3)
                linked.Add($"{JsonData.ShownText(key, 32)} has a link past its third slot");
        }
        if (keyCount > 0) past.Add((keyCount == 1 ? "the node " : "the nodes ") + string.Join(", ", keys) + (keyCount > keys.Count ? $" and {keyCount - keys.Count:N0} more" : ""));
        if (linkedCount > 0) past.Add(string.Join(", ", linked) + (linkedCount > linked.Count ? $" and {linkedCount - linked.Count:N0} more nodes have links past their third slot" : ""));
        return past;
    }

    /// <summary>
    /// What keeps the game from using a network, which reRECOIL v2 refuses to load (docs/agents/engine-evidence.md#net_nnzrd):
    /// no node tag, a link to a node number no tag of the file names, a link after an empty slot. At most three are named.
    /// </summary>
    internal static IReadOnlyList<string> Problems(IReadOnlyList<ZrdNode> items, IReadOnlyList<int> nodeAt, CancellationToken token)
    {
        if (nodeAt.Count == 0) return ["it has no node"];
        HashSet<int> numbers = [];
        foreach (int at in nodeAt) if (MissionAiNetworks.TryNodeIndex(items[at].Text, out int index)) numbers.Add(index);
        List<string> problems = [];
        int count = 0;
        foreach (int at in nodeAt)
        {
            token.ThrowIfCancellationRequested();
            if (at + 1 >= items.Count || LinkSlots(items[at + 1]) is not { } slots) continue;
            string key = JsonData.ShownText(items[at].Text, 32);
            bool empty = false;
            for (int slot = 0; slot < slots.Children.Count; slot++)
            {
                int target = unchecked((int)slots.Children[slot].Bits);
                if (target < 0) { empty = true; continue; }
                string? problem = !numbers.Contains(target) ? $"{key} links to node {target}, which the file does not have (Recoil crashes at mission load on such a link)"
                    : empty ? $"a link of {key} follows an empty slot" : null;
                if (problem == null) continue;
                if (count++ < 3) problems.Add(problem);
                break;
            }
        }
        if (count > problems.Count) problems.Add(count - problems.Count == 1 ? "1 more node like these" : $"{count - problems.Count:N0} more nodes like these");
        return problems;
    }

    /// <summary>A network that Recoil 1.x and reRECOIL v1 cannot read, refused by target v1: what it uses past version 105.</summary>
    internal sealed class PastLimitsException(IReadOnlyList<string> past) : Exception(string.Join("; ", past))
    {
        internal IReadOnlyList<string> Past { get; } = past;
    }

    /// <summary>
    /// The refusal of target v1 for the networks of one archive past version 105's limits: the reason and the remedy first
    /// (diagnostics show a message's start), then the first eight networks, each its source and what it uses.
    /// </summary>
    internal static string Refusal(IReadOnlyList<string> networks) =>
        $"Recoil 1.x and reRECOIL v1 read only version-105 AI networks (net_01.zrd to net_99.zrd, node_00 to node_98, three links per node); "
        + "export for reRECOIL v2 (target v2), which writes such a network as version 200, or keep the networks within those limits. "
        + (networks.Count == 1 ? "Past them: " : $"{networks.Count:N0} networks are past them: ") + string.Join("; ", networks.Take(8))
        + (networks.Count > 8 ? $" and {networks.Count - 8:N0} more." : ".");

    /// <summary>A node value's link slots: the third item of a three-item array, when it is an array of integers; otherwise null.</summary>
    private static ZrdNode? LinkSlots(ZrdNode node) =>
        node is { Kind: ZrdKind.Array, Children.Count: 3 } && node.Children[2] is { Kind: ZrdKind.Array } slots && slots.Children.All(s => s.Kind == ZrdKind.Int) ? slots : null;
    /// <summary>The last slot that holds a link (a node number, 0 or more), or -1.</summary>
    private static int LastLink(ZrdNode slots)
    {
        for (int i = slots.Children.Count - 1; i >= 0; i--) if (unchecked((int)slots.Children[i].Bits) >= 0) return i;
        return -1;
    }
    private static ZrdNode Integer(int value) => ZrdNode.Create(ZrdKind.Int, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    private static ZrdNode String(string text) => ZrdNode.Create(ZrdKind.String) with { Text = text };
}
