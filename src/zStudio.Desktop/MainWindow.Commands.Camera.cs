using System.Windows.Media.Media3D;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Rendering;

namespace Recoil.Zbd.Desktop;

public partial class MainWindow
{
    private void RegisterCameraCommand(StudioCommands registry)
    {
        Register(registry, "camera", "Read or navigate the shared 3D camera. Supports Blender-style orbit/pan/centered zoom/dolly, exact axis views, perspective/orthographic projection and framing. Valid manual navigation exits animation Follow camera. Never captures physical input.", true,
        [
            PreviewParameter, P("action", "string", "Camera operation.", true, "read", "set", "move", "rotate", "frame", "pan", "zoom", "dolly", "view", "projection"),
            new("position", "array", "Absolute XYZ for set.", Items: new("", "number", "Coordinate."), MinItems: 3, MaxItems: 3),
            new("look", "array", "Look direction XYZ for set.", Items: new("", "number", "Direction component."), MinItems: 3, MaxItems: 3),
            new("fov", "number", "Perspective camera field of view in degrees; retained in orthographic mode.", NumberMinimum: 1.000001, NumberMaximum: 178.999999),
            .. new[] { "right", "up", "forward" }.Select(n => new StudioParameter(n, "number", n == "up" ? "World-Y displacement in game units." : "View-relative displacement in game units.", NumberMinimum: -1e9, NumberMaximum: 1e9)),
            .. new[] { "horizontal", "vertical" }.Select(n => new StudioParameter(n, "number", "Mouse-equivalent delta in DIP for rotate or pan.", NumberMinimum: -36000, NumberMaximum: 36000)),
            new("steps", "number", "Wheel-equivalent zoom steps; positive zooms in around the view target.", NumberMinimum: -100, NumberMaximum: 100),
            new("distance", "number", "Dolly displacement in game units; positive moves forward together with the target.", NumberMinimum: -1e9, NumberMaximum: 1e9),
            P("view", "string", "Named view for action=view; axis views are orthographic with automatic perspective on orbit.", false, "front", "back", "left", "right", "top", "bottom", "opposite"),
            P("projection", "string", "Required for action=projection; optional for set. Explicit orthographic mode persists while orbiting.", false, "perspective", "orthographic"),
            new("width", "number", "Orthographic view width in game units, for set or projection.", NumberMinimum: .001, NumberMaximum: 1e12),
            P("target", "string", "Frame target; defaults to asset, preserving the existing animation-only framing. Selected falls back to asset without a node selection.", false, "asset", "all", "selected"),
            new("node", "integer", "Optional source node identity to frame, including visible descendants/runtime instances.", Minimum: 0, Maximum: int.MaxValue)
        ], a =>
        {
            var viewport = TargetViewport(a); string action = Text(a, "action"); var pose = viewport.CaptureView();
            if (action != "read" && viewport.IsPickupDragging)
                throw new StudioCommandException("busy", "A pickup drag is in progress. Finish or cancel the drag before changing the camera.");
            // Complete argument validation before changing Fly, Follow camera, projection, or inertia.
            SceneViewport.ViewPose? explicitPose = null;
            if (action == "set")
            {
                var p = Triple(a, "position"); var l = Triple(a, "look");
                if (new Vector3D(l[0], l[1], l[2]).LengthSquared < 1e-12) throw new StudioCommandException("invalid_argument", "Use a nonzero look direction.");
                string projection = Text(a, "projection", pose.Projection);
                explicitPose = SceneViewport.UprightPose(pose with { Position = new(p[0], p[1], p[2]), LookDirection = new(l[0], l[1], l[2]),
                    FieldOfView = Number(a, "fov", pose.FieldOfView), Projection = projection,
                    OrthographicWidth = projection == "orthographic" ? a.ContainsKey("width") ? Number(a, "width") : pose.OrthographicWidth : null });
            }
            if (action == "view" && !a.ContainsKey("view") || action == "projection" && !a.ContainsKey("projection"))
                throw new StudioCommandException("invalid_argument", "Supply " + action + " for this camera operation.");
            int? node = a.ContainsKey("node") ? Int(a, "node") : null;
            if (node is int index && (viewport.PreviewScene == null || index >= viewport.PreviewScene.Nodes.Count))
                throw new StudioCommandException("stale_record", "Scene node unavailable.");
            if (a.ContainsKey("width") && Text(a, "projection", pose.Projection) != "orthographic")
                throw new StudioCommandException("invalid_argument", "Width requires orthographic projection.");
            if (action != "read")
            {
                flyCamera?.End();
                switch (action)
                {
                    case "frame":
                        if (!viewport.TryFrame(Text(a, "target", "asset"), node)) throw new StudioCommandException("not_ready", "No visible geometry is available to frame.");
                        break;
                    case "set":
                        viewport.BeginManualNavigation(); viewport.StopCameraMotion();
                        viewport.RestoreView(explicitPose!);
                        break;
                    case "move":
                        viewport.BeginManualNavigation(); viewport.StopCameraMotion(); pose = viewport.CaptureView();
                        var look = pose.LookDirection; look.Normalize(); var right = Vector3D.CrossProduct(look, pose.UpDirection); right.Normalize();
                        viewport.RestoreView(pose with { Position = pose.Position + look * Number(a, "forward") + right * Number(a, "right") + new Vector3D(0, Number(a, "up"), 0) });
                        break;
                    case "rotate": viewport.StopCameraMotion(); viewport.RotateBy(Number(a, "horizontal"), Number(a, "vertical")); break;
                    case "pan": viewport.StopCameraMotion(); viewport.PanBy(Number(a, "horizontal"), Number(a, "vertical")); break;
                    case "zoom": viewport.StopCameraMotion(); viewport.ZoomBy(Number(a, "steps")); break;
                    case "dolly": viewport.StopCameraMotion(); viewport.DollyBy(Number(a, "distance")); break;
                    case "view": if (Text(a, "view") == "opposite") viewport.OppositeView(); else viewport.SetAxisView(Text(a, "view")); break;
                    case "projection":
                        viewport.SetProjection(Text(a, "projection"));
                        if (a.ContainsKey("width")) viewport.RestoreView(viewport.CaptureView() with { OrthographicWidth = Number(a, "width") });
                        break;
                }
            }
            pose = viewport.CaptureView();
            return Result(new { pose.Position, pose.LookDirection, pose.UpDirection, pose.FieldOfView, pose.Projection, pose.OrthographicWidth,
                pose.AxisView, pose.AutoPerspective, viewport.FramingSelection });
        });
    }
}
