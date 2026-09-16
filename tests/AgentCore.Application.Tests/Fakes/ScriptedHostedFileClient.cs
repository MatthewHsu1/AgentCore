#pragma warning disable MEAI001 // IHostedFileClient is evaluation-only in Microsoft.Extensions.AI 10.10.0.

using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Fakes;

/// <summary>An <see cref="IHostedFileClient"/> that serves fixed bytes per file id, and records each download's scope.</summary>
internal sealed class ScriptedHostedFileClient : IHostedFileClient
{
    private readonly Dictionary<string, (byte[] Bytes, string? MediaType)> _files = new(StringComparer.Ordinal);

    /// <summary>Gets the scope each download asked for, keyed by file id.</summary>
    public Dictionary<string, string?> ScopesAsked { get; } = new(StringComparer.Ordinal);

    public ScriptedHostedFileClient Serve(string fileId, byte[] bytes, string? mediaType = null)
    {
        _files[fileId] = (bytes, mediaType);
        return this;
    }

    public Task<HostedFileDownloadStream> DownloadAsync(string fileId, HostedFileClientOptions? options = null, CancellationToken cancellationToken = default)
    {
        ScopesAsked[fileId] = options?.Scope;

        return _files.TryGetValue(fileId, out var file)
            ? Task.FromResult<HostedFileDownloadStream>(new FixedDownload(file.Bytes, file.MediaType))
            : throw new InvalidOperationException($"No such file '{fileId}'.");
    }

    public Task<HostedFileContent> UploadAsync(Stream content, string? mediaType = null, string? fileName = null, HostedFileClientOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<HostedFileContent?> GetFileInfoAsync(string fileId, HostedFileClientOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public IAsyncEnumerable<HostedFileContent> ListFilesAsync(HostedFileClientOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<bool> DeleteAsync(string fileId, HostedFileClientOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    private sealed class FixedDownload(byte[] bytes, string? mediaType) : HostedFileDownloadStream
    {
        private readonly MemoryStream _inner = new(bytes);

        public override string? MediaType => mediaType;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
