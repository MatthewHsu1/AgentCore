using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Secrets;
using Amazon.Runtime;
using Amazon.S3;

namespace AgentCore.Infrastructure.Blobs.S3;

/// <summary>
/// The <c>s3</c> blob vendor behind <see cref="IBlobStore"/>.
/// </summary>
public sealed class S3BlobStoreAdapter : IBlobStoreAdapter
{
    /// <summary>The one <c>kind</c> value this adapter serves.</summary>
    public const string ProviderKind = "s3";

    /// <summary>The region signed when the document names none and the host does not say.</summary>
    public const string DefaultRegion = "us-east-1";

    /// <inheritdoc />
    public string Kind => ProviderKind;

    /// <inheritdoc />
    public async ValueTask<IBlobStore> OpenAsync(
        BlobProviderConfiguration entry,
        ISecretResolverPort? secrets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.Endpoint is not { Length: > 0 } endpoint)
        {
            throw Fail("/providers/blobs/endpoint", "providers.blobs: { kind: s3 } names no endpoint. Write the service URL, such as https://s3.us-west-004.backblazeb2.com.");
        }

        if (entry.Bucket is not { Length: > 0 } bucket)
        {
            throw Fail("/providers/blobs/bucket", "providers.blobs: { kind: s3 } names no bucket. Write the bucket every blob is written into.");
        }

        const string because = "A sandbox file under providers.blobs is written to an S3 bucket.";

        var keyId = await secrets.RequireAsync(KnownSecrets.S3AccessKeyId, because, cancellationToken).ConfigureAwait(false);
        var secret = await secrets.RequireAsync(KnownSecrets.S3SecretAccessKey, because, cancellationToken).ConfigureAwait(false);

        AmazonS3Config config = new()
        {
            ServiceURL = endpoint,
            AuthenticationRegion = entry.Region ?? RegionOf(endpoint),
            ForcePathStyle = true,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };

        return new S3BlobStore(new AmazonS3Client(new BasicAWSCredentials(keyId, secret), config), bucket);
    }

    /// <summary>Reads the region off a host shaped <c>s3.{region}.{rest}</c>, as B2's are.</summary>
    /// <param name="endpoint">The service URL.</param>
    /// <returns>The region label, or <see cref="DefaultRegion"/> when the host is not shaped that way.</returns>
    internal static string RegionOf(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return DefaultRegion;
        }

        var labels = uri.Host.Split('.');

        return labels.Length >= 3 && labels[0].Equals("s3", StringComparison.OrdinalIgnoreCase)
            ? labels[1]
            : DefaultRegion;
    }

    private static ConfigurationLoadException Fail(string pointer, string message)
        => new(new ConfigurationError
        {
            Pointer = pointer,
            Message = message,
            Check = ConfigurationCheck.ReferenceResolution,
        });
}
