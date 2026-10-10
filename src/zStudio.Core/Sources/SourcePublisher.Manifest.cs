using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Recoil.Zbd.Core.Sources;

public sealed partial class SourcePublisher
{
    /// <summary>Admit a cold journal before the serializer allocates its lists and decoded strings.</summary>
    internal JournalManifest ParseManifest(ReadOnlySpan<byte> bytes, string id, CancellationToken token,
        PathWorkBudget? pathBudget = null, int maximumFiles = SourceProject.MaximumFiles)
    {
        token.ThrowIfCancellationRequested();
        if (bytes.Length > MaximumManifestBytes) throw new InvalidDataException("its manifest exceeds its byte budget.");
        pathBudget ??= new(root.Length, PlanningPathBytesLimit, token);
        ManifestAdmission admission = new(bytes, Math.Min(maximumFiles, SourceProject.MaximumFiles), JournalFoldersLimit, pathBudget, token);
        admission.Read();
        token.ThrowIfCancellationRequested();
        JournalManifest manifest = JsonSerializer.Deserialize<JournalManifest>(bytes, JournalJson) ?? throw new InvalidDataException("its manifest is empty.");
        token.ThrowIfCancellationRequested();
        ValidateManifest(manifest, id, token, pathsAdmitted: true);
        return manifest;
    }

    // This checks allocation admission, not recovery semantics. The existing serializer still enforces required
    // constructor fields, numeric/date types and nullable annotations; ValidateManifest checks all full identities,
    // changes and folder ownership. No authored string is shortened or substituted. Keys and strings that cannot be decoded
    // are refused as malformed JSON, like any other damaged manifest, before anything decodes them.
    private ref struct ManifestAdmission
    {
        private Utf8JsonReader reader;
        private readonly int maximumFiles, maximumFolders;
        private readonly PathWorkBudget paths;
        private readonly CancellationToken token;
        private int files, folders;

        internal ManifestAdmission(ReadOnlySpan<byte> bytes, int maximumFiles, int maximumFolders, PathWorkBudget paths, CancellationToken token)
        {
            reader = new(bytes, new JsonReaderOptions { MaxDepth = JournalJson.MaxDepth });
            this.maximumFiles = maximumFiles; this.maximumFolders = maximumFolders; this.paths = paths; this.token = token;
            files = folders = 0;
        }

        internal void Read()
        {
            Next();
            if (reader.TokenType == JsonTokenType.Null) throw new InvalidDataException("its manifest is empty.");
            Object(Kind.Manifest);
            token.ThrowIfCancellationRequested();
            if (reader.Read()) throw Shape();
        }

        private enum Kind { Manifest, File, Digest }
        private enum Field { Number, SaveId, Description, Created, Files, Folders, Relative, Digest, Hash }

        private void Object(Kind kind)
        {
            Require(JsonTokenType.StartObject);
            while (true)
            {
                Next();
                if (reader.TokenType == JsonTokenType.EndObject) return;
                Require(JsonTokenType.PropertyName);
                Field field = Property(kind);
                Next();
                switch (field)
                {
                    case Field.Files: Array(files: true); break;
                    case Field.Folders: Array(files: false); break;
                    case Field.Digest:
                        if (reader.TokenType != JsonTokenType.Null) Object(Kind.Digest);
                        break;
                    case Field.Relative: Path(); break;
                    case Field.Number:
                        Require(JsonTokenType.Number);
                        if (reader.ValueSpan.Length > 64) throw new InvalidDataException("a manifest number is too long.");
                        break;
                    case Field.SaveId: String(128, "its save identity is too long."); break;
                    case Field.Description: String(MaximumDescriptionLength, "its description is too long."); break;
                    case Field.Created: String(64, "its creation date is too long."); break;
                    case Field.Hash: String(64, "its content digest is too long."); break;
                }
            }
        }

        private Field Property(Kind kind)
        {
            // ValueTextEquals recognizes escaped property names without allocating a decoded key. Unknown keys
            // are refused without putting their potentially large spelling into a serializer diagnostic.
            // Even a fully escaped known name uses at most six bytes per character. Bound comparison's own
            // possible unescape workspace before asking it to compare an arbitrary damaged key.
            if (reader.ValueSpan.Length > 6 * "description".Length) throw new JsonException("The save manifest contains an unknown property.");
            // Comparing an escaped key decodes it, and the reader throws InvalidOperationException for an unpaired
            // surrogate. Undecodable text is a malformed manifest, so refuse it as one before the comparison.
            Text(long.MaxValue, "");
            if (kind == Kind.Manifest)
            {
                if (reader.ValueTextEquals("format"u8)) return Field.Number;
                if (reader.ValueTextEquals("saveId"u8)) return Field.SaveId;
                if (reader.ValueTextEquals("description"u8)) return Field.Description;
                if (reader.ValueTextEquals("createdUtc"u8)) return Field.Created;
                if (reader.ValueTextEquals("files"u8)) return Field.Files;
                if (reader.ValueTextEquals("folders"u8)) return Field.Folders;
            }
            else if (kind == Kind.File)
            {
                if (reader.ValueTextEquals("relative"u8)) return Field.Relative;
                if (reader.ValueTextEquals("expected"u8) || reader.ValueTextEquals("content"u8)) return Field.Digest;
            }
            else
            {
                if (reader.ValueTextEquals("length"u8)) return Field.Number;
                if (reader.ValueTextEquals("sha256"u8)) return Field.Hash;
            }
            throw new JsonException("The save manifest contains an unknown property.");
        }

        private void Array(bool files)
        {
            Require(JsonTokenType.StartArray);
            while (true)
            {
                Next();
                if (reader.TokenType == JsonTokenType.EndArray) return;
                // Count every occurrence, also in repeated JSON properties, before any collection is created.
                if (files)
                {
                    if (this.files >= maximumFiles) throw new InvalidDataException($"the save journals list more than {maximumFiles:N0} additional files.");
                    this.files++;
                    Object(Kind.File);
                }
                else
                {
                    if (folders >= maximumFolders) throw new InvalidDataException($"it lists more than {maximumFolders:N0} folders.");
                    folders++;
                    Path();
                }
            }
        }

        private void Path()
        {
            var (characters, slashes) = String(long.MaxValue, "its path is too long.");
            paths.Reserve(characters, slashes + 1);
        }

        private (long Characters, long Slashes) String(long maximum, string refusal)
        {
            Require(JsonTokenType.String);
            return Text(maximum, refusal);
        }

        /// <summary>The decoded length of the current key or string; text that cannot be decoded (invalid UTF-8 or an unpaired surrogate) is a malformed manifest.</summary>
        private (long Characters, long Slashes) Text(long maximum, string refusal)
        {
            ReadOnlySpan<byte> raw = reader.ValueSpan;
            long characters = 0, slashes = 0; bool high = false;
            for (int i = 0; i < raw.Length;)
            {
                if ((i & 4095) < 6) token.ThrowIfCancellationRequested();
                int value = raw[i++]; bool escaped = false;
                if (value == '\\')
                {
                    value = raw[i++];
                    if (value == 'u')
                    {
                        value = 0; escaped = true;
                        for (int end = i + 4; i < end; i++)
                            value = value * 16 + (raw[i] <= '9' ? raw[i] - '0' : (raw[i] | 32) - 'a' + 10);
                    }
                    characters++; // Each JSON escape represents one UTF-16 code unit.
                }
                else if (value >= 128)
                {
                    if (Rune.DecodeFromUtf8(raw[(i - 1)..], out Rune rune, out int consumed) != OperationStatus.Done) throw Shape();
                    i += consumed - 1; value = rune.Value; characters += rune.Utf16SequenceLength;
                }
                else characters++;
                // An escaped high surrogate must be followed directly by an escaped low one, and a low one only follows a high one.
                if (high != (escaped && char.IsLowSurrogate((char)value))) throw Shape();
                high = escaped && char.IsHighSurrogate((char)value);
                if (value == '/') slashes++;
                if (characters > maximum) throw new InvalidDataException(refusal);
            }
            if (high) throw Shape();
            return (characters, slashes);
        }

        private void Next() { token.ThrowIfCancellationRequested(); if (!reader.Read()) throw Shape(); }
        private void Require(JsonTokenType type) { if (reader.TokenType != type) throw Shape(); }
        private static JsonException Shape() => new("The save manifest has an invalid JSON value or structure.");
    }
}
