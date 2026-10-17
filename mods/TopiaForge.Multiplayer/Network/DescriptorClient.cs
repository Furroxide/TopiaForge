using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Multiplayer.Network
{
    internal enum DescriptorFetchOutcome
    {
        Fetched = 0,

        /// <summary>Nothing answered at the address.</summary>
        Unreachable = 1,

        /// <summary>The host did not answer within the time allowed.</summary>
        TimedOut = 2,

        /// <summary>The host refused: it has no descriptor, is busy, or is rate-limiting this address.</summary>
        Refused = 3,

        /// <summary>The host answered with something that is not a valid descriptor for the expected server.</summary>
        Invalid = 4
    }

    /// <summary>The result of asking a host for its signed descriptor. A failure carries a reason, never an address.</summary>
    internal sealed class DescriptorFetch
    {
        internal DescriptorFetch(DescriptorFetchOutcome outcome, SignedServerDescriptor? descriptor, string detail)
        {
            Outcome = outcome;
            Descriptor = descriptor;
            Detail = detail;
        }

        internal DescriptorFetchOutcome Outcome { get; }
        internal SignedServerDescriptor? Descriptor { get; }
        internal string Detail { get; }
    }

    /// <summary>
    /// Fetches a host's signed descriptor from its descriptor endpoint and verifies it before anything trusts it: the
    /// response is bounded to 256 KiB, the descriptor must verify, and when the caller expects a server, it must be that
    /// one. Runs off the main thread.
    /// </summary>
    internal static class DescriptorClient
    {
        private const int MaximumHeaderBytes = 64;

        internal static async Task<DescriptorFetch> FetchAsync(IPEndPoint host, string? expectedServerId, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));
            using (var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = true, NoDelay = true })
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                deadline.CancelAfter(timeout);
                using (deadline.Token.Register(socket.Dispose))
                {
                    try
                    {
                        var target = host.Address.AddressFamily == AddressFamily.InterNetwork ? new IPEndPoint(host.Address.MapToIPv6(), host.Port) : host;
                        await socket.ConnectAsync(target).ConfigureAwait(false);
                        var request = Encoding.ASCII.GetBytes("TFD1 descriptor\n");
                        await socket.SendAsync(new ArraySegment<byte>(request), SocketFlags.None).ConfigureAwait(false);
                        return await ReadResponseAsync(socket, expectedServerId).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is SocketException || exception is ObjectDisposedException)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return deadline.IsCancellationRequested
                            ? new DescriptorFetch(DescriptorFetchOutcome.TimedOut, null, "The host did not answer in time.")
                            : new DescriptorFetch(DescriptorFetchOutcome.Unreachable, null, "The host could not be reached.");
                    }
                }
            }
        }

        private static async Task<DescriptorFetch> ReadResponseAsync(Socket socket, string? expectedServerId)
        {
            var header = new byte[MaximumHeaderBytes];
            var headerLength = 0;
            var newline = -1;
            while (newline < 0)
            {
                if (headerLength == header.Length) return Invalid("The host's answer did not start with a status line.");
                var read = await socket.ReceiveAsync(new ArraySegment<byte>(header, headerLength, header.Length - headerLength), SocketFlags.None).ConfigureAwait(false);
                if (read == 0) return Invalid("The host closed the connection without answering.");
                headerLength += read;
                newline = Array.IndexOf(header, (byte)'\n', 0, headerLength);
            }

            var status = Encoding.ASCII.GetString(header, 0, newline);
            if (!status.StartsWith("TFD1 200 ", StringComparison.Ordinal))
            {
                return status.StartsWith("TFD1 ", StringComparison.Ordinal) && status.Length == 8
                    ? new DescriptorFetch(DescriptorFetchOutcome.Refused, null, RefusalDetail(status.Substring(5)))
                    : Invalid("The host's answer did not start with a status line.");
            }

            if (!int.TryParse(status.Substring(9), NumberStyles.None, CultureInfo.InvariantCulture, out var length) ||
                length < 1 || length > SignedServerDescriptor.MaximumBytes || status.Substring(9) != length.ToString(CultureInfo.InvariantCulture))
            {
                return Invalid("The host announced a descriptor of an impossible size.");
            }

            var body = new byte[length];
            var received = Math.Min(headerLength - newline - 1, length);
            Buffer.BlockCopy(header, newline + 1, body, 0, received);
            if (headerLength - newline - 1 > length) return Invalid("The host sent more than it announced.");
            while (received < length)
            {
                var read = await socket.ReceiveAsync(new ArraySegment<byte>(body, received, length - received), SocketFlags.None).ConfigureAwait(false);
                if (read == 0) return Invalid("The host closed the connection before the descriptor was complete.");
                received += read;
            }

            if (!SignedServerDescriptor.TryVerify(body, expectedServerId, out var verified, out var error)) return Invalid(error);
            return new DescriptorFetch(DescriptorFetchOutcome.Fetched, verified, string.Empty);
        }

        private static DescriptorFetch Invalid(string detail) => new DescriptorFetch(DescriptorFetchOutcome.Invalid, null, detail);

        /// <summary>Says why the host refused, in this game's own words: the host's status is matched, never repeated.</summary>
        private static string RefusalDetail(string status)
        {
            switch (status)
            {
                case "404":
                    return "The host is not serving a session.";
                case "429":
                case "503":
                    return "The host is busy; try again shortly.";
                default:
                    return "The host refused the request.";
            }
        }
    }
}
