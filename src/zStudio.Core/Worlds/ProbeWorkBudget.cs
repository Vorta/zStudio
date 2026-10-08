namespace Recoil.Zbd.Core.Worlds;

/// <summary>One allowance shared by every node and polygon occurrence in a probe or comparison.</summary>
internal sealed class ProbeWorkBudget(long remaining, CancellationToken token, string limitation)
{
    public const long MaximumProbeWork = 4 * 1024 * 1024;

    public void Take(long count)
    {
        token.ThrowIfCancellationRequested();
        if (count < 0 || count > remaining) throw new ProbeWorkLimitException(limitation);
        remaining -= count;
    }
}

internal sealed class ProbeWorkLimitException(string message) : Exception(message);
