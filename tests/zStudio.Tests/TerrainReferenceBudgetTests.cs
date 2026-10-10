using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class TerrainReferenceBudgetTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Database = "data/m1/models/m1.gltf";

    [Theory]
    [InlineData("zrd", false)]
    [InlineData("zad", false)]
    [InlineData("zrd", true)]
    [InlineData("zad", true)]
    public void DecodedNameCapacityFailureRefusesInsteadOfConvertingAReferencedObject(string extension, bool compiled)
    {
        using SourceWorldFixture fixture = new();
        var database = JsonNode.Parse(File.ReadAllBytes(fixture.Path(Database)))!;
        database["nodes"]![0]!["name"] = "gate one";
        fixture.Write(Database, database.ToJsonString());
        byte[] original = File.ReadAllBytes(fixture.Path(Database));
        string file = $"data/m1/zrdr/reference.{extension}";
        Write("MODEL (\"gate one\") \"" + new string(' ', SourceTerrainConversion.MaximumNameCharacters + 1) + "\"");
        SourceWorkspace workspace = new(fixture.Project);

        // Parsing succeeds; the whole decoded string, rather than a lexical word, exceeds the name budget.
        var refusal = Assert.Throws<InvalidDataException>(() => SourceTerrainConversion.Plan(workspace, Database,
            SourceTerrainConversion.References(workspace, [file], Token), Token));
        Assert.Contains("characters of them", refusal.Message);
        Assert.Contains("terrain conversion", refusal.Message);
        Assert.Equal(0, workspace.Revision);
        Assert.False(workspace.CanUndo);
        Assert.Empty(workspace.DirtyFiles);
        Assert.Equal(original, File.ReadAllBytes(fixture.Path(Database)));

        // A valid operation immediately after the refusal still protects the full, space-containing identity.
        Write("MODEL (\"gate one\") \"short note\"");
        SourceWorkspace valid = new(fixture.Project);
        var plan = SourceTerrainConversion.Plan(valid, Database, SourceTerrainConversion.References(valid, [file], Token), Token);
        Assert.Equal(0, plan.Converted);
        Assert.Contains(plan.Kept, k => k.Node == "gate one" && k.Reason == "named by a script, resource or animation");

        void Write(string source)
        {
            var tree = ZrdText.Parse(source, Token);
            fixture.Write(file, compiled ? ZrdWriter.Write(tree, Token) : Encoding.Latin1.GetBytes(source));
        }
    }

    [Theory]
    [InlineData("zrd", "names")]
    [InlineData("zad", "names")]
    [InlineData("zrd", "patterns")]
    [InlineData("zad", "patterns")]
    [InlineData("zrd", "patternCharacters")]
    [InlineData("zad", "patternCharacters")]
    public void DecodedReferenceBudgetsAreNotMistakenForMalformedText(string extension, string budget)
    {
        using SourceWorldFixture fixture = new();
        string file = $"data/m1/zrdr/reference.{extension}";
        // Each lexical word remains well within its budget. Only decoded, space-containing strings overflow.
        string source = budget switch
        {
            "names" => string.Join(' ', Enumerable.Range(0, SourceTerrainConversion.MaximumNames + 1)
                .Select(i => $"\"{i % 512} {i / 512}\"")),
            "patterns" => string.Join(' ', Enumerable.Range(0, SourceTerrainConversion.MaximumPatterns + 1)
                .Select(i => $"\"* {i}\"")),
            _ => "\"*" + new string(' ', SourceTerrainConversion.MaximumPatternCharacters) + "\""
        };
        _ = ZrdText.Parse(source, Token);
        fixture.Write(file, source);
        SourceWorkspace workspace = new(fixture.Project);
        var refusal = Assert.Throws<InvalidDataException>(() => SourceTerrainConversion.References(workspace, [file], Token));
        Assert.Contains(budget == "names" ? "different words" : "wildcard patterns", refusal.Message);
        Assert.False(workspace.IsDirty);
    }

    [Theory]
    [InlineData("zrd")]
    [InlineData("zad")]
    public void MalformedTextKeepsItsExistingLexicalFallbackAndCancellationEscapes(string extension)
    {
        using SourceWorldFixture fixture = new();
        string file = $"data/m1/zrdr/reference.{extension}";
        fixture.Write(file, "MODEL ( gate pu*\n");
        SourceWorkspace workspace = new(fixture.Project);
        var references = SourceTerrainConversion.References(workspace, [file], Token);
        Assert.Contains("gate", references.Names);
        Assert.Contains(references.Patterns, p => p.IsMatch("pu1"));
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => SourceTerrainConversion.References(workspace, [file], canceled.Token));
        Assert.False(workspace.IsDirty);
    }
}
