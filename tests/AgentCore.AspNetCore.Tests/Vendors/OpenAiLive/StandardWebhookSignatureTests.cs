using System.Text;
using AgentCore.AspNetCore.Vendors.OpenAiLive.Webhook;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Vendors.OpenAiLive
{
    /// <summary>
    /// The Standard Webhooks signature scheme (standardwebhooks.com; github.com/standard-webhooks/standard-webhooks,
    /// spec/standard-webhooks.md, "Verifying webhook authenticity"): HMAC-SHA256 over "{id}.{timestamp}.{body}" with
    /// the base64 key after "whsec_", sent as "v1,{base64}", space-separated. The vector is the spec's reference-library
    /// example; recomputed with Python's hmac on 2026-10-03.
    /// </summary>
    public sealed class StandardWebhookSignatureTests
    {
        private const string Secret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";

        private const string Id = "msg_p5jXN8AQM9LWM0D4loKWxJek";

        private const string Timestamp = "1614265330";

        private const string Signature = "v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=";

        private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"test\": 2432232314}");

        private static readonly DateTimeOffset SignedAt = DateTimeOffset.FromUnixTimeSeconds(1614265330);

        private static byte[] Key => StandardWebhookSignature.KeyOf(Secret);

        [Fact]
        public void TheSpecsVectorVerifies()
        {
            Assert.True(StandardWebhookSignature.Verify(Key, Id, Timestamp, Body, Signature, SignedAt));
        }

        [Fact]
        public void AChangedBodyFails()
        {
            Assert.False(StandardWebhookSignature.Verify(Key, Id, Timestamp, Encoding.UTF8.GetBytes("{\"test\": 2432232315}"), Signature, SignedAt));
        }

        [Fact]
        public void ATimestampOutsideFiveMinutesFails()
        {
            Assert.False(StandardWebhookSignature.Verify(Key, Id, Timestamp, Body, Signature, SignedAt.AddMinutes(6)));
            Assert.True(StandardWebhookSignature.Verify(Key, Id, Timestamp, Body, Signature, SignedAt.AddMinutes(4)));
        }

        [Fact]
        public void ATimestampMoreThanFiveMinutesInTheFutureFails()
        {
            Assert.False(StandardWebhookSignature.Verify(Key, Id, Timestamp, Body, Signature, SignedAt.AddMinutes(-6)));
            Assert.True(StandardWebhookSignature.Verify(Key, Id, Timestamp, Body, Signature, SignedAt.AddMinutes(-4)));
        }

        // The spec allows several signatures (key rotation); any one valid "v1" passes.
        [Fact]
        public void OneValidSignatureAmongSeveralPasses()
        {
            Assert.True(StandardWebhookSignature.Verify(Key, Id, Timestamp, Body, "v1,AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA= " + Signature, SignedAt));
            Assert.False(StandardWebhookSignature.Verify(Key, Id, Timestamp, Body, "v2," + Signature[3..], SignedAt));
        }

        // A webhook endpoint is public, so any header value can arrive; each must be a refusal, never an exception.
        [Theory]
        [InlineData(Id, Timestamp, "v1,!!!")]
        [InlineData(Id, Timestamp, "v1,")]
        [InlineData(Id, Timestamp, "v2,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=")]
        [InlineData(Id, Timestamp, null)]
        [InlineData(Id, null, Signature)]
        [InlineData(Id, "not-a-number", Signature)]
        [InlineData(Id, "9223372036854775807", Signature)]
        [InlineData(null, Timestamp, Signature)]
        public void AMalformedOrMissingHeaderFailsWithoutThrowing(string? id, string? timestamp, string? signatures)
        {
            Assert.False(StandardWebhookSignature.Verify(Key, id, timestamp, Body, signatures, SignedAt));
        }

        [Theory]
        [InlineData("whsec_")]
        [InlineData("whsec_!!!")]
        public void ASecretThatHoldsNoUsableKeyFails(string secret)
        {
            _ = Assert.Throws<FormatException>(() => StandardWebhookSignature.KeyOf(secret));
        }
    }
}
