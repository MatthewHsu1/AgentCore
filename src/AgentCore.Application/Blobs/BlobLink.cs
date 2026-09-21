namespace AgentCore.Application.Blobs
{
    /// <summary>What every store agrees on when it hands a browser a link to a blob.</summary>
    public static class BlobLink
    {
        /// <summary>How long a link works.</summary>
        public static TimeSpan Lifetime { get; } = TimeSpan.FromMinutes(15);

        /// <summary>
        /// The <c>Content-Disposition</c> a link serves the blob with: <c>inline</c> for what a browser
        /// draws safely, <c>attachment</c> for everything else. SVG and HTML are never inline: both run
        /// script under the store's origin.
        /// </summary>
        /// <param name="mediaType">The blob's IANA media type.</param>
        /// <param name="name">The file name the browser saves it under.</param>
        /// <returns>The header value.</returns>
        public static string DispositionOf(string mediaType, string name)
        {
            ArgumentNullException.ThrowIfNull(mediaType);
            ArgumentNullException.ThrowIfNull(name);

            bool inline = mediaType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)
                || (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                    && !mediaType.Contains("svg", StringComparison.OrdinalIgnoreCase));

            return (inline ? "inline" : "attachment") + "; filename*=UTF-8''" + Uri.EscapeDataString(name);
        }
    }
}
