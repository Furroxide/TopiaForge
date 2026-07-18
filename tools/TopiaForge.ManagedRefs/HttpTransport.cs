using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.ManagedRefs;

internal sealed record HttpProbeResult(long? ContentLength);

internal interface IHttpTransport
{
    Task<HttpProbeResult> HeadAsync(
        Uri uri,
        string label,
        string? bearerToken,
        bool hideUri,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    Task<string> GetStringAsync(
        Uri uri,
        string label,
        string? bearerToken,
        bool hideUri,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    Task DownloadAsync(
        Uri uri,
        string destinationPath,
        string label,
        string? bearerToken,
        bool hideUri,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal sealed class HttpTransport : IHttpTransport, IDisposable
{
    private const int MaxTextResponseBytes = 1024 * 1024;
    private readonly HttpClient client;

    internal HttpTransport()
        : this(CreateHandler())
    {
    }

    internal HttpTransport(HttpMessageHandler handler)
    {
        client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TopiaForge-ManagedRefs/1.0");
    }

    internal static HttpClientHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = false,
    };

    public async Task<HttpProbeResult> HeadAsync(
        Uri uri,
        string label,
        string? bearerToken,
        bool hideUri,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Head, uri, bearerToken);
        using var response = await SendAsync(request, label, hideUri, timeout, cancellationToken)
            .ConfigureAwait(false);
        return new HttpProbeResult(response.Content.Headers.ContentLength);
    }

    public async Task<string> GetStringAsync(
        Uri uri,
        string label,
        string? bearerToken,
        bool hideUri,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, uri, bearerToken);
        using var response = await SendAsync(request, label, hideUri, timeout, cancellationToken)
            .ConfigureAwait(false);
        if (response.Content.Headers.ContentLength > MaxTextResponseBytes)
        {
            throw CreateOversizeResponseError(label, hideUri, uri);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaxTextResponseBytes)
            {
                throw CreateOversizeResponseError(label, hideUri, uri);
            }

            buffer.Write(chunk, 0, read);
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            .GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    public async Task DownloadAsync(
        Uri uri,
        string destinationPath,
        string label,
        string? bearerToken,
        bool hideUri,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, uri, bearerToken);
        using var response = await SendAsync(request, label, hideUri, timeout, cancellationToken)
            .ConfigureAwait(false);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 128,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => client.Dispose();

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        string label,
        bool hideUri,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutSource.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is < 200 or >= 300)
            {
                var statusCode = (int)response.StatusCode;
                response.Dispose();
                throw new InvalidDataException(
                    hideUri
                        ? $"{label} request failed with HTTP {statusCode}."
                        : $"{label} request failed for {request.RequestUri} with HTTP {statusCode}.");
            }

            return response;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidDataException(
                hideUri
                    ? $"{label} request timed out."
                    : $"{label} request timed out for {request.RequestUri}.",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidDataException(
                hideUri
                    ? $"{label} request failed."
                    : $"{label} request failed for {request.RequestUri}. {exception.Message}",
                exception);
        }
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, string? bearerToken)
    {
        var request = new HttpRequestMessage(method, uri);
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        return request;
    }

    private static InvalidDataException CreateOversizeResponseError(string label, bool hideUri, Uri uri) =>
        new(
            hideUri
                ? $"{label} response exceeds the {MaxTextResponseBytes}-byte limit."
                : $"{label} response for {uri} exceeds the {MaxTextResponseBytes}-byte limit.");
}
