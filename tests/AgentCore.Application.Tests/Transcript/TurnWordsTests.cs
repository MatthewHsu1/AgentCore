using AgentCore.Application.Transcript;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Cut;

namespace AgentCore.Application.Tests.Transcript
{
    /// <summary>
    /// The words a cut or failed turn of two steps writes: the user's message, step one's words before its tool
    /// call, the finished pair, then only the shown part of step two, or the fallback line.
    /// </summary>
    public sealed class TurnWordsTests
    {
        private static readonly ChatMessage User = new(ChatRole.User, "first question");

        private static readonly ChatMessage StepOne = new(
            ChatRole.Assistant,
            [new TextContent("Let me check."), new FunctionCallContent("call_1", "look_it_up")]);

        private static readonly ChatMessage Result = new(ChatRole.Tool, [new FunctionResultContent("call_1", "42")]);

        private const string Fallback = "Sorry, the order service did not answer.";

        [Fact]
        public void Compose_StoppedInStepTwo_KeepsEveryYieldedWordInItsStep()
        {
            List<ChatMessage> words = TurnWords.Compose(
                Cut(new TurnCut(ShownText: null, Played: null), StepOne, Result, new ChatMessage(ChatRole.Assistant, "Your order")),
                staged: []);

            Assert.Equal(["first question", "Let me check.", string.Empty, "Your order"], words.Select(message => message.Text));
            _ = Assert.Single(words[1].Contents.OfType<FunctionCallContent>());
            _ = Assert.Single(words[2].Contents.OfType<FunctionResultContent>());
        }

        [Fact]
        public void Compose_StoppedAfterTheToolResultBeforeStepTwoSpoke_KeepsStepOne()
        {
            List<ChatMessage> words = TurnWords.Compose(Cut(new TurnCut(ShownText: null, Played: null), StepOne, Result), staged: []);

            Assert.Equal(["first question", "Let me check.", string.Empty], words.Select(message => message.Text));
            _ = Assert.Single(words[1].Contents.OfType<FunctionCallContent>());
        }

        [Fact]
        public void Compose_HeardEndsInsideStepTwo_KeepsStepOneAndCutsStepTwo()
        {
            List<ChatMessage> words = TurnWords.Compose(
                Cut(new TurnCut("Let me check.Your order", TimeSpan.FromMilliseconds(500)), StepOne, Result, new ChatMessage(ChatRole.Assistant, "Your order ships today")),
                staged: []);

            Assert.Equal(["first question", "Let me check.", string.Empty, "Your order"], words.Select(message => message.Text));
        }

        [Fact]
        public void Compose_HeardEndsInsideStepOne_CutsStepOneAndDropsStepTwosWords()
        {
            List<ChatMessage> words = TurnWords.Compose(
                Cut(new TurnCut("Let me", TimeSpan.FromMilliseconds(300)), StepOne, Result, new ChatMessage(ChatRole.Assistant, "Your order")),
                staged: []);

            Assert.Equal(["first question", "Let me", string.Empty], words.Select(message => message.Text));
            _ = Assert.Single(words[1].Contents.OfType<FunctionCallContent>());
        }

        [Fact]
        public void Compose_StepTwoCallStillRunning_DropsThatCallAndKeepsItsWordsAsShown()
        {
            ChatMessage stepTwo = new(
                ChatRole.Assistant,
                [new TextContent("One more look."), new FunctionCallContent("call_2", "look_it_up")]);

            List<ChatMessage> words = TurnWords.Compose(
                Cut(new TurnCut("Let me check.One more look.", TimeSpan.FromMilliseconds(900)), StepOne, Result, stepTwo),
                staged: []);

            Assert.Equal(["first question", "Let me check.", string.Empty, "One more look."], words.Select(message => message.Text));
            Assert.Empty(words[3].Contents.OfType<FunctionCallContent>());
        }

        [Fact]
        public void Compose_NothingShown_KeepsOnlyTheFinishedPair()
        {
            List<ChatMessage> words = TurnWords.Compose(Cut(new TurnCut(string.Empty, Played: null), StepOne, Result), staged: []);

            Assert.Equal(["first question", string.Empty, string.Empty], words.Select(message => message.Text));
            _ = Assert.Single(words[1].Contents.OfType<FunctionCallContent>());
        }

        [Fact]
        public void Compose_HeardEndsWithASpace_StoresItTrimmed()
        {
            List<ChatMessage> words = TurnWords.Compose(
                Cut(new TurnCut("Hello ", Played: null), new ChatMessage(ChatRole.Assistant, "Hello there")),
                staged: []);

            Assert.Equal(["first question", "Hello"], words.Select(message => message.Text));
        }

        [Fact]
        public void Compose_ToolFaultInStepTwo_KeepsStepOneThePairAndTheFallbackInOrder()
        {
            List<ChatMessage> words = TurnWords.Compose(
                Failed(StepOne, Result, new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call_2", "look_it_up")]), new ChatMessage(ChatRole.Assistant, Fallback)),
                staged: []);

            Assert.Equal(["first question", "Let me check.", string.Empty, Fallback], words.Select(message => message.Text));
            _ = Assert.Single(words[1].Contents.OfType<FunctionCallContent>());
            _ = Assert.Single(words[2].Contents.OfType<FunctionResultContent>());
            Assert.Equal(ChatRole.Assistant, words[3].Role);
        }

        [Fact]
        public void Compose_FallbackStreamedIntoStepTwosMessage_StillWritesItOnceAsItsOwnMessage()
        {
            ChatMessage stepTwo = new(
                ChatRole.Assistant,
                [new FunctionCallContent("call_2", "look_it_up"), new TextContent(Fallback)]);

            List<ChatMessage> words = TurnWords.Compose(Failed(StepOne, Result, stepTwo), staged: []);

            Assert.Equal(["first question", "Let me check.", string.Empty, Fallback], words.Select(message => message.Text));
        }

        [Fact]
        public void Compose_ToolFaultInStepOneWithNoWords_KeepsOnlyTheFallback()
        {
            List<ChatMessage> words = TurnWords.Compose(
                Failed(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call_1", "look_it_up")]), new ChatMessage(ChatRole.Assistant, Fallback)),
                staged: []);

            Assert.Equal(["first question", Fallback], words.Select(message => message.Text));
        }

        [Fact]
        public void Compose_EmptyReplyWhoseOnlyWordsAreTheFallback_WritesTheFallbackOnce()
        {
            List<ChatMessage> words = TurnWords.Compose(Failed(new ChatMessage(ChatRole.Assistant, Fallback)), staged: []);

            Assert.Equal(["first question", Fallback], words.Select(message => message.Text));
        }

        [Fact]
        public void Compose_FailedWithNoFallbackLine_AddsNoEmptyMessage()
        {
            List<ChatMessage> words = TurnWords.Compose(
                new TurnCommit(User) { Seen = new AgentResponse([StepOne, Result]), Reply = string.Empty, Completed = false },
                staged: []);

            Assert.Equal(["first question", "Let me check.", string.Empty], words.Select(message => message.Text));
            _ = Assert.Single(words[2].Contents.OfType<FunctionResultContent>());
        }

        private static TurnCommit Failed(params ChatMessage[] seen)
        {
            return new TurnCommit(User) { Seen = new AgentResponse([.. seen]), Reply = Fallback, Completed = false };
        }

        private static TurnCommit Cut(TurnCut cut, params ChatMessage[] seen)
        {
            return new TurnCommit(User) { Seen = new AgentResponse([.. seen]), Cut = cut, Completed = false };
        }
    }
}
