using System.Collections.Specialized;
using System.Web;
using AgentCore.Application.Blobs;
using AgentCore.Infrastructure.Blobs.S3;
using Amazon.Runtime;
using Amazon.S3;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Blobs.S3
{
    /// <summary>
    /// The presigned link, checked without a bucket: SigV4 signing is local. A browser GET sends
    /// <c>Host</c> and nothing else the bucket can check, so <c>X-Amz-SignedHeaders</c> must be
    /// exactly <c>host</c>; anything more and the bucket answers SignatureDoesNotMatch.
    /// </summary>
    public sealed class S3BlobStoreLinkTests
    {
        private static S3BlobStore Open(string endpoint = "https://s3.us-east-005.backblazeb2.com")
        {
            return new(
                        new AmazonS3Client(
                            new BasicAWSCredentials("fakeKeyId", "fakeSecret"),
                            new AmazonS3Config
                            {
                                ServiceURL = endpoint,
                                AuthenticationRegion = "us-east-005",
                                ForcePathStyle = true,
                            }),
                        "SpiritAI");
        }

        [Fact]
        public async Task LinkAsync_PlainHttpEndpoint_LinksOverHttp()
        {
            // Arrange
            using S3BlobStore store = Open("http://localhost:59000");

            // Act
            Uri? url = await store.LinkAsync(new BlobRef("conversation-1", "rows.csv", "text/csv", 8), TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

            // Assert
            Assert.NotNull(url);
            Assert.Equal("http", url.Scheme);
            Assert.Equal("localhost", url.Host);
            Assert.Equal(59000, url.Port);
        }

        [Fact]
        public async Task LinkAsync_SignsHostOnly_AndCarriesTheResponseOverrides()
        {
            // Arrange
            using S3BlobStore store = Open();
            BlobRef blob = new("conversation-1", "chart.png", "image/png", 48213);

            // Act
            Uri? url = await store.LinkAsync(blob, TimeSpan.FromMinutes(15), TestContext.Current.CancellationToken);

            // Assert
            Assert.NotNull(url);
            Assert.Equal("https", url.Scheme);
            Assert.Equal("s3.us-east-005.backblazeb2.com", url.Host);
            Assert.Equal("/SpiritAI/conversation-1/chart.png", url.AbsolutePath);

            NameValueCollection query = HttpUtility.ParseQueryString(url.Query);
            Assert.Equal("host", query["X-Amz-SignedHeaders"]);
            Assert.Equal("900", query["X-Amz-Expires"]);
            Assert.Equal("AWS4-HMAC-SHA256", query["X-Amz-Algorithm"]);
            Assert.Equal("image/png", query["response-content-type"]);
            Assert.Equal("inline; filename*=UTF-8''chart.png", query["response-content-disposition"]);
            Assert.NotEmpty(query["X-Amz-Signature"]!);
        }

        [Fact]
        public async Task LinkAsync_Csv_IsServedAsAnAttachment()
        {
            // Arrange
            using S3BlobStore store = Open();

            // Act
            Uri? url = await store.LinkAsync(new BlobRef("conversation-1", "rows.csv", "text/csv", 8), TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

            // Assert
            NameValueCollection query = HttpUtility.ParseQueryString(url!.Query);
            Assert.Equal("attachment; filename*=UTF-8''rows.csv", query["response-content-disposition"]);
            Assert.Equal("60", query["X-Amz-Expires"]);
        }

        [Fact]
        public async Task LinkAsync_UnsafeName_Throws()
        {
            // Arrange
            using S3BlobStore store = Open();

            // Act + Assert
            _ = await Assert.ThrowsAsync<ArgumentException>(async () =>
                await store.LinkAsync(new BlobRef("conversation-1", "../x.png", "image/png", 1), TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken));
        }
    }
}
