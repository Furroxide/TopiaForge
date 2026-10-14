using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Multiplayer.NatTraversal.PortMapping
{
    /// <summary>An HTTP answer from a gateway: its status, its body, and the local address the request left from.</summary>
    internal sealed class UpnpHttpResponse
    {
        internal UpnpHttpResponse(int status, byte[] body, IPAddress localAddress)
        {
            Status = status;
            Body = body;
            LocalAddress = localAddress;
        }

        internal int Status { get; }

        internal byte[] Body { get; }

        /// <summary>Gets this machine's address on the interface that reaches the gateway: the mapping's internal client.</summary>
        internal IPAddress LocalAddress { get; }

        public override string ToString() => "UPnP HTTP " + Status.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A deliberately small HTTP/1.1 client for the gateway's description and SOAP calls: one request per connection to an
    /// address literal, no proxy, no redirect, a timeout on every step, and a bounded answer read by Content-Length,
    /// chunked encoding or the connection's end. A general HTTP client might route a LAN request through a configured
    /// proxy or follow a redirect off the LAN; this one cannot.
    /// </summary>
    internal static class UpnpHttp
    {
        internal const int MaximumResponseBytes = 64 * 1024;
        internal const int TimeoutMilliseconds = 3000;

        internal static async Task<UpnpHttpResponse?> SendAsync(Uri uri, string method, string? soapAction, byte[]? body, CancellationToken cancellation)
        {
            if (uri.Scheme != "http" || !IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address)) return null;
            using (var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            using (var client = new TcpClient(address.AddressFamily))
            {
                // Disposing the client is what ends a stalled connect or read, so the budget's expiry does exactly that.
                budget.CancelAfter(TimeoutMilliseconds);
                using (budget.Token.Register(() => client.Dispose()))
                {
                    try
                    {
                        await client.ConnectAsync(address, uri.Port).ConfigureAwait(false);
                        var local = ((IPEndPoint)client.Client.LocalEndPoint!).Address;
                        var stream = client.GetStream();
                        var request = Request(uri, method, soapAction, body);
                        await stream.WriteAsync(request, 0, request.Length, budget.Token).ConfigureAwait(false);
                        var raw = await ReadAllAsync(stream, budget.Token).ConfigureAwait(false);
                        return raw == null ? null : Parse(raw, NatAddresses.Unmap(local));
                    }
                    catch (Exception exception) when (exception is SocketException || exception is IOException || exception is OperationCanceledException ||
                                                      exception is ObjectDisposedException || exception is InvalidOperationException)
                    {
                        return null;
                    }
                }
            }
        }

        private static byte[] Request(Uri uri, string method, string? soapAction, byte[]? body)
        {
            var builder = new StringBuilder(256);
            builder.Append(method).Append(' ').Append(uri.PathAndQuery).Append(" HTTP/1.1\r\n");
            builder.Append("Host: ").Append(uri.Authority).Append("\r\n");
            builder.Append("Connection: close\r\n");
            if (body != null)
            {
                builder.Append("Content-Type: text/xml; charset=\"utf-8\"\r\n");
                if (soapAction != null) builder.Append("SOAPAction: \"").Append(soapAction).Append("\"\r\n");
                builder.Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            }

            builder.Append("\r\n");
            var head = Encoding.ASCII.GetBytes(builder.ToString());
            if (body == null) return head;
            var request = new byte[head.Length + body.Length];
            Buffer.BlockCopy(head, 0, request, 0, head.Length);
            Buffer.BlockCopy(body, 0, request, head.Length, body.Length);
            return request;
        }

        /// <summary>Reads until the gateway closes; more than the bound fails the whole answer.</summary>
        private static async Task<byte[]?> ReadAllAsync(Stream stream, CancellationToken cancellation)
        {
            var buffer = new byte[4096];
            using (var collected = new MemoryStream())
            {
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellation).ConfigureAwait(false);
                    if (read <= 0) return collected.ToArray();
                    if (collected.Length + read > MaximumResponseBytes + 4096) return null;
                    collected.Write(buffer, 0, read);
                }
            }
        }

        /// <summary>Parses the status line, the headers and the body; a malformed answer is null.</summary>
        internal static UpnpHttpResponse? Parse(byte[] raw, IPAddress local)
        {
            var end = IndexOf(raw, new byte[] { 13, 10, 13, 10 }, 0);
            if (end < 0) return null;
            var head = Encoding.ASCII.GetString(raw, 0, end).Split(new[] { "\r\n" }, StringSplitOptions.None);
            var status = head[0].Split(' ');
            if (status.Length < 2 || !status[0].StartsWith("HTTP/1.", StringComparison.Ordinal) ||
                !int.TryParse(status[1], NumberStyles.None, CultureInfo.InvariantCulture, out var code) || code < 100 || code > 599)
            {
                return null;
            }

            var length = -1;
            var chunked = false;
            for (var index = 1; index < head.Length; index++)
            {
                var colon = head[index].IndexOf(':');
                if (colon <= 0) continue;
                var name = head[index].Substring(0, colon).Trim();
                var value = head[index].Substring(colon + 1).Trim();
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out length) || length > MaximumResponseBytes) return null;
                }
                else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) && value.Equals("chunked", StringComparison.OrdinalIgnoreCase))
                {
                    chunked = true;
                }
            }

            var start = end + 4;
            byte[]? body;
            if (chunked) body = Dechunk(raw, start);
            else if (length >= 0) body = start + length <= raw.Length ? Slice(raw, start, length) : null;
            else body = Slice(raw, start, raw.Length - start);
            return body == null || body.Length > MaximumResponseBytes ? null : new UpnpHttpResponse(code, body, local);
        }

        private static byte[]? Dechunk(byte[] raw, int position)
        {
            using (var body = new MemoryStream())
            {
                while (true)
                {
                    var lineEnd = IndexOf(raw, new byte[] { 13, 10 }, position);
                    if (lineEnd < 0) return null;
                    var sizeText = Encoding.ASCII.GetString(raw, position, lineEnd - position).Split(';')[0].Trim();
                    if (!int.TryParse(sizeText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var size) || size < 0 || size > MaximumResponseBytes) return null;
                    position = lineEnd + 2;
                    if (size == 0) return body.ToArray();
                    if (position + size + 2 > raw.Length || body.Length + size > MaximumResponseBytes) return null;
                    body.Write(raw, position, size);
                    position += size + 2;
                }
            }
        }

        private static byte[] Slice(byte[] raw, int start, int length)
        {
            var result = new byte[Math.Max(0, length)];
            Buffer.BlockCopy(raw, start, result, 0, result.Length);
            return result;
        }

        private static int IndexOf(byte[] haystack, byte[] needle, int start)
        {
            for (var index = start; index <= haystack.Length - needle.Length; index++)
            {
                var match = true;
                for (var offset = 0; offset < needle.Length && match; offset++) match = haystack[index + offset] == needle[offset];
                if (match) return index;
            }

            return -1;
        }
    }
}
