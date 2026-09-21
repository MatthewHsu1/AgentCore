using AgentCore.Application.Blobs;
using Xunit;

namespace AgentCore.Application.Tests.Blobs
{
    /// <summary>The disposition a link serves a blob with. RFC 6266 spells the header; the spec picks inline vs attachment.</summary>
    public sealed class BlobLinkTests
    {
        [Theory]
        [InlineData("image/png", "chart.png", "inline; filename*=UTF-8''chart.png")]
        [InlineData("application/pdf", "report.pdf", "inline; filename*=UTF-8''report.pdf")]
        [InlineData("text/csv", "rows.csv", "attachment; filename*=UTF-8''rows.csv")]
        [InlineData("image/svg+xml", "logo.svg", "attachment; filename*=UTF-8''logo.svg")]
        [InlineData("text/html", "page.html", "attachment; filename*=UTF-8''page.html")]
        [InlineData("application/octet-stream", "blob.bin", "attachment; filename*=UTF-8''blob.bin")]
        public void DispositionOf_KnownMediaTypes_MatchesTheSpec(string mediaType, string name, string expected)
        {
            // Act
            string header = BlobLink.DispositionOf(mediaType, name);

            // Assert
            Assert.Equal(expected, header);
        }

        [Fact]
        public void DispositionOf_NameWithSpaceAndUnicode_PercentEncodesPerRfc5987()
        {
            // Act
            string header = BlobLink.DispositionOf("image/png", "my chart é.png");

            // Assert
            Assert.Equal("inline; filename*=UTF-8''my%20chart%20%C3%A9.png", header);
        }

        [Fact]
        public void Lifetime_IsFifteenMinutes()
        {
            // Assert: decision 10 of the sandbox file capture spec.
            Assert.Equal(TimeSpan.FromMinutes(15), BlobLink.Lifetime);
        }
    }
}
