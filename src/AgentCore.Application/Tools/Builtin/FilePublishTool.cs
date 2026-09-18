using System.ComponentModel;
using System.Text.Json.Nodes;
using AgentCore.Application.Blobs;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Runtime.Harness;
using AgentCore.Application.Transcript;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Tools.Builtin;

/// <summary>
/// Copies one file out of the running call's workspace into the blob store, owned by the call, and
/// files a <see cref="FileContent"/> on the turn so the card reaches the person.
/// </summary>
/// <remarks>
/// A background child runs with no turn, so it has no drain to file the card on: its blob is still
/// stored under the parent call, and the link goes back to the child in the result alone.
/// </remarks>
internal sealed class FilePublishTool
{
    private readonly IBlobStore _blobs;

    private readonly BlobPolicy _policy;

    private readonly string _workspaceRoot;

    private readonly ILogger _logger;

    private readonly ToolConfiguration _tool;

    /// <summary>Creates the tool.</summary>
    /// <param name="tool">The declaration the document holds. Its id is the name the model calls.</param>
    /// <param name="blobs">The store <c>providers.blobs</c> opened.</param>
    /// <param name="policy">The cap and the allowlist.</param>
    /// <param name="workspaceRoot">The root every call's folder sits under.</param>
    /// <param name="logger">Where a refused file is reported.</param>
    public FilePublishTool(ToolConfiguration tool, IBlobStore blobs, BlobPolicy policy, string workspaceRoot, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(blobs);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(logger);

        _tool = tool;
        _blobs = blobs;
        _policy = policy;
        _workspaceRoot = workspaceRoot;
        _logger = logger;
    }

    /// <summary>Builds the function the model calls, named after the declaration.</summary>
    /// <returns>The function.</returns>
    public AIFunction AsAIFunction() => AIFunctionFactory.Create(PublishAsync, BuiltinToolOptions.Options(_tool));

    private async ValueTask<JsonObject> PublishAsync(
        [Description("The file's path, relative to the workspace.")] string path,
        [Description("A short label for the person, such as 'Sales by month'.")] string? title = null,
        TurnInvocation? turn = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Failed("path is required: the file's path, relative to the workspace.");
        }

        if (OwnerOf(turn) is not { } owner)
        {
            Log.SandboxFileHasNoOwner(_logger, _tool.Id);
            return Failed("no call is running, so there is nobody to hand the file to.");
        }

        var (callId, workspace) = owner;

        if (ResolveInside(workspace, path) is not { } full)
        {
            return Failed($"'{path}' is not inside the workspace. Give a path relative to the workspace, without '..'.");
        }

        if (!File.Exists(full))
        {
            return Failed($"there is no file at '{path}' in the workspace.");
        }

        var name = Path.GetFileName(full);
        var length = new FileInfo(full).Length;

        if (!BlobName.IsSafe(name))
        {
            Log.SandboxFileRefused(_logger, callId, name, "the name is not a plain file name");
            return Failed($"'{name}' is not a plain file name. Rename the file and publish it again.");
        }

        if (_policy.WhyRefused(name, length) is { } reason)
        {
            Log.SandboxFileRefused(_logger, callId, name, reason);
            return Failed($"'{name}' was refused: {reason}. Allowed extensions: {string.Join(", ", _policy.AllowedExtensions)}; "
                + $"largest file: {_policy.MaxBytes} bytes.");
        }

        BlobRef blob;
        Uri? url;

        try
        {
            await using var content = File.OpenRead(full);

            blob = await _blobs
                .PutAsync(new BlobWrite(callId, name, BlobMediaTypes.Of(name), content, length), cancellationToken)
                .ConfigureAwait(false);

            url = await _blobs.LinkAsync(blob, BlobLink.Lifetime, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log.SandboxFileCaptureFailed(_logger, callId, name, exception);
            return Failed($"'{name}' could not be stored: {exception.Message}");
        }

        turn?.Files?.Publish(new FileContent
        {
            Name = blob.Name,
            FileId = path,
            Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
            MediaType = blob.MediaType,
            Length = blob.Length,
            Kept = true,
        });

        return new JsonObject
        {
            ["name"] = blob.Name,
            ["mediaType"] = blob.MediaType,
            ["length"] = blob.Length,
            ["url"] = url?.ToString(),
        };
    }

    /// <summary>
    /// The call that owns the file and its folder: the filed turn, or for a background child the
    /// stamp its session carries and the folder that call id names under the root.
    /// </summary>
    private (string CallId, string Workspace)? OwnerOf(TurnInvocation? turn)
    {
        if (turn is { CallId: { } fromTurn })
        {
            return turn.Workspace is { } workspace ? (fromTurn, workspace) : null;
        }

        var session = AIAgent.CurrentRunContext?.Session;

        return session is not null
            && session.StateBag.TryGetValue<string>(BlobOwnerKey.Value, out var stamped)
            && stamped is not null
            ? (stamped, Path.Combine(_workspaceRoot, stamped))
            : null;
    }

    /// <summary>Resolves a model-written path under the workspace, or nothing when it escapes.</summary>
    private static string? ResolveInside(string workspace, string path)
    {
        if (Path.IsPathRooted(path))
        {
            return null;
        }

        var root = Path.GetFullPath(workspace);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, path));

        return full.StartsWith(rootWithSeparator, StringComparison.Ordinal) && full.Length > rootWithSeparator.Length
            ? full
            : null;
    }

    private JsonObject Failed(string message) => ToolErrorResult.Create(_tool.Id, message);
}
