using System.Globalization;
using AgentCore.Application.Runtime.Turn;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime.Harness;

/// <summary>
/// Tells the model the current date and time, once per turn, as a system message below the
/// cached instructions. Without it a relative date such as "the past ten days" has no anchor
/// and the model has to ask the person for one. The zone is the caller's when the host named
/// one (<see cref="CallerTimeZone"/>), else the clock's own.
/// </summary>
internal sealed class ClockContextProvider(TimeProvider clock) : AIContextProvider
{
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <inheritdoc />
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The turn's own session carries the zone on a loop-built run; a background child's session
        // carries it stamped; anything else reads the server's zone.
        var zone = TurnRegistry.For(context.Session)?.TimeZone
            ?? CallerTimeZone.Stamped(context.Session)
            ?? _clock.LocalTimeZone;

        return new(new AIContext { Messages = [new ChatMessage(ChatRole.System, Describe(_clock, zone))] });
    }

    /// <summary>The one line the model reads, for one zone.</summary>
    /// <param name="clock">The clock to read.</param>
    /// <param name="zone">The zone to read it in.</param>
    internal static string Describe(TimeProvider clock, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(zone);

        var now = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone);
        var offset = now.Offset;
        var sign = offset < TimeSpan.Zero ? "-" : "+";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Today is {now:dddd}, {now:yyyy-MM-dd}. The local time is {now:HH:mm}, {zone.Id} (UTC{sign}{offset:hh\\:mm}).");
    }
}
