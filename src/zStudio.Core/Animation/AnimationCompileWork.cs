namespace Recoil.Zbd.Core.Animation;

/// <summary>One compilation's raw key/pose visits, including indexing and lookahead which emit no frames.</summary>
internal sealed class AnimationCompileWork(CancellationToken token, long maximum = AnimationCompileWork.MaximumUnits,
    Action? admitted = null)
{
    internal const long MaximumUnits = 16L * 1024 * 1024;
    private readonly long limit = maximum >= 0 ? maximum : throw new ArgumentOutOfRangeException(nameof(maximum));
    private long used;

    internal void Visit()
    {
        token.ThrowIfCancellationRequested();
        if (used >= limit)
            throw new InvalidDataException("Animation keyframe compilation exceeds its aggregate work limit; reduce repeated motion events or simplify their tracks.");
        used++;
        admitted?.Invoke();
        token.ThrowIfCancellationRequested();
    }
}
