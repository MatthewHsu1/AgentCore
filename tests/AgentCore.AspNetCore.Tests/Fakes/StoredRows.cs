using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>Reads the rows a conversation wrote once its writes have settled.</summary>
    internal static class StoredRows
    {
        /// <summary>Waits for at least <paramref name="atLeast"/> rows, then a little longer for a stray second append.</summary>
        public static async Task<List<ConversationMessage>> SettleAsync(IServiceProvider services, string conversationId, int atLeast)
        {
            IConversationStore store = services.GetRequiredService<IConversationStore>();
            CancellationToken cancellationToken = TestContext.Current.CancellationToken;
            await Poll.UntilAsync(() => Read(store, conversationId).Count >= atLeast);

            await Task.Delay(300, cancellationToken);
            return Read(store, conversationId);
        }

        /// <summary>Names each row by role and contents, so a failed order check prints the whole turn.</summary>
        public static string Describe(IEnumerable<ConversationMessage> rows)
        {
            return string.Join(" | ", rows.Select(row => row.Content.Role + "[" + string.Join(",", row.Content.Contents.Select(content => content switch
            {
                TextContent text => "text:" + text.Text,
                FunctionCallContent call => "call:" + call.CallId,
                FunctionResultContent result => "result:" + result.CallId,
                _ => content.GetType().Name,
            })) + "]"));
        }

        /// <summary>Checks every row in order: its role, its words, and whether it carries a tool call or a tool result.</summary>
        public static void AssertTurn(List<ConversationMessage> rows, params (string Role, string Text, bool Call, bool Result)[] expected)
        {
            string all = Describe(rows);
            Assert.True(expected.Length == rows.Count, all);
            for (int index = 0; index < expected.Length; index++)
            {
                ChatMessage message = rows[index].Content;
                Assert.True(
                    expected[index] == (message.Role.Value, message.Text, message.Contents.OfType<FunctionCallContent>().Any(), message.Contents.OfType<FunctionResultContent>().Any()),
                    $"row {index}: {all}");
            }
        }

        private static List<ConversationMessage> Read(IConversationStore store, string conversationId)
        {
            try
            {
                return [.. store.ReadForSessionAsync(conversationId, TestContext.Current.CancellationToken).AsTask().GetAwaiter().GetResult()];
            }
            catch (InvalidOperationException)
            {
                return [];
            }
        }
    }
}
