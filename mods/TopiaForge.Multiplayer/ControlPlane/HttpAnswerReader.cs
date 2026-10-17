using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Multiplayer.ControlPlane
{
    /// <summary>An answer that broke a rule of this client; it carries the failure, and its message is fixed text.</summary>
    internal sealed class ControlPlaneProtocolException : Exception
    {
        internal ControlPlaneProtocolException(ControlPlaneTransportFailure failure)
            : base("The service's answer broke the client's rules: " + failure + ".")
        {
            Failure = failure;
        }

        internal ControlPlaneTransportFailure Failure { get; }
    }

    /// <summary>
    /// Reads one HTTP/1.1 answer strictly and within bounds: a status line, at most 64 header lines in 16 KiB, and a body
    /// framed by Content-Length, chunked encoding or the end of the connection, never larger than the caller allows; a
    /// chunked body's framing has a budget of its own. Every line ends in CR LF, folded and malformed lines are refused,
    /// and an answer that names both framings is refused as ambiguous. A redirect is never followed. Nothing the service
    /// writes is kept but the status, the body, whether the body is JSON, and a plain Retry-After.
    /// </summary>
    internal static class HttpAnswerReader
    {
        internal const int MaximumHeaderBytes = 16 * 1024;
        internal const int MaximumHeaders = 64;
        internal const int MaximumRetryAfterSeconds = 86_400;
        private const int MaximumInterimAnswers = 4;
        private const int MaximumChunkLineBytes = 1024;

        internal static async Task<ControlPlaneResponse> ReadAsync(Stream stream, int maximumBody, CancellationToken cancellation)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (maximumBody < 0) throw new ArgumentOutOfRangeException(nameof(maximumBody));
            var reader = new BoundedReader(stream, cancellation);
            int status;
            List<KeyValuePair<string, string>> headers;
            var interim = 0;
            while (true)
            {
                reader.BeginHead(MaximumHeaderBytes);
                status = ParseStatus(await reader.ReadLineAsync().ConfigureAwait(false));
                headers = await ReadHeadersAsync(reader).ConfigureAwait(false);
                if (status >= 200) break;

                // An interim answer is skipped; a protocol switch never happens on a request that asked for none.
                if (status == 101 || ++interim > MaximumInterimAnswers) throw Malformed();
            }

            if (status >= 300 && status < 400) throw new ControlPlaneProtocolException(ControlPlaneTransportFailure.Redirected);
            if (status > 599) throw Malformed();
            var chunked = false;
            long? length = null;
            string? contentType = null;
            int? retryAfter = null;
            var retryAfterCount = 0;
            foreach (var header in headers)
            {
                if (Is(header.Key, "Transfer-Encoding"))
                {
                    if (chunked || !string.Equals(header.Value, "chunked", StringComparison.OrdinalIgnoreCase)) throw Malformed();
                    chunked = true;
                }
                else if (Is(header.Key, "Content-Length"))
                {
                    var value = ParseLength(header.Value);
                    if (length.HasValue && length.Value != value) throw Malformed();
                    length = value;
                }
                else if (Is(header.Key, "Content-Type"))
                {
                    if (contentType != null) throw Malformed();
                    contentType = header.Value;
                }
                else if (Is(header.Key, "Retry-After"))
                {
                    retryAfterCount++;
                    retryAfter = ParseRetryAfter(header.Value);
                }
            }

            if (chunked && length.HasValue) throw Malformed();

            // A Retry-After sent more than once does not say which delay holds, so it says nothing.
            if (retryAfterCount != 1) retryAfter = null;
            byte[] body;
            if (status == 204)
            {
                if (chunked || (length.HasValue && length.Value != 0)) throw Malformed();
                body = Array.Empty<byte>();
            }
            else if (chunked)
            {
                body = await ReadChunkedAsync(reader, maximumBody).ConfigureAwait(false);
            }
            else if (length.HasValue)
            {
                if (length.Value > maximumBody) throw new ControlPlaneProtocolException(ControlPlaneTransportFailure.TooLarge);
                body = await reader.ReadExactlyAsync((int)length.Value).ConfigureAwait(false);
            }
            else
            {
                body = await reader.ReadToEndAsync(maximumBody).ConfigureAwait(false);
            }

            return ControlPlaneResponse.Answered(status, body, IsJson(contentType), retryAfter);
        }

        /// <summary>Whether a media type is JSON: <c>application/json</c> in any case, with or without parameters.</summary>
        internal static bool IsJson(string? contentType)
        {
            if (contentType == null) return false;
            var semicolon = contentType.IndexOf(';');
            var media = (semicolon < 0 ? contentType : contentType.Substring(0, semicolon)).Trim();
            return string.Equals(media, "application/json", StringComparison.OrdinalIgnoreCase);
        }

        private static int ParseStatus(string line)
        {
            // "HTTP/1.1 200 OK": the version, a space, three digits, and a reason the client never reads.
            if (line.Length < 12 || (!line.StartsWith("HTTP/1.1 ", StringComparison.Ordinal) && !line.StartsWith("HTTP/1.0 ", StringComparison.Ordinal))) throw Malformed();
            var status = 0;
            for (var index = 9; index < 12; index++)
            {
                var character = line[index];
                if (character < '0' || character > '9') throw Malformed();
                status = (status * 10) + (character - '0');
            }

            if (line.Length > 12 && line[12] != ' ') throw Malformed();
            if (status < 100) throw Malformed();
            return status;
        }

        private static async Task<List<KeyValuePair<string, string>>> ReadHeadersAsync(BoundedReader reader)
        {
            var headers = new List<KeyValuePair<string, string>>();
            while (true)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line.Length == 0) return headers;
                if (headers.Count == MaximumHeaders) throw Malformed();
                headers.Add(ParseHeader(line));
            }
        }

        private static KeyValuePair<string, string> ParseHeader(string line)
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) throw Malformed();
            for (var index = 0; index < colon; index++)
            {
                if (!IsTokenCharacter(line[index])) throw Malformed();
            }

            for (var index = colon + 1; index < line.Length; index++)
            {
                var character = line[index];
                if (character != '\t' && (character < 0x20 || character > 0x7E)) throw Malformed();
            }

            return new KeyValuePair<string, string>(line.Substring(0, colon), line.Substring(colon + 1).Trim(' ', '\t'));
        }

        /// <summary>
        /// Reads a chunked body. Its framing, every chunk's size line and line end and the trailers, shares one budget
        /// of a header block plus the body's own bound, so a body sent in tiny chunks with long extensions costs no more
        /// on the wire than twice its bound and a header block; past that the answer is refused.
        /// </summary>
        private static async Task<byte[]> ReadChunkedAsync(BoundedReader reader, int maximumBody)
        {
            var framing = (long)MaximumHeaderBytes + maximumBody;
            using (var body = new MemoryStream())
            {
                while (true)
                {
                    var line = await ReadFramingLineAsync(reader, MaximumChunkLineBytes, framing).ConfigureAwait(false);
                    framing -= line.Length + 2;
                    var semicolon = line.IndexOf(';');
                    var size = ParseChunkSize(semicolon < 0 ? line : line.Substring(0, semicolon));
                    if (size == 0) break;
                    if (body.Length + size > maximumBody) throw new ControlPlaneProtocolException(ControlPlaneTransportFailure.TooLarge);
                    var chunk = await reader.ReadExactlyAsync((int)size).ConfigureAwait(false);
                    body.Write(chunk, 0, chunk.Length);
                    if ((await ReadFramingLineAsync(reader, 2, framing).ConfigureAwait(false)).Length != 0) throw Malformed();
                    framing -= 2;
                }

                // Trailers, if any, are read and dropped within what the framing budget has left, and a header block at most.
                if (framing < 2) throw new ControlPlaneProtocolException(ControlPlaneTransportFailure.TooLarge);
                reader.BeginHead((int)Math.Min(MaximumHeaderBytes, framing));
                await ReadHeadersAsync(reader).ConfigureAwait(false);
                return body.ToArray();
            }
        }

        /// <summary>Reads one line of a chunked body's framing within its own limit and what the budget has left.</summary>
        private static Task<string> ReadFramingLineAsync(BoundedReader reader, int limit, long framing)
        {
            if (framing < 2) throw new ControlPlaneProtocolException(ControlPlaneTransportFailure.TooLarge);
            reader.BeginHead((int)Math.Min(limit, framing));
            return reader.ReadLineAsync();
        }

        private static long ParseChunkSize(string text)
        {
            var trimmed = text.TrimEnd(' ', '\t');
            if (trimmed.Length == 0 || trimmed.Length > 8) throw Malformed();
            long size = 0;
            foreach (var character in trimmed)
            {
                var digit = character >= '0' && character <= '9' ? character - '0'
                    : character >= 'a' && character <= 'f' ? character - 'a' + 10
                    : character >= 'A' && character <= 'F' ? character - 'A' + 10
                    : -1;
                if (digit < 0) throw Malformed();
                size = (size * 16) + digit;
            }

            return size;
        }

        private static long ParseLength(string text)
        {
            if (text.Length == 0 || text.Length > 10) throw Malformed();
            long value = 0;
            foreach (var character in text)
            {
                if (character < '0' || character > '9') throw Malformed();
                value = (value * 10) + (character - '0');
            }

            return value;
        }

        private static int? ParseRetryAfter(string text)
        {
            if (text.Length == 0 || text.Length > 6) return null;
            var value = 0;
            foreach (var character in text)
            {
                if (character < '0' || character > '9') return null;
                value = (value * 10) + (character - '0');
            }

            return value <= MaximumRetryAfterSeconds ? value : (int?)null;
        }

        private static bool Is(string name, string expected) => string.Equals(name, expected, StringComparison.OrdinalIgnoreCase);

        private static bool IsTokenCharacter(char character) =>
            (character >= 'a' && character <= 'z') || (character >= 'A' && character <= 'Z') || (character >= '0' && character <= '9') ||
            "!#$%&'*+-.^_`|~".IndexOf(character) >= 0;

        private static ControlPlaneProtocolException Malformed() => new ControlPlaneProtocolException(ControlPlaneTransportFailure.Malformed);

        /// <summary>A buffered reader that counts what the head may still take and what the body may still hold.</summary>
        private sealed class BoundedReader
        {
            private readonly Stream stream;
            private readonly CancellationToken cancellation;
            private readonly byte[] buffer = new byte[4096];
            private int start;
            private int end;
            private int headBudget;

            internal BoundedReader(Stream stream, CancellationToken cancellation)
            {
                this.stream = stream;
                this.cancellation = cancellation;
            }

            /// <summary>Starts counting header bytes afresh: what the next lines, CR LF included, may take in all.</summary>
            internal void BeginHead(int budget) => headBudget = budget;

            /// <summary>Reads one line ending in CR LF, without it. A bare CR or LF, or a line past the budget, is refused.</summary>
            internal async Task<string> ReadLineAsync()
            {
                var line = new StringBuilder(64);
                while (true)
                {
                    if (start == end && !await FillAsync().ConfigureAwait(false)) throw Malformed();
                    var value = buffer[start++];
                    if (--headBudget < 0) throw Malformed();
                    if (value == '\n') throw Malformed();
                    if (value != '\r')
                    {
                        line.Append((char)value);
                        continue;
                    }

                    if (start == end && !await FillAsync().ConfigureAwait(false)) throw Malformed();
                    if (buffer[start++] != '\n' || --headBudget < 0) throw Malformed();
                    return line.ToString();
                }
            }

            internal async Task<byte[]> ReadExactlyAsync(int count)
            {
                var result = new byte[count];
                var copied = 0;
                while (copied < count)
                {
                    if (start == end && !await FillAsync().ConfigureAwait(false)) throw Malformed();
                    var take = Math.Min(count - copied, end - start);
                    Buffer.BlockCopy(buffer, start, result, copied, take);
                    start += take;
                    copied += take;
                }

                return result;
            }

            internal async Task<byte[]> ReadToEndAsync(int maximum)
            {
                using (var collected = new MemoryStream())
                {
                    while (true)
                    {
                        if (start == end && !await FillAsync().ConfigureAwait(false)) return collected.ToArray();
                        var take = end - start;
                        if (collected.Length + take > maximum) throw new ControlPlaneProtocolException(ControlPlaneTransportFailure.TooLarge);
                        collected.Write(buffer, start, take);
                        start = end;
                    }
                }
            }

            private async Task<bool> FillAsync()
            {
                start = 0;
                end = await stream.ReadAsync(buffer, 0, buffer.Length, cancellation).ConfigureAwait(false);
                if (end < 0) end = 0;
                return end > 0;
            }
        }
    }
}
