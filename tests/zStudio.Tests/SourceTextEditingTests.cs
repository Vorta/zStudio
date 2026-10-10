using System.Text;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>The resource editor on a text .zrd source: edits keep the file's comments and layout, changing only what they change.</summary>
public sealed class SourceTextEditingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Source = "# Mission 1 vehicles\r\n(\r\n  tank_01 ( ltank ( 100.0 0.0 -200.0 ) 90.0 )   # guards the bridge\r\n  tank_02 ( ltank ( 150 0 -250 ) 180.0 )\r\n\r\n  # reinforcements\r\n  tank_03 ( ftank ( 0.0 0.0 0.0 ) 0.0 )\r\n)\r\n";

    private static ResourceEditSession Open() => new(FormatRegistry.Default.OpenBytes("aiv.zrd", Encoding.ASCII.GetBytes(Source), token: Token));

    [Fact]
    public async Task AScalarEditChangesOnlyItsToken()
    {
        var edits = Open();
        Assert.True(edits.IsSourceText);
        var member = edits.Current.Members.Single(); var root = edits.Tree(member, Token);
        // tank_02's X coordinate: an integer in the source.
        var x = root.Children[0].Children[3].Children[1].Children[0];
        edits.Accept(await edits.PrepareZrdAsync(member.Id, x.Id, "type", ZrdKind.Float, "175.5", token: Token));
        Assert.Equal(Source.Replace("( 150 0 -250 )", "( 175.5 0 -250 )"), Encoding.Latin1.GetString(edits.Current.Document.Bytes.Span));
    }

    [Fact]
    public async Task StructuralEditsKeepTheRestOfTheFile()
    {
        var edits = Open();
        var member = edits.Current.Members.Single(); var root = edits.Tree(member, Token);
        // Duplicating a placement record adds its lines; the comments and blank line stay.
        var list = root.Children[0];
        edits.Accept(await edits.PrepareZrdAsync(member.Id, list.Children[3].Id, "duplicate", token: Token));
        string text = Encoding.Latin1.GetString(edits.Current.Document.Bytes.Span);
        Assert.StartsWith("# Mission 1 vehicles\r\n(\r\n  tank_01 ( ltank ( 100.0 0.0 -200.0 ) 90.0 )   # guards the bridge\r\n", text);
        Assert.Contains("\r\n\r\n  # reinforcements\r\n", text);
        Assert.Equal(7, edits.Tree(edits.Current.Members.Single(), Token).Children[0].Children.Count);
        // The edited file parses to the edited tree.
        var reread = Recoil.Zbd.Core.Sources.ZrdText.Parse(text, Token);
        Assert.True(Recoil.Zbd.Core.Sources.ZrdTextSyntax.StructurallyEqual(reread, edits.Tree(edits.Current.Members.Single(), Token)));
        // Undo restores the exact original text.
        edits.UndoRedo(false);
        Assert.Equal(Source, Encoding.Latin1.GetString(edits.Current.Document.Bytes.Span));
    }
}
