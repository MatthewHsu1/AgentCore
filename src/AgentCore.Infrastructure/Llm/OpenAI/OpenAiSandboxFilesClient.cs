#pragma warning disable OPENAI001
#pragma warning disable MEAI001 // IHostedFileClient and HostedFileContent.Scope are evaluation-only in Microsoft.Extensions.AI 10.10.0.

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Responses;

namespace AgentCore.Infrastructure.Llm.OpenAI;

/// <summary>
/// Makes a file the OpenAI sandbox wrote look the way Microsoft.Extensions.AI says it should.
/// </summary>
internal sealed class OpenAiSandboxFilesClient : DelegatingChatClient
{
    private readonly IHostedFileClient _files;

    public OpenAiSandboxFilesClient(IChatClient inner, OpenAIClient openAi)
        : base(inner)
    {
        ArgumentNullException.ThrowIfNull(openAi);

        _files = openAi.AsIHostedFileClient();
    }

    /// <inheritdoc />
    public override object? GetService(Type serviceType, object? serviceKey = null)
        => serviceKey is null && serviceType == typeof(IHostedFileClient)
            ? _files
            : base.GetService(serviceType, serviceKey);

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);

        foreach (var message in response.Messages)
        {
            Surface(message.Contents);
        }

        return response;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            Surface(update.Contents);
            yield return update;
        }
    }

    /// <summary>Appends one <see cref="HostedFileContent"/> for every container-file citation in the list.</summary>
    private static void Surface(IList<AIContent> contents)
    {
        List<HostedFileContent>? found = null;

        foreach (var content in contents)
        {
            if (content is not TextContent { Annotations: { } annotations })
            {
                continue;
            }

            foreach (var annotation in annotations)
            {
                if (annotation.RawRepresentation is ContainerFileCitationMessageAnnotation raw)
                {
                    (found ??= []).Add(new HostedFileContent(raw.FileId) { Scope = raw.ContainerId, Name = raw.Filename });
                }
            }
        }

        if (found is null)
        {
            return;
        }

        foreach (var file in found)
        {
            contents.Add(file);
        }
    }
}
