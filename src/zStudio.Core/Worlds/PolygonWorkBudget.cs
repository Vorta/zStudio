namespace Recoil.Zbd.Core.Worlds;

/// <summary>One allowance for polygon reconstruction across a load, including every referenced mesh and surface.</summary>
internal sealed class PolygonWorkBudget(CancellationToken token = default, long maximumWork = 32_000_000)
{
    private long remaining = maximumWork;
    internal void Charge(long work)
    {
        token.ThrowIfCancellationRequested();
        if (work < 0 || work > remaining)
            throw new InvalidDataException("Polygon reconstruction exceeds its work limit; split the mesh or retain its polygon records before importing.");
        remaining -= work;
    }
    internal void CheckCancellation() => token.ThrowIfCancellationRequested();
}
