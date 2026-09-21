using System.Text;

namespace AgentCore.Application.Blobs
{
    /// <summary>
    /// The one shape a blob name may take. A name is a key, never a path.
    /// </summary>
    public static class BlobName
    {
        /// <summary>The longest name, in UTF-8 bytes, that every backing store accepts.</summary>
        public const int MaxBytes = 255;

        /// <summary>Says whether a name is safe to use as a key.</summary>
        /// <param name="name">What the model or the caller wrote.</param>
        /// <returns><see langword="true"/> when the name is not empty, holds no path separator, no <c>..</c>,
        /// no control character, and fits in <see cref="MaxBytes"/>.</returns>
        public static bool IsSafe(string? name)
        {
            return !string.IsNullOrWhiteSpace(name) && !name.Contains("..", StringComparison.Ordinal) && !name.Any(c => c is '/' or '\\' || char.IsControl(c)) && Encoding.UTF8.GetByteCount(name) <= MaxBytes;
        }
    }
}
