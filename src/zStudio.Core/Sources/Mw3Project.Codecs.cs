using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using zStudio.Core.Export;
using zStudio.Core.Formats;

namespace zStudio.Core.Sources;

internal static partial class Mw3Project
{
    internal static JsonObject Extract(AssetDocument doc, string folder, Action<string, byte[]> write, CancellationToken token)
    {
        JsonObject recipe;
        if (doc.Probe.Family == FormatFamily.GameZ) return Mw3WorldSources.Reconstruct(doc, folder, write, token);
        if (doc.Probe.Family == FormatFamily.Animation) return Mw3AssetSources.Animation(doc, folder, write, token);
        if (doc.Probe.Family == FormatFamily.TexturePack)
        {
            recipe = new() { ["format"] = 1, ["kind"] = "textures",
                ["reserved16"] = BinaryPrimitives.ReadUInt32LittleEndian(doc.Bytes.Span[16..]), ["reserved20"] = BinaryPrimitives.ReadUInt32LittleEndian(doc.Bytes.Span[20..]) }; JsonArray entries = [];
            int pages = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(doc.Bytes.Span[8..])); JsonArray palettes = [];
            int pageOffset = checked(24 + doc.Assets.Count(a => a.Kind == AssetKind.Texture) * 40);
            for (int i = 0; i < pages * 256; i++) palettes.Add((int)BinaryPrimitives.ReadUInt16LittleEndian(doc.Bytes.Span[(pageOffset + i * 2)..]));
            recipe["palettes"] = palettes;
            foreach (var asset in doc.Assets.Where(a => a.Kind == AssetKind.Texture))
            {
                token.ThrowIfCancellationRequested(); string path = $"{folder}/{asset.Index:D5}.png";
                write(path, PngEncoder.Encode(TextureDecoder.Decode(doc, asset, token), token));
                var info = (TextureInfo)asset.Content!;
                JsonArray palette = [];
                if (info.PaletteCount > 0 && (info.Flags & 16) == 0)
                    for (int i = 0; i < info.PaletteCount; i++) palette.Add((int)BinaryPrimitives.ReadUInt16LittleEndian(doc.Bytes.Span[(info.PaletteOffset + i * 2)..]));
                entries.Add(new JsonObject { ["name"] = asset.Name, ["source"] = path, ["width"] = info.Width, ["height"] = info.Height, ["flags"] = (int)info.Flags,
                    ["paletteCount"] = info.PaletteCount, ["palettePage"] = info.PalettePage, ["palette"] = palette,
                    ["headerFlags"] = BinaryPrimitives.ReadUInt32LittleEndian(doc.Bytes.Span.Slice(checked((int)asset.Offset + 8), 4)),
                    ["addressing"] = BinaryPrimitives.ReadUInt16LittleEndian(doc.Bytes.Span.Slice(checked((int)asset.Offset + 14), 2)) });
            }
            recipe["entries"] = entries; return recipe;
        }
        if (doc.Probe.Family == FormatFamily.Scripts)
        {
            recipe = new() { ["format"] = 1, ["kind"] = "scripts" }; JsonArray entries = [];
            foreach (var entry in doc.Scripts!.Entries)
            {
                string path = $"{folder}/{entry.SourceIndex:D5}.gs";
                var lines = entry.Instructions.Select(i => i.Tokens);
                write(path, Encoding.Latin1.GetBytes(GameGenScriptText.Write(lines)));
                entries.Add(new JsonObject { ["name"] = entry.Name, ["source"] = path, ["time"] = entry.FileTime });
            }
            recipe["entries"] = entries; return recipe;
        }
        if (doc.Probe.Family == FormatFamily.Archive)
        {
            recipe = new() { ["format"] = 1, ["kind"] = "archive" }; JsonArray entries = [];
            foreach (var asset in doc.Assets.OrderBy(a => a.Index))
            {
                token.ThrowIfCancellationRequested(); string path = $"{folder}/{asset.Index:D5}"; string kind;
                var payload = doc.Slice(asset.Offset, asset.Length);
                if (asset.Content is ZrdNode tree)
                {
                    path += ".zrd"; kind = "zrd"; byte[] text = ZrdText.Encode(tree, token, SourceProject.MaximumSourceTextBytes);
                    if (!ZrdWriter.Write(ZrdText.Parse(text, token), token).AsSpan().SequenceEqual(payload.Span))
                        throw new InvalidDataException($"{asset.Name} cannot be reconstructed as lossless zReader text.");
                    write(path, text);
                }
                else if (asset.Kind == AssetKind.Sound) { path += ".wav"; kind = "wave"; write(path, payload.ToArray()); }
                else
                {
                    kind = "recipe"; JsonObject member;
                    if (asset.Content is MotionClip motion) member = Mw3AssetSources.Motion(payload, motion, path, write, token);
                    else if (doc.Metadata["content"]?.GetValue<string>() == "Mech library") member = Mw3AssetSources.MechMember(doc, asset, path, write, token);
                    else throw new InvalidDataException($"Unsupported MW3 member {JsonData.ShownText(asset.Name)}; no incomplete project was written.");
                    path += ".json"; write(path, Mw3SourceRecipe.Text(member));
                }
                int directory = checked((int)doc.ArchiveDirectoryOffset!.Value + asset.Index * 148);
                WaveFormat? waveFormat = kind == "wave" ? WaveConverter.Format(payload) : null;
                entries.Add(new JsonObject { ["source"] = path, ["kind"] = kind,
                    ["validation"] = asset.Content is MotionClip ? "motion" : null,
                    ["sourceSha256"] = kind == "wave" ? SourceProject.Sha256(payload.Span) : null,
                    ["waveFormat"] = waveFormat is { } format ? new JsonArray(format.Rate, format.Bits, format.Channels) : null,
                    ["record"] = Mw3Record.Decode(doc.Bytes.Span.Slice(directory, 148), Mw3SourceRecipe.Fields("archive_entry")) });
            }
            recipe["entries"] = entries; return recipe;
        }
        throw new InvalidDataException($"Unsupported MW3 output {doc.Path} ({doc.Probe.Family}).");
    }

    internal static byte[] BuildRecipe(string path, Func<string, byte[]> read, CancellationToken token, int depth = 0, Action<string>? warning = null)
    {
        token.ThrowIfCancellationRequested(); if (depth > 4) throw new InvalidDataException("MW3 recipes are nested too deeply.");
        var recipe = Mw3SourceRecipe.Parse(read(path)); Mw3SourceRecipe.RequireFormat(recipe);
        switch (recipe["kind"]!.GetValue<string>())
        {
            case "stream": return Mw3SourceRecipe.Build(recipe, read, token);
            case "textures": return BuildTextures(recipe, read, token);
            case "scripts":
            {
                List<PreparedScriptEntry> scripts = [];
                foreach (var item in recipe["entries"]!.AsArray().OfType<JsonObject>())
                {
                    var lines = GameGenScriptText.TokenizeCancellable(GameGenScriptText.Decode(read(item["source"]!.GetValue<string>()), token), token);
                    scripts.Add(new(Guid.NewGuid(), null, item["name"]!.GetValue<string>(), item["time"]!.GetValue<uint>(), new byte[128],
                        lines.Select(l => new ScriptInstruction(Guid.NewGuid(), l, ReadOnlyMemory<byte>.Empty, null)).ToArray(), ReadOnlyMemory<byte>.Empty));
                }
                byte[] header = new byte[12]; BinaryPrimitives.WriteUInt32LittleEndian(header, 0x08971119); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 7);
                return PreparedScriptWriter.Write(new(header, ReadOnlyMemory<byte>.Empty, scripts, ReadOnlyMemory<byte>.Empty), token);
            }
            case "archive":
            {
                using MemoryStream output = new(); List<byte[]> directory = [];
                foreach (var entry in recipe["entries"]!.AsArray().OfType<JsonObject>())
                {
                    string source = entry["source"]!.GetValue<string>(); byte[] bytes = entry["kind"]!.GetValue<string>() switch
                    {
                        "recipe" => BuildRecipe(source, read, token, depth + 1, warning),
                        "zrd" => ZrdWriter.Write(ZrdText.Parse(read(source), token), token),
                        "wave" => BuildWave(source, read(source), entry, warning, token), _ => throw new InvalidDataException("Unknown MW3 member source kind.")
                    };
                    if (entry["validation"]?.GetValue<string>() == "motion") _ = MotionClip.Read(bytes, token);
                    FormatRegistry.ValidateDocumentSize(output.Length + bytes.LongLength + (directory.Count + 1L) * 148 + 8);
                    byte[] record = Mw3Record.Encode(entry["record"]!.AsObject(), 148, Mw3SourceRecipe.Fields("archive_entry"));
                    BinaryPrimitives.WriteInt32LittleEndian(record, checked((int)output.Length)); BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(4), bytes.Length);
                    output.Write(bytes); directory.Add(record);
                }
                foreach (var record in directory) output.Write(record);
                using BinaryWriter writer = new(output, Encoding.Latin1, true); writer.Write(1); writer.Write(directory.Count); return output.ToArray();
            }
            default: throw new InvalidDataException("Unknown MW3 source recipe kind.");
        }
    }

    private static byte[] BuildTextures(JsonObject recipe, Func<string, byte[]> read, CancellationToken token)
    {
        var entries = recipe["entries"]!.AsArray().OfType<JsonObject>().ToArray(); FormatRegistry.CheckEntries("MW3 textures", entries.Length);
        ushort[] pages = recipe["palettes"]!.AsArray().Select(n => checked((ushort)n!.GetValue<int>())).ToArray();
        if (pages.Length % 256 != 0) throw new InvalidDataException("MW3 palette pages need 256 entries each.");
        using MemoryStream output = new(); using BinaryWriter writer = new(output, Encoding.Latin1, true);
        writer.Write(0); writer.Write(1); writer.Write(pages.Length / 256); writer.Write(entries.Length); writer.Write(recipe["reserved16"]!.GetValue<uint>()); writer.Write(recipe["reserved20"]!.GetValue<uint>());
        output.Position = 24L + entries.Length * 40L; foreach (ushort color in pages) writer.Write(color);
        List<int> offsets = [], palettePages = [];
        foreach (var entry in entries)
        {
            var image = PngDecoder.Decode(read(entry["source"]!.GetValue<string>()), token: token);
            if (image.Width != entry["width"]!.GetValue<int>() || image.Height != entry["height"]!.GetValue<int>())
                throw new InvalidDataException("MW3 texture editing keeps the original dimensions; resize requires a later pack-budget rebuild.");
            offsets.Add(checked((int)output.Position)); long count = (long)image.Width * image.Height;
            int flags = entry["flags"]!.GetValue<int>(); bool alpha = (flags & 8) != 0, originalAlpha = alpha, keyed = (flags & 2) != 0;
            int paletteCount = entry["paletteCount"]!.GetValue<int>(), page = entry["palettePage"]!.GetValue<int>();
            if (paletteCount is < 0 or > 256) throw new InvalidDataException("MW3 palettes hold at most 256 colors.");
            ushort[] palette = paletteCount == 0 ? [] : (flags & 16) != 0
                ? page < 0 || page >= pages.Length / 256 ? throw new InvalidDataException("Missing MW3 palette page.") : pages.AsSpan(page * 256, 256).ToArray()
                : entry["palette"]!.AsArray().Select(n => checked((ushort)n!.GetValue<int>())).ToArray();
            if (paletteCount > 0 && palette.Length < paletteCount) throw new InvalidDataException("MW3 palette is incomplete.");
            // Retain the original upload path. Promote only when the edited PNG cannot be represented in that mode.
            for (int i = 0; !alpha && i < image.Rgba.Length; i += 4)
            {
                byte a = image.Rgba[i + 3]; ushort rgb = TexturePackWriter.Pack(image.Rgba[i], image.Rgba[i + 1], image.Rgba[i + 2]);
                if (keyed ? a is not (0 or 255) || a == 255 && rgb == 0 : a != 255) alpha = true;
            }
            if (alpha && !originalAlpha) flags = (flags & ~4) | 10;
            Dictionary<ushort, byte> indices = [];
            for (int i = 0; i < palette.Length; i++) indices.TryAdd(palette[i], checked((byte)i));
            ushort Color(int i) => !alpha && keyed && image.Rgba[i + 3] == 0 ? (ushort)0 : TexturePackWriter.Pack(image.Rgba[i], image.Rgba[i + 1], image.Rgba[i + 2]);
            if (paletteCount > 0 && Enumerable.Range(0, checked((int)count)).Any(i => !indices.ContainsKey(Color(i * 4))))
            {
                // A changed image gets its own palette; untouched images retain shared shade-table identity.
                int[] histogram = new int[65536]; for (int i = 0; i < image.Rgba.Length; i += 4) histogram[Color(i)]++;
                bool reserveKey = !alpha && keyed; if (reserveKey) histogram[0] = 0;
                if (reserveKey && paletteCount < 2) throw new InvalidDataException("This keyed MW3 palette has no room for an opaque color.");
                if (reserveKey)
                {
                    ushort[] colors = TexturePackWriter.Quantize(histogram, paletteCount - 1, token).Where(c => c != 0).ToArray();
                    palette = [0, .. colors.Length == 0 ? new ushort[] { 1 } : colors];
                }
                else palette = TexturePackWriter.Quantize(histogram, paletteCount, token);
                indices.Clear(); for (int i = 0; i < palette.Length; i++) indices.TryAdd(palette[i], checked((byte)i));
                paletteCount = palette.Length; flags &= ~16; page = -1;
            }
            FormatRegistry.ValidateDocumentSize(output.Position + 16 + count * ((paletteCount > 0 ? 1 : 2) + (alpha ? 1 : 0)) + ((flags & 16) == 0 ? paletteCount * 2L : 0));
            palettePages.Add(page);
            writer.Write((uint)(flags | (paletteCount == 0 ? 1 : 0) | (alpha ? 10 : 0))); writer.Write(checked((ushort)image.Width)); writer.Write(checked((ushort)image.Height)); writer.Write(entry["headerFlags"]!.GetValue<uint>());
            writer.Write(checked((ushort)paletteCount)); writer.Write(checked((ushort)entry["addressing"]!.GetValue<int>()));
            for (int i = 0; i < image.Rgba.Length; i += 4)
            {
                if ((i & 16383) == 0) token.ThrowIfCancellationRequested(); ushort color = Color(i);
                if (paletteCount == 0) writer.Write(color);
                else
                {
                    if (!indices.TryGetValue(color, out byte index))
                    {
                        int best = 0, distance = int.MaxValue;
                        for (int p = !alpha && keyed && color != 0 ? 1 : 0; p < palette.Length; p++)
                        {
                            int dr = (color >> 11) - (palette[p] >> 11), dg = ((color >> 5) & 63) - ((palette[p] >> 5) & 63), db = (color & 31) - (palette[p] & 31);
                            int d = dr * dr + dg * dg + db * db; if (d < distance) { best = p; distance = d; }
                        }
                        indices[color] = index = checked((byte)best);
                    }
                    writer.Write(index);
                }
            }
            if (alpha) for (int i = 3; i < image.Rgba.Length; i += 4) writer.Write(image.Rgba[i]);
            if (paletteCount > 0 && (flags & 16) == 0) foreach (ushort color in palette) writer.Write(color);
        }
        output.Position = 24;
        for (int i = 0; i < entries.Length; i++)
        {
            string name = entries[i]["name"]!.GetValue<string>(); TexturePackWriter.ValidateTextureName(name);
            byte[] field = new byte[32]; Encoding.Latin1.GetBytes(name, field); writer.Write(field); writer.Write(offsets[i]); writer.Write(palettePages[i]);
        }
        return output.ToArray();
    }
    private static byte[] BuildWave(string source, byte[] bytes, JsonObject entry, Action<string>? warning, CancellationToken token)
    {
        var wave = WaveDecoder.Read(bytes, token); int frames = wave.DataLength / wave.BlockAlign;
        bool unchanged = SourceProject.Sha256(bytes) == entry["sourceSha256"]!.GetValue<string>();
        var declared = entry["waveFormat"]!.AsArray(); WaveFormat target = new(declared[0]!.GetValue<int>(), declared[1]!.GetValue<int>(), declared[2]!.GetValue<int>());
        if (wave.Encoding != 1 || wave.BitsPerSample is not (8 or 16)) throw new InvalidDataException("MW3 source sound exports support 8- and 16-bit PCM WAVs.");
        foreach (var cue in wave.Cues)
            if (WaveConverter.PastEnd(cue, frames)) warning?.Invoke($"{JsonData.ShownText(source, 256)}: cue {cue.Id} is past the samples; {(unchanged ? "the unchanged original WAV was preserved" : "it was written at the sound's end")}.");
        return unchanged ? bytes : WaveConverter.Convert(bytes, target, wave, token);
    }
}
