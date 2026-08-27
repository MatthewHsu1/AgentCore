using AgentCore.Application.Configuration.Schema;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AgentCore.Infrastructure.Tools.Mcp;

/// <summary>
/// Raised when a call names a tool the server has since stopped offering.
/// </summary>
/// <param name="toolName">The remote tool name.</param>
internal sealed class McpToolGoneException(string toolName)
    : InvalidOperationException($"the server no longer offers a tool named '{toolName}'")
{
    /// <summary>Gets the remote tool name the server dropped.</summary>
    public string ToolName { get; } = toolName;
}

/// <summary>
/// One declared <c>mcp:</c> server, for as long as the process runs.
/// </summary>
internal sealed class McpServerSession : IAsyncDisposable
{
    /// <summary>How long one connection attempt gets when the document names nothing.</summary>
    internal static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long one tool call gets when the document names nothing.</summary>
    internal static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The number of connection attempts, including the first, when the document names none.</summary>
    internal const int DefaultRetryAttempts = 3;

    /// <summary>The wait before the second connection attempt when the document names none.</summary>
    internal static readonly TimeSpan DefaultRetryBackoff = TimeSpan.FromSeconds(1);

    private readonly McpServerConfiguration _server;

    private readonly Func<TimeSpan, CancellationToken, ValueTask<McpClient>> _connect;

    private readonly ILogger _log;

    private readonly SemaphoreSlim _gate = new(1, 1);

    private McpClient? _client;

    /// <summary>Guards <see cref="_offered"/>, which is replaced wholesale rather than mutated.</summary>
    private readonly Lock _offeredSync = new();

    /// <summary>What the server still answers for, by remote name, against the schema it last gave.</summary>
    private Dictionary<string, string> _offered = new(StringComparer.Ordinal);

    private bool _disposed;

    /// <summary>Creates the session over a connect step a test substitutes.</summary>
    /// <param name="server">The declaration this session is the one server of.</param>
    /// <param name="connect">
    /// Opens one connection, within the timeout given. It is called once per attempt, and again on
    /// every reconnect, so it must build a fresh transport each time — a transport that has already
    /// carried a session cannot carry another.
    /// </param>
    /// <param name="loggers">Where the reconnect and drop lines are written, or <see langword="null"/> for nowhere.</param>
    internal McpServerSession(
        McpServerConfiguration server,
        Func<TimeSpan, CancellationToken, ValueTask<McpClient>> connect,
        ILoggerFactory? loggers = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(connect);

        _server = server;
        _connect = connect;
        _log = loggers?.CreateLogger<McpServerSession>() ?? NullLogger<McpServerSession>.Instance;
    }

    /// <summary>Gets the id the document declares this server under.</summary>
    public string Id => _server.Id;

    /// <summary>Gets how long one tool call on this server may take.</summary>
    public TimeSpan CallTimeout => Seconds(_server.CallTimeoutSeconds) ?? DefaultCallTimeout;

    /// <summary>Connects, and reports every tool the server offers.</summary>
    /// <param name="cancellationToken">Cancels the whole attempt sequence.</param>
    /// <returns>One descriptor per offered tool, in the order the server listed them.</returns>
    /// <exception cref="TimeoutException">Every attempt ran out of time.</exception>
    /// <remarks>
    /// A failed attempt is retried with a doubling backoff, because the case this exists for is a
    /// cold start — <c>npx</c> fetching a package, a container still warming up — where a healthy
    /// server answers nothing at all on the first try. The last attempt's failure is what leaves.
    /// </remarks>
    public async ValueTask<IReadOnlyList<McpToolDescriptor>> OpenAsync(
        CancellationToken cancellationToken = default)
    {
        var attempts = _server.Retry?.Attempts ?? DefaultRetryAttempts;

        var backoff = _server.Retry?.BackoffMs is { } ms
            ? TimeSpan.FromMilliseconds(ms)
            : DefaultRetryBackoff;

        Exception? last = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await ConnectAndListAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;

                if (attempt == attempts)
                {
                    break;
                }

                McpLog.ConnectAttemptFailed(_log, _server.Id, attempt, attempts, backoff.TotalMilliseconds, ex);

                await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);

                backoff += backoff;
            }
        }

        throw last!;
    }

    /// <summary>Calls one tool, reconnecting once if the connection has died since the last call.</summary>
    /// <param name="toolName">The name the server offers the tool under, never the served id.</param>
    /// <param name="arguments">The arguments the model filled.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Whatever the server answered, error results included.</returns>
    /// <exception cref="McpToolGoneException">The server has stopped offering the tool.</exception>
    /// <exception cref="ObjectDisposedException">The session is closed.</exception>
    /// <remarks>
    /// One reconnect and one repeat, so a server that died between two turns of a telephone call
    /// costs the caller a pause rather than the turn. A second failure is the caller's answer: a
    /// server that is down stays down, and repeating past that only spends the call timeout.
    /// </remarks>
    public async ValueTask<CallToolResult> CallAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        ArgumentNullException.ThrowIfNull(arguments);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!Offers(toolName))
        {
            throw new McpToolGoneException(toolName);
        }

        var client = await CurrentAsync(cancellationToken).ConfigureAwait(false);

        Exception? died;

        try
        {
            return await SendAsync(client, toolName, arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not McpToolGoneException)
        {
            // The connection, not the tool: a tool that fails on its own terms answers with
            // CallToolResult.IsError and never throws.
            died = ex;
        }

        McpLog.CallFailedReconnecting(_log, _server.Id, toolName, died);

        var reopened = await ReconnectAsync(client, cancellationToken).ConfigureAwait(false);

        // The reconnect re-listed, so the tool may have gone in the meantime — a server that came
        // back offering less is exactly the case a raw protocol fault must not reach the model.
        if (!Offers(toolName))
        {
            throw new McpToolGoneException(toolName);
        }

        return await SendAsync(reopened, toolName, arguments, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends one call, and gives up the moment the session behind it ends.</summary>
    private static async ValueTask<CallToolResult> SendAsync(
        McpClient client,
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        var call = client.CallToolAsync(toolName, arguments, cancellationToken: cancellationToken).AsTask();
        var finished = await Task.WhenAny(call, client.Completion).ConfigureAwait(false);

        if (ReferenceEquals(finished, call))
        {
            return await call.ConfigureAwait(false);
        }

        // The call is never going to be answered now, but it is still a live task holding an
        // exception nobody has looked at. Observing it keeps that from surfacing later as an
        // unhandled fault on a finalizer thread, with no context left to say where it came from.
        Observe(call);

        throw Ended(await client.Completion.ConfigureAwait(false));
    }

    /// <summary>Swallows the outcome of a task that has already lost its race.</summary>
    private static void Observe(Task task)
        => _ = task.ContinueWith(
            static finished => _ = finished.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>Turns a finished session into the exception the call itself never threw.</summary>
    private static IOException Ended(ClientCompletionDetails details)
    {
        var why = details.Exception?.Message ?? "it closed the connection";
        if (details is StdioClientCompletionDetails stdio)
        {
            var exit = stdio.ExitCode is { } code ? $" (exit code {code})" : string.Empty;
            var tail = stdio.StandardErrorTail is { Count: > 0 } lines
                ? $" Its last standard error was: {string.Join(" ", lines)}"
                : string.Empty;

            return new IOException($"the server process ended{exit}: {why}.{tail}", details.Exception);
        }

        return new IOException($"the server session ended: {why}.", details.Exception);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await CloseAsync(Interlocked.Exchange(ref _client, null)).ConfigureAwait(false);

        _gate.Dispose();
    }

    /// <summary>Reads a seconds value the document wrote, if it wrote one.</summary>
    private static TimeSpan? Seconds(int? value)
        => value is { } seconds ? TimeSpan.FromSeconds(seconds) : null;

    private bool Offers(string toolName)
    {
        lock (_offeredSync)
        {
            return _offered.ContainsKey(toolName);
        }
    }

    /// <summary>Opens one connection, lists what it offers, and keeps both.</summary>
    private async ValueTask<IReadOnlyList<McpToolDescriptor>> ConnectAndListAsync(CancellationToken cancellationToken)
    {
        var timeout = Seconds(_server.ConnectTimeoutSeconds) ?? DefaultConnectTimeout;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        McpClient? opened = null;
        McpClient client;
        IList<McpClientTool> offered;

        try
        {
            client = opened = await _connect(timeout, deadline.Token).ConfigureAwait(false);
            offered = await client.ListToolsAsync(cancellationToken: deadline.Token).ConfigureAwait(false);
            opened = null;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            await CloseAsync(opened).ConfigureAwait(false);

            throw TimedOut(timeout);
        }
        catch (TimeoutException)
        {
            // The SDK's InitializationTimeout runs the same length as the deadline, so either clock
            // can fire first. The SDK's message does not name the knob, so it is replaced.
            await CloseAsync(opened).ConfigureAwait(false);

            throw TimedOut(timeout);
        }
        catch
        {
            await CloseAsync(opened).ConfigureAwait(false);

            throw;
        }

        var descriptors = offered.Select(Describe).ToArray();

        Remember(descriptors);
        Subscribe(client);

        // The replaced client is already known to be broken — that is why it is being replaced. It is
        // closed only so a stdio server's child process is not left running.
        await CloseAsync(Interlocked.Exchange(ref _client, client)).ConfigureAwait(false);

        return descriptors;
    }

    /// <summary>Names the timeout and the knob that raises it, whichever clock noticed first.</summary>
    private TimeoutException TimedOut(TimeSpan timeout)
        => new(
            $"the MCP server '{_server.Id}' did not finish connecting within {timeout.TotalSeconds:0.###}s. "
            + "Raise connectTimeoutSeconds if the server is simply slow to start.");

    /// <summary>Closes one connection, and never lets the closing itself become the failure.</summary>
    private async ValueTask CloseAsync(McpClient? client)
    {
        if (client is null)
        {
            return;
        }

        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            McpLog.CloseFailed(_log, _server.Id, ex);
        }
    }

    /// <summary>Replaces the connection, unless another caller already did.</summary>
    /// <param name="dead">The client the caller found broken.</param>
    /// <param name="cancellationToken">Cancels the reconnect.</param>
    /// <returns>The live client.</returns>
    private async ValueTask<McpClient> ReconnectAsync(McpClient dead, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Two calls can fail against one dead client at once. The first through here replaces it;
            // the second must use that replacement rather than open a third connection.
            if (!ReferenceEquals(_client, dead))
            {
                return _client ?? throw new ObjectDisposedException(nameof(McpServerSession));
            }

            await ConnectAndListAsync(cancellationToken).ConfigureAwait(false);
            return _client!;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Returns the live connection, opening one if the session has none.</summary>
    /// <remarks>
    /// A client whose <see cref="McpClient.Completion"/> has already finished is replaced before it
    /// is handed out, rather than after a call has been spent discovering it. That is the ordinary
    /// case for a stdio server whose child crashed between two turns of a call.
    /// </remarks>
    private async ValueTask<McpClient> CurrentAsync(CancellationToken cancellationToken)
    {
        if (_client is { } live && !live.Completion.IsCompleted)
        {
            return live;
        }

        if (_client is { } dead)
        {
            return await ReconnectAsync(dead, cancellationToken).ConfigureAwait(false);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_client is { } opened)
            {
                return opened;
            }

            await ConnectAndListAsync(cancellationToken).ConfigureAwait(false);
            return _client!;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Records what the server currently answers for, and reports what changed.</summary>
    /// <param name="offered">The tools the server just listed.</param>
    /// <returns>The names that have gone, and the names whose schema no longer matches.</returns>
    private (string[] Dropped, string[] Drifted) Remember(IEnumerable<McpToolDescriptor> offered)
    {
        Dictionary<string, string> current = new(StringComparer.Ordinal);
        foreach (var tool in offered)
        {
            current[tool.Name] = tool.JsonSchema.GetRawText();
        }

        lock (_offeredSync)
        {
            var previous = _offered;
            _offered = current;

            return (
                [.. previous.Keys.Where(name => !current.ContainsKey(name))],
                [.. previous
                    .Where(was => current.TryGetValue(was.Key, out var now) && !string.Equals(was.Value, now, StringComparison.Ordinal))
                    .Select(was => was.Key)]);
        }
    }

    /// <summary>Follows <c>tools/list_changed</c>, so a dropped tool fails as itself.</summary>
    private void Subscribe(McpClient client)
    {
        client.RegisterNotificationHandler(
            NotificationMethods.ToolListChangedNotification,
            async (_, ct) =>
            {
                try
                {
                    var offered = await client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false);
                    var (dropped, drifted) = Remember(offered.Select(Describe));

                    if (dropped.Length > 0)
                    {
                        McpLog.ToolsDropped(_log, _server.Id, string.Join(", ", dropped));
                    }

                    if (drifted.Length > 0)
                    {
                        McpLog.ToolSchemasDrifted(_log, _server.Id, string.Join(", ", drifted));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A notification handler that throws would take down the connection's read loop,
                    // which is a far worse outcome than a stale list.
                    McpLog.ReListFailed(_log, _server.Id, ex);
                }
            });
    }

    private static McpToolDescriptor Describe(McpClientTool tool)
        => new(tool.Name, tool.Description, tool.JsonSchema);
}
