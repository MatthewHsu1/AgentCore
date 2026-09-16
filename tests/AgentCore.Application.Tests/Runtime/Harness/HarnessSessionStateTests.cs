using System.Text.Json;
using AgentCore.Application.Runtime;
using AgentCore.Application.Runtime.Harness;
using Microsoft.Agents.AI;
using Xunit;

namespace AgentCore.Application.Tests.Runtime.Harness;

/// <summary>
/// <see cref="HarnessSessionState.Capture"/> and <see cref="HarnessSessionState.Wrap"/> — the two
/// halves of moving MAF's per-provider state bag beside <c>CallSessionState</c>.
/// </summary>
public sealed class HarnessSessionStateTests
{
    [Fact]
    public void Capture_NullSession_IsEmpty()
        => Assert.Empty(HarnessSessionState.Capture(null, new HashSet<string>(StringComparer.Ordinal) { "TodoProvider" }));

    [Fact]
    public void Capture_EmptyKeys_IsEmpty()
    {
        var element = JsonDocument.Parse("""{"TodoProvider":{"items":[],"nextId":1}}""").RootElement;
        var session = new TestAgentSession(AgentSessionStateBag.Deserialize(element));

        Assert.Empty(HarnessSessionState.Capture(session, new HashSet<string>(StringComparer.Ordinal)));
    }

    [Fact]
    public void Capture_KeepsOnlyTheNamedKeys_AndClonesTheirRawText()
    {
        const string TodoJson = """{"items":[{"id":1,"title":"buy milk","isComplete":false}],"nextId":2}""";
        var element = JsonDocument.Parse(
            """{"TodoProvider":""" + TodoJson + ""","SomeForeignProvider":{"anything":1}}""").RootElement;

        var session = new TestAgentSession(AgentSessionStateBag.Deserialize(element));
        var keys = new HashSet<string>(StringComparer.Ordinal) { "TodoProvider" };

        var captured = HarnessSessionState.Capture(session, keys);

        var key = Assert.Single(captured.Keys);
        Assert.Equal("TodoProvider", key);
        Assert.Equal(
            JsonDocument.Parse(TodoJson).RootElement.GetRawText(),
            captured["TodoProvider"].GetRawText());
    }

    [Fact]
    public void Wrap_OneKey_ProducesTheStateBagEnvelope()
    {
        var element = JsonDocument.Parse("""{"items":[],"nextId":1}""").RootElement;
        var providers = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["TodoProvider"] = element };

        var wrapped = HarnessSessionState.Wrap(providers);

        Assert.Equal(
            JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["stateBag"] = new Dictionary<string, object>
                {
                    ["TodoProvider"] = new Dictionary<string, object> { ["items"] = Array.Empty<object>(), ["nextId"] = 1 },
                },
            }),
            wrapped.GetRawText());
    }

    private sealed class TestAgentSession : AgentSession
    {
        public TestAgentSession(AgentSessionStateBag stateBag)
            : base(stateBag)
        {
        }
    }
}
