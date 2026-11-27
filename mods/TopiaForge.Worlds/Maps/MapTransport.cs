using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Worlds.Maps
{
    internal sealed class MapEndpointPolicy
    {
        private readonly Uri origin;
        private readonly bool loopbackTests;
        internal MapEndpointPolicy(Uri origin, bool loopbackTests = false)
        {
            this.origin = origin;
            this.loopbackTests = loopbackTests && IPAddress.TryParse(origin.Host, out var address) && IPAddress.IsLoopback(address);
            Require(origin);
        }
        internal void Require(Uri uri)
        {
            if (!uri.IsAbsoluteUri || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
                (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) && !(loopbackTests && uri.Scheme == "http")) ||
                !string.Equals(uri.IdnHost, origin.IdnHost, StringComparison.OrdinalIgnoreCase) || uri.Port != origin.Port)
                throw new FormatException("The map source is not the game's trusted public map service. Update TopiaForge or choose another map.");
        }
    }

    internal interface IMapTransport
    {
        Task<MapResponse> FetchAsync(Uri uri, MapCacheRecord? cached, CancellationToken token);
    }

    internal sealed class MapHttpTransport : IMapTransport, IDisposable
    {
        private readonly HttpClient client;
        internal MapHttpTransport(HttpMessageHandler? handler = null)
        {
            client = new HttpClient(handler ?? new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                UseCookies = false,
                UseDefaultCredentials = false,
            })
            { Timeout = Timeout.InfiniteTimeSpan };
        }
        public async Task<MapResponse> FetchAsync(Uri uri, MapCacheRecord? cached, CancellationToken token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (!string.IsNullOrEmpty(cached?.Etag)) request.Headers.TryAddWithoutValidation("If-None-Match", cached!.Etag);
            if (!string.IsNullOrEmpty(cached?.LastModified)) request.Headers.TryAddWithoutValidation("If-Modified-Since", cached!.LastModified);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            try
            {
                return new MapResponse((int)response.StatusCode, await response.Content.ReadAsStreamAsync().ConfigureAwait(false),
                    response.Content.Headers.ContentLength, response.Headers.ETag?.ToString(),
                    response.Content.Headers.LastModified?.ToString("R"), response);
            }
            catch { response.Dispose(); throw; }
        }
        public void Dispose() => client.Dispose();
    }

    internal sealed class MapResponse : IDisposable
    {
        private readonly IDisposable? owner;
        internal MapResponse(int status, Stream content, long? length = null, string? etag = null, string? modified = null, IDisposable? owner = null)
        { Status = status; Content = content; Length = length; Etag = etag; Modified = modified; this.owner = owner; }
        internal int Status { get; }
        internal Stream Content { get; }
        internal long? Length { get; }
        internal string? Etag { get; }
        internal string? Modified { get; }
        public void Dispose() { try { Content.Dispose(); } finally { owner?.Dispose(); } }
    }
}
