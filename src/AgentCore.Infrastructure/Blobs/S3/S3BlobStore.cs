using System.Net;
using AgentCore.Application.Blobs;
using AgentCore.Application.Ports;
using Amazon.S3;
using Amazon.S3.Model;

namespace AgentCore.Infrastructure.Blobs.S3
{
    /// <summary>
    /// <see cref="IBlobStore"/> on any S3-compatible bucket: Backblaze B2 today, Tigris or AWS tomorrow.
    /// </summary>
    public sealed class S3BlobStore : IBlobStore, IDisposable
    {
        /// <summary>The most keys one <c>DeleteObjects</c> call accepts.</summary>
        private const int DeleteBatchSize = 1000;

        private readonly IAmazonS3 _client;

        private readonly string _bucket;

        /// <summary>Wraps an S3 client the store then owns.</summary>
        /// <param name="client">The client. Disposed with the store.</param>
        /// <param name="bucket">The bucket every blob is written into.</param>
        public S3BlobStore(IAmazonS3 client, string bucket)
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentException.ThrowIfNullOrWhiteSpace(bucket);

            _client = client;
            _bucket = bucket;
        }

        /// <inheritdoc />
        public async ValueTask<BlobRef> PutAsync(BlobWrite write, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(write);
            ArgumentNullException.ThrowIfNull(write.Content);

            string key = KeyOf(write.OwnerId, write.Name);

            PutObjectRequest request = new()
            {
                BucketName = _bucket,
                Key = key,
                InputStream = write.Content,
                ContentType = write.MediaType,
                AutoCloseStream = false,
                AutoResetStreamPosition = false,
            };

            request.Headers.ContentLength = write.Length;

            _ = await _client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);

            return new BlobRef(write.OwnerId, write.Name, write.MediaType, write.Length);
        }

        /// <inheritdoc />
        public async ValueTask<BlobRead?> OpenReadAsync(
            string ownerId,
            string name,
            CancellationToken cancellationToken = default)
        {
            string key = KeyOf(ownerId, name);

            GetObjectResponse response;

            try
            {
                response = await _client.GetObjectAsync(_bucket, key, cancellationToken).ConfigureAwait(false);
            }
            catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            return new BlobRead(response.Headers.ContentType, response.ContentLength, response.ResponseStream);
        }

        /// <inheritdoc />
        public async ValueTask<BlobRef?> StatAsync(
            string ownerId,
            string name,
            CancellationToken cancellationToken = default)
        {
            string key = KeyOf(ownerId, name);

            GetObjectMetadataResponse head;

            try
            {
                head = await _client.GetObjectMetadataAsync(_bucket, key, cancellationToken).ConfigureAwait(false);
            }
            catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            return new BlobRef(ownerId, name, head.Headers.ContentType, head.ContentLength);
        }

        /// <inheritdoc />
        public ValueTask<Uri?> LinkAsync(BlobRef blob, TimeSpan lifetime, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(blob);

            GetPreSignedUrlRequest request = new()
            {
                BucketName = _bucket,
                Key = KeyOf(blob.OwnerId, blob.Name),
                Verb = HttpVerb.GET,
                Protocol = LinkProtocol(),
                Expires = DateTime.UtcNow.Add(lifetime),
                ResponseHeaderOverrides =
                {
                    ContentType = blob.MediaType,
                    ContentDisposition = BlobLink.DispositionOf(blob.MediaType, blob.Name),
                },
            };

            return new(new Uri(_client.GetPreSignedURL(request)));
        }

        /// <inheritdoc />
        public async ValueTask DeleteByOwnerAsync(string ownerId, CancellationToken cancellationToken = default)
        {
            string prefix = OwnerPrefix(ownerId);

            ListObjectsV2Request list = new() { BucketName = _bucket, Prefix = prefix };

            List<KeyVersion> batch = new(DeleteBatchSize);

            do
            {
                ListObjectsV2Response page = await _client.ListObjectsV2Async(list, cancellationToken).ConfigureAwait(false);

                foreach (S3Object? found in page.S3Objects ?? [])
                {
                    batch.Add(new KeyVersion { Key = found.Key });

                    if (batch.Count == DeleteBatchSize)
                    {
                        await DeleteBatchAsync(batch, cancellationToken).ConfigureAwait(false);
                    }
                }

                list.ContinuationToken = page.NextContinuationToken;
            }
            while (list.ContinuationToken is not null);

            await DeleteBatchAsync(batch, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _client.Dispose();
        }

        /// <summary>The endpoint's scheme, or https when the client was given a region and no URL.</summary>
        private Protocol LinkProtocol()
        {
            return Uri.TryCreate(_client.Config.ServiceURL, UriKind.Absolute, out Uri? endpoint)
                        && string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                        ? Protocol.HTTP
                        : Protocol.HTTPS;
        }

        private async ValueTask DeleteBatchAsync(List<KeyVersion> batch, CancellationToken cancellationToken)
        {
            if (batch.Count == 0)
            {
                return;
            }

            DeleteObjectsRequest request = new() { BucketName = _bucket, Objects = [.. batch] };

            _ = await _client.DeleteObjectsAsync(request, cancellationToken).ConfigureAwait(false);

            batch.Clear();
        }

        private static string KeyOf(string ownerId, string name)
        {
            return !BlobName.IsSafe(name)
                ? throw new ArgumentException("A blob name must pass BlobName.IsSafe.", nameof(name))
                : OwnerPrefix(ownerId) + name;
        }

        private static string OwnerPrefix(string ownerId)
        {
            return !BlobOwner.IsSafe(ownerId)
                ? throw new ArgumentException("An owner id must pass BlobOwner.IsSafe.", nameof(ownerId))
                : ownerId + "/";
        }
    }
}
