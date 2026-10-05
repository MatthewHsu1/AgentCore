using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.BuiltIn;
using AgentCore.Application.Hooks.Notices;
using AgentCore.TestSupport;
using Xunit;
using AgentCore.Application.Runtime.Turn.Lifecycle;

namespace AgentCore.Application.Tests.Hooks
{
    public sealed class LoggingHookTests
    {
        // EventIds from Diagnostics/Log.cs: ExtractionFailed 1, ToolBudgetSpent 2, EmptyReply 3, PromptRefused 6,
        // ModerationUnavailable 7, TranscriptWriteFailed 9, StateRestorePartial 13, TranscriptResyncFailed 19,
        // TurnRunFaulted 28, TurnRefused 40.
        private static readonly HookScope Scope = new("c1", "main", 4, "", Guid.CreateVersion7(), 1, DateTimeOffset.UnixEpoch);

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public async Task AnEmptyTurnIsTheEmptyReplyLine()
        {
            RecordingLoggerFactory logs = new();
            await new LoggingHook(logs.CreateLogger("t")).OnTurnCompletedAsync(
                new TurnCompleted(Scope, TurnOutcome.Empty, "secret words", "", "", "", TimeSpan.Zero, TurnFailureReasons.EmptyReply, false, null), Ct);

            CapturedLine line = Assert.Single(logs.Of(3));
            Assert.Equal(4, line.Field<int>("TurnIndex"));
            Assert.DoesNotContain("secret words", line.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task AToolFallbackIsTheToolBudgetLineAndARunFallbackIsTheRunFaultLine()
        {
            RecordingLoggerFactory logs = new();
            LoggingHook hook = new(logs.CreateLogger("t"));
            InvalidOperationException toolFault = new("the tool spent its budget");
            InvalidOperationException runFault = new("the run threw");

            await hook.OnTurnCompletedAsync(new TurnCompleted(Scope, TurnOutcome.Fallback, "u", "r", "", "", TimeSpan.Zero, "why", true, toolFault), Ct);
            await hook.OnTurnCompletedAsync(new TurnCompleted(Scope, TurnOutcome.Fallback, "u", "r", "", "", TimeSpan.Zero, "why", false, runFault), Ct);

            Assert.Same(toolFault, Assert.Single(logs.Of(2)).Exception);
            Assert.Same(runFault, Assert.Single(logs.Of(28)).Exception);
        }

        [Fact]
        public async Task AnExtractionFaultIsTheExtractionLine()
        {
            RecordingLoggerFactory logs = new();
            await new LoggingHook(logs.CreateLogger("t")).OnFaultAsync(new Fault(Scope, FaultKind.ExtractionFailed, "the object was invalid", null), Ct);

            Assert.Equal("the object was invalid", Assert.Single(logs.Of(1)).Field<string>("Reason"));
        }

        // HookFailed is logged once by the engine itself; the logging hook does not log it again.
        [Fact]
        public async Task AHookFaultIsNotLoggedTwice()
        {
            RecordingLoggerFactory logs = new();
            await new LoggingHook(logs.CreateLogger("t")).OnFaultAsync(new Fault(Scope, FaultKind.HookFailed, "x failed", null), Ct);

            Assert.Empty(logs.Lines);
        }

        [Fact]
        public async Task ARefusalIsTheTurnRefusedLineWithItsToken()
        {
            RecordingLoggerFactory logs = new();
            await new LoggingHook(logs.CreateLogger("t")).OnTurnRefusedAsync(new TurnRefused(Scope, TurnRefusal.InUse, AfterEnd: false), Ct);

            Assert.Equal("in_use", Assert.Single(logs.Of(40)).Field<string>("Reason"));
        }

        [Fact]
        public async Task AFlaggedInputIsThePromptRefusedLineAndAnUnavailableOneSaysWhy()
        {
            RecordingLoggerFactory logs = new();
            LoggingHook hook = new(logs.CreateLogger("t"));

            await hook.OnInputModeratedAsync(new InputModerated(Scope, InputVerdict.Flagged, ["violence", "harassment"], null), Ct);
            await hook.OnInputModeratedAsync(new InputModerated(Scope, InputVerdict.Unavailable, [], ModerationUnavailableReason.TimedOut), Ct);

            Assert.Equal("violence,harassment", Assert.Single(logs.Of(6)).Field<string>("Categories"));
            Assert.Equal(TurnFailureReasons.ModerationTimedOut, Assert.Single(logs.Of(7)).Field<string>("Reason"));
        }

        [Fact]
        public async Task AStateRestoreFaultIsTheStateRestoreLine()
        {
            RecordingLoggerFactory logs = new();
            await new LoggingHook(logs.CreateLogger("t")).OnFaultAsync(new Fault(Scope, FaultKind.StateRestorePartial, "the document no longer declares the slot 'retired'.", null), Ct);

            Assert.Equal("the document no longer declares the slot 'retired'.", Assert.Single(logs.Of(13)).Field<string>("Reason"));
        }

        [Fact]
        public async Task StoreFaultsAreTheTranscriptLinesWithTheirExceptions()
        {
            RecordingLoggerFactory logs = new();
            LoggingHook hook = new(logs.CreateLogger("t"));
            IOException write = new("disk full");
            IOException read = new("disk gone");

            await hook.OnFaultAsync(new Fault(Scope, FaultKind.TranscriptWriteFailed, "IOException: disk full", write), Ct);
            await hook.OnFaultAsync(new Fault(Scope, FaultKind.TranscriptResyncFailed, "IOException: disk gone", read), Ct);

            Assert.Same(write, Assert.Single(logs.Of(9)).Exception);
            Assert.Same(read, Assert.Single(logs.Of(19)).Exception);
        }
    }
}
