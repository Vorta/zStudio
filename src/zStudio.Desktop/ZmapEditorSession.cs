using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Numerics;
using zStudio.Automation;
using zStudio.Core;
using zStudio.Core.Formats;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

internal sealed class ZmapEditorSession(DocumentModel document)
{
    private const int HistoryLimit = 128;
    private readonly List<Zmap> undo = [], redo = [];
    private Zmap? saved;
    private byte[]? savedBytes;
    public DocumentModel Document { get; } = document;
    public Guid Id { get; private set; } = Guid.NewGuid();
    public long Revision { get; private set; }
    public Zmap? Map { get; private set; }
    public string? OpenedPath { get; private set; }
    public string? SavePath { get; private set; }
    public bool Dirty => Map != null && !ReferenceEquals(Map, saved);
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public Guid? Outline { get; private set; }
    public Guid? Vertex { get; private set; }
    public ZmapOutline? Selected => Map?.Outlines.FirstOrDefault(o => o.Id == Outline);
    public int? ObjectiveCount { get; } = ReadObjectives(document);

    public void Replace(Zmap map) => Replace(map, null, null);

    public void Replace(ZmapFile.Snapshot file) => Replace(file.Map, file.Path, file.Bytes);

    private void Replace(Zmap map, string? openedPath, byte[]? openedBytes)
    {
        Id = Guid.NewGuid();
        Revision++;
        Map = map;
        OpenedPath = SavePath = openedPath;
        savedBytes = openedBytes;
        saved = openedPath == null ? null : map;
        undo.Clear();
        redo.Clear();
        Select(null, null);
    }

    public void Select(Guid? outline, Guid? vertex)
    {
        if (outline is Guid id)
        {
            var selected = FindOutline(id);
            if (vertex is Guid point) FindVertex(selected, point);
        }
        else if (vertex != null) throw new InvalidDataException("Choose an outline for the vertex.");
        if (Outline != outline || Vertex != vertex)
        {
            Outline = outline;
            Vertex = vertex;
            Touch();
        }
    }

    public void Touch() => Revision++;

    public void Add(ImmutableArray<Vector3> points, ZmapColor color, int objective)
    {
        RequireObjective(objective);
        var map = RequireMap();
        int remaining = ZmapCodec.MaximumVertices - ZmapCodec.Validate(map);
        if (points.IsDefaultOrEmpty || points.Length > remaining)
            throw new InvalidDataException($"Supply at least one vertex and at most {remaining:N0} remaining vertices.");
        var outline = new ZmapOutline(Guid.NewGuid(), color, objective,
            points.Select(p => new ZmapVertex(Guid.NewGuid(), p)).ToImmutableArray());
        Accept(map with { Outlines = map.Outlines.Add(outline) });
        Select(outline.Id, null);
    }

    public void SetFields(Guid id, Guid? vertex, Vector3? position, ZmapColor? color, int? objective)
    {
        var outline = FindOutline(id);
        if (objective is int index && index != outline.ObjectiveIndex) RequireObjective(index);
        var vertices = outline.Vertices;
        if (vertex is Guid point && position is Vector3 next)
        {
            int at = FindVertex(outline, point);
            if (vertices[at].Position != next) vertices = vertices.SetItem(at, new(point, next));
        }
        Update(outline, outline with { Color = color ?? outline.Color, ObjectiveIndex = objective ?? outline.ObjectiveIndex, Vertices = vertices });
    }

    public void Move(Guid id, Guid vertex, Vector3 position)
    {
        var outline = FindOutline(id);
        int index = FindVertex(outline, vertex);
        if (outline.Vertices[index].Position != position)
            Update(outline, outline with { Vertices = outline.Vertices.SetItem(index, new(vertex, position)) });
        Select(id, vertex);
    }

    public void Insert(Guid id, Guid start, double fraction)
    {
        var outline = FindOutline(id);
        int index = FindVertex(outline, start);
        if (outline.Vertices.Length < 2 || !double.IsFinite(fraction) || fraction <= 0 || fraction >= 1
            || (outline.Vertices.Length == 2 && index != 0))
            throw new InvalidDataException("Choose a position between the two vertices of an edge.");
        if (ZmapCodec.Validate(RequireMap()) == ZmapCodec.MaximumVertices)
            throw new InvalidDataException("This ZMAP has reached the vertex limit.");
        var a = outline.Vertices[index].Position;
        var b = outline.Vertices[(index + 1) % outline.Vertices.Length].Position;
        float Mix(float x, float y) => (float)(x + ((double)y - x) * fraction);
        Vector3 position = new(Mix(a.X, b.X), Mix(a.Y, b.Y), Mix(a.Z, b.Z));
        var vertex = new ZmapVertex(Guid.NewGuid(), position);
        Update(outline, outline with { Vertices = outline.Vertices.Insert(index + 1, vertex) });
        Select(id, vertex.Id);
    }

    public void RemoveVertex(Guid id, Guid vertex)
    {
        var outline = FindOutline(id);
        int index = FindVertex(outline, vertex);
        if (outline.Vertices.Length == 1) throw new InvalidDataException("Use Remove outline to remove its last vertex.");
        Update(outline, outline with { Vertices = outline.Vertices.RemoveAt(index) });
        Select(id, null);
    }

    public void RemoveOutline(Guid id)
    {
        var map = RequireMap();
        Accept(map with { Outlines = map.Outlines.Remove(FindOutline(id)) });
    }

    /// <summary>
    /// Moves an outline to a zero-based place in the file's order, which is the order the game draws the outlines in, and
    /// selects it.
    /// </summary>
    public void MoveOutline(Guid id, int index)
    {
        var map = RequireMap();
        var outline = FindOutline(id);
        if (index < 0 || index >= map.Outlines.Length) throw new InvalidDataException("Choose a place within the outlines.");
        int from = map.Outlines.IndexOf(outline);
        if (from != index) Accept(map with { Outlines = map.Outlines.RemoveAt(from).Insert(index, outline) });
        Select(id, Outline == id ? Vertex : null);
    }

    public static string? SplitProblem(ZmapOutline outline, int first, int second)
    {
        int count = outline.Vertices.Length;
        int distance = Math.Abs(first - second);
        if (count < 4 || distance <= 1 || distance == count - 1)
            return "Choose two nonadjacent vertices on this outline.";
        var a = outline.Vertices[first].Position;
        var b = outline.Vertices[second].Position;
        return a.X == b.X && a.Z == b.Z ? "Choose vertices at different X/Z positions." : null;
    }

    public void Split(Guid id, Guid first, Guid second)
    {
        var map = RequireMap();
        var outline = FindOutline(id);
        int a = FindVertex(outline, first), b = FindVertex(outline, second);
        if (SplitProblem(outline, a, b) is { } problem) throw new InvalidDataException(problem);
        if (ZmapCodec.Validate(map) > ZmapCodec.MaximumVertices - 2)
            throw new InvalidDataException("Splitting needs space for two shared endpoints. This ZMAP is at its vertex limit.");

        int index = map.Outlines.IndexOf(outline);
        var left = outline with { Vertices = Arc(a, b, false) };
        var right = outline with { Id = Guid.NewGuid(), Vertices = Arc(b, a, true) };
        Accept(map with { Outlines = map.Outlines.SetItem(index, left).Insert(index + 1, right) });
        Select(left.Id, first);

        ImmutableArray<ZmapVertex> Arc(int start, int end, bool copyEndpoints)
        {
            int length = (end - start + outline.Vertices.Length) % outline.Vertices.Length + 1;
            var result = ImmutableArray.CreateBuilder<ZmapVertex>(length);
            for (int i = 0; i < length; i++)
            {
                var point = outline.Vertices[(start + i) % outline.Vertices.Length];
                result.Add(copyEndpoints && (i == 0 || i == length - 1) ? point with { Id = Guid.NewGuid() } : point);
            }
            return result.MoveToImmutable();
        }
    }

    public void History(bool forward)
    {
        var from = forward ? redo : undo;
        var to = forward ? undo : redo;
        if (from.Count == 0) return;
        to.Add(RequireMap());
        Map = from[^1];
        from.RemoveAt(from.Count - 1);
        Revision++;
        RepairSelection();
    }

    public (string Path, Zmap Map, byte[]? Expected) PrepareSave(string? destination)
    {
        var map = RequireMap();
        string path = ZmapFile.FullPath(destination ?? SavePath ?? throw new InvalidDataException("Choose a destination with Save As."));
        if (destination != null && string.Equals(path, SavePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a different path for Save As.");
        return (path, map, destination == null ? savedBytes : null);
    }

    public void Saved(string path, Zmap map, byte[] bytes)
    {
        SavePath = path;
        saved = map;
        savedBytes = bytes;
        Revision++;
    }

    public Zmap RequireMap() => Map ?? throw new StudioCommandException("not_ready", "Create or open a ZMAP first.");

    public ZmapOutline FindOutline(Guid id) => RequireMap().Outlines.FirstOrDefault(o => o.Id == id)
        ?? throw new StudioCommandException("stale_record", "That outline is unavailable in this ZMAP.");

    public static int FindVertex(ZmapOutline outline, Guid vertex)
    {
        for (int i = 0; i < outline.Vertices.Length; i++)
            if (outline.Vertices[i].Id == vertex) return i;
        throw new StudioCommandException("stale_record", "That vertex is unavailable in this ZMAP outline.");
    }

    public static ZmapColor ParseColor(string text) =>
        text.Length == 7 && text[0] == '#' && uint.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint rgb)
            ? new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
            : throw new InvalidDataException("Enter a colour as #RRGGBB.");

    private void Update(ZmapOutline original, ZmapOutline updated)
    {
        if (original == updated) return;
        var map = RequireMap();
        Accept(map with { Outlines = map.Outlines.Replace(original, updated) });
    }

    private void Accept(Zmap next)
    {
        ZmapCodec.Validate(next);
        undo.Add(RequireMap());
        if (undo.Count > HistoryLimit) undo.RemoveAt(0);
        redo.Clear();
        Map = next;
        Revision++;
        RepairSelection();
    }

    private void RepairSelection()
    {
        if (Selected is not { } outline) Select(null, null);
        else if (Vertex != null && !outline.Vertices.Any(v => v.Id == Vertex)) Select(outline.Id, null);
    }

    private void RequireObjective(int index)
    {
        if (index < -1 || index >= (ObjectiveCount ?? 10))
            throw new InvalidDataException("Choose No objective or an available mission objective slot.");
    }

    private static int? ReadObjectives(DocumentModel doc)
    {
        if (doc.Document is not SourceSceneSnapshot snapshot ||
            !snapshot.Resources.TryGetValue("objectives.zrd", out var resource) || resource.Root is not { } root) return null;
        var fields = MissionAiValves.Fields(root).Children;
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i + 1 < fields.Count; i++)
            if (fields[i] is { Kind: ZrdKind.String } tag && fields[i + 1].Kind == ZrdKind.Array) tags.Add(tag.Text);
        int count = 0;
        while (count < 10 && tags.Contains($"OBJECTIVE{count + 1}")) count++;
        return count;
    }
}
