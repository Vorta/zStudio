using System.Globalization;
using System.IO;
using System.Numerics;
using System.Windows.Controls;
using Recoil.Zbd.Core;

namespace Recoil.Zbd.Desktop;

public sealed partial class ResourcePropertiesEditor
{
    private readonly Func<string, int, int, MotionFrame?, float?, Task>? motionEdit;
    private int motionPart, motionFrame;
    internal int MotionPart => motionPart;
    internal int MotionFrameIndex => motionFrame;
    private MotionClip? Motion
    {
        get
        {
            if (nodeId != null) return null;
            var snapshot = document.ResourceEdits!.Current;
            int index = snapshot.Members.ToList().FindIndex(m => m.Id == memberId);
            return index < 0 ? null : snapshot.Document.Assets[index].Content as MotionClip;
        }
    }
    private void BuildMotionFields(StackPanel panel)
    {
        var clip = Motion!;
        string Format(params float[] values) => string.Join(", ", values.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));
        float[] Parse(string value, int count)
        {
            var components = value.Split(',').Select(v => float.Parse(v.Trim(), CultureInfo.InvariantCulture)).ToArray();
            if (components.Length != count || components.Any(v => !float.IsFinite(v))) throw new InvalidDataException($"Enter {count} finite components.");
            return components;
        }
        MotionFrame Current() => Motion!.Parts[motionPart].Frames[motionFrame];
        Task Apply(string action, MotionFrame? value = null, float? time = null) => motionEdit?.Invoke(action, motionPart, motionFrame, value, time) ?? throw new InvalidDataException("Motion editing is unavailable.");
        Input(panel, "Loop seconds", Format(clip.LoopTime), _ => { }, getter: () => Format(Motion!.LoopTime),
            asyncCommit: text => Apply("timing", time: Parse(text, 1)[0]));
        Input(panel, "Part index", motionPart.ToString(CultureInfo.InvariantCulture), _ => { }, hint: $"Zero-based part index, 0–{clip.Parts.Count - 1}.", getter: () => motionPart.ToString(CultureInfo.InvariantCulture), asyncCommit: text =>
        {
            int part = int.Parse(text, CultureInfo.InvariantCulture);
            if (part < 0 || part >= Motion!.Parts.Count) throw new InvalidDataException("Part is outside this motion clip.");
            motionPart = part; form = null; return Task.CompletedTask;
        });
        Input(panel, "Part name", clip.Parts[motionPart].Name, _ => { }, true, getter: () => Motion!.Parts[motionPart].Name);
        Input(panel, "Frame index", motionFrame.ToString(CultureInfo.InvariantCulture), _ => { }, hint: $"Zero-based frame index, 0–{clip.FrameCount - 1}. The separate closing sample is retained.", getter: () => motionFrame.ToString(CultureInfo.InvariantCulture), asyncCommit: text =>
        {
            int frame = int.Parse(text, CultureInfo.InvariantCulture);
            if (frame < 0 || frame >= Motion!.FrameCount) throw new InvalidDataException("Frame is outside this motion clip.");
            motionFrame = frame; form = null; return Task.CompletedTask;
        });
        string Position() { var v = Current().Translation; return Format(v.X, v.Y, v.Z); }
        string Rotation() { var q = Current().Rotation; return Format(q.W, q.X, q.Y, q.Z); }
        Input(panel, "Translation", Position(), _ => { }, components: ["X", "Y", "Z"], getter: Position, asyncCommit: text =>
        {
            var v = Parse(text, 3); return Apply("set", Current() with { Translation = new Vector3(v[0], v[1], v[2]) });
        });
        Input(panel, "Quaternion", Rotation(), _ => { }, components: ["W", "X", "Y", "Z"], getter: Rotation,
            hint: "Authored quaternion; nonzero finite values are preserved. Playback normalizes for interpolation.", asyncCommit: text =>
        {
            var q = Parse(text, 4); return Apply("set", Current() with { Rotation = new Quaternion(q[1], q[2], q[3], q[0]) });
        });
        AsyncButton(panel, "Insert frame after", () => Apply("insert"));
        AsyncButton(panel, "Delete frame", () => Apply("delete"));
        Label(panel, $"{clip.FrameCount} frames · {clip.Parts.Count} parts. Frame insertion/deletion affects every track and retains the loop duration.");
    }
}
