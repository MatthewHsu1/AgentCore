using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Runtime;
using AgentCore.Application.State;
using AgentCore.Application.Tests.Fakes;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.State;

/// <summary>
/// The gate and the linker in the extractor path (design §6): what <c>StateExtractor.Write</c> does
/// with a <c>vocabulary:</c> slot's free-text answer, and the K19 guarantee that nothing changes for
/// a document that declares none.
/// </summary>
public sealed class StateExtractorLinkerTests
{
    private const string VocabularyYaml =
        """
        apiVersion: agentcore/v1
        name: linker
        state:
          applies_to: { type: string, writer: extractor, vocabulary: { from: knowledge } }
          brand:      { type: string, writer: extractor, enum: [f80, f65] }
        extractor:
          model: { ref: fill }
          when: after_reply
        agents:
          items:
            - { id: only }
        """;

    private const string UnknownLinkerYaml =
        """
        apiVersion: agentcore/v1
        name: unknown-linker
        state:
          applies_to: { type: string, writer: extractor, vocabulary: { from: knowledge, linker: mine } }
        extractor:
          model: { ref: fill }
          when: after_reply
        agents:
          items:
            - { id: only }
        """;

    private const string NoVocabularyYaml =
        """
        apiVersion: agentcore/v1
        name: no-vocabulary
        state:
          callerSaidGoodbye: { type: boolean, default: false, writer: extractor }
        extractor:
          model: { ref: fill }
          when: after_reply
        agents:
          items:
            - { id: only }
        """;

    private static readonly AgentCoreConfiguration VocabularyDocument = ConfigurationLoader.LoadYaml(VocabularyYaml);

    private static readonly AgentCoreConfiguration NoVocabularyDocument = ConfigurationLoader.LoadYaml(NoVocabularyYaml);

    private static readonly AgentCoreConfiguration UnknownLinkerDocument = ConfigurationLoader.LoadYaml(UnknownLinkerYaml);

    [Fact]
    public void ALinkedOutcome_WritesTheCollectionsSpelling()
    {
        var (extractor, state, clarifications) = Build("CT900", "CT900ENT");

        var result = extractor.Write(state, """{ "applies_to": "the CT900ENT" }""", clarifications);

        Assert.Equal(1, result.Filled);
        Assert.Equal("CT900ENT", state.Read("applies_to")?.GetValue<string>());
    }

    [Fact]
    public void AnAmbiguousOutcome_LeavesTheSlotUnfilledAndSetsThePendingList()
    {
        var (extractor, state, clarifications) = Build("CT900", "CT900ENT");

        var result = extractor.Write(state, """{ "applies_to": "the CT900" }""", clarifications);

        Assert.True(state.IsUnfilled("applies_to"));
        Assert.Equal(0, result.Filled);
        Assert.Equal(["CT900", "CT900ENT"], clarifications.Read("applies_to").Pending);
    }

    [Fact]
    public void APendingListAlreadySetByAnotherChannel_ANoMatchLeavesItAlone()
    {
        var (extractor, state, clarifications) = Build("CT900", "CT900ENT");
        clarifications.Update("applies_to", s => s.Pending = ["OTHER1", "OTHER2"]);

        extractor.Write(state, """{ "applies_to": "the elliptical machine" }""", clarifications);

        // NoMatch does nothing at all — the pending list survives untouched.
        Assert.Equal(["OTHER1", "OTHER2"], clarifications.Read("applies_to").Pending);
        Assert.True(state.IsUnfilled("applies_to"));
    }

    [Fact]
    public void AnAmbiguousOutcome_OverwritesAnExistingPendingListWithTheNewNearTie()
    {
        var (extractor, state, clarifications) = Build("CT900", "CT900ENT", "CT850", "CT850ENT");
        clarifications.Update("applies_to", s => s.Pending = ["CT900", "CT900ENT"]);

        extractor.Write(state, """{ "applies_to": "the CT850" }""", clarifications);

        // A caller who near-ties a second, different pair must not be silently dropped because
        // the first pair is still sitting unresolved: extraction runs after_reply, so this is the
        // caller's own later words beating whatever was pending (§7's recovery path).
        Assert.True(state.IsUnfilled("applies_to"));
        Assert.Equal(["CT850", "CT850ENT"], clarifications.Read("applies_to").Pending);
    }

    [Fact]
    public void ANoMatchOutcome_LeavesTheSlotAndThePendingListAlone()
    {
        var (extractor, state, clarifications) = Build("CT900", "CT900ENT");

        var result = extractor.Write(state, """{ "applies_to": "the elliptical machine" }""", clarifications);

        Assert.True(state.IsUnfilled("applies_to"));
        Assert.Equal(0, result.Filled);
        Assert.Null(clarifications.Read("applies_to").Pending);
    }

    [Fact]
    public void AWrittenSlot_NamedAgainByItsShorterPrefix_IsHeardAsANearTie_NotOverwritten()
    {
        var (extractor, state, clarifications) = Build("CT900", "CT900ENT");

        // Simulates a prior ask that named both candidates. Left in place, this would let the
        // tie-break in §6 step 4 fire on the second mention and silently replace the caller's
        // confirmed machine — exactly the bug K30's clear exists to prevent.
        clarifications.Update(
            "applies_to",
            slot => slot.LastNamed = Clarifications.LastNamed.Of(
                new HashSet<string>(StringComparer.Ordinal) { "CT900", "CT900ENT" }));

        extractor.Write(state, """{ "applies_to": "the CT900ENT" }""", clarifications);

        Assert.Equal("CT900ENT", state.Read("applies_to")?.GetValue<string>());
        Assert.Equal(Clarifications.LastNamedKind.None, clarifications.Read("applies_to").LastNamed.Kind);

        extractor.Write(state, """{ "applies_to": "the CT900" }""", clarifications);

        // Ambiguous, not Linked: LastNamed was cleared by the write above, so the shorter mention
        // cannot tie-break and the filled slot stands.
        Assert.Equal("CT900ENT", state.Read("applies_to")?.GetValue<string>());
        Assert.Equal(["CT900", "CT900ENT"], clarifications.Read("applies_to").Pending);
    }

    [Fact]
    public void AnEnumOnlySlot_ASuccessfulWrite_ClearsItsPendingListAndLastNamed()
    {
        // K30 is unconditional over every slot StateExtractor.Write iterates, enum:-only slots
        // included — not just the vocabulary: slots the linker touches. "brand" has a hand-written
        // enum: and never sees a linker (§6's closing paragraph), so this is the one channel that
        // can ever populate its Pending/LastNamed: a probe or a prior ask, simulated here directly.
        var (extractor, state, clarifications) = Build("CT900", "CT900ENT");

        clarifications.Update("brand", slot =>
        {
            slot.Pending = ["f80", "f65"];
            slot.LastNamed = Clarifications.LastNamed.Of(new HashSet<string>(StringComparer.Ordinal) { "f80", "f65" });
        });

        extractor.Write(state, """{ "brand": "f80" }""", clarifications);

        Assert.Equal("f80", state.Read("brand")?.GetValue<string>());
        Assert.Null(clarifications.Read("brand").Pending);
        Assert.Equal(Clarifications.LastNamedKind.None, clarifications.Read("brand").LastNamed.Kind);
    }

    [Fact]
    public void ANonStringAnswerToAVocabularySlot_StillGoesThroughTheLinker()
    {
        // The schema asks for a string, but nothing stops a model answering the bare number. Coerced
        // straight to "900" it would pass the gate, because "900" is a member — silently picking one
        // side of the near-tie the linker exists to refuse.
        var (extractor, state, clarifications) = Build("900", "900ENT");

        var result = extractor.Write(state, """{ "applies_to": 900 }""", clarifications);

        Assert.True(state.IsUnfilled("applies_to"));
        Assert.Equal(0, result.Filled);
        Assert.Equal(["900", "900ENT"], clarifications.Read("applies_to").Pending);
    }

    [Fact]
    public void ALinkerThatThrows_RefusesTheSlotAndNotTheTurn()
    {
        var client = new ScriptedChatClient("{}");
        var extractor = new StateExtractor(UnknownLinkerDocument, client, new StateValueLinkers([]));
        var state = new StateDocument(
            UnknownLinkerDocument,
            vocabulary: new Dictionary<string, VocabularyView>(StringComparer.Ordinal)
            {
                ["applies_to"] = ViewOf("CT900"),
            });

        // "mine" is registered nowhere, so Resolve throws. Section 8.7: that must cost the slot, not
        // the call.
        var result = extractor.Write(state, """{ "applies_to": "the CT900" }""", new Clarifications());

        Assert.True(result.Deserialized);
        Assert.Equal(1, result.Rejected);
        Assert.True(state.IsUnfilled("applies_to"));
    }

    [Fact]
    public async Task NoVocabularySlot_TheRequestIsByteIdenticalThroughTheInternalOverload()
    {
        RecordingChatClient viaPublicApi = new("""{ "callerSaidGoodbye": null }""");
        StateExtractor publicExtractor = new(NoVocabularyDocument, viaPublicApi);

        await publicExtractor.ExtractAsync(
            new StateDocument(NoVocabularyDocument),
            [new ChatMessage(ChatRole.User, "hello")],
            TestContext.Current.CancellationToken);

        RecordingChatClient viaInternalApi = new("""{ "callerSaidGoodbye": null }""");
        StateExtractor internalExtractor = new(NoVocabularyDocument, viaInternalApi, new StateValueLinkers([]));

        await internalExtractor.ExtractAsync(
            new StateDocument(NoVocabularyDocument),
            [new ChatMessage(ChatRole.User, "hello")],
            new Clarifications(),
            TestContext.Current.CancellationToken);

        Assert.NotNull(viaPublicApi.LastMessages);
        Assert.NotNull(viaInternalApi.LastMessages);
        Assert.Equal(Render(viaPublicApi.LastMessages!), Render(viaInternalApi.LastMessages!));
    }

    private static string Render(IEnumerable<ChatMessage> messages)
        => string.Join('\u001e', messages.Select(message => $"{message.Role}\u001f{message.Text}"));

    private static (
        StateExtractor Extractor,
        StateDocument State,
        Clarifications Clarifications) Build(params string[] vocabularyValues)
    {
        var client = new ScriptedChatClient("{}");
        var extractor = new StateExtractor(VocabularyDocument, client, new StateValueLinkers([]));
        var vocabulary = new Dictionary<string, VocabularyView>(StringComparer.Ordinal)
        {
            ["applies_to"] = ViewOf(vocabularyValues),
        };
        var state = new StateDocument(VocabularyDocument, vocabulary: vocabulary);

        return (extractor, state, new Clarifications());
    }

    private static VocabularyView ViewOf(params string[] originals)
    {
        Dictionary<string, string> normalisedToOriginal = new(StringComparer.Ordinal);
        foreach (var original in originals)
        {
            normalisedToOriginal[VocabularyFold.Fold(original)] = original;
        }

        return new VocabularyView { NormalisedToOriginal = normalisedToOriginal, Originals = originals };
    }

    private sealed class RecordingChatClient : IChatClient
    {
        private readonly string _reply;

        public RecordingChatClient(string reply) => _reply = reply;

        public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastMessages = [.. messages];
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _reply)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("StateExtractor never streams.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
            // Nothing to release.
        }
    }
}
