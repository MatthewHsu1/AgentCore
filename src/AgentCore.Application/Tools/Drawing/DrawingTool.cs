using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tools.Builtin;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tools.Drawing;

/// <summary>The <c>ui.draw</c> built-in.</summary>
/// <remarks>
/// <para>
/// The tool the calling agent sees takes one string. The vocabulary of things that can be drawn —
/// 27 components and their props — never reaches that agent: it is the instructions of a second,
/// private model call this tool makes for itself, and it is written as prose rather than as a tool
/// schema. The shipped JSON Schema for the same vocabulary is 19,355 bytes and would ride every
/// request of every turn of every agent that may draw; the prose is a third of that and rides only
/// the drawing call.
/// </para>
/// <para>
/// The tree the inner model produces goes to <see cref="IRenderPort"/> and nowhere else — not into
/// the transcript, not into the audit record. The calling agent learns one line naming the buttons,
/// because a click comes back as caller words and it has to be able to read one.
/// </para>
/// </remarks>
internal sealed class DrawingTool
{
    /// <summary>The renderer name the browser looks up.</summary>
    internal const string RendererName = "generative-ui";

    /// <summary>The inner tool the drawing model calls. It is never declared in a document.</summary>
    internal const string PresentToolName = "present";

    /// <summary>
    /// How many times the drawing model is asked before this gives up.
    /// </summary>
    /// <remarks>
    /// Section 8.7 budgets 40 rounds for the calling agent, and this whole tool is one of them. A
    /// drawing model that cannot produce a valid tree in three tries is not going to.
    /// </remarks>
    private const int MaxRounds = 3;

    private const string VocabularyResource = "AgentCore.Application.Tools.Drawing.vocabulary.md";

    private static readonly Lazy<string> Vocabulary = new(ReadVocabulary);

    private readonly string _toolId;

    private readonly IChatClientFactory? _chatClients;

    private DrawingTool(string toolId, IChatClientFactory? chatClients)
    {
        _toolId = toolId;
        _chatClients = chatClients;
    }

    /// <summary>The prose vocabulary the drawing model is given as its instructions.</summary>
    internal static string VocabularyText => Vocabulary.Value;

    /// <summary>Builds the tool the model calls.</summary>
    /// <param name="tool">The declared tool.</param>
    /// <param name="chatClients">The factory this draws with, or <see langword="null"/> when the host bound none.</param>
    /// <returns>The tool.</returns>
    internal static AIFunction Create(ToolConfiguration tool, IChatClientFactory? chatClients)
        => AIFunctionFactory.Create(
            new DrawingTool(tool.Id, chatClients).DrawAsync,
            BuiltinToolOptions.Options(tool));

    private async Task<JsonObject> DrawAsync(
        [Description("What to draw, in words, and the data to draw it from. Whoever draws it cannot "
                     + "see the conversation, so anything it needs has to be in here.")]
        string request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request))
        {
            return ToolErrorResult.Create(_toolId, "the call filled no 'request', so there is nothing to draw.");
        }

        if (CallRenderScope.Current is not { } screen)
        {
            return ToolErrorResult.Create(
                _toolId,
                "this call has no screen, so nothing can be drawn on it. Say it in words instead.");
        }

        if (_chatClients is not { } clients)
        {
            return ToolErrorResult.Create(
                _toolId,
                "no model is configured to draw with. Say it in words instead.");
        }

        var present = BuildPresentTool();

        List<ChatMessage> messages = [new(ChatRole.User, request)];

        ChatOptions options = new()
        {
            Instructions = VocabularyText,
            Tools = [present],
            ToolMode = ChatToolMode.RequireAny,
        };

        string? lastFault = null;

        for (var round = 0; round < MaxRounds; round++)
        {
            ChatResponse response;

            try
            {
                response = await clients
                    .GetChatClient(null)
                    .GetResponseAsync(messages, options, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Section 8.7: a built-in answers with an error result and does not throw, because
                // ending the turn would leave the caller with silence.
                return ToolErrorResult.Create(
                    _toolId, $"the drawing model failed: {exception.Message}. Say it in words instead.");
            }

            if (FindPresentCall(response) is not { } call)
            {
                lastFault = $"no call to '{PresentToolName}' came back.";
                messages.AddRange(response.Messages);
                messages.Add(new ChatMessage(
                    ChatRole.User, $"You did not call {PresentToolName}. Call it once with the tree."));
                continue;
            }

            if (ReadTree(call) is not { } tree)
            {
                lastFault = "the tree was not a JSON object.";
                messages.AddRange(response.Messages);
                messages.Add(new ChatMessage(ChatRole.User, $"That was not a JSON object. {lastFault} Try again."));
                continue;
            }

            if (DrawingTree.Validate(tree) is { } fault)
            {
                lastFault = fault;
                messages.AddRange(response.Messages);
                messages.Add(new ChatMessage(ChatRole.User, $"That tree is not valid: {fault} Try again."));
                continue;
            }

            screen.Publish(RendererName, tree);

            return new JsonObject { ["drew"] = DrawingReceipt.Describe(tree) };
        }

        return ToolErrorResult.Create(
            _toolId,
            $"nothing could be drawn after {MaxRounds} tries: {lastFault} Say it in words instead.");
    }

    /// <summary>
    /// Declares the inner tool, and only the inner tool.
    /// </summary>
    /// <remarks>
    /// The argument is one free-form object. The shape it must hold is in the instructions, not
    /// here, which is the whole point of this design: a declared shape would cost the 19 KB this
    /// avoids. Nothing constrains the model's output as a result, so <see cref="DrawingTree.Validate"/>
    /// is the only thing standing between the model and the renderer.
    /// </remarks>
    private static AIFunction BuildPresentTool()
        => AIFunctionFactory.Create(
            ([Description("The tree to draw. One object with $type and its props, nested with children.")]
             JsonElement tree) => string.Empty,
            new AIFunctionFactoryOptions
            {
                Name = PresentToolName,
                Description = "Draw one tree for the caller. Call this once.",
                ExcludeResultSchema = true,
            });

    private static FunctionCallContent? FindPresentCall(ChatResponse response)
        => response.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>()
            .LastOrDefault(call => string.Equals(call.Name, PresentToolName, StringComparison.Ordinal));

    private static JsonObject? ReadTree(FunctionCallContent call)
    {
        if (call.Arguments is not { } arguments
            || !arguments.TryGetValue("tree", out var raw)
            || raw is null)
        {
            return null;
        }

        // The value arrives as whatever the provider deserialised the arguments into, so it is
        // normalised through JSON rather than cast.
        try
        {
            return JsonSerializer.SerializeToNode(raw) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ReadVocabulary()
    {
        var assembly = typeof(DrawingTool).GetTypeInfo().Assembly;
        using var stream = assembly.GetManifestResourceStream(VocabularyResource)
            ?? throw new InvalidOperationException(
                $"The embedded resource '{VocabularyResource}' is missing from the assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
