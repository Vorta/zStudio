using System.Numerics;
using Recoil.Zbd.Core.Export;

namespace Recoil.Zbd.Core.Formats;

public static partial class ModelReplacementWriter
{
    internal static byte[] EncodeMesh(byte[] header, uint? version, ImportedMesh mesh, int material, CancellationToken token)
    {
        mesh.Validate(); var layout = GameZLayouts.For(version); int shift = version == 27 ? 4 : 0;
        if (header.Length != layout.ModelSize) throw new InvalidDataException("Invalid model header size.");
        if (shift != 0) { Put(header, 4, 0); Put(header, 88, 0); }
        Put(header, 0, 0);
        Put(header, 12 + shift, mesh.Triangles.Length / 3); Put(header, 16 + shift, mesh.Positions.Length);
        Put(header, 20 + shift, mesh.Normals.Length); Put(header, 24 + shift, 0); Put(header, 28 + shift, 0);
        Put(header, 48 + shift, 1); Put(header, 52 + shift, 1); Put(header, 56 + shift, version == 15 ? 0 : 1); Put(header, 60 + shift, 0); Put(header, 64 + shift, version == 15 ? 1 : 0);
        var (center, radius) = DisplayInstanceSphere(mesh.Bounds.Min, mesh.Bounds.Max);
        Vector(header, 68 + shift, center); Float(header, 80 + shift, radius);
        using MemoryStream stream = new(); using BinaryWriter w = new(stream);
        foreach (var v in mesh.Positions) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
        foreach (var v in mesh.Normals) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
        for (int i = 0; i < mesh.Triangles.Length; i += 3)
        { token.ThrowIfCancellationRequested(); w.Write(0x203u); w.Write(0); w.Write(1); w.Write(1); w.Write(1); if (layout.HasVertexColors) { w.Write(1); w.Write(0); } w.Write(material); w.Write(0); }
        for (int i = 0; i < mesh.Triangles.Length; i += 3)
        {
            token.ThrowIfCancellationRequested();
            for (int j = 0; j < 3; j++) w.Write(mesh.Triangles[i + j]);
            for (int j = 0; j < 3; j++) w.Write(mesh.Triangles[i + j]);
            for (int j = 0; j < 3; j++) { var uv = mesh.Uvs[mesh.Triangles[i + j]]; w.Write(uv.X); w.Write(uv.Y); }
            if (layout.HasVertexColors) for (int j = 0; j < 3; j++)
            { var color = mesh.Colors.Length == 0 ? Vector3.One : mesh.Colors[mesh.Triangles[i + j]]; w.Write(color.X * 255); w.Write(color.Y * 255); w.Write(color.Z * 255); }
        }
        return stream.ToArray();
    }

    /// <summary>Replace one member-local mech model; shared materials and all other member bytes remain intact.</summary>
    public static byte[] ReplaceMechMember(ZbdDocument source, int memberIndex, int localModel, ImportedMesh mesh, int material, CancellationToken token = default)
    {
        if (source.Diagnostics.Any(d => d.Severity == "Error") || source.Scene is not { } scene || source.Assets.SingleOrDefault(a => a.Index == memberIndex)?.Content is not MechAssembly assembly)
            throw new InvalidDataException("An intact mech assembly member is required.");
        if (localModel < 0 || localModel >= assembly.ModelCount || material < 0 || material >= scene.Materials.Count) throw new InvalidDataException("Model or material index is outside the selected member/library.");
        var asset = source.Assets.Single(a => a.Index == memberIndex); var model = scene.Models[assembly.FirstModel + localModel];
        if (model.Morphs.Length != 0 || model.Metadata.Int("light_count") != 0) throw new InvalidDataException("Models with morph or light channels require their authored geometry.");
        int headerAt = checked((int)model.Metadata["source_header_offset"]!.GetValue<long>());
        int dataAt = checked((int)model.Metadata["source_data_offset"]!.GetValue<long>()), dataLength = checked((int)model.Metadata["source_data_length"]!.GetValue<long>());
        byte[] header = source.Slice(headerAt, 92).ToArray(); byte[] data = EncodeMesh(header, 27, mesh, material, token);
        FormatRegistry.ValidateDocumentSize(asset.Length - dataLength + data.Length);
        byte[] nodes = new byte[checked(scene.Nodes.Count * 208)];
        foreach (var node in scene.Nodes)
        { token.ThrowIfCancellationRequested(); source.Slice(node.Metadata["source_header_offset"]!.GetValue<long>(), 208).Span.CopyTo(nodes.AsSpan(node.Index * 208)); }
        UpdateNodeBounds(scene, nodes, new Dictionary<int, ImportedMesh> { [model.Index] = mesh }, 208, token);
        List<(long Offset, int Length, byte[] Bytes)> patches = [(headerAt, 92, header), (dataAt, dataLength, data)];
        for (int i = assembly.RootNode; i < assembly.RootNode + assembly.NodeCount; i++)
            patches.Add((scene.Nodes[i].Metadata["source_header_offset"]!.GetValue<long>(), 208, nodes.AsSpan(i * 208, 208).ToArray()));
        using MemoryStream output = new(); long position = asset.Offset;
        foreach (var patch in patches.OrderBy(p => p.Offset))
        {
            token.ThrowIfCancellationRequested();
            if (patch.Offset < position || patch.Offset + patch.Length > asset.Offset + asset.Length) throw new InvalidDataException("Invalid or overlapping mech source ranges.");
            output.Write(source.Slice(position, patch.Offset - position).Span); output.Write(patch.Bytes); position = patch.Offset + patch.Length;
        }
        output.Write(source.Slice(position, asset.Offset + asset.Length - position).Span); return output.ToArray();
    }
}
