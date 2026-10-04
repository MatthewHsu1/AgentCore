using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;

namespace AgentCore.TestSupport
{
    /// <summary>
    /// What every message store must do with a summary row: hand it to the session's read alone, leave it out
    /// of every consumer read, and take it on a truncate only when the cut reaches a row it covers.
    /// Each store's test class runs these against its own instance. This project carries no test
    /// framework, so a fact that fails throws <see cref="InvalidOperationException"/> naming what it saw.
    /// </summary>
    public static class ConversationStoreSummaryRowFacts
    {
        /// <summary>
        /// Seeds three plain turns (ordinals 0-5), a summary over 0-3 at ordinal 6, a fourth turn
        /// (7-8), a second summary over 0-5 at ordinal 9, and a fifth turn (10-11).
        /// </summary>
        public static async Task SeedAsync(IConversationStore store, CancellationToken cancellationToken)
        {
            _ = await store.CreateAsync("c1", cancellationToken);
            await TurnAsync(store, 0, cancellationToken);
            await TurnAsync(store, 1, cancellationToken);
            await TurnAsync(store, 2, cancellationToken);
            await SummaryAsync(store, turnIndex: 3, "first summary", coversUpTo: 3, cancellationToken);
            await TurnAsync(store, 3, cancellationToken);
            await SummaryAsync(store, turnIndex: 4, "second summary", coversUpTo: 5, cancellationToken);
            await TurnAsync(store, 4, cancellationToken);
        }

        [AssertionMethod]
        public static async Task SessionReadHandsBackTheNewestSummaryAndTheRowsAboveWhatItCovers(IConversationStore store, CancellationToken cancellationToken)
        {
            await SeedAsync(store, cancellationToken);

            IReadOnlyList<ConversationMessage> rows = await store.ReadForSessionAsync("c1", cancellationToken);

            ExpectOrdinals([7, 8, 9, 10, 11], rows, "the session read");
            ConversationMessage summary = rows.Single(row => row.CoversUpTo is not null);
            Expect("9/5/second summary", $"{summary.Ordinal}/{summary.CoversUpTo}/{summary.Content.Text}", "the summary the session read");
        }

        [AssertionMethod]
        public static async Task SessionReadWithNoSummaryHandsBackEveryRow(IConversationStore store, CancellationToken cancellationToken)
        {
            _ = await store.CreateAsync("c1", cancellationToken);
            await TurnAsync(store, 0, cancellationToken);
            await TurnAsync(store, 1, cancellationToken);

            ExpectOrdinals([0, 1, 2, 3], await store.ReadForSessionAsync("c1", cancellationToken), "the session read");
        }

        [AssertionMethod]
        public static async Task ConsumerReadsLeaveEverySummaryOut(IConversationStore store, CancellationToken cancellationToken)
        {
            await SeedAsync(store, cancellationToken);

            ExpectOrdinals([0, 1, 2, 3, 4, 5, 7, 8, 10, 11], await store.ReadAllAsync("c1", cancellationToken), "ReadAsync");
            ExpectOrdinals([7, 8, 10, 11], await store.ReadWindowAsync("c1", new TranscriptWindow(null, 2), cancellationToken), "ReadWindowAsync(2 turns)");
        }

        [AssertionMethod]
        public static async Task TruncateUnderWhatTheSummaryCoversTakesTheSummary(IConversationStore store, CancellationToken cancellationToken)
        {
            await SeedAsync(store, cancellationToken);

            // Cut at 4: rows 4-5 go, and so does the second summary (covers 5 >= 4). The first summary
            // (covers 3 < 4) stays, and the session read falls back to it.
            ConversationCut went = await store.TruncateAsync("c1", 4, cancellationToken);

            Expect("7", went.Rows.ToString(System.Globalization.CultureInfo.InvariantCulture), "rows the cut at 4 took");
            Expect("2-4", Turns(went), "turns the cut at 4 took");
            IReadOnlyList<ConversationMessage> rows = await store.ReadForSessionAsync("c1", cancellationToken);
            ExpectOrdinals([6], rows, "the session read after the cut at 4");
            Expect("3", rows[0].CoversUpTo?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null", "what the standing summary covers");
        }

        [AssertionMethod]
        public static async Task TruncateAboveWhatTheSummaryCoversKeepsTheSummaryWhateverItsOrdinal(IConversationStore store, CancellationToken cancellationToken)
        {
            await SeedAsync(store, cancellationToken);

            // Cut at 7: rows 7-8 and 10-11 go. Both summaries sit at ordinals >= 7 but cover rows < 7, so both stay.
            ConversationCut went = await store.TruncateAsync("c1", 7, cancellationToken);

            Expect("4", went.Rows.ToString(System.Globalization.CultureInfo.InvariantCulture), "rows the cut at 7 took");
            Expect("3-4", Turns(went), "turns the cut at 7 took");
            ExpectOrdinals([9], await store.ReadForSessionAsync("c1", cancellationToken), "the session read after the cut at 7");
            ExpectOrdinals([0, 1, 2, 3, 4, 5], await store.ReadAllAsync("c1", cancellationToken), "ReadAsync after the cut at 7");
        }

        [AssertionMethod]
        public static async Task TruncateAboveTheSummaryLeavesEverythingUnderItAlone(IConversationStore store, CancellationToken cancellationToken)
        {
            await SeedAsync(store, cancellationToken);

            ConversationCut went = await store.TruncateAsync("c1", 10, cancellationToken);

            Expect("2", went.Rows.ToString(System.Globalization.CultureInfo.InvariantCulture), "rows the cut at 10 took");
            Expect("4-4", Turns(went), "turns the cut at 10 took");
            ExpectOrdinals([7, 8, 9], await store.ReadForSessionAsync("c1", cancellationToken), "the session read after the cut at 10");

            // Nothing left above 10: a second cut there takes no row and names no turn.
            ConversationCut again = await store.TruncateAsync("c1", 10, cancellationToken);
            Expect("0/none", $"{again.Rows}/{Turns(again)}", "a cut with nothing above it");
        }

        [AssertionMethod]
        public static async Task OrdinalOfFindsASpokenRowAndNotASummary(IConversationStore store, CancellationToken cancellationToken)
        {
            await SeedAsync(store, cancellationToken);

            Expect("5", (await store.OrdinalOfAsync("c1", "m-2-a", cancellationToken))?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null", "the ordinal of turn 2's reply");
            Expect("null", (await store.OrdinalOfAsync("c1", "s-5", cancellationToken))?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null", "the ordinal of a summary row");
            Expect("null", (await store.OrdinalOfAsync("c1", "nobody", cancellationToken))?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null", "the ordinal of a message nobody said");
        }

        private static string Turns(ConversationCut cut)
        {
            return cut.Turns is { } turns ? $"{turns.First}-{turns.Last}" : "none";
        }

        private static void ExpectOrdinals(int[] expected, IReadOnlyList<ConversationMessage> rows, string what)
        {
            Expect(string.Join(",", expected), string.Join(",", rows.Select(row => row.Ordinal)), what);
        }

        private static void Expect(string expected, string actual, string what)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"{what}: expected [{expected}], got [{actual}].");
            }
        }

        private static async Task TurnAsync(IConversationStore store, int turnIndex, CancellationToken cancellationToken)
        {
            _ = await store.AppendAsync(
                "c1",
                [
                    new ConversationMessageDraft(turnIndex, new ChatMessage(ChatRole.User, $"q{turnIndex}"), $"m-{turnIndex}-q"),
                    new ConversationMessageDraft(turnIndex, new ChatMessage(ChatRole.Assistant, $"a{turnIndex}"), $"m-{turnIndex}-a"),
                ],
                state: null,
                cancellationToken);
        }

        private static async Task SummaryAsync(IConversationStore store, int turnIndex, string text, int coversUpTo, CancellationToken cancellationToken)
        {
            _ = await store.AppendAsync(
                "c1",
                [new ConversationMessageDraft(turnIndex, new ChatMessage(ChatRole.Assistant, text), $"s-{coversUpTo}") { CoversUpTo = coversUpTo }],
                state: null,
                cancellationToken);
        }
    }
}
