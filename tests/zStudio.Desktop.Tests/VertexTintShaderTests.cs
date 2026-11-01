using System.Numerics;
using zStudio.Rendering.Direct3D;
using Xunit;

namespace zStudio.Desktop.Tests;

public sealed class VertexTintShaderTests
{
    [Fact]
    public void EyeCrossingLinesKeepTheirVisibleRibbonAndRejectInvisibleOrDegenerateSegments()
    {
        // The production geometry shader used to divide by zero at the eye, dropping ordinary guides crossing the camera.
        Vector4 front = new(.6f, 0, 0, 1), behind = new(-.2f, 0, 0, -1), eye = new(.2f, 0, 0, 0);
        var pixels = ShaderTestBench.RenderLinePixels([
            (front, new(2, 0, 0, 1)), (front, behind), (behind, front), (front, eye), (eye, front),
            (behind, new(.2f, 0, 0, -2)), (eye, Vector4.Zero), (front, front)]);
        Assert.True(pixels[0].Count(b => b > 0) > 100, "The visible control ribbon must cover the right edge.");
        for (int i = 1; i <= 4; i++) Assert.Equal(pixels[0], pixels[i]);
        for (int i = 5; i < pixels.Length; i++) Assert.All(pixels[i], b => Assert.Equal(0, b));
    }
    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    public void TextureTintIsViewIndependentAndOnlyUntexturedLitModeShades(bool textured, bool unlit, bool flat)
    {
        // Exercise the production pixel shader without a window or hardware GPU.
        // The triangle has a +Z surface normal, authored RGBA and variable eye direction.
        (float EyeZ, byte Alpha)[] cases = [.. new[] { 1f, .5f, 0f }.SelectMany(eyeZ => new byte[] { 0, 128, 255 }.Select(alpha => (eyeZ, alpha)))];
        var pixels = ShaderTestBench.RenderVertexTintPixels(textured, unlit, flat, cases);
        for (int i = 0; i < cases.Length; i++)
        {
            var (eyeZ, alpha) = cases[i]; byte[] pixel = pixels[i];
            float light = !textured && !unlit ? .5f + .5f * eyeZ : 1;
            float[] expected = [127.5f * light, (textured ? 32 : 63.75f) * light, (textured ? 64 : 255) * light, textured ? alpha * .5f : 127.5f];
            for (int channel = 0; channel < 4; channel++)
                Assert.True(Math.Abs(pixel[channel] - expected[channel]) <= 1,
                    $"Textured={textured}, unlit={unlit}, flat={flat}, eyeZ={eyeZ}, alpha={alpha}, channel={channel}: expected {expected[channel]}, got {pixel[channel]}.");
        }
    }
}
