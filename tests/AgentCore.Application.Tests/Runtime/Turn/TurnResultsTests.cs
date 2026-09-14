using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Runtime;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tools;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Runtime.Turn;

/// <summary>
/// <see cref="TurnResults"/>: what the turn's tools answered, kept so a drawing script can read the
/// rows instead of a model retyping them.
/// </summary>
public sealed class TurnResultsTests
{
    private static readonly JsonNode Orders = JsonNode.Parse("""[{"id":"SO-1"},{"id":"SO-2"}]""")!;

    [Fact]
    public void ARecordedObject_IsTheLatestAndTheOnlyOne()
    {
        TurnResults results = new();

        results.Record("lookup_orders", Orders);

        var data = results.Data();
        Assert.Equal(Orders.ToJsonString(), data["lookup_orders"]!.ToJsonString());
        Assert.Equal($"[{Orders.ToJsonString()}]", data["$all"]!["lookup_orders"]!.ToJsonString());
        Assert.Equal(["lookup_orders"], results.Tools);
    }

    [Fact]
    public void ASecondRecord_BecomesTheLatest_AndBothStayInOrder()
    {
        TurnResults results = new();
        results.Record("lookup_order", JsonNode.Parse("""{"id":"SO-1"}""")!);

        results.Record("lookup_order", JsonNode.Parse("""{"id":"SO-2"}""")!);

        var data = results.Data();
        Assert.Equal("""{"id":"SO-2"}""", data["lookup_order"]!.ToJsonString());
        Assert.Equal("""[{"id":"SO-1"},{"id":"SO-2"}]""", data["$all"]!["lookup_order"]!.ToJsonString());
        Assert.Equal(["lookup_order"], results.Tools);
    }

    [Fact]
    public void TheDataHandedOut_IsACopy_SoAScriptCannotChangeWhatWasRecorded()
    {
        TurnResults results = new();
        results.Record("lookup_orders", Orders);

        results.Data()["lookup_orders"]!.AsArray().Clear();

        Assert.Equal(Orders.ToJsonString(), results.Data()["lookup_orders"]!.ToJsonString());
    }

    [Theory]
    [MemberData(nameof(StructuredResults))]
    public void AStructuredResult_IsRecorded_WhateverShapeTheToolAnsweredIn(object result, string expected)
    {
        TurnResults results = new();

        results.Record("tool", result);

        Assert.Equal(expected, results.Data()["tool"]!.ToJsonString());
    }

    public static TheoryData<object, string> StructuredResults => new()
    {
        { JsonNode.Parse("""{"a":1}""")!, """{"a":1}""" },
        { JsonSerializer.Deserialize<JsonElement>("""[1,2]"""), "[1,2]" },
        { """{"from":"a string holding json"}""", """{"from":"a string holding json"}""" },
        { new { name = "an object" }, """{"name":"an object"}""" },
    };

    [Theory]
    [MemberData(nameof(UnstructuredResults))]
    public void AnUnstructuredResult_IsNotRecorded(object? result)
    {
        TurnResults results = new();

        results.Record("tool", result);

        Assert.Empty(results.Tools);
        Assert.Null(results.Data()["tool"]);
    }

    public static TheoryData<object?> UnstructuredResults =>
    [
        (object?)null,
        "Order SO-1 shipped on Tuesday.",
        42,
        ToolErrorResult.Create("tool", "it failed."),
    ];

    [Fact]
    public async Task AnOutermostToolCall_IsRecordedUnderTheToolsName()
    {
        TurnResults results = new();
        var turn = new TurnInvocation { CallId = "call", TurnIndex = 0, Stage = "", Results = results };
        var tool = AIFunctionFactory.Create(() => Orders, "lookup_orders");

        await Run(tool, turn);

        Assert.Equal(["lookup_orders"], results.Tools);
        Assert.Equal(Orders.ToJsonString(), results.Data()["lookup_orders"]!.ToJsonString());
    }

    [Fact]
    public async Task AToolCallNestedInsideAnother_IsNotRecorded()
    {
        // The outer model never saw a nested result, so it cannot ask to draw it.
        TurnResults results = new();
        var turn = new TurnInvocation { CallId = "call", TurnIndex = 0, Stage = "", Results = results };
        var inner = AIFunctionFactory.Create(() => Orders, "inner_lookup");
        var outer = AIFunctionFactory.Create(
            async () =>
            {
                await Run(inner, turn with { Nested = true, Clarifications = null });
                return JsonNode.Parse("""{"outer":true}""");
            },
            "delegate");

        await Run(outer, turn);

        Assert.Equal(["delegate"], results.Tools);
    }

    private static async Task Run(AIFunction tool, TurnInvocation turn)
    {
        ToolCallingChatClient model = new("done.");
        using AuditingFunctionInvokingChatClient client = new(model);
        var options = new ChatOptions
        {
            Tools = [tool],
            AdditionalProperties = new AdditionalPropertiesDictionary { [TurnInvocation.ArgumentsKey] = turn },
        };

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "go")],
            options,
            TestContext.Current.CancellationToken);
    }
}
