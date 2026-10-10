using System.IO;
using System.Text;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class AnimationSourceAdmissionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Root = "data/m1/zrdr/anim.zad", Child = "data/m1/zrdr/child.zad", Script = "data/m1/zrdr/move.zan";
    private static byte[] Bytes(string text) => Encoding.ASCII.GetBytes(text);

    // Represents a held header/length before reading a payload. An oversized virtual input has no large array.
    private sealed class Files(Dictionary<string, byte[]> data) : IProjectFiles
    {
        internal Dictionary<string, long> Lengths { get; } = new(StringComparer.Ordinal);
        internal List<(string Path, ProjectReadLimits Limits)> Requests { get; } = [];
        internal int Materialized { get; private set; }
        public bool Exists(string relative) => data.ContainsKey(relative);
        public byte[] Read(string relative, CancellationToken token, ProjectReadLimits limits)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add((relative, limits));
            byte[] source = data[relative];
            limits.CheckPrefix(source.AsSpan(0, Math.Min(20, source.Length)), Lengths.GetValueOrDefault(relative, source.Length));
            Materialized++;
            return source.ToArray();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RootAndStandaloneDefinitionAdmissionPrecedesPayloadMaterialization(bool standalone)
    {
        Files files = new(new() { [Root] = Bytes("ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ) )") });
        files.Lengths[Root] = SourceProject.MaximumSourceTextBytes + 1L;
        Assert.Throws<InvalidDataException>(() =>
        {
            if (standalone) _ = AnimationDefinitionSet.Read(files, Root, Token);
            else _ = AnimationDefinitionSet.Load(files, Root, Token);
        });
        Assert.Equal(SourceProject.MaximumSourceTextBytes, Assert.Single(files.Requests).Limits.MaximumBytes);
        Assert.Equal(0, files.Materialized);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IncludedSourcesReceiveExactRemainingAllowance(bool compiledChild)
    {
        byte[] root = Bytes("ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION_FILE ( child.zad ) ) )");
        byte[] child = Bytes("ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION ( NAME ( gate ) ) ) )");
        if (compiledChild) child = ZrdWriter.Write(ZrdText.Parse(child, Token), Token);
        Files Ref() => new(new() { [Root] = root, [Child] = child });
        var refused = Ref();
        Assert.Throws<InvalidDataException>(() => AnimationDefinitionSet.Load(refused, Root, root.Length + child.Length - 1,
            AnimationDefinitionSet.MaximumSourceNodes, Token));
        Assert.Equal(child.Length - 1, refused.Requests[^1].Limits.MaximumBytes);
        Assert.Equal(1, refused.Materialized);
        var accepted = Ref();
        var set = AnimationDefinitionSet.Load(accepted, Root, root.Length + child.Length, AnimationDefinitionSet.MaximumSourceNodes, Token);
        Assert.Equal("gate", Assert.Single(set.Definitions).Item.TextOf("NAME"));
        Assert.Equal(child.Length, accepted.Requests[^1].Limits.MaximumBytes);
        Assert.Equal(2, accepted.Materialized);
        Assert.NotNull(AnimationDefinitionSet.Read(Ref(), Child, Token));
    }

    [Fact]
    public void KeyframeReadsAndRepeatedReadsShareTheDefinitionAllowance()
    {
        byte[] root = Bytes("ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ) )"), script = Bytes("# already bounded source\n");
        Files files = new(new() { [Root] = root, [Script] = script });
        var set = AnimationDefinitionSet.Load(files, Root, root.Length + script.Length, AnimationDefinitionSet.MaximumSourceNodes, Token);
        Assert.Equal(script, set.ReadScript("move.zan", Root)!.Value.Bytes);
        Assert.Equal(script.Length, files.Requests[^1].Limits.MaximumBytes);
        Assert.Throws<InvalidDataException>(() => set.ReadScript("move.zan", Root));
        Assert.Equal(0, files.Requests[^1].Limits.MaximumBytes);
        Assert.Equal(2, files.Materialized);
    }

    [Fact]
    public void ReconstructionWrappersNarrowBeforeReadAndValidateOwnedOverlayWithoutCloning()
    {
        byte[] text = Bytes("VALUE ( 1 )"), binary = ZrdWriter.Write(ZrdText.Parse(text, Token), Token);
        Files files = new(new() { [Root] = text });
        var work = new AnimationSources.RepairWork(text.Length - 1);
        var repair = new AnimationSources.RepairFiles(files, work);
        Assert.Contains("definition-repair work limit", Assert.Throws<IOException>(() => repair.Read(Root, Token, ProjectReadLimits.Resource())).Message);
        Assert.Equal(text.Length - 1, Assert.Single(files.Requests).Limits.MaximumBytes);
        Assert.Equal(0, files.Materialized); Assert.Equal(text.Length - 1, work.Remaining);

        var overlay = new AnimationSources.Overlay(files, new ReconstructionBudget());
        overlay.Write(Root, "VALUE ( 1 )", Encoding.ASCII);
        byte[] owned = overlay.Written[Root];
        Assert.Throws<InvalidDataException>(() => overlay.Read(Root, Token, ProjectReadLimits.Text(owned.Length - 1)));
        Assert.Same(owned, overlay.Read(Root, Token, ProjectReadLimits.Text(owned.Length)));
        Assert.Equal(0, files.Materialized);
        // WithMaximum must preserve structural admission, including binary-positive/text-negative behavior.
        overlay.Written[Root] = binary;
        var resource = ProjectReadLimits.Resource(binary.Length, maximumTextBytes: 1);
        var binaryRepair = new AnimationSources.RepairFiles(overlay, new(binary.Length));
        Assert.Same(binary, binaryRepair.Read(Root, Token, resource));
        overlay.Written[Root] = text;
        Assert.Throws<InvalidDataException>(() => overlay.Read(Root, Token, resource));
        var typedRepair = new AnimationSources.RepairFiles(overlay, new(binary.Length));
        Assert.Contains("Resource text", Assert.Throws<InvalidDataException>(() => typedRepair.Read(Root, Token, resource)).Message);
    }

    [Fact]
    public void CancellationBeforeAdmissionLeavesProvidersAndOverlayUntouched()
    {
        Files files = new(new() { [Root] = Bytes("ANIMATION_DEFINITIONS ( )") });
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => AnimationDefinitionSet.Load(files, Root, canceled.Token));
        var overlay = new AnimationSources.Overlay(files, new ReconstructionBudget());
        overlay.Write(Root, "VALUE ( 1 )", Encoding.ASCII);
        byte[] owned = overlay.Written[Root];
        var work = new AnimationSources.RepairWork(owned.Length);
        var repair = new AnimationSources.RepairFiles(overlay, work);
        Assert.ThrowsAny<OperationCanceledException>(() => repair.Read(Root, canceled.Token, ProjectReadLimits.Document));
        Assert.Empty(files.Requests); Assert.Equal(0, files.Materialized);
        Assert.Equal(owned.Length, work.Remaining); Assert.Same(owned, overlay.Written[Root]);
    }
}
