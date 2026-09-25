using System.Windows.Media.Media3D;
using Recoil.Zbd.Rendering;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

public sealed class FlyCameraMotionTests
{
    [Theory]
    [InlineData(30)] [InlineData(60)] [InlineData(144)]
    public void TravelDependsOnTimeNotFrameRate(int fps)
    {
        Vector3D position = default;
        for (int i = 0; i < fps * 2; i++) position += FlyCameraMotion.Displacement(new(0, 0, -100), new(0, 0, 1), 30, 1d / fps);
        Assert.True((position - new Vector3D(0, 0, -60)).Length < 1e-8);
    }

    [Theory]
    [InlineData(1, 0, 0, 1, 0, 0)] [InlineData(-1, 0, 0, -1, 0, 0)]
    [InlineData(0, 1, 0, 0, 1, 0)] [InlineData(0, -1, 0, 0, -1, 0)]
    [InlineData(0, 0, 1, 0, 0, -1)] [InlineData(0, 0, -1, 0, 0, 1)]
    public void SixDirections(int x, int y, int z, int expectedX, int expectedY, int expectedZ)
    {
        var move = FlyCameraMotion.Displacement(new(0, 0, -10), new(x, y, z), 10, .1);
        Assert.Equal(new Vector3D(expectedX, expectedY, expectedZ), move);
    }

    [Fact]
    public void CombinedAxesDoNotIncreaseSpeedAndReleasedKeysStop()
    {
        Assert.Equal(1, FlyCameraMotion.Displacement(new(0, 0, -1), new(1, 1, 1), 10, .1).Length, 10);
        Assert.Equal(default, FlyCameraMotion.Displacement(new(0, 0, -1), default, 10, .1));
    }

    [Fact]
    public void ForwardIncludesPitchButVerticalRemainsWorldY()
    {
        var forward = FlyCameraMotion.Displacement(new(0, 1, -1), new(0, 0, 1), 10, .1);
        Assert.Equal(Math.Sqrt(.5), forward.Y, 10);
        Assert.Equal(-Math.Sqrt(.5), forward.Z, 10);
        Assert.Equal(new Vector3D(0, 1, 0), FlyCameraMotion.Displacement(new(1, 1, -1), new(0, 1, 0), 10, .1));
    }

    [Fact]
    public void MouseRightAndUpTurnRightAndUpWithoutChangingFocusDistance()
    {
        var look = FlyCameraMotion.Look(new(0, 0, -10), 100, -100);
        Assert.True(look.X > 0 && look.Y > 0);
        Assert.Equal(10, look.Length, 10);
        foreach (double vertical in new[] { -100000d, 100000d })
        {
            look = FlyCameraMotion.Look(look, 10000, vertical);
            Assert.Equal(Math.Sin(89 * Math.PI / 180), Math.Abs(look.Y / look.Length), 10);
        }
    }

    [Fact]
    public void SpeedAdjustmentHandlesFractionalWheelAndBounds()
    {
        Assert.Equal(125, FlyCameraMotion.AdjustSpeed(100, 120));
        Assert.Equal(80, FlyCameraMotion.AdjustSpeed(100, -120));
        Assert.Equal(125, FlyCameraMotion.AdjustSpeed(FlyCameraMotion.AdjustSpeed(100, 60), 60), 10);
        Assert.Equal(.1, FlyCameraMotion.AdjustSpeed(.1, -120));
        Assert.Equal(100000, FlyCameraMotion.AdjustSpeed(100000, 120));
        Assert.Equal(100, FlyCameraMotion.InitialSpeed(double.NaN));
        Assert.Equal(1, FlyCameraMotion.InitialSpeed(.1));
        Assert.Equal(1000, FlyCameraMotion.InitialSpeed(100000));
    }

    [Fact]
    public void StallsAreBoundedAndInvalidTimeDoesNotMove()
    {
        Assert.Equal(1, FlyCameraMotion.Displacement(new(0, 0, -1), new(0, 0, 1), 10, 30).Length);
        Assert.Equal(default, FlyCameraMotion.Displacement(new(0, 0, -1), new(0, 0, 1), 10, -1));
        Assert.Equal(default, FlyCameraMotion.Displacement(new(0, 0, -1), new(0, 0, 1), 10, double.NaN));
    }
}
