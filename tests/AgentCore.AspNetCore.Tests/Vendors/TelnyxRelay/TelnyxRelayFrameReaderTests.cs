using System.Text;
using System.Text.Json;
using AgentCore.AspNetCore.Vendors.TelnyxRelay;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Vendors.TelnyxRelay;

/// <summary>
/// The reader against the frames the vendor documents.
/// </summary>
/// <remarks>
/// Every JSON body below is copied from the Telnyx Conversation Relay page, so these are golden
/// inputs and not invented ones. Every test here runs offline. There is no network call and no API
/// key anywhere in this file.
/// </remarks>
public sealed class TelnyxRelayFrameReaderTests
{
    // -------------------------------------------------------------------------------------------
    // The five inbound frames.
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void ASetupFrame_ReadsTheIdsTheTransferWillNeed()
    {
        const string Json =
            """
            {"type":"setup","sessionId":"7a7e6a4f","accountSid":"1f1a8b6f",
             "callSid":"v2:abc","callControlId":"v2:abc","callSessionId":"ff55a038",
             "callLegId":"428c31b6","from":"+13122010094","to":"+13122123456",
             "direction":"inbound","callerName":"","callStatus":"active",
             "customParameters":{"customer_id":"customer_123"}}
            """;

        Assert.True(Read(Json, out var frame, out _));

        var setup = Assert.IsType<RelayFrame.Setup>(frame);
        Assert.Equal("7a7e6a4f", setup.SessionId);
        Assert.Equal("v2:abc", setup.CallSid);
        Assert.Equal("v2:abc", setup.CallControlId);
        Assert.Equal("ff55a038", setup.CallSessionId);
        Assert.Equal("+13122010094", setup.From);
        Assert.Equal("+13122123456", setup.To);
        Assert.Equal("customer_123", setup.CustomParameters!["customer_id"]);
    }

    [Fact]
    public void APromptFrame_ReadsTheTranscriptAndTheFinalFlag()
    {
        const string Json =
            """{"type":"prompt","voicePrompt":"hello there how are you","lang":"en","last":true}""";

        Assert.True(Read(Json, out var frame, out _));

        var prompt = Assert.IsType<RelayFrame.Prompt>(frame);
        Assert.Equal("hello there how are you", prompt.VoicePrompt);
        Assert.Equal("en", prompt.Lang);
        Assert.True(prompt.Last);
    }

    [Fact]
    public void AnInterruptFrame_ReadsTheHeardTextAndTheDurationInMilliseconds()
    {
        // Section 7.1 first printed a TimeSpan here. The wire carries an integer of milliseconds.
        const string Json =
            """
            {"type":"interrupt","utteranceUntilInterrupt":"Welcome to Telnyx, how can I help",
             "durationUntilInterruptMs":1820}
            """;

        Assert.True(Read(Json, out var frame, out _));

        var interrupt = Assert.IsType<RelayFrame.Interrupt>(frame);
        Assert.Equal("Welcome to Telnyx, how can I help", interrupt.UtteranceUntilInterrupt);
        Assert.Equal(1820, interrupt.DurationUntilInterruptMs);
    }

    [Fact]
    public void ADtmfFrame_ReadsTheSingleDigit()
    {
        Assert.True(Read("""{"type":"dtmf","digit":"1"}""", out var frame, out _));
        Assert.Equal("1", Assert.IsType<RelayFrame.Dtmf>(frame).Digit);
    }

    [Fact]
    public void AnErrorFrame_ReadsTheDescription()
    {
        const string Json =
            """{"type":"error","description":"Invalid message: missing required field: token"}""";

        Assert.True(Read(Json, out var frame, out _));
        Assert.StartsWith("Invalid message", Assert.IsType<RelayFrame.Error>(frame).Description);
    }

    // -------------------------------------------------------------------------------------------
    // What the reader refuses, and how. It never throws on vendor input.
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void AnUnknownType_IsRefusedAndNamed()
    {
        // A vendor that adds a frame must not be able to drop a call. Section 7.1.
        Assert.False(Read("""{"type":"whisper","text":"x"}""", out var frame, out var unknown));
        Assert.Null(frame);
        Assert.Equal("whisper", unknown);
    }

    [Fact]
    public void AMalformedBody_IsRefusedAndThrowsNothing()
    {
        Assert.False(Read("{ this is not JSON", out var frame, out var unknown, out var refused));
        Assert.Null(frame);
        Assert.Null(unknown);
        Assert.Null(refused);
    }

    [Fact]
    public void ADecimalInterruptDuration_IsARefusedBodyAndNotAnUnreadableFrame()
    {
        // The wire carries an integer of milliseconds, and the record binds one. A vendor that
        // starts sending 1820.5 must not thereby end a call at the exact moment of a barge-in, so
        // the type is named on refusedType and the caller keeps the socket. Section 7.1.
        const string Json =
            """
            {"type":"interrupt","utteranceUntilInterrupt":"Welcome to Telnyx, how can I help",
             "durationUntilInterruptMs":1820.5}
            """;

        Assert.False(Read(Json, out var frame, out var unknown, out var refused));
        Assert.Null(frame);
        Assert.Null(unknown);
        Assert.Equal("interrupt", refused);
    }

    [Fact]
    public void ANonStringCustomParameter_IsARefusedBodyAndNotAnUnreadableFrame()
    {
        // customParameters binds to a dictionary of strings, and the host chooses what goes in it.
        // A number there would otherwise end the call on the setup frame, before it ever began.
        const string Json =
            """
            {"type":"setup","sessionId":"7a7e6a4f","callSid":"v2:abc","callControlId":"v2:abc",
             "callSessionId":"ff55a038","from":"+13122010094","to":"+13122123456",
             "customParameters":{"a":7}}
            """;

        Assert.False(Read(Json, out var frame, out var unknown, out var refused));
        Assert.Null(frame);
        Assert.Null(unknown);
        Assert.Equal("setup", refused);
    }

    [Fact]
    public void AnUnmodelledType_NamesUnknownTypeAndNeverRefusedType()
    {
        // The two names answer different questions, and a caller acts differently on each: an
        // unmodelled type is a frame from a later version of the vendor, and a refused body is a
        // field of a frame this build already knows.
        Assert.False(Read("""{"type":"whisper","text":"x"}""", out _, out var unknown, out var refused));
        Assert.Equal("whisper", unknown);
        Assert.Null(refused);
    }

    [Fact]
    public void AFrameWithNoType_IsRefused()
    {
        Assert.False(Read("""{"voicePrompt":"hi","last":true}""", out var frame, out _));
        Assert.Null(frame);
    }

    [Fact]
    public void ATypeThatIsNotFirst_IsStillRead()
    {
        // Nothing in the vendor contract promises the discriminator comes first, and this is one
        // reason the reader switches by hand instead of using polymorphic deserialization.
        Assert.True(Read("""{"digit":"5","type":"dtmf"}""", out var frame, out _));
        Assert.Equal("5", Assert.IsType<RelayFrame.Dtmf>(frame).Digit);
    }

    [Fact]
    public void AnEmptyBody_IsRefusedAndThrowsNothing()
    {
        Assert.False(Read(string.Empty, out var frame, out var unknown));
        Assert.Null(frame);
        Assert.Null(unknown);
    }

    [Fact]
    public void AJsonArray_IsRefusedAndThrowsNothing()
    {
        // Valid JSON, but not an object, so there is no "type" field to read.
        Assert.False(Read("[1,2,3]", out var frame, out var unknown));
        Assert.Null(frame);
        Assert.Null(unknown);
    }

    [Fact]
    public void ATypeFieldThatIsNotAString_IsRefusedAndThrowsNothing()
    {
        Assert.False(Read("""{"type":123}""", out var frame, out var unknown));
        Assert.Null(frame);
        Assert.Null(unknown);
    }

    [Fact]
    public void TrailingBytesAfterACompleteObject_AreToleratedNotRejected()
    {
        // JsonDocument.ParseValue reads only the first complete JSON value and leaves the rest of
        // the buffer unread, so it does not throw on the trailing text and the reader accepts the
        // frame it did parse. This documents that behaviour; the reader does not reject it.
        Assert.True(Read("""{"type":"dtmf","digit":"1"} extra garbage""", out var frame, out _));
        Assert.Equal("1", Assert.IsType<RelayFrame.Dtmf>(frame).Digit);
    }

    // -------------------------------------------------------------------------------------------
    // The outbound wire, so a future change to Type cannot silently drop the discriminator.
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void ARelayToken_SerializesWithTheTextDiscriminator()
    {
        var json = JsonSerializer.Serialize(new RelayToken("hello", Last: true), TelnyxRelayJson.Options);

        Assert.Contains("\"type\":\"text\"", json);
        Assert.Contains("\"token\":\"hello\"", json);
        Assert.Contains("\"last\":true", json);
    }

    private static bool Read(string json, out RelayFrame? frame, out string? unknownType)
        => Read(json, out frame, out unknownType, out _);

    private static bool Read(string json, out RelayFrame? frame, out string? unknownType, out string? refusedType)
        => TelnyxRelayFrameReader.TryRead(
            Encoding.UTF8.GetBytes(json), out frame, out unknownType, out refusedType);
}
