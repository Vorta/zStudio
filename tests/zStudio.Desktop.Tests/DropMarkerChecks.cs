using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using zStudio.Rendering;
using Xunit;

namespace zStudio.Desktop.Tests;

internal static class DropMarkerChecks
{
    // Actual rendered contrast over water and pale terrain; stale evidence cannot become an editing target.
    internal static void Run()
    {
        foreach (var background in new[] { new Vector4(.05f,.12f,.2f,1), new Vector4(.95f,.9f,.7f,1) })
        {
            using var preview = new SceneViewport();
            var window = new Window { Content = preview, Width = 420, Height = 440, ShowInTaskbar = false, ShowActivated = false };
            try
            {
                window.Show();
                preview.AddFixtureMesh(new([new(-20,-20,-1),new(20,-20,-1),new(20,20,-1),new(-20,20,-1)], [0,1,2,0,2,3]) { Color = background, Unlit = true });
                preview.SetNavigationAids(false, false);
                preview.RestoreView(new(new(0,0,10),new(0,0,-10),new(0,1,0),45));
                preview.SetPlacementEditing(true);
                Guid id = Guid.NewGuid();
                ParachutePathPoint[] path = [new(0,new(-3,0,0)),new(1,new(-3,0,0)),new(3,new(3,0,0))];
                ParachuteWindowOverlay[] windows = [new(id,2,[new(2.5,new(1.5f,0,0))])];
                preview.SetParachuteOverlay(path, windows);
                var normal = Pixels();
                Assert.True(Count(normal, (r,g,b) => r > 220 && g > 170 && b < 30) > 3, "Yellow starts must be visible.");
                Assert.True(Count(normal, (r,g,b) => r > 220 && g is > 100 and < 180 && b < 80) > 0, "Orange releases must be visible.");
                Assert.True(Count(normal, (r,g,b) => r < 20 && g < 20 && b < 20) > 3, "Dark rims must contrast with terrain.");
                preview.SelectParachuteWindow(id, notify: false);
                Assert.True(Count(Pixels(), (r,g,b) => r > 240 && g > 240 && b > 240) > 0, "Selection must have a white halo.");
                preview.SetParachuteOverlay(path, windows, stale: true);
                Assert.Equal(0, Count(Pixels(), (r,g,b) => r > 220 && g > 170 && b < 30));
                Assert.Throws<InvalidOperationException>(() => preview.SelectParachuteWindow(id));

                byte[] Pixels()
                {
                    var image = new FormatConvertedBitmap(preview.RenderImage(400,400), PixelFormats.Bgra32, null, 0);
                    byte[] bytes = new byte[400*400*4]; image.CopyPixels(bytes, 400*4, 0); return bytes;
                }
            }
            finally { window.Close(); }
        }
        static int Count(byte[] pixels, Func<byte,byte,byte,bool> matches)
        {
            int count = 0;
            for (int i=0;i<pixels.Length;i+=4) if(matches(pixels[i+2],pixels[i+1],pixels[i])) count++;
            return count;
        }
    }
}
