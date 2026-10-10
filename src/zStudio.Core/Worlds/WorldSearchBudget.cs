namespace Recoil.Zbd.Core.Worlds;

/// <summary>
/// What one world build may spend finding and reading its inputs, across all of its scripts and loads. Every existence
/// probe counts: each model candidate a <c>LoadGameGen</c> tries (search folders × .gltf/.glb), each texture folder a textured
/// material tries, each folder a directory command tests, and each script, model and referenced file looked up. So do the
/// bytes of every model file, buffer and terrain recipe read; a load reads its files again, declared but unused buffers
/// included. The retail missions stay far below both (see <see cref="MaximumProbes"/>, <see cref="MaximumModelBytes"/>).
/// </summary>
internal sealed class WorldSearchBudget(long maximumProbes = WorldSearchBudget.MaximumProbes, long maximumModelBytes = WorldSearchBudget.MaximumModelBytes)
{
    /// <summary>
    /// The probes of one build: 131,072, about 28 times the most a retail mission makes (4,710, 1999 m6). Through a source
    /// export's snapshot a probe of a folder deep in the project costs up to about 0.13 ms, so a build refused here has
    /// spent well under half a minute.
    /// </summary>
    internal const long MaximumProbes = 1L << 17;
    /// <summary>
    /// The model bytes one build reads: 1 GiB, about 87 times the most a retail mission reads (12.3 MB, 1999 m6), and room
    /// for two files of the largest size one read admits.
    /// </summary>
    internal const long MaximumModelBytes = 1L << 30;
    private readonly long probeLimit = maximumProbes is >= 0 and <= MaximumProbes ? maximumProbes : throw new ArgumentOutOfRangeException(nameof(maximumProbes));
    private readonly long byteLimit = maximumModelBytes is >= 0 and <= MaximumModelBytes ? maximumModelBytes : throw new ArgumentOutOfRangeException(nameof(maximumModelBytes));
    internal long Probes { get; private set; }
    internal long ModelBytes { get; private set; }
    /// <summary>The model bytes the build may still read.</summary>
    internal long ModelBytesLeft => byteLimit - ModelBytes;

    /// <summary>Charges one existence probe before it is made.</summary>
    internal void Probe()
    {
        if (Probes >= probeLimit)
            throw new InvalidDataException($"The world's build looks for files more than {probeLimit:N0} times (each load tries every model folder, each textured material every texture folder). Name fewer model and texture folders, load fewer models, or split the mission.");
        Probes++;
    }

    /// <summary>Charges bytes read; a provider that ignored the read limit is refused here.</summary>
    internal void Read(string path, long bytes)
    {
        if (bytes < 0 || bytes > ModelBytesLeft) throw Exceeded(path, null);
        ModelBytes += bytes;
    }

    /// <summary>The refusal of a read that would go past what the build may read.</summary>
    internal InvalidDataException Exceeded(string path, Exception? inner) => new(
        $"{JsonData.ShownText(path)} cannot be read within the {byteLimit / (1024 * 1024):N0} MiB of models, buffers and terrain one world's build reads in total ({ModelBytes / (1024 * 1024):N0} MiB read before it; every load reads its files again, unused buffers included). Load fewer or smaller models, remove unused buffers, or split the mission.{(inner == null ? "" : " " + inner.Message)}",
        inner);
}
