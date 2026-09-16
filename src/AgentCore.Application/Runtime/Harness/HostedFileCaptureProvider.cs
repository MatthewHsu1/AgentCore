#pragma warning disable MEAI001 // IHostedFileClient and HostedFileContent.Scope are evaluation-only in Microsoft.Extensions.AI 10.10.0.

using AgentCore.Application.Blobs;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime.Turn;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Runtime.Harness;

/// <summary>
/// Copies every file the vendor's sandbox wrote during a run into the call's blob store.
/// </summary>
/// <remarks>
/// <para>
/// The model takes no action. It writes a file under <c>/mnt/data</c> and links it as
/// <c>sandbox:/mnt/data/&lt;name&gt;</c>, as it does unprompted. After the run this provider reads
/// each <see cref="HostedFileContent"/> the vendor surfaced, downloads it, and stores it under
/// <c>(callId, name)</c>. The host rewrites the link later; the model never learns a route.
/// </para>
/// <para>
/// A file that cannot be kept is logged, never thrown: the reply still goes out, with a link the
/// host cannot resolve. Capture runs once per run, at its end. The sandbox container idles out
/// after twenty minutes; a run that idles longer than that before it ends would lose the file.
/// </para>
/// </remarks>
internal sealed class HostedFileCaptureProvider : AIContextProvider
{
    private const string Instructions =
        "When you produce a file (a chart, a PDF, a CSV), save it under /mnt/data with a short plain "
        + "file name, and refer to it in your reply as sandbox:/mnt/data/<name>.";

    private readonly IHostedFileClient _files;

    private readonly IBlobStore _blobs;

    private readonly BlobPolicy _policy;

    private readonly ILogger _logger;

    /// <summary>Creates the provider for one agent.</summary>
    /// <param name="files">The vendor's downloader, taken off the agent's chat client.</param>
    /// <param name="blobs">The store <c>providers.blobs</c> opened.</param>
    /// <param name="policy">The cap and the allowlist.</param>
    /// <param name="logger">Where a file that could not be kept is reported.</param>
    public HostedFileCaptureProvider(IHostedFileClient files, IBlobStore blobs, BlobPolicy policy, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(blobs);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(logger);

        _files = files;
        _blobs = blobs;
        _policy = policy;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override ValueTask<AIContext> InvokingCoreAsync(InvokingContext context, CancellationToken cancellationToken = default)
        => new(new AIContext { Instructions = Instructions });

    /// <inheritdoc />
    protected override async ValueTask InvokedCoreAsync(InvokedContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        List<HostedFileContent>? found = null;

        foreach (var message in context.ResponseMessages ?? [])
        {
            foreach (var file in FilesIn(message))
            {
                (found ??= []).Add(file);
            }
        }

        if (found is null)
        {
            return;
        }

        if (OwnerOf(context.Session) is not { } callId)
        {
            Log.SandboxFileHasNoOwner(_logger, context.Agent.Name ?? context.Agent.Id);
            return;
        }

        foreach (var file in found)
        {
            await KeepAsync(callId, file, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The call that owns this run's files: the filed turn, or the stamp a child session carries.</summary>
    private static string? OwnerOf(AgentSession? session)
    {
        if (TurnRegistry.For(session)?.CallId is { } fromTurn)
        {
            return fromTurn;
        }

        return session is not null && session.StateBag.TryGetValue<string>(BlobOwnerKey.Value, out var stamped)
            ? stamped
            : null;
    }

    /// <summary>Every sandbox file reference in one message: on the message, or inside an interpreter result.</summary>
    private static IEnumerable<HostedFileContent> FilesIn(ChatMessage message)
    {
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case HostedFileContent file:
                    yield return file;
                    break;

                case CodeInterpreterToolResultContent { Outputs: { } outputs }:
                    foreach (var output in outputs)
                    {
                        if (output is HostedFileContent nested)
                        {
                            yield return nested;
                        }
                    }

                    break;
            }
        }
    }

    private async ValueTask KeepAsync(string callId, HostedFileContent file, CancellationToken cancellationToken)
    {
        var name = file.Name ?? file.FileId;

        if (!BlobName.IsSafe(name))
        {
            Log.SandboxFileRefused(_logger, callId, name, "the name is not a plain file name");
            return;
        }

        try
        {
            await using var download = await _files
                .DownloadAsync(file.FileId, new HostedFileClientOptions { Scope = file.Scope }, cancellationToken)
                .ConfigureAwait(false);

            // The download stream rarely knows its length, and the policy needs one: read up to one
            // byte past the cap, so an oversized file is refused without buffering all of it.
            using MemoryStream buffer = new();
            await CopyCappedAsync(download, buffer, _policy.MaxBytes + 1, cancellationToken).ConfigureAwait(false);

            if (_policy.WhyRefused(name, buffer.Length) is { } reason)
            {
                Log.SandboxFileRefused(_logger, callId, name, reason);
                return;
            }

            buffer.Position = 0;

            var mediaType = download.MediaType ?? file.MediaType ?? "application/octet-stream";

            await _blobs
                .PutAsync(new BlobWrite(callId, name, mediaType, buffer, buffer.Length), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log.SandboxFileCaptureFailed(_logger, callId, name, exception);
        }
    }

    private static async ValueTask CopyCappedAsync(Stream source, Stream target, long cap, CancellationToken cancellationToken)
    {
        var chunk = new byte[81920];
        long copied = 0;

        while (copied < cap)
        {
            var wanted = (int)Math.Min(chunk.Length, cap - copied);
            var read = await source.ReadAsync(chunk.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                return;
            }

            await target.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            copied += read;
        }
    }
}
