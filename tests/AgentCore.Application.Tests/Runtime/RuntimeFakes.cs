using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Tools;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// A clock a test owns. The turn loop reads <c>conversationDurationSeconds</c> from a
    /// <see cref="TimeProvider"/>, so no test needs a stopwatch.
    /// </summary>
    internal sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            return _now;
        }

        /// <summary>Moves the clock forward.</summary>
        public void Advance(TimeSpan span)
        {
            _now = _now.Add(span);
        }
    }

    /// <summary>
    /// A deterministic offline model that answers one reply for each request, in order.
    /// </summary>
    internal sealed class SequencedChatClient(params string[] replies) : IChatClient
    {
        private readonly string[] _replies = replies;
        private int _calls;

        /// <summary>Gets the messages of each request, in call order.</summary>
        public List<List<ChatMessage>> Requests { get; } = [];

        /// <summary>Gets the options of each request, in call order. A ChatClientAgent sends its
        /// instructions here rather than as a message.</summary>
        public List<ChatOptions?> Options { get; } = [];

        /// <summary>Gets how many requests this client answered.</summary>
        public int Calls => Volatile.Read(ref _calls);

        /// <summary>Gets the text of the system messages of one request, joined by a newline.</summary>
        public string SystemText(int request)
        {
            lock (Requests)
            {
                return string.Join(
                    '\n',
                    Requests[request].Where(message => message.Role == ChatRole.System).Select(message => message.Text));
            }
        }

        /// <summary>Gets the text of the last user message of one request.</summary>
        public string LastUserText(int request)
        {
            lock (Requests)
            {
                return Requests[request].LastOrDefault(message => message.Role == ChatRole.User)?.Text ?? string.Empty;
            }
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);

            int index = Interlocked.Increment(ref _calls) - 1;
            lock (Requests)
            {
                Requests.Add([.. messages]);
                Options.Add(options);
            }

            await Task.Yield();

            string responseId = Guid.NewGuid().ToString("N");
            yield return new ChatResponseUpdate(ChatRole.Assistant, _replies[Math.Min(index, _replies.Length - 1)])
            {
                ResponseId = responseId,
                MessageId = responseId,
            };
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate? update in GetStreamingResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false))
            {
                updates.Add(update);
            }

            return updates.ToChatResponse();
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
            // Nothing to release.
        }
    }

    /// <summary>
    /// A model that yields lifecycle updates between its text fragments.
    /// </summary>
    internal sealed class LifecycleChatClient(params string[] fragments) : IChatClient
    {
        private readonly string[] _fragments = fragments;

        /// <summary>Gets how many updates this client yields, content and lifecycle together.</summary>
        public int Yielded { get; private set; }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);
            await Task.Yield();

            string responseId = Guid.NewGuid().ToString("N");
            Yielded = 0;

            foreach (string fragment in _fragments)
            {
                // A lifecycle event. It carries nothing at all.
                Yielded++;
                yield return new ChatResponseUpdate { ResponseId = responseId, MessageId = responseId };

                Yielded++;
                yield return new ChatResponseUpdate(ChatRole.Assistant, fragment)
                {
                    ResponseId = responseId,
                    MessageId = responseId,
                };

                // The other empty shape: one content, and it holds no text.
                Yielded++;
                yield return new ChatResponseUpdate(ChatRole.Assistant, string.Empty)
                {
                    ResponseId = responseId,
                    MessageId = responseId,
                };
            }
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate? update in GetStreamingResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false))
            {
                updates.Add(update);
            }

            return updates.ToChatResponse();
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
            // Nothing to release.
        }
    }

    /// <summary>
    /// A model that calls the first tool it is offered on every request, and never answers with text.
    /// </summary>
    internal sealed class LoopingToolCallingChatClient : IChatClient
    {
        private int _calls;

        /// <summary>Gets how many requests this client answered.</summary>
        public int Calls => Volatile.Read(ref _calls);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);

            int index = Interlocked.Increment(ref _calls);
            await Task.Yield();

            string responseId = Guid.NewGuid().ToString("N");
            if (options?.Tools?.OfType<AIFunction>().FirstOrDefault() is not { } tool)
            {
                // The extractor is offered no tool, so it still answers.
                yield return new ChatResponseUpdate(ChatRole.Assistant, "{}")
                {
                    ResponseId = responseId,
                    MessageId = responseId,
                };
                yield break;
            }

            yield return new ChatResponseUpdate(
                ChatRole.Assistant,
                [new FunctionCallContent($"conversation_{index}", tool.Name, new Dictionary<string, object?>(StringComparer.Ordinal))])
            {
                ResponseId = responseId,
                MessageId = responseId,
            };
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate? update in GetStreamingResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false))
            {
                updates.Add(update);
            }

            return updates.ToChatResponse();
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
            // Nothing to release.
        }
    }

    /// <summary>
    /// Builds one function for each declared tool, and every one of them throws a fault the model
    /// cannot answer.
    /// </summary>
    internal sealed class ThrowingToolBuilder
    {
        /// <summary>The message every fault carries.</summary>
        public const string Message = "the tool is down.";

        private int _calls;

        /// <summary>Gets how many times a tool of this factory ran.</summary>
        public int Calls => Volatile.Read(ref _calls);

        public AITool? Create(ToolConfiguration tool)
        {
            ArgumentNullException.ThrowIfNull(tool);
            return AIFunctionFactory.Create(Fail, tool.Id, tool.Description ?? tool.Id);
        }

        private string Fail()
        {
            _ = Interlocked.Increment(ref _calls);
            throw new TimeoutException(Message);
        }
    }


    /// <summary>
    /// Builds one function for each declared tool, and answers it with a fixed JSON document.
    /// </summary>
    /// <param name="result">The JSON document each tool answers.</param>
    /// <param name="asText">
    /// Whether the tool answers the document as one string. A real tool answers either way, because a
    /// tool result has no declared shape.
    /// </param>
    internal sealed class StubToolBuilder(string result, bool asText = false)
    {
        private readonly string _result = result;
        private readonly bool _asText = asText;

        /// <summary>Gets the id of each tool this factory ran, in call order.</summary>
        public List<string> Called { get; } = [];

        public AITool? Create(ToolConfiguration tool)
        {
            ArgumentNullException.ThrowIfNull(tool);

            string id = tool.Id;
            string description = tool.Description ?? id;

            return _asText
                ? AIFunctionFactory.Create(
                    () =>
                    {
                        Record(id);
                        return _result;
                    },
                    id,
                    description)
                : AIFunctionFactory.Create(
                () =>
                {
                    Record(id);
                    using JsonDocument document = JsonDocument.Parse(_result);
                    return document.RootElement.Clone();
                },
                id,
                description);
        }

        private void Record(string id)
        {
            lock (Called)
            {
                Called.Add(id);
            }
        }
    }

    /// <summary>
    /// A model that calls one tool by a name of the test's choosing, once, then answers with text.
    /// </summary>
    /// <param name="toolName">The name the model calls. It need not be a name the document declares.</param>
    /// <param name="reply">What it says once the tool round is over.</param>
    /// <param name="callsPerTurn">
    /// How many calls to that one name it emits in a single assistant message. Two reproduces the
    /// parallel-conversation case, where the name alone no longer identifies the conversation.
    /// </param>
    internal sealed class NamedToolCallingChatClient(string toolName, string reply, int callsPerTurn = 1) : IChatClient
    {
        private readonly string _toolName = toolName;
        private readonly string _reply = reply;
        private readonly int _callsPerTurn = callsPerTurn;
        private int _calls;

        /// <summary>Gets the call ids this client handed out, in the order it emitted them.</summary>
        public List<string> CallIds { get; } = [];

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);
            await Task.Yield();

            string responseId = Guid.NewGuid().ToString("N");
            bool answered = messages.Any(message => message.Contents.Any(content => content is FunctionResultContent));

            // The extractor is offered no tool and must still answer, so a request with no tool at all
            // never opens a tool round.
            if (answered || options?.Tools is not { Count: > 0 })
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, _reply)
                {
                    ResponseId = responseId,
                    MessageId = responseId,
                };
                yield break;
            }

            int round = Interlocked.Increment(ref _calls);
            List<AIContent> conversations = [];
            for (int index = 0; index < _callsPerTurn; index++)
            {
                // Every call of one message carries its own id, exactly as a vendor emits them. This is
                // the only thing that tells two calls to the same tool apart.
                string conversationId = $"conversation_{round}_{index}";
                lock (CallIds)
                {
                    CallIds.Add(conversationId);
                }

                conversations.Add(new FunctionCallContent(conversationId, _toolName, new Dictionary<string, object?>(StringComparer.Ordinal)));
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, conversations)
            {
                ResponseId = responseId,
                MessageId = responseId,
            };
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate? update in GetStreamingResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false))
            {
                updates.Add(update);
            }

            return updates.ToChatResponse();
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
            // Nothing to release.
        }
    }

    /// <summary>
    /// Builds one REAL <see cref="DeclaredTool"/> for each declared tool, and every one of them throws a
    /// fault the model cannot answer.
    /// </summary>
    internal sealed class UnreachableEndpointToolBuilder
    {
        /// <summary>The message every fault carries.</summary>
        public const string Message = "no such host";

        private int _calls;

        /// <summary>Gets how many times a tool of this factory ran.</summary>
        public int Calls => Volatile.Read(ref _calls);

        public AITool? Create(ToolConfiguration tool)
        {
            ArgumentNullException.ThrowIfNull(tool);
            return new UnreachableEndpointTool(tool, this);
        }

        private void Record()
        {
            _ = Interlocked.Increment(ref _calls);
        }

        private sealed class UnreachableEndpointTool(ToolConfiguration tool, UnreachableEndpointToolBuilder owner) : DeclaredTool(tool)
        {
            private readonly UnreachableEndpointToolBuilder _owner = owner;

            protected override ValueTask<object?> CallAsync(
                AIFunctionArguments arguments,
                CancellationToken cancellationToken)
            {
                _owner.Record();
                throw new HttpRequestException(Message);
            }
        }
    }

    /// <summary>
    /// Builds one REAL <see cref="DeclaredTool"/> for each declared tool, and every one of them throws a
    /// fault the model CAN answer.
    /// </summary>
    internal sealed class RefusedRequestToolBuilder
    {
        /// <summary>The message every fault carries.</summary>
        public const string Message = "the order is already closed.";

        private int _calls;

        /// <summary>Gets how many times a tool of this factory ran.</summary>
        public int Calls => Volatile.Read(ref _calls);

        public AITool? Create(ToolConfiguration tool)
        {
            ArgumentNullException.ThrowIfNull(tool);
            return new RefusedRequestTool(tool, this);
        }

        private void Record()
        {
            _ = Interlocked.Increment(ref _calls);
        }

        private sealed class RefusedRequestTool(ToolConfiguration tool, RefusedRequestToolBuilder owner) : DeclaredTool(tool)
        {
            private readonly RefusedRequestToolBuilder _owner = owner;

            protected override ValueTask<object?> CallAsync(
                AIFunctionArguments arguments,
                CancellationToken cancellationToken)
            {
                _owner.Record();
                throw new InvalidOperationException(Message);
            }
        }
    }

    /// <summary>
    /// A model that throws instead of answering, on both run shapes.
    /// </summary>
    internal sealed class ThrowingChatClient(Exception fault) : IChatClient
    {
        private readonly Exception _fault = fault;

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw _fault;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw _fault;
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
            // Nothing to release.
        }
    }

    /// <summary>
    /// A model that calls a scripted list of tools by name, one per request, then answers in words.
    /// </summary>
    internal sealed class ScriptedToolCallingChatClient(params (string Tool, string Arguments)[] script) : IChatClient
    {
        private readonly (string Tool, string Arguments)[] _script = script;
        private int _calls;

        /// <summary>Gets or sets the words the model ends on once the script is spent.</summary>
        public string? FinalText { get; set; }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);

            int index = Interlocked.Increment(ref _calls) - 1;
            await Task.Yield();

            string responseId = Guid.NewGuid().ToString("N");

            // Past the end of the script, or offered no tool at all on the cap's last request, it
            // answers in words. Answering with a conversation it cannot make would hang the loop.
            if (index >= _script.Length || options?.Tools is not { Count: > 0 })
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, FinalText ?? string.Empty)
                {
                    ResponseId = responseId,
                    MessageId = responseId,
                };
                yield break;
            }

            (string? tool, string? arguments) = _script[index];
            Dictionary<string, JsonElement> parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments)
                ?? throw new InvalidOperationException($"the script step for '{tool}' is not a JSON object.");

            Dictionary<string, object?> callArguments = new(StringComparer.Ordinal);
            foreach ((string? name, JsonElement value) in parsed)
            {
                callArguments[name] = value;
            }

            yield return new ChatResponseUpdate(
                ChatRole.Assistant,
                [new FunctionCallContent($"conversation_{index}", tool, callArguments)])
            {
                ResponseId = responseId,
                MessageId = responseId,
            };
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate? update in GetStreamingResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false))
            {
                updates.Add(update);
            }

            return updates.ToChatResponse();
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
        }
    }
}
