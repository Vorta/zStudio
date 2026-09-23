using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Rendering;
using HCamera = HelixToolkit.Wpf.SharpDX.PerspectiveCamera;

internal static class MissionPreviewCheck
{
    public static int Run(string root)
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, ProcessCommandLine = false }; app.InitializeComponent(); int exit = 0;
        app.Startup += async (_, _) =>
        {
            var window = (MainWindow)app.MainWindow;
            using var resolver = new AssetResolver(Path.GetFullPath(root)); using var preview = new SceneViewport();
            try
            {
                window.Content = preview; window.Width = 1100; window.Height = 760;
                GameScene scene = new();
                scene.Materials.Add(new() { ["alpha"]=255,["color"]=new System.Text.Json.Nodes.JsonObject { ["r"]=255,["g"]=0,["b"]=0 } });
                scene.Materials.Add(new() { ["alpha"]=255,["color"]=new System.Text.Json.Nodes.JsonObject { ["r"]=0,["g"]=255,["b"]=0 } });
                scene.Models.Add(Plane(0,300,0)); scene.Models.Add(Plane(1,100,-5));
                scene.Nodes.Add(new(0,"world","world",null,[],[1,2],[],[]));
                scene.Nodes.Add(Node(1,"horizon",1)); scene.Nodes.Add(Node(2,"foreground",0));
                var context = new AnimationPreviewContext { Package = new() { Prefix=[],Tail=[] }, World = new("fixture",new(0,DateTime.MinValue),new(FormatFamily.GameZ,15,Recognition.Supported,""),ReadOnlyMemory<byte>.Empty) { Scene=scene } };
                AnimationFrame frame = new(0,[new(1,1,1,Matrix4x4.Identity,true,1,-1,0,0),new(2,2,0,Matrix4x4.Identity,true,1,-1,0,0)],[],[],[],null,null,Vector4.Zero,Vector4.Zero,[],[],[]);
                await preview.ShowAnimationAsync(context,frame,resolver,false,CancellationToken.None);
                var viewport = (Viewport3DX)preview.Content; viewport.ShowCoordinateSystem = viewport.ShowViewCube = false; viewport.IsInertiaEnabled = false;
                var camera = (HCamera)viewport.Camera!; camera.Position = new(0,0,1000); camera.LookDirection = new(0,0,-1000); camera.UpDirection = new(0,1,0);
                await Task.Delay(200);
                var rendered = preview.RenderImage(300,200);
                var front = Pixel(rendered,150,100); var sky = Pixel(rendered,10,10);
                Require(front[2] > 245 && front[1] < 5,"Horizon drew over foreground: " + string.Join(',',front));
                Require(sky[1] > 245 && sky[2] < 5,"Horizon clipped at the foreground near plane: " + string.Join(',',sky));
                preview.HorizonEnabled = false; await Task.Delay(80); var hidden = Pixel(preview.RenderImage(300,200),10,10);
                Require(hidden[1] < 100,"Horizon checkbox did not hide world-bound poses");
                Console.WriteLine("PASS: horizon outside the scene clip range remains visible, draws behind foreground, and obeys its checkbox.");

                var animation = await resolver.OpenCachedAsync(Path.Combine(resolver.Root,"m1","anim.zbd"),default);
                context = await AnimationPreviewContext.LoadAsync(animation.Animations!,animation.Path,resolver);
                var entry = context.Package.Entries.First(e=>e.Name=="call_flufvtol"); AnimationPlayer player = new(context,entry.Index);
                await preview.ShowAnimationAsync(context,player.Frame(),resolver,false,CancellationToken.None);
                camera.Position = new(2250,40,1400); camera.LookDirection = new(0,-15,-100); camera.UpDirection = new(0,1,0);
                await Task.Delay(300);
                string output = Path.Combine(Path.GetTempPath(),"zbd-mission-preview-"+Path.GetFileName(resolver.Root)); Directory.CreateDirectory(output);
                Save(preview.RenderImage(960,640),Path.Combine(output,"mission-start.png"));
                var live = player.AdvanceTo(100.1,true); preview.UpdateAnimationFrame(live);
                camera.Position = new(1870,100,3550); camera.LookDirection = new(-67,-41,-92);
                await Task.Delay(250); Save(preview.RenderImage(960,640),Path.Combine(output,"vtol-flight.png"));
                int horizon = context.Scene.Nodes.First(n=>n.Name=="horizon").Index;
                var horizonPose = live.Nodes.Single(n=>n.SourceNode==horizon);
                var group = viewport.Items.OfType<SortingGroupModel3D>().Single();
                var skyMeshes = group.Children.OfType<MeshGeometryModel3D>().Where(m=>m.RenderOrder==0).ToArray();
                Require(skyMeshes.Length>0,"Missing horizon geometry");
                foreach (var mesh in skyMeshes)
                {
                    var matrix = mesh.Transform.Value;
                    Require(Math.Abs(matrix.OffsetX-camera.Position.X)<.01 && Math.Abs(matrix.OffsetY-camera.Position.Y)<.01 && Math.Abs(matrix.OffsetZ-camera.Position.Z)<.01,"Paused free camera did not move the horizon");
                }
                preview.UpdateAnimationFrame(live with { Camera=new(new(2010,160,3400),new(1800,60,3400),60) },true);
                await Task.Delay(100); // Camera-dependent appearance is finalized at the render boundary.
                Require(Math.Abs(skyMeshes[0].Transform.Value.OffsetY-160)<.01,"Authored camera did not move horizon");
                var world = context.World.Assets.First(a=>a.Kind==AssetKind.World);
                await preview.ShowAsync(context.World,world,resolver,null,0,default,false,context.Mission); var without = camera.Position;
                await preview.ShowAsync(context.World,world,resolver,null,0,default,true,context.Mission);
                Require((camera.Position-without).Length<.01,"Horizon changed Frame all bounds");
                Require(preview.Mission!.Actors.Count(a => a.Pickup == null)==87 && preview.Mission.Actors.Count(a => a.Pickup != null)==64,"Whole world is missing mission actor/pickup instances");
                Console.WriteLine("PASS: m1 animation and Whole world share 87 actor and 64 pickup placements; free and authored camera tracking and framing are correct.");
                Console.WriteLine("Images: " + output);
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); exit=1; }
            finally { window.Close(); app.Shutdown(exit); }
        };
        app.Run(); return exit;
    }
    private static GameNode Node(int index,string name,int model) => new(index,name,"object3d",model,[0],[],new() { ["flags"]=4 },new() { ["flags"]=8 });
    private static GameModel Plane(int material,float size,float z) => new(material,[new(-size,-size,z),new(size,-size,z),new(size,size,z),new(-size,size,z)],[],[],[new(material,0,[0,1,2,3],[],[],[])],[]);
    private static byte[] Pixel(BitmapSource image,int x,int y)
    { var bitmap = new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0); byte[] pixel=new byte[4]; bitmap.CopyPixels(new Int32Rect(x,y,1,1),pixel,4,0); return pixel; }
    private static void Save(BitmapSource bitmap,string path) { PngBitmapEncoder encoder=new(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream=File.Create(path); encoder.Save(stream); }
    private static void Require(bool condition,string message) { if (!condition) throw new InvalidDataException(message); }
}
