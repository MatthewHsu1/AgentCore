using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Runtime;
using AgentCore.Application.State;
using AgentCore.Application.Tests.Fakes;
using AgentCore.TestSupport;
using Xunit;

namespace AgentCore.Application.Tests.Runtime;

/// <summary>
/// Channel 1 across a turn the caller spent on something else. The instruction rides every turn
/// after the near-tie, whatever that turn says, so the reply — not the injection — is what charges
/// the ask: a turn answered with sympathy rather than the question must leave the slot free to ask
/// on the next turn that calls for it.
/// </summary>
public sealed class CallSessionOffTopicTurnTests
{
    private const string AppliesToDescription = "The model, as printed on the machine.";

    private const string Yaml =
        """
        apiVersion: agentcore/v1
        name: clarification-off-topic
        state:
          applies_to:
            type: string
            writer: extractor
            description: "The model, as printed on the machine."
            vocabulary: { from: knowledge }
        extractor:
          model: { ref: fill }
          when: after_reply
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          knowledge:
            kind: qdrant
            collection: kb
            fields: { body: text }
            scope:
              template: "facets.{key}"
              fromState: [applies_to]
              wildcard: { value: "*", facets: [applies_to] }
            ambiguity: { maxCandidates: 6, maxAsks: 2 }
        agents:
          items:
            - id: only
        """;

    [Fact]
    public async Task AnOffTopicTurn_DoesNotSpendTheQuestion()
    {
        using SequencedChatClient reply = new(
            "I can help with that.",
            """{"applies_to":"the CT900"}""",
            "I'm sorry to hear that. What is going wrong?",
            "{}",
            "Is it the ct900 or the ct900ent?",
            "{}",
            "Happy to help.",
            "{}");

        var session = Build(reply);
        var instruction = ClarificationText.Instruction(
            AppliesToDescription, ["ct900", "ct900ent"], 6, first: true);

        await session.RunTurnAsync("I have a CT900 and a CT900ENT", TestContext.Current.CancellationToken);
        Assert.Equal(string.Empty, reply.SystemText(0));

        await session.RunTurnAsync("i hate you", TestContext.Current.CancellationToken);
        Assert.Equal(instruction, reply.SystemText(2));

        // The turn answered the caller instead of putting the question, so the ask went uncharged and
        // the same instruction rides the next turn rather than the slot falling silent under K37.
        await session.RunTurnAsync("which belt does mine take?", TestContext.Current.CancellationToken);
        Assert.Equal(instruction, reply.SystemText(4));

        // That reply named both candidates, so the ask is charged and K37 suppresses the repeat.
        await session.RunTurnAsync("thanks", TestContext.Current.CancellationToken);
        Assert.Equal(string.Empty, reply.SystemText(6));
    }

    private static CallSession Build(SequencedChatClient reply)
    {
        var chatClients = new FakeChatClientFactory(reply);
        var compiled = ConfigurationCompiler.Compile(
            ConfigurationLoader.LoadYaml(Yaml), new AgentCompilationContext(chatClients));

        var extractor = CallSessionFactory.CreateExtractor(compiled, chatClients);
        VocabularyCache vocabulary = new();
        vocabulary.Replace("applies_to", ["ct900", "ct900ent"], maxValues: 2000);

        return new CallSessionFactory(
            compiled, new GuardEvaluator(compiled.Configuration.Guards), extractor, vocabulary: vocabulary)
            .Create("call-1");
    }
}
