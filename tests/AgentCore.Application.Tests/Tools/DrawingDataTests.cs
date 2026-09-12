using System.Text.Json.Nodes;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tools.Drawing;
using Xunit;

namespace AgentCore.Application.Tests.Tools;

/// <summary>
/// <see cref="DrawingData"/>: the few lines that tell the drawing model what its script's
/// <c>data</c> holds, so it never needs the rows themselves.
/// </summary>
public sealed class DrawingDataTests
{
    [Fact]
    public void AnArrayOfObjects_IsDescribedByCountFieldsAndThreeSamples()
    {
        TurnResults results = new();
        results.Record("lookup_orders", Orders(100));

        var text = DrawingData.Describe(results)!;

        Assert.Contains("`data.lookup_orders`", text, StringComparison.Ordinal);
        Assert.Contains("100", text, StringComparison.Ordinal);
        Assert.Contains("id, customer, total", text, StringComparison.Ordinal);
        Assert.Contains("SO-1003", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SO-1004", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnObject_IsDescribedByItsFieldsAndItself()
    {
        TurnResults results = new();
        results.Record("lookup_order", JsonNode.Parse("""{"id":"SO-7","total":12.5}""")!);

        var text = DrawingData.Describe(results)!;

        Assert.Contains("`data.lookup_order`", text, StringComparison.Ordinal);
        Assert.Contains("an object", text, StringComparison.Ordinal);
        Assert.Contains("id, total", text, StringComparison.Ordinal);
        Assert.Contains("SO-7", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AToolCalledTwice_PointsAtAllOfItsAnswers()
    {
        TurnResults results = new();
        results.Record("lookup_order", JsonNode.Parse("""{"id":"SO-1"}""")!);
        results.Record("lookup_order", JsonNode.Parse("""{"id":"SO-2"}""")!);

        var text = DrawingData.Describe(results)!;

        Assert.Contains("2 times", text, StringComparison.Ordinal);
        Assert.Contains("`data.$all.lookup_order`", text, StringComparison.Ordinal);
        Assert.Contains("SO-2", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ALongString_IsCutShort()
    {
        TurnResults results = new();
        var essay = new string('x', 300);
        results.Record("fetch_page", JsonNode.Parse($$"""{"body":"{{essay}}"}""")!);

        var text = DrawingData.Describe(results)!;

        Assert.DoesNotContain(essay, text, StringComparison.Ordinal);
        Assert.Contains("…", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DeepNesting_IsElided()
    {
        TurnResults results = new();
        results.Record("tree", JsonNode.Parse("""{"a":{"b":{"c":{"secret":"deep"}}}}""")!);

        var text = DrawingData.Describe(results)!;

        Assert.DoesNotContain("deep", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NoResults_DescribesNothing()
    {
        Assert.Null(DrawingData.Describe(new TurnResults()));
        Assert.Null(DrawingData.Describe(null));
    }

    private static JsonArray Orders(int count)
        => new([.. Enumerable.Range(1, count).Select(i => (JsonNode)new JsonObject
        {
            ["id"] = $"SO-{1000 + i}",
            ["customer"] = $"Customer {i}",
            ["total"] = 10 * i,
        })]);
}
