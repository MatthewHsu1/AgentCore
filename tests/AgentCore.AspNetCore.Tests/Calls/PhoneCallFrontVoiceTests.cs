using AgentCore.Application.Hooks.BuiltIn;
using AgentCore.AspNetCore.Calls;
using AgentCore.AspNetCore.Tests.Fakes;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Calls
{
    /// <summary>
    /// A vendor that speaks for itself (GPT-Live) puts its own lines in the history; every turn of its calls is told
    /// they were already heard, whether or not the call has a brief. A vendor that does not is told nothing.
    /// </summary>
    public sealed class PhoneCallFrontVoiceTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public async Task AFrontVoiceCallWithNoBriefStillTellsItsTurnsTheNote()
        {
            InstructionsSeenChatClient model = new(new StallOnCueChatClient());
            using PhoneCallHarness harness = PhoneCallHarness.Create(model, []);

            await AskAsync(harness, harness.Host with { FrontVoice = true });

            Assert.EndsWith("\n" + CallBriefHook.FrontVoiceNote, model.Instructions[^1], StringComparison.Ordinal);
        }

        [Fact]
        public async Task ACallWhoseVendorDoesNotSpeakForItselfIsToldNoNote()
        {
            InstructionsSeenChatClient model = new(new StallOnCueChatClient());
            using PhoneCallHarness harness = PhoneCallHarness.Create(model, []);

            await AskAsync(harness, harness.Host);

            Assert.DoesNotContain(CallBriefHook.FrontVoiceNote, model.Instructions[^1], StringComparison.Ordinal);
        }

        private static async Task AskAsync(PhoneCallHarness harness, PhoneCallHost host)
        {
            PhoneCall call = (await PhoneCall.AdmitAsync(host, PhoneCallHarness.Offer(), Ct)).Call!;
            await call.StartAsync(Ct);
            _ = await call.Asks.AskAsync("d1", "What are your hours?", [], _ => default, Ct);
        }
    }
}
