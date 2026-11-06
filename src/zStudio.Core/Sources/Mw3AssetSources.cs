using System.Buffers.Binary;
using System.Text.Json.Nodes;
using zStudio.Core.Animation;
using zStudio.Core.Formats;

namespace zStudio.Core.Sources;

internal static class Mw3AssetSources
{
    internal static JsonObject Animation(AssetDocument doc, string folder, Action<string, byte[]> write, CancellationToken token)
    {
        var package = doc.Animations ?? throw new InvalidDataException("Missing MW3 animations.");
        Mw3SourceRecipe.Tape tape = new(doc.Bytes, folder, write, token);
        tape.Array(0, 3, "u32", "animation signature, version and source count");
        int stamps = BinaryPrimitives.ReadInt32LittleEndian(doc.Bytes.Span[8..]);
        for (int i = 0; i < stamps; i++) tape.Fixed(tape.Position, 84, "ANIM_STAMP_LAYOUT", "source stamp");
        tape.Fixed(tape.Position, 68, "ANIM_GLOBALS_LAYOUT", "animation globals");
        string[] refs = ["ANIM_TRACKED_NODE_LAYOUT", "ANIM_NODEREF_LAYOUT", "ANIM_LIGHTREF_LAYOUT", "ANIM_SOUNDREF_LAYOUT", "ANIM_SAMPLEREF_LAYOUT", "ANIM_EFFECTREF_LAYOUT", "ANIM_PREREQ_LAYOUT", "ANIM_RUNTIMEREF_LAYOUT"];
        foreach (var entry in package.Entries)
        {
            tape.Fixed(tape.Position, 316, "mw3_animation_entry", $"animation {entry.Index}: {entry.Name}");
            for (int lane = 0; lane < 8; lane++)
            {
                if (lane == 3) foreach (var puffer in entry.Puffers) tape.Array(tape.Position, puffer.Bytes.Length / 4, "u32", "puffer reference");
                foreach (var reference in entry.References[lane]) tape.Fixed(tape.Position, reference.Bytes.Length, refs[lane], "reference");
            }
            foreach (var sequence in entry.AllSequences)
            {
                tape.Fixed(tape.Position, 64, "sequence", sequence.Name);
                foreach (var ev in sequence.Events)
                {
                    if (ev.Spec is { } spec)
                    {
                        tape.Fixed(tape.Position, spec.Size, "event_" + ev.Type, ev.Name);
                        // Spline coefficients are source data; preserving them never requires an inverse SI compiler.
                        tape.Array(tape.Position, (ev.Bytes.Length - spec.Size) / 4, "u32", "event extension / keyframe words");
                        tape.Array(tape.Position, (ev.Bytes.Length - spec.Size) % 4, "u8", "event suffix");
                    }
                    else tape.Array(tape.Position, ev.Bytes.Length, "u8", "unknown event");
                }
                tape.Array(tape.Position, sequence.OpaqueTail.Length, "u8", "sequence suffix");
            }
        }
        return tape.Finish();
    }

    internal static JsonObject Motion(ReadOnlyMemory<byte> bytes, MotionClip clip, string folder, Action<string, byte[]> write, CancellationToken token)
    {
        Mw3SourceRecipe.Tape tape = new(bytes, folder, write, token);
        tape.Fixed(0, 24, "motion_header", "motion");
        foreach (var part in clip.Parts)
        {
            int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.Span[tape.Position..]);
            tape.Array(tape.Position, 1, "u32", "part name length");
            tape.Fixed(tape.Position, length, "name_" + length, "part name");
            tape.Array(tape.Position, 1, "u32", "channel flags");
            tape.Array(tape.Position, (clip.FrameCount + 1) * 3, "f32", "translations");
            tape.Array(tape.Position, (clip.FrameCount + 1) * 4, "f32", "rotations WXYZ");
        }
        return tape.Finish();
    }

    internal static JsonObject MechMember(AssetDocument doc, AssetRecord asset, string folder, Action<string, byte[]> write, CancellationToken token)
    {
        var bytes = doc.Slice(asset.Offset, asset.Length); Mw3SourceRecipe.Tape tape = new(bytes, folder, write, token);
        if (asset.Content is MechAssembly)
        {
            foreach (var node in doc.Scene!.Nodes.Where(n => n.Metadata.Int("member_index", -1) == asset.Index))
            {
                int at = checked((int)(node.Metadata["source_header_offset"]!.GetValue<long>() - asset.Offset));
                tape.Fixed(at, 208, "GAMEZ_NODE_BASE_LAYOUT", node.Name);
                tape.Fixed(tape.Position, 144, "GAMEZ_OBJECT3D_LAYOUT", "object transform");
                if (node.ModelIndex is { } index)
                {
                    var model = doc.Scene.Models[index]; tape.Fixed(tape.Position, 92, "GAMEZ_MODEL_INFO_LAYOUT", "model");
                    Mw3WorldSources.Model(tape, model, tape.Position, token);
                }
            }
        }
        else if (asset.Name == "materials")
        {
            int count = BinaryPrimitives.ReadInt32LittleEndian(bytes.Span); tape.Array(0, 1, "u32", "material count");
            for (int i = 0; i < count; i++)
            {
                byte flags = bytes.Span[tape.Position + 1];
                tape.Fixed(tape.Position, 40, "GAMEZ_MATERIAL_LAYOUT", $"material {i}");
                if ((flags & 1) != 0)
                {
                    int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.Span[tape.Position..]);
                    tape.Array(tape.Position, 1, "u32", "texture name length"); tape.Fixed(tape.Position, length, "name_" + length, "texture name");
                }
            }
        }
        else if (bytes.Length == 4) tape.Array(0, 1, "u32", asset.Name);
        else throw new InvalidDataException($"Unsupported MW3 library member {JsonData.ShownText(asset.Name)}.");
        return tape.Finish();
    }
}
