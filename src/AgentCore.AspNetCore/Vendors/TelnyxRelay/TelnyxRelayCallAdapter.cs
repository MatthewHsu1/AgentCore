using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.AspNetCore.Call;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace AgentCore.AspNetCore.Vendors.TelnyxRelay;

/// <summary>
/// The Telnyx Conversation Relay as a call transport: it owns the socket and speaks Telnyx frames.
/// </summary>
/// <remarks>
/// <para>
/// D28 buys the whole speech layer — recognition, turn detection, synthesis, and interruption —
/// inside the relay, so <see cref="CarriesText"/> is <see langword="true"/> and
/// <c>providers.speech.kind</c> must name this same vendor.
/// </para>
/// <para>
/// This owns only the route. <c>TelnyxRelayConnection</c> is still what a call runs on, and
/// <c>TelnyxRelayCallChannelFactory</c> is still what hands out its two ports.
/// </para>
/// </remarks>
public sealed class TelnyxRelayCallAdapter : ICallTransportAdapter
{
    /// <summary>The one <c>providers.call.kind</c> value this vendor answers to.</summary>
    public const string TelnyxRelayKind = "telnyx-relay";

    /// <summary>The longest delay <c>Task.Delay</c>, <c>CancelAfter</c>, and <c>Task.WaitAsync</c> all accept.</summary>
    /// <remarks>
    /// One millisecond short of <see cref="uint.MaxValue"/> — about 49.7 days — confirmed on net10
    /// for all three. The schema caps the document at 4294967 whole seconds for the same reason;
    /// this check stays because the schema cannot express the ceiling without duplicating the
    /// runtime constant.
    /// </remarks>
    private static readonly TimeSpan MaximumBoundedDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <inheritdoc/>
    public string Kind => TelnyxRelayKind;

    /// <inheritdoc/>
    /// <remarks>The relay's frames carry text: the vendor performs recognition and synthesis itself.</remarks>
    public bool CarriesText => true;

    /// <inheritdoc/>
    public IEndpointConventionBuilder Map(
        IEndpointRouteBuilder endpoints,
        string pattern,
        CallProviderConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrEmpty(pattern);
        ArgumentNullException.ThrowIfNull(configuration);

        return TelnyxRelayEndpointRouteBuilderExtensions.MapTelnyxRelay(
            endpoints, pattern, BuildOptions(configuration));
    }

    /// <summary>Turns the document's limits into the options the endpoint runs on.</summary>
    /// <param name="configuration">The <c>providers.call</c> block.</param>
    /// <returns>The options, with the shipped default kept for every value the document omits.</returns>
    /// <exception cref="ConfigurationLoadException">
    /// A value would be refused at run time by <c>Task.Delay</c>, <c>CancelAfter</c>, or
    /// <c>Task.WaitAsync</c>, or a frame cap is not positive. The pointer names the exact field.
    /// </exception>
    /// <remarks>
    /// It throws <see cref="ConfigurationLoadException"/> and not
    /// <see cref="ArgumentOutOfRangeException"/> because the value came from a document rather than
    /// from a C# caller, and a reader needs the line to fix rather than a property name.
    /// </remarks>
    internal static TelnyxRelayOptions BuildOptions(CallProviderConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new TelnyxRelayOptions();

        if (configuration.MaxFrameBytes is { } frame)
        {
            // Every frame is measured against this before it is written into the message buffer.
            // Zero or less would refuse the first byte of every message forever, which is not a
            // limit — it is a call nobody could ever place.
            if (frame <= 0)
            {
                throw Fail("maxFrameBytes", $"must be positive, and it is {frame}.");
            }

            options.MaxFrameBytes = frame;
        }

        if (configuration.IdleTimeoutSeconds is { } idle)
        {
            options.IdleTimeout = ToTimeSpan("idleTimeoutSeconds", idle);
        }

        if (configuration.CloseTimeoutSeconds is { } close)
        {
            options.CloseTimeout = ToTimeSpan("closeTimeoutSeconds", close);
        }

        return options;
    }

    /// <summary>Turns whole seconds from the document into the span a bounded wait accepts.</summary>
    /// <param name="field">The <c>providers.call</c> field the value came from, for the pointer.</param>
    /// <param name="seconds">The whole seconds the document wrote.</param>
    /// <returns>The span, or <see cref="Timeout.InfiniteTimeSpan"/> for <c>-1</c>.</returns>
    private static TimeSpan ToTimeSpan(string field, int seconds)
    {
        if (seconds == -1)
        {
            return Timeout.InfiniteTimeSpan;
        }

        if (seconds < 0)
        {
            throw Fail(field, $"must be -1 for never, or zero or more seconds, and it is {seconds}.");
        }

        var value = TimeSpan.FromSeconds(seconds);
        return value <= MaximumBoundedDelay
            ? value
            : throw Fail(
                field,
                $"is {seconds} seconds, which Task.Delay, CancelAfter, and Task.WaitAsync all "
                + $"refuse at run time. The longest a timer can hold is {MaximumBoundedDelay}.");
    }

    /// <summary>Builds the one failure every check here raises, pointed at the field.</summary>
    /// <param name="field">The <c>providers.call</c> field that is wrong.</param>
    /// <param name="problem">What is wrong with it, in the words a reader can act on.</param>
    /// <returns>The failure to throw.</returns>
    private static ConfigurationLoadException Fail(string field, string problem)
        => new(new ConfigurationError
        {
            Pointer = $"/providers/call/{field}",
            Message = $"providers.call.{field} {problem}",
            Check = ConfigurationCheck.ReferenceResolution,
        });
}
