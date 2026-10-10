using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Sources;

namespace Recoil.Zbd.Core.Worlds;

/// <summary>Admission required before a provider allocates, hashes or copies a project file's payload.</summary>
public readonly record struct ProjectReadLimits
{
    private enum ContentKind { Bytes, Resource, Model }
    private readonly ContentKind kind;
    private readonly int typedMaximum;
    public long MaximumBytes { get; }
    internal bool RequiresPrefix => kind != ContentKind.Bytes;

    private ProjectReadLimits(long maximumBytes, ContentKind kind, int typedMaximum)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(typedMaximum);
        MaximumBytes = Math.Min(maximumBytes, FormatRegistry.MaximumDocumentBytes);
        this.kind = kind;
        this.typedMaximum = typedMaximum;
    }

    public static ProjectReadLimits Document => Bytes(FormatRegistry.MaximumDocumentBytes);
    public static ProjectReadLimits Bytes(long maximumBytes) => new(maximumBytes, ContentKind.Bytes, 0);
    public static ProjectReadLimits Text(long maximumBytes = SourceProject.MaximumSourceTextBytes) =>
        Bytes(Math.Min(maximumBytes, SourceProject.MaximumSourceTextBytes));
    public static ProjectReadLimits Resource(long maximumBytes = FormatRegistry.MaximumDocumentBytes, int maximumTextBytes = SourceProject.MaximumSourceTextBytes) =>
        new(maximumBytes, ContentKind.Resource, Math.Min(maximumTextBytes, SourceProject.MaximumSourceTextBytes));
    public static ProjectReadLimits Model(long maximumBytes = FormatRegistry.MaximumDocumentBytes, int maximumJsonBytes = GltfDocument.MaximumJsonBytes) =>
        new(maximumBytes, ContentKind.Model, Math.Min(maximumJsonBytes, GltfDocument.MaximumJsonBytes));

    /// <summary>Intersect with an operation's remaining allowance without relaxing its structural rule.</summary>
    public ProjectReadLimits WithMaximum(long maximumBytes) => new(Math.Min(MaximumBytes, maximumBytes), kind, typedMaximum);

    /// <summary>Validate already owned bytes before any clone, hash, decode or parser materialization.</summary>
    public void Validate(ReadOnlySpan<byte> bytes) => CheckPrefix(bytes[..Math.Min(20, bytes.Length)], bytes.Length);

    /// <summary>Validate the first min(20,length) bytes read through the same held handle as the eventual payload.</summary>
    public void CheckPrefix(ReadOnlySpan<byte> prefix, long length)
    {
        if (length < 0 || length > MaximumBytes || length > Array.MaxLength)
            throw new InvalidDataException($"Project input exceeds this operation's {MaximumBytes:N0}-byte limit.");
        if (RequiresPrefix && prefix.Length < Math.Min(20, length))
            throw new InvalidDataException("Project input admission needs its complete bounded header.");
        switch (kind)
        {
            case ContentKind.Resource when ZrdText.LooksLikeText(prefix) && length > typedMaximum:
                throw new InvalidDataException($"Resource text exceeds its {typedMaximum:N0}-byte limit; simplify the source before retrying.");
            case ContentKind.Model:
                GltfDocument.CheckContainerAdmission(prefix, length, typedMaximum);
                break;
        }
    }
}
