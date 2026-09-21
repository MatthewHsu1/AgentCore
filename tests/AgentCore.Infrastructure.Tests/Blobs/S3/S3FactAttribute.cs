using System.Runtime.CompilerServices;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Blobs.S3
{
    /// <summary>A test that needs a live S3-compatible bucket, and skips itself when none is named.</summary>
    public sealed class S3FactAttribute : FactAttribute
    {
        /// <summary>Creates the attribute.</summary>
        /// <param name="sourceFilePath">Supplied by the compiler.</param>
        /// <param name="sourceLineNumber">Supplied by the compiler.</param>
        public S3FactAttribute(
            [CallerFilePath] string? sourceFilePath = null,
            [CallerLineNumber] int sourceLineNumber = -1)
            : base(sourceFilePath, sourceLineNumber)
        {
            Skip = S3Bucket.SkipReason;
            SkipUnless = nameof(S3Bucket.IsConfigured);
            SkipType = typeof(S3Bucket);
        }
    }
}
