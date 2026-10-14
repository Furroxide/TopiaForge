using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Multiplayer.NatTraversal.PortMapping
{
    /// <summary>One NAT-PMP answer: the result code, the gateway's epoch, and the mapping or external address it reports.</summary>
    internal readonly struct NatPmpAnswer
    {
        internal NatPmpAnswer(int resultCode, uint epoch, IPAddress? externalAddress, int internalPort, int externalPort, uint lifetime)
        {
            ResultCode = resultCode;
            Epoch = epoch;
            ExternalAddress = externalAddress;
            InternalPort = internalPort;
            ExternalPort = externalPort;
            Lifetime = lifetime;
        }

        /// <summary>Gets the result: 0 success, 1 unsupported version, 2 refused, 3 network failure, 4 out of resources, 5 unsupported opcode.</summary>
        internal int ResultCode { get; }

        internal uint Epoch { get; }

        internal IPAddress? ExternalAddress { get; }

        internal int InternalPort { get; }

        internal int ExternalPort { get; }

        internal uint Lifetime { get; }

        internal bool Succeeded => ResultCode == 0;

        public override string ToString() => "NAT-PMP answer " + ResultCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// NAT-PMP (RFC 6886) against the default gateway, best effort: the external address request (opcode 0) and UDP
    /// mappings (opcode 1), which renew with the same request and delete with a lifetime of zero. Requests go out up to
    /// four times, 250 ms apart and doubling, far fewer than the RFC's nine, because a game would rather fall back to
    /// UPnP or the relay than wait two minutes. An answer counts only from the gateway's address and port 5351, with the
    /// right version, opcode, length and internal port. Nothing here logs or reports an address.
    /// </summary>
    internal sealed class NatPmpClient
    {
        internal const int GatewayPort = 5351;
        internal const int AddressOpcode = 0;
        internal const int UdpMapOpcode = 1;
        internal const int Attempts = 4;
        internal const int FirstWaitMilliseconds = 250;

        private readonly IPEndPoint gateway;
        private readonly IPAddress bindAddress;

        /// <param name="gateway">The gateway to ask: its address and, outside tests, port 5351.</param>
        /// <param name="bindAddress">The local address to bind; tests pass loopback so no firewall prompt appears.</param>
        internal NatPmpClient(IPEndPoint gateway, IPAddress? bindAddress = null)
        {
            this.gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
            if (gateway.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException("NAT-PMP speaks to an IPv4 gateway.", nameof(gateway));
            this.bindAddress = bindAddress ?? IPAddress.Any;
        }

        internal static byte[] EncodeAddressRequest() => new byte[] { 0, AddressOpcode };

        /// <summary>A UDP mapping request: version, opcode, two reserved zero bytes, the ports and the lifetime, big-endian.</summary>
        internal static byte[] EncodeMapRequest(int internalPort, int suggestedExternalPort, uint lifetimeSeconds)
        {
            if (internalPort < 1 || internalPort > 65535) throw new ArgumentOutOfRangeException(nameof(internalPort));
            if (suggestedExternalPort < 0 || suggestedExternalPort > 65535) throw new ArgumentOutOfRangeException(nameof(suggestedExternalPort));
            return new byte[]
            {
                0, UdpMapOpcode, 0, 0,
                (byte)(internalPort >> 8), (byte)internalPort,
                (byte)(suggestedExternalPort >> 8), (byte)suggestedExternalPort,
                (byte)(lifetimeSeconds >> 24), (byte)(lifetimeSeconds >> 16), (byte)(lifetimeSeconds >> 8), (byte)lifetimeSeconds
            };
        }

        /// <summary>Reads an answer to <paramref name="opcode"/>; false for anything of the wrong version, opcode or length.</summary>
        internal static bool TryDecode(byte[] buffer, int count, int opcode, out NatPmpAnswer answer)
        {
            answer = default;
            if (buffer == null || count < 8 || count > buffer.Length || buffer[0] != 0 || buffer[1] != 128 + opcode) return false;
            var result = (buffer[2] << 8) | buffer[3];
            var epoch = ReadUInt32(buffer, 4);
            if (opcode == AddressOpcode)
            {
                if (count != 12) return false;
                var address = result == 0 ? new IPAddress(new[] { buffer[8], buffer[9], buffer[10], buffer[11] }) : null;
                answer = new NatPmpAnswer(result, epoch, address, 0, 0, 0);
                return true;
            }

            if (count != 16) return false;
            answer = new NatPmpAnswer(result, epoch, null, (buffer[8] << 8) | buffer[9], (buffer[10] << 8) | buffer[11], ReadUInt32(buffer, 12));
            return true;
        }

        internal Task<NatPmpAnswer?> GetExternalAddressAsync(CancellationToken cancellation) =>
            Task.Run(() => Exchange(EncodeAddressRequest(), AddressOpcode, 0, Attempts, cancellation));

        /// <summary>Maps <paramref name="internalPort"/>, or renews the mapping when the port it was given is suggested again.</summary>
        internal Task<NatPmpAnswer?> MapUdpAsync(int internalPort, int suggestedExternalPort, uint lifetimeSeconds, CancellationToken cancellation) =>
            Task.Run(() => Exchange(EncodeMapRequest(internalPort, suggestedExternalPort, lifetimeSeconds), UdpMapOpcode, internalPort, Attempts, cancellation));

        /// <summary>Deletes the mapping of <paramref name="internalPort"/>: lifetime and suggested port zero, sent twice at most.</summary>
        internal Task<NatPmpAnswer?> DeleteUdpAsync(int internalPort, CancellationToken cancellation) =>
            Task.Run(() => Exchange(EncodeMapRequest(internalPort, 0, 0), UdpMapOpcode, internalPort, 2, cancellation));

        /// <summary>One request and its answer, on a pool thread: it waits in slices of 50 ms so a cancellation lands quickly.</summary>
        private NatPmpAnswer? Exchange(byte[] request, int opcode, int internalPort, int attempts, CancellationToken cancellation)
        {
            var opened = PortMappingSocket.Open(bindAddress);
            if (opened == null) return null;
            using (var socket = opened)
            {
                var buffer = new byte[64];
                var wait = FirstWaitMilliseconds;
                for (var attempt = 0; attempt < attempts && !cancellation.IsCancellationRequested; attempt++)
                {
                    try
                    {
                        socket.SendTo(request, gateway);
                    }
                    catch (SocketException)
                    {
                        return null;
                    }

                    var deadline = Environment.TickCount + wait;
                    while (!cancellation.IsCancellationRequested)
                    {
                        var remaining = deadline - Environment.TickCount;
                        if (remaining <= 0) break;
                        if (!Poll(socket, Math.Min(remaining, 50))) continue;
                        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                        int received;
                        try
                        {
                            received = socket.ReceiveFrom(buffer, ref from);
                        }
                        catch (SocketException)
                        {
                            continue;
                        }

                        // Only the gateway itself, from the NAT-PMP port, can answer.
                        var source = (IPEndPoint)from;
                        if (!source.Address.Equals(gateway.Address) || source.Port != gateway.Port) continue;
                        if (!TryDecode(buffer, received, opcode, out var answer)) continue;
                        if (opcode == UdpMapOpcode && answer.InternalPort != internalPort) continue;
                        return answer;
                    }

                    wait *= 2;
                }
            }

            return null;
        }

        private static bool Poll(Socket socket, int milliseconds)
        {
            try
            {
                return socket.Poll(milliseconds * 1000, SelectMode.SelectRead);
            }
            catch (SocketException)
            {
                return false;
            }
        }

        private static uint ReadUInt32(byte[] buffer, int offset) =>
            ((uint)buffer[offset] << 24) | ((uint)buffer[offset + 1] << 16) | ((uint)buffer[offset + 2] << 8) | buffer[offset + 3];
    }
}
