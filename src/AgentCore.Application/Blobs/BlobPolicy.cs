namespace AgentCore.Application.Blobs;

/// <summary>
/// What a captured sandbox file must satisfy before it is stored.
/// </summary>
public sealed class BlobPolicy
{
    /// <summary>The cap a sandbox file must fit under. 10 MB.</summary>
    public const long DefaultMaxBytes = 10L * 1024 * 1024;

    /// <summary>
    /// The extensions stored by default: images, PDF, plain data and Office files. Never
    /// <c>html</c>, <c>svg</c> or <c>xml</c>: a presigned link serves those as a live page from
    /// the bucket, so a model-written file could pose as one of the operator's own.
    /// </summary>
    public static readonly IReadOnlySet<string> DefaultExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpg", "jpeg", "gif", "webp", "pdf", "csv", "tsv", "txt", "md", "json", "zip", "xlsx", "docx", "pptx",
    };

    /// <summary>The policy the harness uses when the document names none.</summary>
    public static BlobPolicy Default { get; } = new(DefaultMaxBytes, DefaultExtensions);

    /// <summary>Makes a policy.</summary>
    /// <param name="maxBytes">The largest file stored. Must be positive.</param>
    /// <param name="allowedExtensions">Extensions stored, without the dot. Case does not matter.</param>
    public BlobPolicy(long maxBytes, IEnumerable<string> allowedExtensions)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentNullException.ThrowIfNull(allowedExtensions);

        MaxBytes = maxBytes;
        AllowedExtensions = new HashSet<string>(allowedExtensions, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Gets the largest file stored, in bytes.</summary>
    public long MaxBytes { get; }

    /// <summary>Gets the extensions stored, without the dot.</summary>
    public IReadOnlySet<string> AllowedExtensions { get; }

    /// <summary>Says why a file is refused, or that it is not.</summary>
    /// <param name="name">The file's name, already normalised by <see cref="BlobName"/>.</param>
    /// <param name="length">The file's size in bytes.</param>
    /// <returns>A short reason fit for a log line, or <see langword="null"/> when the file may be stored.</returns>
    public string? WhyRefused(string name, long length)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (length < 0 || length > MaxBytes)
        {
            return $"size {length} is outside 0..{MaxBytes} bytes";
        }

        var dot = name.LastIndexOf('.');

        if (dot < 0 || dot == name.Length - 1)
        {
            return "no extension";
        }

        var extension = name[(dot + 1)..];

        return AllowedExtensions.Contains(extension)
            ? null
            : $"extension '{extension}' is not allowed";
    }
}
