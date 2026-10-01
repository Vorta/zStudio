using System.Text;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ZrdTextSyntaxTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A hand-authored pickup placement list: one record per line, comments, a section header and blank lines.</summary>
    private static readonly string Pickups = """
        # Pickup placements, m1 (medium)
        (
          # sabre ammo near the gate
          ( LASER_SABRE_AMMO 10 ( 512.0 0.0 -1024.5 ) ( 0.0 1.5707964 0.0 ) 30.0 ) # respawn 30 s
          ( "HEALTH PACK" 25 ( 12 0 -88.25 ) ( 0.0 0.0 0.0 ) 1.50 )

          # --- bridge ---

          # missile ammo
          ( MISSILE_AMMO 4 ( -300.0 12.5 640.0 ) ( 0.0 3.1415927 0.0 ) 60.0 )
          ( MISSILE_AMMO 4 ( -310.0 12.5 640.0 ) ( 0.0 3.1415927 0.0 ) 60.0 )
        )

        """.ReplaceLineEndings("\n");

    /// <summary>An animation-definition file in the ZrdText.Write layout, with comments added.</summary>
    private static readonly string Definitions = """
        # Animation definitions for m1
        ANIMATION_DEFINITIONS (
          GRAVITY ( -9.8 )
          # gate animations
          gate.flt (
            NAME ( gate_open ) # opens on trigger
            SPEED ( 1.0 )
            MAPS ( a.tif b.tif )
          )
          smoke.flt (
            NAME ( smoke1 )
          )
        )

        """.ReplaceLineEndings("\r\n");

    private static readonly string[] Inputs =
    [
        Pickups, Pickups.ReplaceLineEndings("\r\n"), Definitions, Definitions.ReplaceLineEndings("\n"),
        "", "# only a comment", "\n\n", "A B C 1 -2 +3 007 3.5 1e3 -0.0 2.50",
        "( f32:7FC00001 f32:FF800000 f32:00000001 -0.0 1.4E-45 1E-40 3.4028235E+38 )",
        "( ) () (\n) ( # inside\n)",
        "( ( ( ( 1 ) ) ) )# trailing comment without newline",
        "(a\"b\"c)\"d\"e(f)",
        "NAME \"a \\\"quoted\\\" \\\\ \\x7F \\xE9 #not a comment\" ( 1 2 3 )\r\n",
        "\t( 1\t2 )\v\f( 3 )",
    ];

    [Fact]
    public void ParsingMatchesZrdText()
    {
        foreach (string input in Inputs)
        {
            var syntax = ZrdTextSyntax.Parse(input, Token);
            Assert.Same(input, syntax.Text);
            Assert.True(ZrdTextSyntax.StructurallyEqual(ZrdText.Parse(input, Token), syntax.Root), input);
            Assert.Equal(ZrdText.Write(ZrdText.Parse(input, Token), Token), ZrdText.Write(syntax.Root, Token));
            // The byte form reads Latin-1 and writes it back unchanged.
            byte[] bytes = Encoding.Latin1.GetBytes(input);
            var fromBytes = ZrdTextSyntax.Parse(bytes, Token);
            Assert.Equal(input, fromBytes.Text);
            Assert.True(ZrdTextSyntax.StructurallyEqual(ZrdText.Parse(bytes, Token), fromBytes.Root));
            Assert.Equal(bytes, ZrdTextSyntax.Encode(fromBytes.Text));
        }
        // Values, not just agreement: raw bits, negative zero, subnormals and escapes.
        var floats = ZrdTextSyntax.Parse(Inputs[8], Token).Root.Children.Single().Children;
        Assert.Equal([0x7FC00001u, 0xFF800000u, 0x00000001u, 0x80000000u, 0x00000001u, BitConverter.SingleToUInt32Bits(1e-40f), 0x7F7FFFFFu], floats.Select(f => f.Bits));
        Assert.All(floats, f => Assert.Equal(ZrdKind.Float, f.Kind));
        var name = ZrdTextSyntax.Parse(Inputs[12], Token).Root.Children;
        Assert.Equal(3, name.Count);
        Assert.Equal("a \"quoted\" \\ \u007F \u00E9 #not a comment", name[1].Text);
        var adjacent = ZrdTextSyntax.Parse(Inputs[11], Token).Root.Children;
        Assert.Equal(["a", "b", "c"], adjacent[0].Children.Select(c => c.Text));
        Assert.Equal(["d", "e"], adjacent.Skip(1).Take(2).Select(c => c.Text));
        var empties = ZrdTextSyntax.Parse(Inputs[9], Token).Root.Children;
        Assert.Equal(4, empties.Count);
        Assert.All(empties, e => Assert.Empty(e.Children));
        Assert.Empty(ZrdTextSyntax.Parse("", Token).Root.Children);
        Assert.Throws<InvalidDataException>(() => ZrdTextSyntax.Encode("\u0100"));
    }

    [Fact]
    public void MalformedTextFailsExactlyAsZrdTextDoes()
    {
        string[] malformed =
        [
            "\"abc", "\"a\nb\"", "( 1 2", "( 1 ( 2 )", ")", "1 )", "\"\\q\"", "\"\\x4\"", "\"\\", "1x", "f32:123", "f32:7FC0000G",
            "1.5.5", "-1.5e", "1e99", "\u00E9", "a\"", "\"\u0100\"", "( 1 # )", "a:b",
            new string('(', ZrdText.MaximumDepth + 2) + new string(')', ZrdText.MaximumDepth + 2),
        ];
        foreach (string input in malformed)
        {
            string? expected = Failure(() => ZrdText.Parse(input, Token)), actual = Failure(() => ZrdTextSyntax.Parse(input, Token));
            Assert.True(expected != null, $"ZrdText accepted {input}");
            Assert.Equal(expected, actual);
        }
        // The deepest accepted nesting is the same too.
        string deepest = new string('(', ZrdText.MaximumDepth + 1) + new string(')', ZrdText.MaximumDepth + 1);
        Assert.True(ZrdTextSyntax.StructurallyEqual(ZrdText.Parse(deepest, Token), ZrdTextSyntax.Parse(deepest, Token).Root));
        // Oversized files are refused before they are decoded, with the same message.
        byte[] huge = new byte[SourceProject.MaximumSourceTextBytes + 1];
        Assert.Equal(Assert.Throws<InvalidDataException>(() => ZrdText.Parse(huge, Token)).Message, Assert.Throws<InvalidDataException>(() => ZrdTextSyntax.Parse(huge, Token)).Message);
        Assert.Throws<OperationCanceledException>(() => ZrdTextSyntax.Parse(Pickups, new CancellationToken(true)));
    }

    [Fact]
    public void EveryNodeSpansTextThatReadsBackAsThatNode()
    {
        foreach (string input in Inputs)
        {
            var syntax = ZrdTextSyntax.Parse(input, Token);
            Assert.Equal(new TextSpan(0, input.Length), syntax.SpanOf(syntax.Root.Id));
            foreach (var (node, parent) in Walk(syntax.Root).Skip(1))
            {
                var span = syntax.SpanOf(node.Id);
                string text = input.Substring(span.Start, span.Length);
                var read = ZrdText.Parse(text, Token);
                Assert.True(ZrdTextSyntax.StructurallyEqual(node, Assert.Single(read.Children)), text);
                if (node.Kind == ZrdKind.Array) { Assert.StartsWith("(", text); Assert.EndsWith(")", text); }
                // Children lie inside their parent, in order and without overlap.
                var outer = syntax.SpanOf(parent.Id);
                Assert.True(span.Start >= outer.Start && span.End <= outer.End);
                int index = parent.Children.ToList().IndexOf(node);
                if (index > 0) Assert.True(syntax.SpanOf(parent.Children[index - 1].Id).End <= span.Start);
            }
        }
        var pickups = ZrdTextSyntax.Parse(Pickups, Token);
        var health = pickups.Root.Children[0].Children[1];
        Assert.Equal("\"HEALTH PACK\"", Text(pickups, health.Children[0].Id));
        Assert.Equal("1.50", Text(pickups, health.Children[4].Id));
        Assert.Equal("( 12 0 -88.25 )", Text(pickups, health.Children[2].Id));
        Assert.Throws<ArgumentException>(() => pickups.SpanOf(Guid.NewGuid()));
        Assert.False(pickups.TryGetSpan(Guid.NewGuid(), out _));
        Assert.True(pickups.TryGetSpan(health.Id, out var recordSpan) && recordSpan.Length > 0);
    }

    [Fact]
    public void ReplacingScalarsChangesOnlyTheirTokens()
    {
        var syntax = ZrdTextSyntax.Parse(Pickups, Token);
        var records = syntax.Root.Children[0].Children;
        var sabre = records[0]; var health = records[1];
        // A whole-number coordinate becomes fractional; a float before a trailing comment; an integer in a row with a quoted string.
        string result = syntax.ReplaceScalars(new Dictionary<Guid, ZrdNode>
        {
            [health.Children[2].Children[0].Id] = Float(12.5f),
            [sabre.Children[4].Id] = Float(45f),
            [health.Children[1].Id] = Int(30),
            [health.Children[4].Id] = Float(1.5f), // the same value keeps its spelling "1.50"
        }, Token);
        Assert.Equal(Pickups
            .Replace("( 12 0 -88.25 )", "( 12.5 0 -88.25 )", StringComparison.Ordinal)
            .Replace("30.0 ) # respawn", "45.0 ) # respawn", StringComparison.Ordinal)
            .Replace("\"HEALTH PACK\" 25", "\"HEALTH PACK\" 30", StringComparison.Ordinal), result);
        var read = ZrdText.Parse(result, Token).Children[0].Children;
        Assert.Equal(ZrdKind.Float, read[1].Children[2].Children[0].Kind);
        Assert.Equal(12.5f, BitConverter.UInt32BitsToSingle(read[1].Children[2].Children[0].Bits));
        // Strings change quoting as needed, and non-finite floats use raw bits.
        result = syntax.ReplaceScalars(new Dictionary<Guid, ZrdNode>
        {
            [sabre.Children[0].Id] = Str("SABRE AMMO"),
            [health.Children[0].Id] = Str("HEALTH"),
            [sabre.Children[2].Children[2].Id] = Bits(0x7FC00001),
        }, Token);
        Assert.Equal(Pickups
            .Replace("( LASER_SABRE_AMMO 10 ( 512.0 0.0 -1024.5 )", "( \"SABRE AMMO\" 10 ( 512.0 0.0 f32:7FC00001 )", StringComparison.Ordinal)
            .Replace("\"HEALTH PACK\"", "HEALTH", StringComparison.Ordinal), result);
        // Replacing with identical values leaves the text untouched.
        Assert.Same(syntax.Text, syntax.ReplaceScalars(new Dictionary<Guid, ZrdNode> { [health.Children[4].Id] = Float(1.5f), [sabre.Children[1].Id] = Int(10) }, Token));
        Assert.Same(syntax.Text, syntax.ReplaceScalars(new Dictionary<Guid, ZrdNode>(), Token));
        // CRLF files keep their line endings.
        var crlf = ZrdTextSyntax.Parse(Definitions, Token);
        var speed = crlf.Root.Children[1].Children[3].Children[3].Children[0];
        Assert.Equal(Definitions.Replace("SPEED ( 1.0 )", "SPEED ( 2.0 )", StringComparison.Ordinal), crlf.ReplaceScalars(new Dictionary<Guid, ZrdNode> { [speed.Id] = Float(2f) }, Token));
    }

    [Fact]
    public void ReplacementsKeepAdjacentTokensApart()
    {
        var syntax = ZrdTextSyntax.Parse("(a\"b\"c)\"d\"e(f)", Token);
        var array = syntax.Root.Children[0];
        // A quoted value equal to its bare spelling is not a change.
        Assert.Same(syntax.Text, syntax.ReplaceScalars(new Dictionary<Guid, ZrdNode> { [array.Children[1].Id] = Str("b") }, Token));
        Assert.Equal("(a x c)\"d\"e(f)", syntax.ReplaceScalars(new Dictionary<Guid, ZrdNode> { [array.Children[1].Id] = Str("x") }, Token));
        Assert.Equal("(a\"b\"c)y e(f)", syntax.ReplaceScalars(new Dictionary<Guid, ZrdNode> { [syntax.Root.Children[1].Id] = Str("y") }, Token));
        Assert.Equal("(1 2 3)\"d\"e(f)", syntax.ReplaceScalars(new Dictionary<Guid, ZrdNode> { [array.Children[0].Id] = Int(1), [array.Children[1].Id] = Int(2), [array.Children[2].Id] = Int(3) }, Token));
        Assert.Equal("(\"x y\"\"b\"\"z z\")\"d\"e(f)", syntax.ReplaceScalars(new Dictionary<Guid, ZrdNode> { [array.Children[0].Id] = Str("x y"), [array.Children[2].Id] = Str("z z") }, Token));
    }

    [Fact]
    public void ReplacementsAreValidated()
    {
        var syntax = ZrdTextSyntax.Parse(Pickups, Token);
        var record = syntax.Root.Children[0].Children[0];
        Assert.Throws<ArgumentException>(() => syntax.ReplaceScalars(new Dictionary<Guid, ZrdNode> { [Guid.NewGuid()] = Int(1) }, Token));
        Assert.Throws<ArgumentException>(() => syntax.ReplaceScalars(new Dictionary<Guid, ZrdNode> { [record.Id] = Int(1) }, Token));
        Assert.Throws<ArgumentException>(() => syntax.ReplaceScalars(new Dictionary<Guid, ZrdNode> { [syntax.Root.Id] = Int(1) }, Token));
        Assert.Throws<ArgumentException>(() => syntax.ReplaceScalars(new Dictionary<Guid, ZrdNode> { [record.Children[1].Id] = Arr() }, Token));
        Assert.Throws<InvalidDataException>(() => syntax.ReplaceScalars(new Dictionary<Guid, ZrdNode> { [record.Children[0].Id] = Str("\u0100") }, Token));
        Assert.Throws<InvalidDataException>(() => syntax.ReplaceScalars(new Dictionary<Guid, ZrdNode> { [record.Children[0].Id] = Str(new string('x', 100)) }, Token, maximumCharacters: Pickups.Length));
        Assert.Throws<OperationCanceledException>(() => syntax.ReplaceScalars(new Dictionary<Guid, ZrdNode> { [record.Children[1].Id] = Int(1) }, new CancellationToken(true)));
    }

    [Fact]
    public void RewritingAnUnchangedTreeKeepsTheText()
    {
        foreach (string input in Inputs)
        {
            var syntax = ZrdTextSyntax.Parse(input, Token);
            Assert.Equal((input, true), syntax.Rewrite(syntax.Root, Token));
            // A rebuilt tree with the same ids is unchanged as well.
            Assert.Equal((input, true), syntax.Rewrite(Rebuild(syntax.Root), Token));
        }
    }

    [Fact]
    public void RewritingScalarEditsMatchesReplaceScalars()
    {
        var syntax = ZrdTextSyntax.Parse(Pickups, Token);
        var records = syntax.Root.Children[0].Children;
        Dictionary<Guid, ZrdNode> replacements = new()
        {
            [records[1].Children[2].Children[0].Id] = Float(12.5f),
            [records[0].Children[4].Id] = Float(45f),
            [records[0].Children[0].Id] = Str("SABRE AMMO"),
            [records[3].Children[1].Id] = Int(-7),
        };
        var edited = syntax.Root;
        foreach (var (id, value) in replacements) edited = Change(edited, id, n => value with { Id = n.Id });
        Assert.Equal((syntax.ReplaceScalars(replacements, Token), true), syntax.Rewrite(edited, Token));
        Assert.Equal((syntax.ReplaceScalars(replacements, Token), true), syntax.Rewrite(Rebuild(edited), Token));
        var adjacent = ZrdTextSyntax.Parse("(a\"b\"c)\"d\"e(f)", Token);
        Dictionary<Guid, ZrdNode> quoted = new() { [adjacent.Root.Children[0].Children[1].Id] = Str("x"), [adjacent.Root.Children[1].Id] = Str("y") };
        var bare = adjacent.Root;
        foreach (var (id, value) in quoted) bare = Change(bare, id, n => value with { Id = n.Id });
        Assert.Equal((adjacent.ReplaceScalars(quoted, Token), true), adjacent.Rewrite(bare, Token));
    }

    [Fact]
    public void InsertingARecordKeepsCommentsAndLayout()
    {
        var syntax = ZrdTextSyntax.Parse(Pickups, Token);
        var list = syntax.Root.Children[0];
        var added = Arr(Str("AMMO_BOX"), Int(5), Arr(Float(1f), Float(2f), Float(3f)), Arr(Float(0f), Float(0f), Float(0f)), Float(15f));
        var (text, lossless) = syntax.Rewrite(Change(syntax.Root, list.Id, l => l with { Children = [.. l.Children.Take(2), added, .. l.Children.Skip(2)] }), Token);
        Assert.True(lossless);
        Assert.Equal(Pickups.Replace("1.50 )\n", """
            1.50 )
              (
                AMMO_BOX
                5
                ( 1.0 2.0 3.0 )
                ( 0.0 0.0 0.0 )
                15.0
              )

            """.ReplaceLineEndings("\n"), StringComparison.Ordinal), text);
        // Appending at the end and at the front of the list, and at the root.
        (text, lossless) = syntax.Rewrite(syntax.Root with { Children = [Change(list, list.Id, l => l with { Children = [Int(1), .. l.Children, Int(2)] }), Str("EXTRA"), Arr(Int(1), Int(2))] }, Token);
        Assert.True(lossless);
        Assert.Equal(Pickups
            .Replace("(\n  # sabre", "(\n  1\n  # sabre", StringComparison.Ordinal)
            .Replace("60.0 )\n)\n", "60.0 )\n  2\n)\nEXTRA ( 1 2 )\n", StringComparison.Ordinal), text);
    }

    [Fact]
    public void DeletingARecordTakesItsOwnCommentsButNotTheSectionHeader()
    {
        var syntax = ZrdTextSyntax.Parse(Pickups, Token);
        var list = syntax.Root.Children[0];
        string Without(params int[] removed) => Expect(syntax.Rewrite(Change(syntax.Root, list.Id, l => l with { Children = l.Children.Where((_, i) => !removed.Contains(i)).ToArray() }), Token));
        Assert.Equal(Pickups.Replace("  # sabre ammo near the gate\n  ( LASER_SABRE_AMMO 10 ( 512.0 0.0 -1024.5 ) ( 0.0 1.5707964 0.0 ) 30.0 ) # respawn 30 s\n", "", StringComparison.Ordinal), Without(0));
        Assert.Equal("""
            # Pickup placements, m1 (medium)
            (
              # sabre ammo near the gate
              ( LASER_SABRE_AMMO 10 ( 512.0 0.0 -1024.5 ) ( 0.0 1.5707964 0.0 ) 30.0 ) # respawn 30 s
              ( "HEALTH PACK" 25 ( 12 0 -88.25 ) ( 0.0 0.0 0.0 ) 1.50 )

              # --- bridge ---

              ( MISSILE_AMMO 4 ( -310.0 12.5 640.0 ) ( 0.0 3.1415927 0.0 ) 60.0 )
            )

            """.ReplaceLineEndings("\n"), Without(2));
        Assert.Equal("""
            # Pickup placements, m1 (medium)
            (
              ( "HEALTH PACK" 25 ( 12 0 -88.25 ) ( 0.0 0.0 0.0 ) 1.50 )

              # --- bridge ---

            )

            """.ReplaceLineEndings("\n"), Without(0, 2, 3));
        Assert.Equal("# Pickup placements, m1 (medium)\n(\n\n  # --- bridge ---\n\n)\n", Without(0, 1, 2, 3));
        // Removing the value array of a key keeps the line structure.
        var definitions = ZrdTextSyntax.Parse(Definitions, Token);
        var gate = definitions.Root.Children[1].Children[3];
        Assert.Equal(Definitions.Replace("    MAPS ( a.tif b.tif )\r\n", "", StringComparison.Ordinal),
            Expect(definitions.Rewrite(Change(definitions.Root, gate.Id, g => g with { Children = g.Children.Take(4).ToArray() }), Token)));
        Assert.Equal(Definitions.Replace("    SPEED ( 1.0 )\r\n    MAPS", "    SPEED\r\n    MAPS", StringComparison.Ordinal),
            Expect(definitions.Rewrite(Change(definitions.Root, gate.Id, g => g with { Children = g.Children.Where((_, i) => i != 3).ToArray() }), Token)));
    }

    [Fact]
    public void ReorderingMovesRecordsWithTheirComments()
    {
        var syntax = ZrdTextSyntax.Parse(Pickups, Token);
        var list = syntax.Root.Children[0]; var c = list.Children;
        Assert.Equal("""
            # Pickup placements, m1 (medium)
            (
              ( "HEALTH PACK" 25 ( 12 0 -88.25 ) ( 0.0 0.0 0.0 ) 1.50 )
              # sabre ammo near the gate
              ( LASER_SABRE_AMMO 10 ( 512.0 0.0 -1024.5 ) ( 0.0 1.5707964 0.0 ) 30.0 ) # respawn 30 s

              # --- bridge ---

              # missile ammo
              ( MISSILE_AMMO 4 ( -300.0 12.5 640.0 ) ( 0.0 3.1415927 0.0 ) 60.0 )
              ( MISSILE_AMMO 4 ( -310.0 12.5 640.0 ) ( 0.0 3.1415927 0.0 ) 60.0 )
            )

            """.ReplaceLineEndings("\n"), Expect(syntax.Rewrite(Change(syntax.Root, list.Id, l => l with { Children = [c[1], c[0], c[2], c[3]] }), Token)));
        // Moving the last record to the front leaves the section header where it was.
        Assert.Equal("""
            # Pickup placements, m1 (medium)
            (
              ( MISSILE_AMMO 4 ( -310.0 12.5 640.0 ) ( 0.0 3.1415927 0.0 ) 60.0 )
              # sabre ammo near the gate
              ( LASER_SABRE_AMMO 10 ( 512.0 0.0 -1024.5 ) ( 0.0 1.5707964 0.0 ) 30.0 ) # respawn 30 s
              ( "HEALTH PACK" 25 ( 12 0 -88.25 ) ( 0.0 0.0 0.0 ) 1.50 )

              # --- bridge ---

              # missile ammo
              ( MISSILE_AMMO 4 ( -300.0 12.5 640.0 ) ( 0.0 3.1415927 0.0 ) 60.0 )
            )

            """.ReplaceLineEndings("\n"), Expect(syntax.Rewrite(Change(syntax.Root, list.Id, l => l with { Children = [c[3], c[0], c[1], c[2]] }), Token)));
        // One-line arrays stay on one line.
        var row = ZrdTextSyntax.Parse("( 1 2 3 ) # xyz\n", Token);
        var r = row.Root.Children[0].Children;
        Assert.Equal("( 3 1 2 ) # xyz\n", Expect(row.Rewrite(Change(row.Root, row.Root.Children[0].Id, a => a with { Children = [r[2], r[0], r[1]] }), Token)));
        Assert.Equal("( 0 1 2 3 4.5 ) # xyz\n", Expect(row.Rewrite(Change(row.Root, row.Root.Children[0].Id, a => a with { Children = [Int(0), .. r, Float(4.5f)] }), Token)));
        Assert.Equal("( 1 3 ) # xyz\n", Expect(row.Rewrite(Change(row.Root, row.Root.Children[0].Id, a => a with { Children = [r[0], r[2]] }), Token)));
    }

    [Fact]
    public void ReplacingASubtreeRewritesOnlyThatRecord()
    {
        var syntax = ZrdTextSyntax.Parse(Pickups, Token);
        var list = syntax.Root.Children[0];
        var replacement = Arr(Str("HEALTH"), Int(50));
        Assert.Equal(Pickups.Replace("  ( \"HEALTH PACK\" 25 ( 12 0 -88.25 ) ( 0.0 0.0 0.0 ) 1.50 )\n", "  ( HEALTH 50 )\n", StringComparison.Ordinal),
            Expect(syntax.Rewrite(Change(syntax.Root, list.Id, l => l with { Children = [l.Children[0], replacement, l.Children[2], l.Children[3]] }), Token)));
        // A node that keeps its id but changes kind is rewritten in its place, keeping the comments around it.
        var sabre = list.Children[0];
        Assert.Equal(Pickups.Replace("( LASER_SABRE_AMMO 10 ( 512.0 0.0 -1024.5 ) ( 0.0 1.5707964 0.0 ) 30.0 ) # respawn", "7 # respawn", StringComparison.Ordinal),
            Expect(syntax.Rewrite(Change(syntax.Root, sabre.Id, n => n with { Kind = ZrdKind.Int, Bits = 7, Children = [] }), Token)));
        // A record moved into another array keeps its own spelling.
        var health = list.Children[1];
        var moved = Change(syntax.Root, list.Id, l => l with { Children = l.Children.Where(n => n.Id != health.Id).ToArray() });
        moved = moved with { Children = [.. moved.Children, Str("SPARE"), Arr(health)] };
        Assert.Equal(Pickups.Replace("  ( \"HEALTH PACK\" 25 ( 12 0 -88.25 ) ( 0.0 0.0 0.0 ) 1.50 )\n", "", StringComparison.Ordinal) + "SPARE (\n  ( \"HEALTH PACK\" 25 ( 12 0 -88.25 ) ( 0.0 0.0 0.0 ) 1.50 )\n)\n",
            Expect(syntax.Rewrite(moved, Token)));
    }

    [Fact]
    public void RewritingNestedDefinitionsKeepsCrlfAndIndentation()
    {
        var syntax = ZrdTextSyntax.Parse(Definitions, Token);
        var definitions = syntax.Root.Children[1];
        var gate = definitions.Children[3];
        var speed = gate.Children[3].Children[0];
        var wave = Arr(Str("NAME"), Arr(Str("wave1")), Str("SPEED"), Arr(Float(0.5f)));
        var edited = Change(syntax.Root, speed.Id, n => Float(2.5f) with { Id = n.Id });
        edited = Change(edited, definitions.Id, d => d with { Children = [.. d.Children, Str("wave.flt"), wave] });
        Assert.Equal(Definitions
            .Replace("SPEED ( 1.0 )", "SPEED ( 2.5 )", StringComparison.Ordinal)
            .Replace("""
                    NAME ( smoke1 )
                  )
                )
                """.ReplaceLineEndings("\r\n"), """
                    NAME ( smoke1 )
                  )
                  wave.flt (
                    NAME ( wave1 )
                    SPEED ( 0.5 )
                  )
                )
                """.ReplaceLineEndings("\r\n"), StringComparison.Ordinal), Expect(syntax.Rewrite(edited, Token)));
        // An empty array that spans lines keeps its comment; a one-line empty array takes the canonical layout.
        var empty = ZrdTextSyntax.Parse("A (\n  # nothing yet\n)\nB ( )\n", Token);
        var filled = empty.Root with { Children = [empty.Root.Children[0], empty.Root.Children[1] with { Children = [Int(1)] }, empty.Root.Children[2], empty.Root.Children[3] with { Children = [Arr(Int(1), Arr(Int(2)))] }] };
        Assert.Equal("A (\n  1\n  # nothing yet\n)\nB (\n  (\n    1\n    ( 2 )\n  )\n)\n", Expect(empty.Rewrite(filled, Token)));
    }

    [Fact]
    public void RewriteHandlesTheEdgesOfAFile()
    {
        static string Edit(string text, Func<ZrdNode, ZrdNode[]> children)
        {
            var syntax = ZrdTextSyntax.Parse(text, Token);
            return Expect(syntax.Rewrite(syntax.Root with { Children = children(syntax.Root) }, Token));
        }
        // A comment at the end of the file is finished before anything follows it.
        Assert.Equal("A # c\nB\n", Edit("A # c", r => [.. r.Children, Str("B")]));
        Assert.Equal("B\nA # c", Edit("A # c", r => [Str("B"), .. r.Children]));
        Assert.Equal("# only a comment\nX\n", Edit("# only a comment", r => [Str("X")]));
        Assert.Equal("X\n", Edit("", r => [Str("X")]));
        Assert.Equal("A\n", Edit("A\nB", r => [r.Children[0]]));
        Assert.Equal("A\nB\nC\n", Edit("A\nB", r => [.. r.Children, Str("C")]));
        Assert.Equal("A\r\nX\r\nB\r\n", Edit("A\r\nB\r\n", r => [r.Children[0], Str("X"), r.Children[1]]));
        // Removing a token between two bare ones keeps them apart.
        Assert.Equal("(a c)", Edit("(a\"b\"c)", r => [r.Children[0] with { Children = [r.Children[0].Children[0], r.Children[0].Children[2]] }]));
        // The root is the file whatever its id.
        var syntax = ZrdTextSyntax.Parse(Pickups, Token);
        Assert.Equal((Pickups, true), syntax.Rewrite(syntax.Root with { Id = Guid.NewGuid() }, Token));
    }

    [Fact]
    public void RewriteFallsBackToTheCanonicalLayoutOnlyWhenItMust()
    {
        var syntax = ZrdTextSyntax.Parse("# " + new string('x', 200) + "\nA\n", Token);
        var edited = Change(syntax.Root, syntax.Root.Children[0].Id, n => Str("B") with { Id = n.Id });
        Assert.Equal(("B\n", false), syntax.Rewrite(edited, Token, maximumCharacters: 100));
        Assert.True(syntax.Rewrite(edited, Token).Lossless);
        Assert.Throws<InvalidDataException>(() => syntax.Rewrite(Change(syntax.Root, syntax.Root.Children[0].Id, n => Str("\u0100") with { Id = n.Id }), Token));
        Assert.Throws<InvalidDataException>(() => syntax.Rewrite(Int(1), Token));
        ZrdNode deep = Int(1);
        for (int i = 0; i <= ZrdText.MaximumDepth + 1; i++) deep = Arr(deep);
        Assert.Throws<InvalidDataException>(() => syntax.Rewrite(syntax.Root with { Children = [deep] }, Token));
        Assert.Throws<OperationCanceledException>(() => syntax.Rewrite(edited, new CancellationToken(true)));
    }

    [Fact]
    public void RandomEditsAlwaysRewriteLosslesslyToTheEditedTree()
    {
        for (int seed = 0; seed < 1000; seed++)
        {
            Random random = new(seed);
            string newline = random.Next(2) == 0 ? "\n" : "\r\n";
            var generated = Arr([.. Enumerable.Range(0, random.Next(0, 6)).Select(_ => RandomNode(random, 0))]);
            string text = Format(random, generated, newline);
            var syntax = ZrdTextSyntax.Parse(text, Token);
            Assert.True(ZrdTextSyntax.StructurallyEqual(generated, syntax.Root), $"seed {seed}: {text}");
            for (int round = 0; round < 4; round++)
            {
                var edited = syntax.Root; bool dropsComments = false, scalarsOnly = true;
                Dictionary<Guid, ZrdNode> scalars = [];
                for (int count = random.Next(1, 4); count > 0; count--) edited = Mutate(random, edited, scalars, ref dropsComments, ref scalarsOnly);
                if (random.Next(3) == 0) edited = Rebuild(edited);
                var (rewritten, lossless) = syntax.Rewrite(edited, Token);
                string context = $"seed {seed} round {round}:\n{syntax.Text}\n---\n{rewritten}";
                Assert.True(lossless, context);
                Assert.True(ZrdTextSyntax.StructurallyEqual(ZrdText.Parse(rewritten, Token), edited), context);
                if (scalarsOnly) Assert.Equal(syntax.ReplaceScalars(scalars, Token), rewritten);
                if (!dropsComments) Assert.Equal(Comments(syntax.Text).Order(), Comments(rewritten).Order());
                // Inserted lines follow the file's line endings.
                if (syntax.Text.Contains("\r\n", StringComparison.Ordinal)) Assert.False(rewritten.Replace("\r\n", "", StringComparison.Ordinal).Contains('\n'), context);
                syntax = ZrdTextSyntax.Parse(rewritten, Token);
            }
        }
    }

    private static string Expect((string Text, bool Lossless) result) { Assert.True(result.Lossless); return result.Text; }
    private static string? Failure(Action parse)
    {
        try { parse(); return null; }
        catch (InvalidDataException ex) { return ex.Message; }
    }
    private static string Text(ZrdTextSyntax syntax, Guid id) { var span = syntax.SpanOf(id); return syntax.Text.Substring(span.Start, span.Length); }
    private static ZrdNode Int(int value) => new(Guid.NewGuid(), ZrdKind.Int, unchecked((uint)value), "", []);
    private static ZrdNode Float(float value) => new(Guid.NewGuid(), ZrdKind.Float, BitConverter.SingleToUInt32Bits(value), "", []);
    private static ZrdNode Bits(uint bits) => new(Guid.NewGuid(), ZrdKind.Float, bits, "", []);
    private static ZrdNode Str(string value) => new(Guid.NewGuid(), ZrdKind.String, 0, value, []);
    private static ZrdNode Arr(params ZrdNode[] children) => new(Guid.NewGuid(), ZrdKind.Array, 0, "", children);
    /// <summary>The same tree as new objects with the same ids, as an editor that rebuilds every array produces.</summary>
    private static ZrdNode Rebuild(ZrdNode node) => node with { Children = node.Children.Select(Rebuild).ToArray() };

    /// <summary>Replaces the node <paramref name="id"/>, rebuilding only its ancestors so untouched subtrees stay shared.</summary>
    private static ZrdNode Change(ZrdNode node, Guid id, Func<ZrdNode, ZrdNode> edit)
    {
        if (node.Id == id) return edit(node);
        ZrdNode[]? children = null;
        for (int i = 0; i < node.Children.Count; i++)
        {
            var child = Change(node.Children[i], id, edit);
            if (!ReferenceEquals(child, node.Children[i])) { children ??= [.. node.Children]; children[i] = child; }
        }
        return children == null ? node : node with { Children = children };
    }

    private static IEnumerable<(ZrdNode Node, ZrdNode Parent)> Walk(ZrdNode root)
    {
        yield return (root, root);
        Stack<ZrdNode> pending = new([root]);
        while (pending.TryPop(out var parent))
            foreach (var child in parent.Children) { yield return (child, parent); pending.Push(child); }
    }

    /// <summary>The comments of a source text, read outside quoted strings.</summary>
    private static List<string> Comments(string text)
    {
        List<string> comments = [];
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '"') { for (i++; text[i] != '"'; i++) if (text[i] == '\\') i++; continue; }
            if (text[i] != '#') continue;
            int end = text.IndexOf('\n', i); if (end < 0) end = text.Length;
            comments.Add(text[i..end].TrimEnd('\r')); i = end;
        }
        return comments;
    }

    private static ZrdNode RandomNode(Random random, int depth) => random.Next(depth >= 4 ? 3 : 6) switch
    {
        0 => Int(random.Next(-1000, 1000)),
        1 => Bits(random.Next(6) switch { 0 => 0x80000000, 1 => 0x7FC00001, 2 => 0x00000001, 3 => 0xFF800000, _ => BitConverter.SingleToUInt32Bits((float)(random.NextDouble() * 2000 - 1000)) }),
        2 => Str(random.Next(6) switch { 0 => $"KEY{random.Next(100)}", 1 => "a b", 2 => "q\"uote\\#", 3 => "gate.flt", 4 => "", _ => "\u00E9t\u00E9\n" }),
        _ => Arr([.. Enumerable.Range(0, random.Next(0, 5)).Select(_ => RandomNode(random, depth + 1))]),
    };

    /// <summary>Hand-written-looking text for a tree: mixed line layouts, comments, blank lines, odd spellings and unspaced tokens.</summary>
    private static string Format(Random random, ZrdNode root, string newline)
    {
        StringBuilder text = new(); int comments = 0;
        if (random.Next(3) == 0) text.Append("# header ").Append(++comments).Append(newline).Append(newline);
        foreach (var child in root.Children) { Separate(0, true); Write(child, 0); }
        if (random.Next(3) == 0) text.Append(" # end ").Append(++comments);
        else if (random.Next(2) == 0) text.Append(newline);
        return text.ToString();

        void Write(ZrdNode node, int depth)
        {
            if (node.Kind != ZrdKind.Array) { Token(Spell(node)); return; }
            Token("(");
            bool lines = random.Next(2) == 0;
            if (lines && random.Next(3) == 0) text.Append(" # open ").Append(++comments).Append(newline);
            foreach (var child in node.Children) { Separate(depth + 1, lines); Write(child, depth + 1); }
            if (lines) text.Append(newline).Append(' ', depth * 2);
            else if (random.Next(2) == 0) text.Append(' ');
            Token(")");
        }
        void Separate(int depth, bool lines)
        {
            string indent = new(' ', depth * 2 + random.Next(2));
            switch (random.Next(lines ? 6 : 2))
            {
                case 0: break;
                case 1: text.Append(' '); break;
                case 2: text.Append(newline).Append(indent); break;
                case 3: text.Append(" # note ").Append(++comments).Append(newline).Append(indent); break;
                case 4: text.Append(newline).Append(newline).Append("# section ").Append(++comments).Append(newline).Append(newline).Append(indent); break;
                default: text.Append(newline).Append(indent).Append("# above ").Append(++comments).Append(newline).Append(indent); break;
            }
        }
        void Token(string value)
        {
            if (text.Length > 0 && IsWord(text[^1]) && IsWord(value[0])) text.Append(' ');
            text.Append(value);
        }
        string Spell(ZrdNode node)
        {
            string canonical = ZrdText.Scalar(node);
            return node.Kind switch
            {
                ZrdKind.Int when random.Next(3) == 0 => unchecked((int)node.Bits) >= 0 ? "+00" + canonical : canonical,
                ZrdKind.Float when random.Next(3) == 0 && canonical.Contains('.') && !canonical.Contains('E') => canonical + "00",
                ZrdKind.String when random.Next(3) == 0 && !canonical.StartsWith('"') => "\"" + canonical + "\"",
                _ => canonical,
            };
        }
        static bool IsWord(char c) => !char.IsWhiteSpace(c) && c is not ('(' or ')' or '"' or '#');
    }

    /// <summary>One random edit of the kinds the resource editor makes: values, types, inserts, deletes, moves and duplicates.</summary>
    private static ZrdNode Mutate(Random random, ZrdNode root, Dictionary<Guid, ZrdNode> scalars, ref bool dropsComments, ref bool scalarsOnly)
    {
        var all = Walk(root).ToList();
        var nodes = all.Skip(1).ToList();
        var arrays = all.Where(n => n.Node.Kind == ZrdKind.Array).ToList();
        int operation = random.Next(8);
        if (operation == 0)
        {
            var targets = nodes.Where(n => n.Node.Kind != ZrdKind.Array && !scalars.ContainsKey(n.Node.Id)).ToList();
            if (targets.Count > 0)
            {
                var target = targets[random.Next(targets.Count)].Node;
                var value = RandomScalar(random) with { Id = target.Id };
                scalars[target.Id] = value;
                return Change(root, target.Id, _ => value);
            }
            operation = 1;
        }
        scalarsOnly = false;
        if (nodes.Count == 0 || operation == 1)
        {
            var array = arrays[random.Next(arrays.Count)].Node;
            ZrdNode[] added = random.Next(3) == 0 ? [Str($"NEW{random.Next(100)}"), Arr([.. Enumerable.Range(0, random.Next(0, 4)).Select(_ => RandomNode(random, 2))])] : [RandomNode(random, 1)];
            int at = random.Next(array.Children.Count + 1);
            return Change(root, array.Id, a => a with { Children = [.. a.Children.Take(at), .. added, .. a.Children.Skip(at)] });
        }
        var (node, parent) = nodes[random.Next(nodes.Count)];
        int index = parent.Children.ToList().IndexOf(node);
        switch (operation)
        {
            case 2:
                dropsComments = true;
                return Change(root, parent.Id, p => p with { Children = p.Children.Where((_, i) => i != index).ToArray() });
            case 3:
                dropsComments = true;
                return Change(root, node.Id, _ => RandomNode(random, 1));
            case 4 when parent.Children.Count > 1:
            {
                int to = random.Next(parent.Children.Count);
                return Change(root, parent.Id, p => { var list = p.Children.Where((_, i) => i != index).ToList(); list.Insert(to, node); return p with { Children = list.ToArray() }; });
            }
            case 5:
            {
                var destinations = arrays.Where(a => node.Find(a.Node.Id) == null).ToList();
                var destination = destinations[random.Next(destinations.Count)].Node;
                dropsComments = true;
                var removed = Change(root, parent.Id, p => p with { Children = p.Children.Where((_, i) => i != index).ToArray() });
                return Change(removed, destination.Id, a => { var list = a.Children.ToList(); list.Insert(random.Next(list.Count + 1), node); return a with { Children = list.ToArray() }; });
            }
            case 6:
                dropsComments = true;
                return Change(root, node.Id, n => n.Kind == ZrdKind.Array ? RandomScalar(random) with { Id = n.Id } : n with { Kind = ZrdKind.Array, Bits = 0, Text = "", Children = [.. Enumerable.Range(0, random.Next(0, 3)).Select(_ => RandomNode(random, 3))] });
            default:
                return Change(root, parent.Id, p => p with { Children = [.. p.Children.Take(index + 1), node.Duplicate(), .. p.Children.Skip(index + 1)] });
        }
    }

    private static ZrdNode RandomScalar(Random random)
    {
        ZrdNode node;
        do node = RandomNode(random, 4); while (node.Kind == ZrdKind.Array);
        return node;
    }
}
