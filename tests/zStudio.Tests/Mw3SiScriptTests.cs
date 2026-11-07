using System.Text;
using Xunit;
using zStudio.Core.Animation;

namespace zStudio.Tests;

public sealed class Mw3SiScriptTests
{
    [Fact]
    public void NativeRepeatedTerminalFrameRetainsItsNegativeZeroSegment()
    {
        // Retail ts2 train scripts finish with two poses at frame1014 (SIlabel1015), rate15.
        var token = TestContext.Current.CancellationToken;
        string pose = "Object: engine\nScaling: 1 1 1\nRotation: 0 0 0\nTranslation: 0 0 0\n";
        string input = $"SI Animation Script\nFRAMES: 4\nOBJECTS: 1\nFrame: 1\n{pose}Frame: 4\n{pose}Frame: 1015\n{pose}Frame: 1015\n{pose}";
        var parsed = SiAnimationScript.Parse(Encoding.Latin1.GetBytes(input), "train", token);
        var frames = SiAnimationScript.Compile(parsed, "engine", 15, "train", token, new(token), native: true);
        Assert.Equal(3, frames.Count);
        Assert.Equal(frames[2].Start, frames[2].End);
        Assert.All(frames, frame => Assert.Equal(7, frame.Flags));
        foreach (int channel in new[] { 0, 1, 2 })
            for (int axis = 0; axis < 3; axis++) Assert.Equal(0x80000000u, SiMath.Bits(frames[2].F32(frames[2].ChannelOffset(channel) + 16 + axis * 4)));
        string output = SiScriptWriter.Write([new("engine", frames, 15)], new(null, Native: true), token);
        Assert.Equal(2, output.Split("Frame: 1015").Length - 1);
        Assert.Equal(2, SiAnimationScript.Compile(parsed, "engine", 15, "train", token).Count);
    }

    [Fact]
    public void NativeSixDecimalComparisonAcceptsRetailRoundingAndRejectsMaterialDrift()
    {
        // The same dmill segment in different retail builds retains identical quaternion/time operands.
        var from = new SiMath.Quat(BitConverter.UInt32BitsToSingle(0x3F7FEF53), BitConverter.UInt32BitsToSingle(0xBCB5A4F8),
            BitConverter.UInt32BitsToSingle(0x38C0B4BC), BitConverter.UInt32BitsToSingle(0xBB87C29E));
        var to = new SiMath.Quat(BitConverter.UInt32BitsToSingle(0x3F7FDDAD), BitConverter.UInt32BitsToSingle(0xBCB62E4B),
            BitConverter.UInt32BitsToSingle(0x3CBE4371), BitConverter.UInt32BitsToSingle(0xBB6D918D));
        Assert.Equal(3097092155u, SiMath.Bits(Mw3SiMath.Spin(from, to, 1, 0, 3).X));
        var expected = AnimationKeyframe.Create(2); expected.Start = 1f / 3; expected.End = 0;
        int at = expected.ChannelOffset(1); expected.SetFloat(at + 16, BitConverter.UInt32BitsToSingle(3097092156u));
        var rebuilt = new AnimationKeyframe(expected.Bytes.ToArray()); rebuilt.SetFloat(at + 16, Mw3SiMath.Spin(from, to, 1, 0, 3).X);
        Assert.False(SiScriptWriter.Same(expected, rebuilt));
        Assert.True(Mw3SiMath.Equivalent(expected, rebuilt));
        rebuilt.SetFloat(at + 16, expected.F32(at + 16) + 0.001f);
        Assert.False(Mw3SiMath.Equivalent(expected, rebuilt));
        rebuilt = new(expected.Bytes.ToArray()); rebuilt.End = 0.001f;
        Assert.False(Mw3SiMath.Equivalent(expected, rebuilt));
    }

    [Fact]
    public void RetailHeldChannelEndUsesTheLaterKnownPoseWithinATinyBudget()
    {
        // tweed tw@299->300 ends a rotation channel before the next key listing rotation, at314.
        var token = TestContext.Current.CancellationToken;
        var start = Mw3SiRotationSolver.Key(new(0.3764719f, -0.24553996f, 0.104190946f, -0.8871997f), new(), token)!.Value;
        var next = Mw3SiRotationSolver.Key(new(0.96377087f, 1.1431839E-06f, 0.26673144f, 2.3006285E-06f), new(), token)!.Value;
        var spin = (4.198945f, 2.1909862E-06f, 7.004894f);
        Assert.Equal(next, Mw3SiRotationSolver.End(start, spin, 299, 300, 7, new(2), token, next));
    }

    [Theory]
    [InlineData(0.375512, 0.397711, 19.643795, 0.9023796f, 0.09367558f, 0.24974117f, 0.33847496f)]
    [InlineData(-0.058280, -0.537156, 97.502907, 0.0469605f, -0.26641455f, 0.012996776f, -0.96262616f)]
    public void NativeWindingAnglesRecoverRetailQuaternionOperands(double a, double b, double g, float w, float x, float y, float z)
        => Assert.True(Mw3SiMath.CompileRotation(a, b, g).BitEquals(new(w, x, y, z)));

    [Fact]
    public void NativeReversedHeldRotationRetainsNegativeZeroRates()
    {
        // dmill larm frame1 ->frame0; native stores negativezero for all three spin operands.
        var rotation = new SiMath.Quat(0.9635758f, -0.25942767f, 0.016886586f, -0.06272078f);
        var spin = Mw3SiMath.Spin(rotation, rotation, 1, 0, 3);
        Assert.Equal(0x80000000u, SiMath.Bits(spin.X));
        Assert.Equal(0x80000000u, SiMath.Bits(spin.Y));
        Assert.Equal(0x80000000u, SiMath.Bits(spin.Z));
        Assert.Equal(0u, SiMath.Bits(SiMath.Spin(rotation, rotation, 1, 0, 3).X));
    }

    [Fact]
    public void NativeHalfTurnUsesTheRetailQuaternionHemisphere()
    {
        // c1 tweed, tw at frame9. The inverse retains the quaternion hemisphere before six-decimal formatting.
        var got = Mw3SiMath.CompileRotation(-0.0018850326f, -0.539997f, -3.1379251f);
        var expected = new SiMath.Quat(0.0015158966f, -0.26673108f, 0.0004192458f, -0.96376973f);
        Assert.True(got.BitEquals(expected));
        Assert.False(SiMath.CompileRotation(-0.0018850326, -0.539997, -3.1379251).BitEquals(expected));
    }

    [Fact]
    public void NativeSiWriterUsesSixDecimalsAndOriginalLayoutWithinSourceTolerance()
    {
        var token = TestContext.Current.CancellationToken;
        var script = SiAnimationScript.Parse(Encoding.Latin1.GetBytes("""
            SI Animation Script
            FRAMES: 2
            OBJECTS: 1

            Frame: 1
            Object: tw
            Scaling:     1 1 1
            Rotation:    -0.0018850326 -0.539997 -3.1379251
            Translation: -0.0 0.000000123456 3

            Frame: 2
            Object: tw
            Scaling:     1 1 1
            Rotation:    -0.0018850326 -0.539997 -3.1379251
            Translation: -0.0 0.000000123456 3
            """), "native", token);
        var frames = SiAnimationScript.Compile(script, "tw", 7, "native", token, new(token), native: true);
        string text = SiScriptWriter.Write([new("tw", frames, 7)], new(null, Native: true), token);
        Assert.StartsWith("SI Animation Script\r\nFRAMES: 2\r\nOBJECTS: 1\r\n\r\nFrame: 1\r\nObject: tw\r\nScaling:     ", text);
        Assert.Contains("\r\n\r\nFrame: 2\r\n", text);
        Assert.Contains("-0.000000", text);
        Assert.DoesNotContain("RECOIL", text);
        var compiled = SiAnimationScript.Compile(SiAnimationScript.Parse(Encoding.Latin1.GetBytes(text), "native", token), "tw", 7, "native", token, new(token), native: true);
        Assert.Equal(frames.Count, compiled.Count);
        Assert.All(frames.Zip(compiled), p => Assert.True(Mw3SiMath.Equivalent(p.First, p.Second)));
        Assert.Contains("Translation: -0.000000 0.000000 3.000000", text);
        Assert.Matches(@"Rotation:    -?\d+\.\d{6} -?\d+\.\d{6} -?\d+\.\d{6}\r\n", text);
        Assert.Contains(frames.Zip(compiled), p => !SiScriptWriter.Same(p.First, p.Second));
    }
}
