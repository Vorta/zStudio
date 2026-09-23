using System.IO;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Rendering;
using HCamera = HelixToolkit.Wpf.SharpDX.PerspectiveCamera;
using DiffuseMaterial = HelixToolkit.Wpf.SharpDX.DiffuseMaterial;

internal static class AlphaCheck
{
    public static int Run(string root)
    {
        string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RecoilZbdStudio", "settings.json");
        byte[]? originalSettings = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, ProcessCommandLine = false }; app.InitializeComponent(); int exit = 0;
        app.Startup += async (_, _) =>
        {
            var window = (MainWindow)app.MainWindow;
            using var resolver = new AssetResolver(Path.GetFullPath(root)); using var preview = new SceneViewport();
            try
            {
                window.Content = preview;
                var scene = new GameScene();
                scene.Materials.Add(new JsonObject { ["alpha"] = 255 });
                scene.Models.Add(new(0, [new(-1,-1,0), new(1,-1,0), new(1,1,0), new(-1,1,0)], [], [], [new(0, 4, [0,1,2,3], [], [Vector2.Zero, Vector2.UnitX, Vector2.One, Vector2.UnitY], [])], []));
                var context = new AnimationPreviewContext { Package = new() { Prefix = [], Tail = [] }, World = new("alpha", new(0, DateTime.MinValue), new(FormatFamily.GameZ, 15, Recognition.Supported, ""), ReadOnlyMemory<byte>.Empty) { Scene = scene } };
                AnimationFrame frame = new(0, [new(1,0,0,Matrix4x4.Identity,true,1,-1,0,0), new(2,1,0,Matrix4x4.CreateTranslation(0,0,-.1f),true,1,-1,0,0)], [], [], [], null,null,Vector4.Zero,Vector4.Zero,[],[],[]);
                await preview.ShowAnimationAsync(context, frame, resolver, false, CancellationToken.None);
                var viewport = (Viewport3DX)preview.Content; viewport.ShowCoordinateSystem = viewport.ShowViewCube = false;
                var camera = (HCamera)viewport.Camera!; camera.Position = new(0,0,3); camera.LookDirection = new(0,0,-3); camera.UpDirection = new(0,1,0);
                var group = viewport.Items.OfType<SortingGroupModel3D>().Single(); var surfaces = group.Children.OfType<MeshGeometryModel3D>().ToArray();
                foreach (var mesh in surfaces) mesh.IsTransparent = true;
                var front = (DiffuseMaterial)surfaces[0].Material!; var back = (DiffuseMaterial)surfaces[1].Material!;
                back.DiffuseMap = new TextureModel(new byte[] { 0,0,255,128 }, SharpDX.DXGI.Format.R8G8B8A8_UNorm, 1,1);
                // Force the transparent front surface first. Its alpha-zero pixels must
                // leave the depth buffer untouched so the later back surface survives.
                group.EnableSorting = false;
                front.DiffuseMap = new TextureModel(new byte[] { 255,0,0,0 }, SharpDX.DXGI.Format.R8G8B8A8_UNorm,1,1);
                await Task.Delay(200); var clearPixel = Pixel(preview.RenderImage(128,128));
                if (clearPixel[0] < 140 || clearPixel[2] > 20) throw new InvalidDataException($"Alpha-zero texels mask the surface behind: BGR {string.Join(',',clearPixel)}.");
                group.EnableSorting = true;
                front.DiffuseMap = new TextureModel(new byte[] { 255,0,0,128 }, SharpDX.DXGI.Format.R8G8B8A8_UNorm,1,1);
                await Task.Delay(200); var halfPixel = Pixel(preview.RenderImage(128,128));
                if (Math.Abs(halfPixel[2]-134)>3 || Math.Abs(halfPixel[1]-8)>3 || Math.Abs(halfPixel[0]-73)>3) throw new InvalidDataException($"Half-alpha composition incorrect: BGR {string.Join(',',halfPixel)}.");
                Console.WriteLine($"PASS: alpha zero preserves rear geometry; half-alpha red over half-alpha blue renders RGB ({halfPixel[2]},{halfPixel[1]},{halfPixel[0]}).");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); exit = 1; }
            finally { window.Close(); app.Shutdown(exit); }
        };
        try { app.Run(); }
        finally { if (originalSettings != null) File.WriteAllBytes(settings, originalSettings); else if (File.Exists(settings)) File.Delete(settings); }
        return exit;
    }
    private static byte[] Pixel(BitmapSource image)
    {
        var bitmap = new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0); byte[] pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(bitmap.PixelWidth/2,bitmap.PixelHeight/2,1,1),pixel,4,0); return pixel;
    }
}
