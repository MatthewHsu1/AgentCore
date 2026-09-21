namespace AgentCore.Application.Blobs
{
    /// <summary>The media type a file name implies, one entry per <see cref="BlobPolicy.DefaultExtensions"/>.</summary>
    internal static class BlobMediaTypes
    {
        /// <summary>The type for a name whose extension is not listed. A browser saves it rather than showing it.</summary>
        public const string Fallback = "application/octet-stream";

        private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
        {
            ["png"] = "image/png",
            ["jpg"] = "image/jpeg",
            ["jpeg"] = "image/jpeg",
            ["gif"] = "image/gif",
            ["webp"] = "image/webp",
            ["pdf"] = "application/pdf",
            ["csv"] = "text/csv",
            ["tsv"] = "text/tab-separated-values",
            ["txt"] = "text/plain",
            ["md"] = "text/markdown",
            ["json"] = "application/json",
            ["zip"] = "application/zip",
            ["xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ["docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ["pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        };

        /// <summary>Reads the media type off a file name's extension.</summary>
        /// <param name="name">The file name.</param>
        /// <returns>The IANA media type, or <see cref="Fallback"/> when the extension is not listed.</returns>
        public static string Of(string name)
        {
            ArgumentNullException.ThrowIfNull(name);

            string extension = Path.GetExtension(name).TrimStart('.');

            return extension.Length > 0 && ByExtension.TryGetValue(extension, out string? mediaType)
                ? mediaType
                : Fallback;
        }
    }
}
