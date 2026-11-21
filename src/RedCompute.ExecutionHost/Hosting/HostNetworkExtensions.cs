using System.Net.Http;

namespace RedCompute.PluginSdk.Hosting;

public static class HostNetworkExtensions
{
    /// <summary>
    /// An <see cref="HttpClient"/> over the host's handler for the service it serves at
    /// <paramref name="hostBaseUrl"/> (<see cref="IHostNetwork.CreateServiceHandler"/>). Locally the same
    /// as <c>new HttpClient { Timeout = timeout }</c>. A base URL that is not an absolute Uri gets a
    /// handler that refuses every request: locally no request reaches it (an HttpClient refuses such a
    /// URL first), and elsewhere nothing goes around the host.
    /// </summary>
    public static HttpClient CreateServiceClient(this IHostNetwork network, string hostBaseUrl, TimeSpan timeout)
        => new(Uri.TryCreate(hostBaseUrl, UriKind.Absolute, out var uri)
            ? network.CreateServiceHandler(uri)
            : new UnresolvableServiceHandler(hostBaseUrl)) { Timeout = timeout };

    /// <summary>
    /// <see cref="CreateServiceClient(IHostNetwork, string, TimeSpan)"/> with every response body capped at
    /// <paramref name="maxResponseBytes"/> (read on each request): a declared length over it is refused before a byte is
    /// read, and a body that grows past it fails its read with <see cref="ServiceResponseTooLargeException"/> and is
    /// aborted. For a service the server does not run itself (a remote node's runtime), whose answers it buffers.
    /// </summary>
    public static HttpClient CreateServiceClient(this IHostNetwork network, string hostBaseUrl, TimeSpan timeout, Func<long> maxResponseBytes)
    {
        HttpMessageHandler inner = Uri.TryCreate(hostBaseUrl, UriKind.Absolute, out var uri)
            ? network.CreateServiceHandler(uri)
            : new UnresolvableServiceHandler(hostBaseUrl);
        return new HttpClient(new ResponseCapHandler(inner, maxResponseBytes)) { Timeout = timeout };
    }

    private sealed class ResponseCapHandler(HttpMessageHandler inner, Func<long> cap) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var limit = cap();
            var path = request.RequestUri?.AbsolutePath ?? "?";
            var response = await base.SendAsync(request, cancellationToken);
            if (response.Content.Headers.ContentLength is { } declared && declared > limit)
            {
                response.Dispose();
                throw new ServiceResponseTooLargeException(path, limit);
            }
            var original = response.Content;
            var capped = new CappedContent(original, limit, path);
            foreach (var header in original.Headers) capped.Headers.TryAddWithoutValidation(header.Key, header.Value);
            response.Content = capped;
            return response;
        }
    }

    private sealed class CappedContent(HttpContent inner, long cap, string path) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context, CancellationToken cancellationToken)
        {
            await using var source = await CreateContentReadStreamAsync(cancellationToken);
            await source.CopyToAsync(stream, cancellationToken);
        }

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
            => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
            => new CappedStream(await inner.ReadAsStreamAsync(cancellationToken), cap, path);

        protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(CancellationToken.None);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Counts what it reads; one byte past the cap disposes the source (the exchange is aborted) and throws.</summary>
    private sealed class CappedStream(Stream inner, long cap, string path) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => Count(await inner.ReadAsync(buffer, cancellationToken));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        private int Count(int read)
        {
            _read += read;
            if (_read <= cap) return read;
            inner.Dispose();
            throw new ServiceResponseTooLargeException(path, cap);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class UnresolvableServiceHandler(string hostBaseUrl) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException($"'{hostBaseUrl}' is not a service address");
    }

    /// <summary>
    /// The base URL the server uses for a service the host serves at <paramref name="hostBaseUrl"/>
    /// (scheme, host and port, no trailing slash). Locally the host resolves every URI to itself, so
    /// the configured string comes back exactly as given, casing and all.
    /// </summary>
    public static string ResolveServiceBaseUrl(this IHostNetwork network, string hostBaseUrl)
    {
        if (!Uri.TryCreate(hostBaseUrl, UriKind.Absolute, out var uri)) return hostBaseUrl;
        var resolved = network.ResolveServiceUri(uri);
        return ReferenceEquals(resolved, uri) || resolved.Equals(uri)
            ? hostBaseUrl
            : resolved.GetLeftPart(UriPartial.Authority);
    }
}

/// <summary>A service's answer was over the size the caller accepts from it; the exchange was aborted.</summary>
public sealed class ServiceResponseTooLargeException(string path, long cap)
    : InvalidOperationException($"The answer to {path} is over the {Describe(cap)} cap, so it was cut off and refused.")
{
    public string Path { get; } = path;
    public long Cap { get; } = cap;

    private static string Describe(long bytes) => bytes >= 1L << 30 && bytes % (1L << 30) == 0 ? $"{bytes >> 30} GiB"
        : bytes >= 1L << 20 && bytes % (1L << 20) == 0 ? $"{bytes >> 20} MiB"
        : bytes >= 1L << 10 && bytes % (1L << 10) == 0 ? $"{bytes >> 10} KiB" : $"{bytes} bytes";
}
