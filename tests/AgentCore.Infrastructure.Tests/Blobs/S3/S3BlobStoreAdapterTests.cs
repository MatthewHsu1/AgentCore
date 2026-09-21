using AgentCore.Infrastructure.Blobs.S3;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Blobs.S3
{
    /// <summary>The <c>s3</c> vendor's reading of a Backblaze-shaped endpoint.</summary>
    public sealed class S3BlobStoreAdapterTests
    {
        [Theory]
        [InlineData("https://s3.us-west-004.backblazeb2.com", "us-west-004")]
        [InlineData("https://s3.eu-central-003.backblazeb2.com", "eu-central-003")]
        [InlineData("https://S3.us-east-005.backblazeb2.com/", "us-east-005")]
        public void RegionOf_BackblazeHost_ReadsTheLabelAfterS3(string endpoint, string expected)
        {
            Assert.Equal(expected, S3BlobStoreAdapter.RegionOf(endpoint));
        }

        [Theory]
        [InlineData("https://fly.storage.tigris.dev")]
        [InlineData("http://localhost:9000")]
        [InlineData("not a url")]
        public void RegionOf_OtherHost_FallsBackToTheDefault(string endpoint)
        {
            Assert.Equal(S3BlobStoreAdapter.DefaultRegion, S3BlobStoreAdapter.RegionOf(endpoint));
        }

        [Fact]
        public void Kind_IsS3()
        {
            Assert.Equal("s3", new S3BlobStoreAdapter().Kind);
        }
    }
}
