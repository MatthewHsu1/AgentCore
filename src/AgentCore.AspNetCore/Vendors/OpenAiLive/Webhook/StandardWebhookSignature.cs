using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgentCore.AspNetCore.Vendors.OpenAiLive.Webhook
{
    /// <summary>
    /// The Standard Webhooks signature check OpenAI webhooks use: HMAC-SHA256 over "{id}.{timestamp}.{body}", sent as
    /// one or more space-separated "v1,{base64}" values (standardwebhooks.com).
    /// </summary>
    internal static class StandardWebhookSignature
    {
        /// <summary>How far the timestamp may be from now, either way, before a replay is assumed.</summary>
        internal static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

        private const string SecretPrefix = "whsec_";

        private const string VersionPrefix = "v1,";

        /// <exception cref="FormatException">The secret is not base64 after its prefix, or holds no key.</exception>
        internal static byte[] KeyOf(string secret)
        {
            ArgumentException.ThrowIfNullOrEmpty(secret);
            byte[] key = Convert.FromBase64String(secret.StartsWith(SecretPrefix, StringComparison.Ordinal) ? secret[SecretPrefix.Length..] : secret);
            return key.Length > 0 ? key : throw new FormatException("The webhook secret holds no key after its prefix.");
        }

        internal static bool Verify(byte[] key, string? id, string? timestamp, byte[] body, string? signatures, DateTimeOffset now)
        {
            ArgumentNullException.ThrowIfNull(key);
            ArgumentNullException.ThrowIfNull(body);

            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(signatures)
                || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out long seconds)
                || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds()
                || (now - DateTimeOffset.FromUnixTimeSeconds(seconds)).Duration() > Tolerance)
            {
                return false;
            }

            byte[] signed = [.. Encoding.UTF8.GetBytes($"{id}.{timestamp}."), .. body];
            byte[] expected = HMACSHA256.HashData(key, signed);
            foreach (string signature in signatures.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (signature.StartsWith(VersionPrefix, StringComparison.Ordinal)
                    && TryBase64(signature[VersionPrefix.Length..]) is { } given
                    && CryptographicOperations.FixedTimeEquals(given, expected))
                {
                    return true;
                }
            }

            return false;
        }

        private static byte[]? TryBase64(string text)
        {
            byte[] buffer = new byte[text.Length];
            return Convert.TryFromBase64String(text, buffer, out int written) ? buffer[..written] : null;
        }
    }
}
