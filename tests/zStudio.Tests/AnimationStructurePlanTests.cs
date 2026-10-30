using System.Text;
using System.Numerics;
using zStudio.Core.Animation;
using zStudio.Core.Formats;
using zStudio.Core.Sources;
using Xunit;

namespace zStudio.Tests;

public sealed class AnimationStructurePlanTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static AnimationCompiler.Evaluation Evaluate(VtolDropFixture.MemoryFiles files, bool provenance = true) =>
        AnimationCompiler.Evaluate(files, VtolDropFixture.Root, ["ground"], null,
            FormatRegistry.MaximumDocumentBytes, Token, captureSourceBindings: provenance);

    [Fact]
    public void ProvenanceUsesExactSourceFieldsWithoutChangingCompiledBytes()
    {
        var files = VtolDropFixture.Files();
        files.Content[VtolDropFixture.Root] = Encoding.Latin1.GetBytes(VtolDropFixture.Definitions()
            .Replace("NAME ( route )", "NAME ( earlier ) NAME ( route ) START_TIME ( EVENT_OFFSET 9 ) IGNORED_ITEM ( VALUE ( 7 ) )", StringComparison.Ordinal));
        var captured = Evaluate(files);
        Assert.Null(captured.EngineRejection);
        Assert.Equal(AnimationWriter.Write(Evaluate(files, false).Package, Token), AnimationWriter.Write(captured.Package, Token));
        var provenance = Assert.IsType<AnimationSourceProvenance>(captured.Provenance);
        var plan = AnimationStructurePlan.Create(captured.Package, provenance);
        var editable = SourceAnimationEdits.Read(files, VtolDropFixture.Root, 0, Token);
        var sequences = plan.Sequences(1, token: Token).Rows;
        Assert.NotNull(sequences[0].Source!.ReadOnlyReason);
        Assert.Null(sequences[0].Source!.RecordId);
        var route = sequences.Single(s => s.Name == "route");
        Assert.True(route.Source!.IgnoredStartTime);
        Assert.Contains(editable.Records, r => r.Id == route.Source.RecordId);
        var ignored = Assert.Single(plan.Ignored(1, route.Id, token: Token).Rows);
        Assert.Equal("IGNORED_ITEM", ignored.Kind);
        Assert.Contains(editable.Records, r => r.Id == ignored.RecordId && r.Fields.All(f => f.ReadOnly != null));
        foreach (var sequence in sequences.Skip(1))
            foreach (var ev in plan.Events(1, sequence.Id, token: Token).Rows)
            {
                var source = Assert.IsType<AnimationEventSource>(ev.Source);
                var record = Assert.Single(editable.Records, r => r.Id == source.RecordId);
                var field = Assert.Single(record.Fields, f => f.Id == source.TimingFieldId);
                Assert.Equal(source.TimingPresent, field.Present);
                if (!field.Present) { Assert.EndsWith("/@START_TIME", field.Id); Assert.Equal(1, ev.Clock); Assert.Equal(0, ev.Threshold); }
            }
        var cloned = new AnimationPackage { Prefix = captured.Package.Prefix, Tail = captured.Package.Tail };
        cloned.Entries.AddRange(captured.Package.Entries.Select(e => e.Clone()));
        var copy = AnimationStructurePlan.Create(cloned, provenance).Events(1, route.Id, token: Token).Rows;
        Assert.All(copy, e => Assert.NotNull(e.Source));
    }

    [Fact]
    public void StructuralCallsAndKeyframesArePagedWithoutExecution()
    {
        var evaluated = Evaluate(VtolDropFixture.Files());
        var plan = AnimationStructurePlan.Create(evaluated.Package, evaluated.Provenance);
        var calls = plan.Calls(1, offset: 0, limit: 1, token: Token);
        Assert.Equal(2, calls.Total);
        Assert.Equal(1, calls.NextOffset);
        var call = Assert.Single(calls.Rows);
        Assert.Equal(2, call.TargetEntry);
        Assert.Equal(AnimationExecutionPolicy.ChildPath("", 1, call.Event, 2), call.Path);
        var childCall = Assert.Single(plan.Calls(1, call.Path, 1, 1, Token).Rows);
        Assert.StartsWith(call.Path + "/", childCall.Path);
        var route = plan.Sequences(1, token: Token).Rows.Single(s => s.Name == "route");
        var motion = plan.Events(1, route.Id, token: Token).Rows.Single(e => e.Type == 12);
        Assert.Equal(10, motion.Source!.ScriptFrameRate);
        var samples = plan.Keyframes(1, route.Id, motion.Id, limit: 1, token: Token);
        Assert.Equal(motion.KeyframeCount, samples.Total);
        Assert.Single(samples.Rows);
        Assert.Equal(1, samples.NextOffset);
        Assert.NotNull(samples.Rows[0].Position);
        Assert.Equal(0, samples.Rows[0].Start);
        Assert.Throws<ArgumentOutOfRangeException>(() => plan.Events(1, route.Id, limit: 101, token: Token));
        Assert.Empty(plan.Events(1, route.Id, offset: int.MaxValue, token: Token).Rows);
        Assert.Null(plan.Events(1, route.Id, offset: int.MaxValue, token: Token).NextOffset);
    }

    [Fact]
    public void KeyframeMetadataPreservesQuaternionComponentsAndSampleOrder()
    {
        var evaluated = Evaluate(VtolDropFixture.Files());
        var sequence = evaluated.Package.Entries[1].Sequences[0];
        int motion = sequence.Events.FindIndex(e => e.Type == 12);
        var identity = AnimationKeyframe.Create(2);
        var rotated = AnimationKeyframe.Create(2);
        int offset = rotated.ChannelOffset(1);
        rotated.SetFloat(offset, .5f);
        rotated.SetFloat(offset + 4, -.5f);
        rotated.SetFloat(offset + 8, .5f);
        rotated.SetFloat(offset + 12, -.5f);
        sequence.Events[motion] = sequence.Events[motion].WithKeyframes([identity, rotated]);
        var samples = AnimationStructurePlan.Create(evaluated.Package, evaluated.Provenance)
            .Keyframes(1, sequence.Id, sequence.Events[motion].Id, token: Token).Rows;
        Assert.Equal(Quaternion.Identity, samples[0].Rotation);
        Assert.Equal(new Quaternion(-.5f, .5f, -.5f, .5f), samples[1].Rotation);
        Assert.Equal(0, samples[0].Start);
        Assert.Equal(0, samples[1].Start);
    }

    [Fact]
    public void WildcardBindingsShareSourceAddressesButKeepSeparateEventIdentities()
    {
        const string source = """
            ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION (
              NAME ( "crate_*" ) ANIMATION_NAME ( fall ) ACTIVATION ( ON_CALL )
              RESET_STATE ( CALLBACK ( VALUE ( 1 ) ) )
              SEQUENCE_DEFINITION ( NAME ( fall ) CALLBACK ( VALUE ( 2 ) START_TIME ( EVENT_OFFSET 0.25 ) ) )
            ) ) )
            """;
        var files = new VtolDropFixture.MemoryFiles(new(StringComparer.OrdinalIgnoreCase)
        { [VtolDropFixture.Root] = Encoding.Latin1.GetBytes(source) });
        var evaluated = AnimationCompiler.Evaluate(files, VtolDropFixture.Root, ["crate_1", "crate_2"], null,
            FormatRegistry.MaximumDocumentBytes, Token, captureSourceBindings: true);
        var plan = AnimationStructurePlan.Create(evaluated.Package, evaluated.Provenance);
        var first = plan.Sequences(1, token: Token).Rows;
        var second = plan.Sequences(2, token: Token).Rows;
        Assert.Equal(plan.Entry(1).Name, plan.Entry(2).Name);
        Assert.NotEqual(plan.Entry(1).Root, plan.Entry(2).Root);
        var cleanup = Assert.Single(plan.Events(1, first[0].Id, token: Token).Rows);
        Assert.True(first[0].Cleanup);
        Assert.NotNull(cleanup.Source!.RecordId);
        var one = Assert.Single(plan.Events(1, first[1].Id, token: Token).Rows);
        var two = Assert.Single(plan.Events(2, second[1].Id, token: Token).Rows);
        Assert.NotEqual(one.Id, two.Id);
        Assert.Equal(one.Source!.RecordId, two.Source!.RecordId);
        Assert.Equal(one.Source.Definition, two.Source.Definition);
        Assert.Equal(3, one.Clock);
        Assert.Equal(0.25f, one.Threshold);
    }
}
