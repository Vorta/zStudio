namespace Recoil.Zbd.Core.Sources;

/// <summary>Additional retained reconstruction data, beyond the decoded input folders' own budget.</summary>
internal sealed class ReconstructionBudget(long maximum = ReconstructionBudget.MaximumBytes)
{
    internal const long MaximumBytes = 512L << 20;
    private long retained;

    // Script writers may finish on several worker threads. Charge before publishing into their shared result array.
    internal void Retain(long bytes)
    {
        if (bytes < 0 || Interlocked.Add(ref retained, bytes) > maximum)
            throw new IOException($"Reconstructed source data exceeds its {maximum:N0}-byte memory limit. Reconstruct from the original game files.");
    }
}
