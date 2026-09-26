using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Knowledge;
using AgentCore.Application.Tests.Knowledge.Fakes;
using AgentCore.TestSupport;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Xunit;
using static AgentCore.Application.Tests.Knowledge.KnowledgeProviderFactoryTestSupport;

namespace AgentCore.Application.Tests.Knowledge
{
    public sealed class KnowledgeProviderFactoryAuditTests
    {
        [Fact]
        public async Task Create_ASearchThatAnswered_WritesTheRetrievalRecord()
        {
            // A19. Ruling 21 gave KnowledgeAuditRecord a producer: without one the type shipped tested and
            // dead, and a retrieval left no artifact of any kind.
            RecordingLoggerFactory loggers = new();
            StubKnowledgePort port = new([Card("a"), Linked("z")]);

            AIContextProvider provider = KnowledgeProviderFactory.Create(
                port, Resolved(KnowledgeMode.Prefetch), "analyst", new SourceLocatorCitationFormatter(), loggers);
            _ = await InvokePrefetchAsync(provider, "the screen says e33", PrefetchTurn(), new StubSession());

            CapturedLine line = Assert.Single(loggers.Of(11));
            Assert.Equal("analyst", line.Field<string>("Agent"));
            Assert.Equal(2, line.Field<int>("CardCount"));

            KnowledgeAuditRecord.LogView? record = line.Field<KnowledgeAuditRecord.LogView>("Record");
            Assert.NotNull(record);
            // "link", not "see_also": the audit record says how the card arrived, in its own words,
            // rather than repeating one collection's payload key back at the reader.
            Assert.Equal(["ranked", "link"], record.Cards.Select(card => card.Via));
            Assert.Equal("analyst", record.Agent);
        }

        [Fact]
        public async Task Create_ASearchThatThrew_WritesTheCauseTheModelNeverSees()
        {
            // In tool mode the framework replaces the message with "Error: Function failed.", and in
            // prefetch mode this delegate answers a notice rather than throwing. Neither channel carries
            // the cause, so an outage during a live conversation turns on this one line existing.
            RecordingLoggerFactory loggers = new();
            InvalidOperationException down = new("qdrant is down");

            AIContextProvider provider = KnowledgeProviderFactory.Create(
                new ThrowingKnowledgePort(down), Resolved(KnowledgeMode.Prefetch), "resolver", new SourceLocatorCitationFormatter(), loggers);
            _ = await InvokePrefetchAsync(provider, "the screen says e33", PrefetchTurn(), new StubSession());

            CapturedLine line = Assert.Single(loggers.Of(12));
            Assert.Equal(LogLevel.Error, line.Level);
            Assert.Equal("resolver", line.Field<string>("Agent"));

            // The cause travels as the log's own exception argument, which is where a structured sink
            // stores a stack trace. Ruling 22 took the duplicate copy off the record's log view rather
            // than write the same trace into a message field as well.
            Assert.Same(down, line.Exception);
            Assert.Contains("qdrant is down", line.Exception!.ToString(), StringComparison.Ordinal);

            KnowledgeAuditRecord.LogView? record = line.Field<KnowledgeAuditRecord.LogView>("Record");
            Assert.NotNull(record);
            Assert.Equal("resolver", record.Agent);
        }

        [Fact]
        public async Task Create_NeitherLoggedRow_CarriesWhatTheCallerSaid()
        {
            // Ruling 22, and a regression this branch introduced. Query is the framework-composed search
            // input: the caller's current utterance plus, at RecentMessageMemoryLimit = 4, up to four
            // earlier messages. The failure row is an Error, which a default production configuration
            // keeps on, so logging the record whole copied every caller's words into a log store once per
            // agent per turn for as long as an outage lasted -- the exact thing Log.PromptRefused refuses
            // in the same file.
            const string spoken = "my ct900 shows e33 and my name is jane quimby on account 40771";

            RecordingLoggerFactory loggers = new();

            AIContextProvider answered = KnowledgeProviderFactory.Create(
                new StubKnowledgePort([Card("a")]), Resolved(KnowledgeMode.Prefetch), "analyst", new SourceLocatorCitationFormatter(), loggers);
            _ = await InvokePrefetchAsync(answered, spoken, PrefetchTurn(), new StubSession());

            AIContextProvider threw = KnowledgeProviderFactory.Create(
                new ThrowingKnowledgePort(new InvalidOperationException("qdrant is down")),
                Resolved(KnowledgeMode.Prefetch),
                "resolver",
                new SourceLocatorCitationFormatter(),
                loggers);
            _ = await InvokePrefetchAsync(threw, spoken, PrefetchTurn(), new StubSession());

            // Both rows were written -- a test that logged nothing would pass the assertions below
            // vacuously, and this is exactly the fact that must not pass vacuously.
            _ = Assert.Single(loggers.Of(11));
            _ = Assert.Single(loggers.Of(12));

            foreach (CapturedLine line in loggers.Lines)
            {
                // The formatted text AND every structured field: a sink reads the fields, not the string.
                Assert.DoesNotContain(spoken, line.Message, StringComparison.Ordinal);
                Assert.DoesNotContain(spoken, line.Exception?.ToString() ?? string.Empty, StringComparison.Ordinal);

                foreach (KeyValuePair<string, object?> field in line.Fields)
                {
                    Assert.DoesNotContain(
                        spoken, field.Value?.ToString() ?? string.Empty, StringComparison.Ordinal);
                }
            }
        }

        [Fact]
        public async Task Create_TheLoggedRow_CarriesTheQuerysLengthInstead()
        {
            // What replaces the text, and why a length rather than a hash: an outage is diagnosed by
            // whether the input was well formed. A zero-length query is a provider bug and a runaway one
            // is the recent-message concatenation gone wrong; a length answers both, and a hash answers
            // only "the same text again" while still being a per-caller correlation handle.
            const string spoken = "the screen says e33";

            RecordingLoggerFactory loggers = new();

            AIContextProvider provider = KnowledgeProviderFactory.Create(
                new StubKnowledgePort([Card("a")]), Resolved(KnowledgeMode.Prefetch), "analyst", new SourceLocatorCitationFormatter(), loggers);
            _ = await InvokePrefetchAsync(provider, spoken, PrefetchTurn(), new StubSession());

            KnowledgeAuditRecord.LogView? view = Assert.Single(loggers.Of(11)).Field<KnowledgeAuditRecord.LogView>("Record");
            Assert.NotNull(view);
            Assert.Equal(spoken.Length, view.QueryLength);
            Assert.Equal("analyst", view.Agent);
            Assert.Equal(KnowledgeMode.Prefetch, view.Mode);
        }
    }
}
