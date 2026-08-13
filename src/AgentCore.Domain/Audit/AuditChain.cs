using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgentCore.Domain.Audit;

/// <summary>
/// The hash chain: it canonicalizes one event, hashes it over the hash before it, and verifies the
/// result.
/// </summary>
/// <remarks>
/// <para>
/// D23 puts three defences under the audit table, and the third is a <c>CHECK</c> constraint that
/// recomputes SHA-256 <b>inside PostgreSQL</b>. That is what makes this class unusual: the algorithm
/// is the easy half, and the canonical form is the load-bearing half. Two processes must produce the
/// same bytes for the same event, and one of those two processes is a database with no C# in it.
/// </para>
/// <para><b>The canonical form.</b> The bytes are UTF-8, with no byte-order mark. The text is a
/// header line, then a fixed sequence of fields. Every field is written as
/// <c>&lt;byteCount&gt;:&lt;value&gt;\n</c>, where <c>byteCount</c> is the number of UTF-8 BYTES in
/// the value, written in invariant decimal. The line feed is U+000A alone, never a carriage return.
/// </para>
/// <para><b>Why a byte-count prefix and not a delimiter.</b> A reply, a tool error, and the text a
/// caller heard are free text, and free text holds line feeds, colons, quotation marks, and every
/// delimiter a designer could pick. A delimited form would then need escaping, and two
/// implementations of one escape rule disagree about the first awkward character. A byte count needs
/// no escaping and no agreement: <c>octet_length(v) || ':' || v</c> in PostgreSQL is the same rule as
/// <c>Encoding.UTF8.GetByteCount(v)</c> here. Bytes, not characters, because
/// <c>length()</c> in PostgreSQL counts characters and .NET counts UTF-16 code units, and the two
/// disagree the first time a caller says something outside the basic multilingual plane.
/// </para>
/// <para><b>Why the fields are in a fixed order and never sorted.</b> The order below is the order of
/// this file, and it never changes. A payload is different: its keys arrive in whatever order a
/// caller built the dictionary, so the canonical form sorts them by ordinal — by UTF-16 code unit,
/// which for the ASCII key names the design uses is the same order as PostgreSQL's <c>C</c> collation
/// on the same text. Keep payload keys ASCII for that reason.
/// </para>
/// <para><b>Why the time is Unix milliseconds and not ISO 8601.</b> An ISO 8601 rendering has to
/// agree about the number of fractional digits, about the offset spelling, and about whether
/// <c>Z</c> or <c>+00:00</c> appears. Unix milliseconds is one integer with one spelling.
/// PostgreSQL reproduces it exactly as
/// <c>(floor(extract(epoch from occurred_at) * 1000))::bigint</c>. The offset is dropped, so a host
/// in one time zone and a host in another hash one moment the same way. Sub-millisecond precision is
/// dropped with it, and <see cref="AuditEvent.Sequence"/> orders two events inside one millisecond.
/// </para>
/// <para><b>Why the kind is a token.</b> <see cref="AuditEventKinds.ToToken"/> writes
/// <c>turn.completed</c> and never <c>TurnCompleted</c> and never <c>1</c>. A C# rename must not
/// change a hash PostgreSQL already stored, and the engine has no enum to read.
/// </para>
/// <para><b>The fields, in order.</b> The previous hash; the call id; the sequence; the kind token;
/// the time in Unix milliseconds; the turn index, or empty when there is none; the amended sequence,
/// or empty when there is none; the number of payload entries; then, for each entry in ordinal order
/// of its key, the key and then the value. A number is written in invariant decimal with no group
/// separator, no leading zero, and a leading <c>-</c> only when it is negative.
/// </para>
/// </remarks>
public static class AuditChain
{
    /// <summary>
    /// The header line of the canonical form. It names the version of the rule the bytes follow.
    /// </summary>
    /// <remarks>
    /// It is the first line, so two chains built under two rules can never collide, and a stored row
    /// says which rule verifies it. A change to the canonical form takes a new version here and
    /// leaves every stored hash valid under the old one.
    /// </remarks>
    public const string CanonicalFormVersion = "agentcore-audit-v1";

    /// <summary>Writes the canonical text of one event over the hash before it.</summary>
    /// <param name="auditEvent">The event to write.</param>
    /// <param name="previousHash">
    /// The hash of the link before it, or <see cref="AuditHash.Genesis"/> for the first link.
    /// </param>
    /// <returns>The canonical text. Encode it as UTF-8 to get the bytes SHA-256 reads.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The event is not one the vocabulary permits.</exception>
    public static string Canonicalize(AuditEvent auditEvent, AuditHash previousHash)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        ArgumentNullException.ThrowIfNull(previousHash);
        Validate(auditEvent);

        StringBuilder builder = new();
        builder.Append(CanonicalFormVersion).Append('\n');

        AppendField(builder, previousHash.Value);
        AppendField(builder, auditEvent.CallId);
        AppendField(builder, Number(auditEvent.Sequence));
        AppendField(builder, AuditEventKinds.ToToken(auditEvent.Kind));
        AppendField(builder, Number(auditEvent.OccurredAt.ToUnixTimeMilliseconds()));
        AppendField(builder, auditEvent.TurnIndex is int turnIndex ? Number(turnIndex) : string.Empty);
        AppendField(builder, auditEvent.AmendsSequence is long amends ? Number(amends) : string.Empty);

        // The payload keys are sorted by ordinal, so the order a caller built the dictionary in never
        // changes a hash. Two callers that add the same pairs in two orders hash the same event.
        List<KeyValuePair<string, string>> entries = [.. auditEvent.Payload];
        entries.Sort(static (left, right) => string.CompareOrdinal(left.Key, right.Key));

        AppendField(builder, Number(entries.Count));
        foreach (KeyValuePair<string, string> entry in entries)
        {
            AppendField(builder, entry.Key);
            AppendField(builder, entry.Value);
        }

        return builder.ToString();
    }

    /// <summary>Computes the SHA-256 hash of one event over the hash before it.</summary>
    /// <param name="auditEvent">The event to hash.</param>
    /// <param name="previousHash">
    /// The hash of the link before it, or <see cref="AuditHash.Genesis"/> for the first link.
    /// </param>
    /// <returns>The hash of the event.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The event is not one the vocabulary permits.</exception>
    public static AuditHash ComputeHash(AuditEvent auditEvent, AuditHash previousHash)
    {
        string canonical = Canonicalize(auditEvent, previousHash);
        byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(canonical);
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(bytes, digest);
        return AuditHash.FromDigest(digest);
    }

    /// <summary>Joins one event to the chain.</summary>
    /// <param name="auditEvent">The event to append.</param>
    /// <param name="previousHash">
    /// The hash of the link before it, or <see cref="AuditHash.Genesis"/> for the first link.
    /// </param>
    /// <returns>The link a store writes.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The event is not one the vocabulary permits.</exception>
    public static AuditChainLink Link(AuditEvent auditEvent, AuditHash previousHash) => new()
    {
        Event = auditEvent,
        PreviousHash = previousHash,
        Hash = ComputeHash(auditEvent, previousHash),
    };

    /// <summary>Joins a run of events into a chain, starting at <see cref="AuditHash.Genesis"/>.</summary>
    /// <param name="events">The events, in the order they happened.</param>
    /// <returns>The links, in the same order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="events"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An event is not one the vocabulary permits.</exception>
    public static IReadOnlyList<AuditChainLink> LinkAll(IEnumerable<AuditEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        List<AuditChainLink> links = [];
        AuditHash previousHash = AuditHash.Genesis;
        foreach (AuditEvent auditEvent in events)
        {
            AuditChainLink link = Link(auditEvent, previousHash);
            links.Add(link);
            previousHash = link.Hash;
        }

        return links;
    }

    /// <summary>Verifies a chain, and names the first link that does not agree.</summary>
    /// <param name="links">The links, in the order the store holds them.</param>
    /// <returns>
    /// <see cref="AuditChainVerification.Ok"/> when every link agrees, and
    /// <see cref="AuditChainVerification.LinkBroken"/> naming the first that does not.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This is the <c>chain_check</c> of section 11, item 6. It makes two checks for each link: the
    /// link points at the hash the link before it produced, and the link's own hash is what its event
    /// hashes to. The first check catches a deleted or reordered row, and the second catches an
    /// edited payload.
    /// </para>
    /// <para>
    /// It stops at the first break. Every later hash is computed over the broken one, so everything
    /// after the break is a consequence and not a finding.
    /// </para>
    /// <para>
    /// An empty chain verifies. Nothing was written, so nothing is missing, and a call that never
    /// started must not read as tampered with.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="links"/> is <see langword="null"/>.</exception>
    public static AuditChainVerification Verify(IReadOnlyList<AuditChainLink> links)
    {
        ArgumentNullException.ThrowIfNull(links);

        AuditHash expectedPrevious = AuditHash.Genesis;
        for (int index = 0; index < links.Count; index++)
        {
            AuditChainLink link = links[index];

            if (link.PreviousHash != expectedPrevious)
            {
                return AuditChainVerification.LinkBroken(
                    index,
                    $"The link points at '{link.PreviousHash}', and the link before it produced '{expectedPrevious}'.");
            }

            AuditHash recomputed;
            try
            {
                recomputed = ComputeHash(link.Event, link.PreviousHash);
            }
            catch (ArgumentException exception)
            {
                return AuditChainVerification.LinkBroken(
                    index,
                    $"The event cannot be canonicalized: {exception.Message}");
            }

            if (recomputed != link.Hash)
            {
                return AuditChainVerification.LinkBroken(
                    index,
                    $"The stored hash is '{link.Hash}', and the event hashes to '{recomputed}'.");
            }

            expectedPrevious = link.Hash;
        }

        return AuditChainVerification.Ok();
    }

    /// <summary>Writes one length-prefixed field.</summary>
    private static void AppendField(StringBuilder builder, string value) =>
        builder
            .Append(Number(Encoding.UTF8.GetByteCount(value)))
            .Append(':')
            .Append(value)
            .Append('\n');

    /// <summary>Writes one number the way both C# and PostgreSQL write it.</summary>
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Reads whether a value is a comma-separated list in which no member is empty.</summary>
    /// <remarks>
    /// It checks the SHAPE of the list and NEVER the names in it. A reader splits this value on a
    /// comma and counts the members, so <c>""</c>, <c>"a,,b"</c>, <c>"a,"</c>, and <c>",a"</c> all
    /// make the count wrong. The names stay unchecked on purpose: the moderation taxonomy belongs to
    /// the endpoint and it grows, and a closed set here would refuse the record the chain exists to
    /// keep. See <see cref="AuditEventKind.PromptFlagged"/>.
    /// </remarks>
    private static bool IsCommaSeparatedListWithNoEmptyMember(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (string member in value.Split(','))
        {
            if (member.Length == 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Refuses an event the vocabulary does not permit.</summary>
    private static void Validate(AuditEvent auditEvent)
    {
        if (string.IsNullOrEmpty(auditEvent.CallId))
        {
            throw new ArgumentException("An audit event carries a call id.", nameof(auditEvent));
        }

        if (auditEvent.Sequence < 0)
        {
            throw new ArgumentException(
                $"An audit event sequence counts from zero. This one is {auditEvent.Sequence}.",
                nameof(auditEvent));
        }

        // The token lookup refuses a value outside the closed set.
        _ = AuditEventKinds.ToToken(auditEvent.Kind);

        if (auditEvent.AmendsSequence is long amends && amends >= auditEvent.Sequence)
        {
            throw new ArgumentException(
                $"An amendment names an earlier event. Event {auditEvent.Sequence} names {amends}.",
                nameof(auditEvent));
        }

        if (auditEvent.Kind == AuditEventKind.ReplyInterrupted)
        {
            // T23: the chain is append-only, so a barge-in is a second event that references the
            // turn event. An interruption that amends nothing loses the turn it belongs to.
            if (auditEvent.AmendsSequence is null)
            {
                throw new ArgumentException(
                    "A reply.interrupted event amends the turn.completed event of the same turn, so it sets AmendsSequence. See T23.",
                    nameof(auditEvent));
            }

            // Section 11, item 6a: the event records the text the caller ACTUALLY HEARD.
            if (!auditEvent.Payload.TryGetValue(AuditPayloadKeys.UtteranceUntilInterrupt, out string? utterance)
                || utterance is null)
            {
                throw new ArgumentException(
                    $"A reply.interrupted event carries '{AuditPayloadKeys.UtteranceUntilInterrupt}', the text the caller actually heard. See section 11, item 6a.",
                    nameof(auditEvent));
            }
        }

        if (auditEvent.Kind == AuditEventKind.PromptFlagged)
        {
            // §9 makes this chain the only long-term record. The kind alone says something flagged
            // the caller, and the categories are the only other fact the event carries, so the fact
            // goes in with the event or it is lost. This is the argument the chain already makes for
            // utteranceUntilInterrupt above. No AmendsSequence rule stands here: the verdict is known
            // BEFORE the model runs, so the event is written before the turn.completed event of the
            // same turn and amends nothing. TurnIndex names the turn.
            if (!auditEvent.Payload.TryGetValue(AuditPayloadKeys.ModerationCategories, out string? categories)
                || !IsCommaSeparatedListWithNoEmptyMember(categories))
            {
                throw new ArgumentException(
                    $"A prompt.flagged event carries '{AuditPayloadKeys.ModerationCategories}', a comma-separated list with no empty member. See section 11, item 11.",
                    nameof(auditEvent));
            }
        }

        if (auditEvent.Kind == AuditEventKind.CallEnded)
        {
            // The reason is countable, so the chain refuses free text here. §9 makes this table the
            // only long-term record, and a report that counts the endings of one year reads the
            // token. Detail belongs under another key. See CallEndReason.
            if (!auditEvent.Payload.TryGetValue(AuditPayloadKeys.EndReason, out string? endReason)
                || !CallEndReasons.TryParse(endReason, out _))
            {
                throw new ArgumentException(
                    $"A call.ended event carries '{AuditPayloadKeys.EndReason}', and the value is one token of the closed set. See CallEndReason.",
                    nameof(auditEvent));
            }
        }

        foreach (KeyValuePair<string, string> entry in auditEvent.Payload)
        {
            if (string.IsNullOrEmpty(entry.Key))
            {
                throw new ArgumentException("An audit payload key is not empty.", nameof(auditEvent));
            }

            if (entry.Value is null)
            {
                throw new ArgumentException(
                    $"The audit payload value of '{entry.Key}' is null. A missing fact is an absent key.",
                    nameof(auditEvent));
            }
        }
    }
}
