using AgentCore.Application.Blobs;
using Xunit;

namespace AgentCore.Application.Tests.Blobs
{
    /// <summary>The cap and the allowlist a captured file must pass.</summary>
    public sealed class BlobPolicyTests
    {
        [Theory]
        [InlineData("chart.png", 15_769)]
        [InlineData("report.PDF", 1)]
        [InlineData("rows.csv", 0)]
        [InlineData("rows.tsv", 0)]
        [InlineData("big.png", 10L * 1024 * 1024)]
        [InlineData("report.md", 518)]
        [InlineData("units.xlsx", 4_096)]
        [InlineData("bundle.zip", 4_096)]
        public void WhyRefused_AllowedExtensionUnderCap_ReturnsNull(string name, long length)
        {
            // Act
            string? reason = BlobPolicy.Default.WhyRefused(name, length);

            // Assert
            Assert.Null(reason);
        }

        [Fact]
        public void WhyRefused_OneByteOverCap_NamesTheSize()
        {
            // Act
            string? reason = BlobPolicy.Default.WhyRefused("chart.png", (10L * 1024 * 1024) + 1);

            // Assert
            Assert.NotNull(reason);
            Assert.Contains("10485760", reason, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("page.html")]
        [InlineData("logo.svg")]
        [InlineData("feed.xml")]
        [InlineData("run.exe")]
        [InlineData("chart.png.js")]
        public void WhyRefused_ExtensionOffTheList_NamesTheExtension(string name)
        {
            // Act
            string? reason = BlobPolicy.Default.WhyRefused(name, 10);

            // Assert
            Assert.NotNull(reason);
            Assert.Contains("not allowed", reason, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("README")]
        [InlineData("chart.")]
        public void WhyRefused_NoExtension_Refuses(string name)
        {
            // Act
            string? reason = BlobPolicy.Default.WhyRefused(name, 10);

            // Assert
            Assert.Equal("no extension", reason);
        }

        [Fact]
        public void WhyRefused_NegativeLength_Refuses()
        {
            // Act
            string? reason = BlobPolicy.Default.WhyRefused("chart.png", -1);

            // Assert
            Assert.NotNull(reason);
        }

        [Fact]
        public void Constructor_CustomList_MatchesWithoutRegardToCase()
        {
            // Arrange
            BlobPolicy policy = new(100, ["JPG"]);

            // Act
            string? accepted = policy.WhyRefused("photo.jpg", 50);
            string? refused = policy.WhyRefused("chart.png", 50);

            // Assert
            Assert.Null(accepted);
            Assert.NotNull(refused);
        }

        [Fact]
        public void Constructor_ZeroCap_Throws()
        {
            // Act & Assert
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => new BlobPolicy(0, ["png"]));
        }

        [Fact]
        public void DefaultExtensions_EachHasAMediaType()
        {
            // Act
            List<string> untyped = [.. BlobPolicy.DefaultExtensions.Where(extension => BlobMediaTypes.Of("file." + extension) == BlobMediaTypes.Fallback)];

            // Assert
            Assert.Empty(untyped);
        }
    }
}
