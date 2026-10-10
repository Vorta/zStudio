using System.Buffers.Binary;
using System.Text;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class AnimationTextureOperandWorkTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly string[][] Before = [["FindNode", "n1"], ["CycleTextureSetOn", "1"], ["CycleTextureSetMap", "before"]];
    private static readonly string[][] After = [["CycleTextureSetOn", "1"], ["CycleTextureSetMap", "after"]];

    [Fact]
    public void ReaderAdmittedRepeatedMissingIncludesStopBeforeUnboundedNormalization()
    {
        string operand = new string('x', 65_535) + "/";
        byte[] archive = Archive([.. Before, .. Enumerable.Repeat(new[] { "source", "repeat" }, 1000), .. After], [["source", operand]]);
        byte[] original = archive.ToArray();
        var document = FormatRegistry.Default.OpenBytes("interp.zbd", archive, token: Token);
        Assert.Empty(document.Diagnostics);
        Assert.NotNull(document.Scripts);
        Assert.Equal(2, document.Assets.Count);
        var scripts = document.Assets.ToDictionary(a => a.Name, a => Assert.IsType<ScriptContent>(a.Content), StringComparer.OrdinalIgnoreCase);
        var context = Context();
        long before = GC.GetAllocatedBytesForCurrentThread();
        context.ReadTextureScript("mission", scripts, Token);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Contains("script operands exceed", Assert.Single(context.Diagnostics));
        Assert.InRange(allocated, 0, 40 * 1024 * 1024); // Previously about131MB for this admitted fixture.
        Assert.Equal("before", context.MaterialCycles[0].At(0));
        Assert.Equal(operand, scripts["repeat"].Instructions[0][1]);
        Assert.Equal(original, archive);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("CycleTextureSetOn")]
    [InlineData("CycleTextureSetSpeed")]
    [InlineData("CycleTextureSetMap")]
    [InlineData("CycleTextureSetLooping")]
    [InlineData("FindSubNode")]
    public void RepeatedAuthoredOperandsShareOnePreDispatchLimit(string command)
    {
        var context = Context();
        // Leading zeros remain valid numeric text; the limit must precede int/float parsing, not depend on failure.
        string operand = command == "source" ? new string('x', 999) + "/" : new string('0', 999) + "1";
        var scripts = Scripts([.. Before, .. Enumerable.Repeat(new[] { "source", "repeat" }, 20), .. After], [[command, operand]]);
        context.ReadTextureScript("mission", scripts, Token, LookupWorkBudget.MaximumUnits, maximumOperandCharacters: 4096);
        Assert.Contains("script operands exceed", Assert.Single(context.Diagnostics));
        Assert.Equal("before", context.MaterialCycles[0].At(0));
        Assert.Equal(operand, scripts["repeat"].Instructions[0][1]);
        context.ReadTextureScript("mission", Scripts([.. Before, .. After], []), Token);
        Assert.Equal("after", context.MaterialCycles[0].At(0));
    }

    [Fact]
    public void HugeSourceOperandIsRefusedBeforeTheFirstNormalizedCopy()
    {
        var context = Context();
        var scripts = Scripts([["source", new string('x', 200_000) + "/"]], []);
        long before = GC.GetAllocatedBytesForCurrentThread();
        context.ReadTextureScript("mission", scripts, Token, LookupWorkBudget.MaximumUnits, maximumOperandCharacters: 1024);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 32 * 1024);
        Assert.Contains("script operands exceed", Assert.Single(context.Diagnostics));
        Assert.Empty(context.MaterialCycles);
    }

    [Fact]
    public void SourceHashPassesAreChargedEvenWhenNormalizationDoesNotAllocate()
    {
        var context = Context();
        var scripts = Scripts([["source", new string('x', 300)]], []);
        // The line alone fits1024 units, but line plus four source-path passes does not.
        context.ReadTextureScript("mission", scripts, Token, LookupWorkBudget.MaximumUnits, maximumOperandCharacters: 1024);
        Assert.Contains("script operands exceed", Assert.Single(context.Diagnostics));
    }

    [Fact]
    public void IncludeAndMapIdentitiesStayCompleteAndNumericSettingsRetainTheirSemantics()
    {
        var context = Context();
        string prefix = new string('p', 4096), first = prefix + "/first", second = prefix + "/second";
        string map = new string('m', 4096) + " final";
        var scripts = Scripts([["source", first], ["source", second]], []);
        scripts[first.Replace('/', '\\')] = new([.. Before], "");
        scripts[second.Replace('/', '\\')] = new([["CycleTextureSetOn", "0001"], ["CycleTextureSetMap", map], ["CycleTextureSetSpeed", "0002.5"], ["CycleTextureSetLooping", "true"]], "");
        context.ReadTextureScript("mission", scripts, Token);
        Assert.Empty(context.Diagnostics);
        var cycle = context.MaterialCycles[0];
        Assert.Equal(map, cycle.At(0));
        Assert.Equal(2.5f, cycle.Speed);
        Assert.True(cycle.Loop);
    }

    [Fact]
    public void IgnoredExtraOperandsAreBoundedAndCancellationPropagatesBeforeNextInstruction()
    {
        var context = Context();
        context.ReadTextureScript("mission", Scripts([["unknown", "short", new string('x', 10_000)]], []), Token,
            LookupWorkBudget.MaximumUnits, maximumOperandCharacters: 1024);
        Assert.Contains("script operands exceed", Assert.Single(context.Diagnostics));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        CancellingScripts scripts = new(cancel) { ["mission"] = new([["source", "repeat"]], ""), ["repeat"] = new([.. Before], "") };
        Assert.ThrowsAny<OperationCanceledException>(() => context.ReadTextureScript("mission", scripts, cancel.Token));
        Assert.Empty(context.MaterialCycles);
        context.ReadTextureScript("mission", Scripts([.. Before], []), Token);
        Assert.Equal("before", context.MaterialCycles[0].At(0));
    }

    private sealed class CancellingScripts(CancellationTokenSource cancel) : Dictionary<string, ScriptContent>, IReadOnlyDictionary<string, ScriptContent>
    {
        bool IReadOnlyDictionary<string, ScriptContent>.TryGetValue(string key, out ScriptContent value)
        {
            if (key == "repeat") cancel.Cancel();
            return TryGetValue(key, out value!);
        }
    }

    private static Dictionary<string, ScriptContent> Scripts(string[][] main, string[][] repeat) => new(StringComparer.OrdinalIgnoreCase)
    { ["mission"] = new(main, ""), ["repeat"] = new(repeat, "") };

    private static AnimationPreviewContext Context()
    {
        GameScene scene = new();
        scene.Nodes.Add(new(0, "world", "world", null, [], [1], new(), new()));
        scene.Nodes.Add(new(1, "n1", "object3d", 0, [0], [], new(), new()));
        scene.Models.Add(new(0, [], [], [], [new(0, 0, [], [], [], [])], new()));
        var world = new ZbdDocument("world.zbd", new(0, DateTime.MinValue), new(FormatFamily.GameZ, 15, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = scene };
        return new() { Package = new() { Prefix = [], Tail = [] }, World = world };
    }

    private static byte[] Archive(string[][] main, string[][] repeat)
    {
        byte[] first = Block(main), second = Block(repeat);
        byte[] bytes = new byte[268 + first.Length + second.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x08971119);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 7);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 2);
        Encoding.Latin1.GetBytes("mission", bytes.AsSpan(12));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(136), 268);
        Encoding.Latin1.GetBytes("repeat", bytes.AsSpan(140));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(264), 268 + first.Length);
        first.CopyTo(bytes, 268); second.CopyTo(bytes, 268 + first.Length);
        return bytes;
    }

    private static byte[] Block(string[][] commands)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        foreach (var args in commands)
        {
            writer.Write(args.Sum(a => a.Length + 1)); writer.Write(args.Length);
            foreach (string arg in args) { writer.Write(Encoding.Latin1.GetBytes(arg)); writer.Write((byte)0); }
        }
        writer.Write(0); return stream.ToArray();
    }
}
