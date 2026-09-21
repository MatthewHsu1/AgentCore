using System.Text;
using AgentCore.Application.Blobs;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Blobs.S3;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Blobs.S3
{
    /// <summary>Put, read, replace and sweep, against a real bucket.</summary>
    public sealed class S3BlobStoreTests : IAsyncLifetime
    {
        private readonly string _owner = "test-" + Guid.NewGuid().ToString("N");

        private IBlobStore? _store;

        private IBlobStore Store => _store ?? throw new InvalidOperationException("Not opened.");

        /// <inheritdoc />
        public async ValueTask InitializeAsync()
        {
            if (!S3Bucket.IsConfigured)
            {
                return;
            }

            BlobProviderConfiguration entry = new()
            {
                Kind = S3BlobStoreAdapter.ProviderKind,
                Endpoint = S3Bucket.Endpoint,
                Bucket = S3Bucket.Bucket,
            };

            _store = await new S3BlobStoreAdapter().OpenAsync(entry, secrets: null, TestContext.Current.CancellationToken);
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (_store is null)
            {
                return;
            }

            await _store.DeleteByOwnerAsync(_owner, CancellationToken.None);
            (_store as IDisposable)?.Dispose();
        }

        [S3Fact]
        public async Task PutAsync_ThenOpenReadAsync_ReturnsTheSameBytesAndMediaType()
        {
            // Arrange
            byte[] bytes = Encoding.UTF8.GetBytes("a,b\n1,2\n");
            CancellationToken token = TestContext.Current.CancellationToken;

            // Act
            BlobRef put = await Store.PutAsync(new BlobWrite(_owner, "rows.csv", "text/csv", new MemoryStream(bytes), bytes.Length), token);
            await using BlobRead? read = await Store.OpenReadAsync(_owner, "rows.csv", token);

            // Assert
            Assert.Equal(new BlobRef(_owner, "rows.csv", "text/csv", bytes.Length), put);
            Assert.NotNull(read);
            Assert.Equal("text/csv", read.MediaType);
            Assert.Equal(bytes.Length, read.Length);
            using MemoryStream copy = new();
            await read.Content.CopyToAsync(copy, token);
            Assert.Equal(bytes, copy.ToArray());
        }

        [S3Fact]
        public async Task PutAsync_SameNameTwice_SecondReplacesFirst()
        {
            // Arrange
            CancellationToken token = TestContext.Current.CancellationToken;
            byte[] first = Encoding.UTF8.GetBytes("first");
            byte[] second = Encoding.UTF8.GetBytes("second, longer");

            // Act
            _ = await Store.PutAsync(new BlobWrite(_owner, "chart.png", "image/png", new MemoryStream(first), first.Length), token);
            _ = await Store.PutAsync(new BlobWrite(_owner, "chart.png", "image/png", new MemoryStream(second), second.Length), token);
            await using BlobRead? read = await Store.OpenReadAsync(_owner, "chart.png", token);

            // Assert
            Assert.NotNull(read);
            Assert.Equal(second.Length, read.Length);
        }

        [S3Fact]
        public async Task OpenReadAsync_UnknownName_ReturnsNull()
        {
            // Act
            BlobRead? read = await Store.OpenReadAsync(_owner, "missing.pdf", TestContext.Current.CancellationToken);

            // Assert
            Assert.Null(read);
        }

        [S3Fact]
        public async Task DeleteByOwnerAsync_RemovesEveryBlobOfThatOwnerOnly()
        {
            // Arrange
            CancellationToken token = TestContext.Current.CancellationToken;
            string other = _owner + "-other";
            byte[] bytes = [1, 2, 3];

            _ = await Store.PutAsync(new BlobWrite(_owner, "a.png", "image/png", new MemoryStream(bytes), bytes.Length), token);
            _ = await Store.PutAsync(new BlobWrite(_owner, "b.pdf", "application/pdf", new MemoryStream(bytes), bytes.Length), token);
            _ = await Store.PutAsync(new BlobWrite(other, "a.png", "image/png", new MemoryStream(bytes), bytes.Length), token);

            try
            {
                // Act
                await Store.DeleteByOwnerAsync(_owner, token);

                // Assert
                Assert.Null(await Store.OpenReadAsync(_owner, "a.png", token));
                Assert.Null(await Store.OpenReadAsync(_owner, "b.pdf", token));
                await using BlobRead? kept = await Store.OpenReadAsync(other, "a.png", token);
                Assert.NotNull(kept);
            }
            finally
            {
                await Store.DeleteByOwnerAsync(other, CancellationToken.None);
            }
        }

        [S3Fact]
        public async Task StatAsync_StoredBlob_ReturnsItsFactsWithoutTheBytes()
        {
            // Arrange
            byte[] bytes = Encoding.UTF8.GetBytes("a,b\n1,2\n");
            CancellationToken token = TestContext.Current.CancellationToken;
            _ = await Store.PutAsync(new BlobWrite(_owner, "rows.csv", "text/csv", new MemoryStream(bytes), bytes.Length), token);

            // Act
            BlobRef? stat = await Store.StatAsync(_owner, "rows.csv", token);

            // Assert
            Assert.Equal(new BlobRef(_owner, "rows.csv", "text/csv", bytes.Length), stat);
        }

        [S3Fact]
        public async Task StatAsync_UnknownName_ReturnsNull()
        {
            // Act
            BlobRef? stat = await Store.StatAsync(_owner, "never.png", TestContext.Current.CancellationToken);

            // Assert
            Assert.Null(stat);
        }

        [S3Fact]
        public async Task LinkAsync_StoredBlob_IsFetchableWithNoCredential()
        {
            // Arrange
            byte[] bytes = [0x89, 0x50, 0x4E, 0x47];
            CancellationToken token = TestContext.Current.CancellationToken;
            BlobRef blob = await Store.PutAsync(new BlobWrite(_owner, "chart.png", "image/png", new MemoryStream(bytes), bytes.Length), token);

            // Act
            Uri? url = await Store.LinkAsync(blob, TimeSpan.FromMinutes(1), token);
            using HttpClient anonymous = new();
            using HttpResponseMessage fetched = await anonymous.GetAsync(url, token);

            // Assert
            Assert.Equal(System.Net.HttpStatusCode.OK, fetched.StatusCode);
            Assert.Equal("image/png", fetched.Content.Headers.ContentType?.MediaType);
            Assert.Equal("inline", fetched.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Equal(bytes, await fetched.Content.ReadAsByteArrayAsync(token));
        }

        [S3Fact]
        public async Task DeleteByOwnerAsync_NothingStored_DoesNotThrow()
        {
            Exception? error = await Record.ExceptionAsync(() => Store.DeleteByOwnerAsync(_owner, TestContext.Current.CancellationToken).AsTask());

            Assert.Null(error);
        }
    }
}
