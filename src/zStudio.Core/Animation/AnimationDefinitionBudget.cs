using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core.Animation;

/// <summary>One repair run's syntax growth, including candidate graphs, generalized copies and file wrappers.</summary>
internal sealed class AnimationDefinitionBudget(CancellationToken token, Action<long>? reserve = null,
    int maximumNodes = ZrdText.MaximumNodes)
{
    private long bytes;
    private int nodes;
    private long visits;
    private readonly int nodeLimit = maximumNodes is >= 0 and <= ZrdText.MaximumNodes ? maximumNodes : throw new ArgumentOutOfRangeException(nameof(maximumNodes));
    // Includes a node, temporary/list slots and the bounded canonical text made from it. String text has an
    // additional worst-case escape/builder allowance. Cloned child arrays are reserved separately before copying.
    private const int NodeBytes = 384;
    internal int UsedNodes => nodes;

    internal void Visit()
    {
        token.ThrowIfCancellationRequested();
        if (++visits > 8L * ZrdText.MaximumNodes) throw new IOException("Animation definition repair exceeds its syntax traversal limit.");
    }

    internal void CheckNodes(long count)
    {
        token.ThrowIfCancellationRequested();
        if (count < 0 || count > nodeLimit - nodes || count * NodeBytes > ReconstructionBudget.MaximumBytes - bytes)
            throw new IOException("Animation definition repair exceeds its syntax graph limit; split the animation definitions into smaller files.");
    }

    internal void Node(int textLength = 0)
    {
        Visit(); CheckNodes(1);
        Reserve(NodeBytes + 16L * textLength);
        nodes++;
    }

    internal void Reserve(long count)
    {
        token.ThrowIfCancellationRequested();
        if (count < 0 || count > ReconstructionBudget.MaximumBytes - bytes)
            throw new IOException("Animation definition repair exceeds its syntax graph memory limit.");
        reserve?.Invoke(count);
        token.ThrowIfCancellationRequested();
        bytes += count;
    }

    internal void CopyChildren(int count) => Reserve(64L + 16L * count);

    /// <summary>Reserve the writer's builder and returned string, including untouched parts of a replaced file.</summary>
    internal void Text(ZrdNode root)
    {
        long characters = 0;
        Measure(root, 0);
        Reserve(64 + 4 * characters);
        void Measure(ZrdNode node, int depth)
        {
            Visit();
            if (depth > ZrdText.MaximumDepth) throw new InvalidDataException("Animation definition nesting limit exceeded.");
            // Delimiters, scalar numbers and both indentation edges, with worst-case Latin-1 string escapes.
            characters = Math.Min(SourceProject.MaximumSourceTextBytes, characters + 32L + 4L * depth + 4L * node.Text.Length);
            if (characters == SourceProject.MaximumSourceTextBytes) return;
            foreach (var child in node.Children)
            {
                Measure(child, depth + 1);
                if (characters == SourceProject.MaximumSourceTextBytes) break;
            }
        }
    }
}
