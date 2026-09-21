namespace AgentCore.Infrastructure.Tests.Blobs.S3
{
    /// <summary>The S3-compatible bucket the integration tests write into, if the environment names one.</summary>
    /// <remarks>
    /// <c>AGENTCORE_TEST_S3_ENDPOINT</c> and <c>AGENTCORE_TEST_S3_BUCKET</c> name it. The credential is
    /// read the way the adapter reads it: <c>AWS_ACCESS_KEY_ID</c> and <c>AWS_SECRET_ACCESS_KEY</c>.
    /// </remarks>
    public static class S3Bucket
    {
        private const string EndpointVariable = "AGENTCORE_TEST_S3_ENDPOINT";

        private const string BucketVariable = "AGENTCORE_TEST_S3_BUCKET";

        /// <summary>Why a test skipped, when it did.</summary>
        public const string SkipReason = "Set AGENTCORE_TEST_S3_ENDPOINT and AGENTCORE_TEST_S3_BUCKET to run.";

        /// <summary>The service URL, or null when the environment names none.</summary>
        public static string? Endpoint => Read(EndpointVariable);

        /// <summary>The bucket, or null when the environment names none.</summary>
        public static string? Bucket => Read(BucketVariable);

        /// <summary>Whether a bucket was named.</summary>
        public static bool IsConfigured => Endpoint is not null && Bucket is not null;

        private static string? Read(string variable)
        {
            return Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? value : null;
        }
    }
}
