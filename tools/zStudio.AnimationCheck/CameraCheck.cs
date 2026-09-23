using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using System.Numerics;
using System.Security.Cryptography;

internal static class CameraCheck
{
    public static async Task<int> RunAsync(string[] roots)
    {
        foreach (string rootArg in roots)
        {
            string root = Path.GetFullPath(rootArg), path = Path.Combine(root, "m1", "anim.zbd");
            using AssetResolver resolver = new(root);
            byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(path));
            var document = await resolver.OpenCachedAsync(path, default);
            var package = document.Animations!;
            var context = await AnimationPreviewContext.LoadAsync(package, path, resolver);
            var entry = package.Entries.First(e => e.Name == "start_single_player");
            byte[] serialized = AnimationWriter.Write(package);
            var cameraEntry = package.Entries.First(e => e.Name == "m1_start_animation");
            Require(cameraEntry.RootName == "camera1", "Intro camera binding changed");
            AnimationPlayer player = new(context, entry.Index);
            List<AnimationCamera> cameras = [];
            foreach (double time in new double[] { .1, 5, 10, 15, 20 })
            {
                var camera = player.AdvanceTo(time, true).Camera ?? throw new InvalidDataException("Missing intro camera");
                Require(camera.Name == "camera1" && camera.SourceNode == 2, "Wrong camera identity");
                Require(camera.Position.X > 2000 && camera.Position.Z > 1000, "Camera eye is not at the authored world coordinates");
                Require(Math.Abs(camera.FieldOfView - 60) < .01, "Radian FOV was not converted to degrees");
                Require(camera.Target.Y < camera.Position.Y, "Intro camera should look down toward the ground");
                Require(Math.Abs((camera.Target - camera.Position).Length() - 1) < .001, "Camera forward basis was not normalized");
                if (cameras.Count > 0) Require(camera.Position.Y < cameras[^1].Position.Y, "Intro did not descend");
                cameras.Add(camera); Console.WriteLine($"{rootArg} t={time:0.0}: {camera}");
            }
            Require(cameras[0].Position.Y > 40 && cameras[^1].Position.Y < 9, "Incorrect descent range");
            Require(Vector3.Dot(cameras[0].Target - cameras[0].Position, cameras[3].Target - cameras[3].Position) < .5f, "Intro camera orientation did not orbit");
            player.AdvanceTo(.1, true);
            Require(player.AdvanceTo(10, true).Camera == cameras[2], "Camera rewind diverged");
            Require(AnimationWriter.Write(package).AsSpan().SequenceEqual(serialized), "Camera playback modified serialized records");
            Require(SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(sourceHash), "Source bytes changed");
            Console.WriteLine("PASS: camera1 authored descent/orbit, 60-degree FOV, deterministic seek, unchanged records and source hash.");
        }
        return 0;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
