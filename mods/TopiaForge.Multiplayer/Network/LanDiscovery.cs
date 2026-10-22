using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using TopiaForge.Multiplayer.Transport;

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>
    /// Announces a hosted session on the local network: a beacon every two seconds, as an IPv4 broadcast to UDP 27251,
    /// sent from its own socket so the game socket never needs broadcast rights. The beacon names the server and the game
    /// port; browsers take the address from the datagram itself. Directed broadcasts follow each interface's subnet,
    /// refreshed every 30 seconds, because a limited broadcast leaves through one interface only on some systems.
    /// </summary>
    internal sealed class LanAnnouncer : INetworkService
    {
        private const long TargetRefreshMicros = 30_000_000;

        private readonly Socket socket;
        private readonly IReadOnlyList<IPEndPoint>? fixedTargets;
        private readonly byte[] beacon;
        private IReadOnlyList<IPEndPoint> targets = Array.Empty<IPEndPoint>();
        private long nextBeaconAt;
        private long targetsRefreshedAt = long.MinValue;

        /// <param name="beacon">The beacon to send.</param>
        /// <param name="targets">Where to send it; null means every local broadcast address, as in the game.</param>
        /// <param name="bindAddress">The IPv4 address to send from, any by default; tests send from loopback.</param>
        internal LanAnnouncer(LanBeacon beacon, IReadOnlyList<IPEndPoint>? targets = null, IPAddress? bindAddress = null)
        {
            this.beacon = beacon.Encode();
            fixedTargets = targets;
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { Blocking = false, EnableBroadcast = true };
            try
            {
                socket.Bind(new IPEndPoint(bindAddress ?? IPAddress.Any, 0));
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        /// <summary>Gets how many beacons went out, for tests and diagnostics.</summary>
        internal long Sent { get; private set; }

        public void Turn(NetworkStack stack, long now)
        {
            if (now < nextBeaconAt) return;
            nextBeaconAt = now + LanBeacon.IntervalMicros;
            if (fixedTargets != null)
            {
                targets = fixedTargets;
            }
            else if (now - targetsRefreshedAt >= TargetRefreshMicros || targetsRefreshedAt == long.MinValue)
            {
                targetsRefreshedAt = now;
                targets = BroadcastTargets();
            }

            foreach (var target in targets)
            {
                try
                {
                    socket.SendTo(beacon, 0, beacon.Length, SocketFlags.None, target);
                    Sent++;
                }
                catch (SocketException)
                {
                    // An interface that went away or refuses broadcasts loses this beacon; the next refresh drops it.
                }
            }
        }

        public bool TryClaim(TransportInfoReply reply, long now) => false;

        public long NextTimeoutMicros() => nextBeaconAt;

        public void Dispose() => socket.Dispose();

        /// <summary>The limited broadcast address and each up interface's directed broadcast, for the discovery port.</summary>
        private static IReadOnlyList<IPEndPoint> BroadcastTargets()
        {
            var result = new List<IPEndPoint> { new IPEndPoint(IPAddress.Broadcast, LanBeacon.DiscoveryPort) };
            try
            {
                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask == null) continue;
                        var address = unicast.Address.GetAddressBytes();
                        var mask = unicast.IPv4Mask.GetAddressBytes();
                        if (mask.Length != 4) continue;
                        var directed = new byte[4];
                        for (var index = 0; index < 4; index++) directed[index] = (byte)(address[index] | ~mask[index]);
                        var target = new IPEndPoint(new IPAddress(directed), LanBeacon.DiscoveryPort);
                        if (!result.Exists(existing => existing.Equals(target))) result.Add(target);
                    }
                }
            }
            catch (NetworkInformationException)
            {
                // Interfaces cannot be listed here; the limited broadcast still goes out.
            }

            return result;
        }
    }

    /// <summary>A session heard on the local network, as the browser lists it. Its path stays in the network layer.</summary>
    internal sealed class LanServer
    {
        internal LanServer(string serverId, SessionInfo? info, bool incompatible, long roundTripMicros, long lastSeenMicros, TransportPath path)
        {
            ServerId = serverId;
            Info = info;
            Incompatible = incompatible;
            RoundTripMicros = roundTripMicros;
            LastSeenMicros = lastSeenMicros;
            Path = path;
        }

        internal string ServerId { get; }

        /// <summary>Gets the session's info once its host has answered, or null while the browser waits.</summary>
        internal SessionInfo? Info { get; }

        /// <summary>Gets whether the host speaks another transport version, so the browser can say why it cannot join.</summary>
        internal bool Incompatible { get; }

        internal long RoundTripMicros { get; }

        internal long LastSeenMicros { get; }

        /// <summary>Gets the opaque path to the host's game socket, for the join flow only.</summary>
        internal TransportPath Path { get; }
    }

    /// <summary>
    /// Browses the local network: listens for beacons on UDP 27251 (with address reuse, so the game and the launcher
    /// can both listen), asks each announced host for its session info through the transport's info query, and keeps a
    /// list of the sessions whose info names the server their beacon did. A session silent for six seconds leaves the
    /// list. The main thread reads <see cref="Servers"/>, a snapshot replaced whenever the list changes.
    /// </summary>
    /// <remarks>
    /// The list holds at most 64 sessions, and at most 8 of them from one sender address, as the launcher's browser
    /// does, so one host announcing many servers can never hide the others. Only silence makes room: a beacon that
    /// would add a session to a full list, add one for a sender at its share, or move one there from another address
    /// is dropped and counted. A session that moves to another port on its own sender keeps its place. Forged beacons
    /// from many spoofed addresses can still fill the list, as they can the launcher's; on a local network that is
    /// accepted, and a sender's share only stops one host doing it.
    /// </remarks>
    internal sealed class LanBrowser : INetworkService
    {
        internal const long ExpiryMicros = 6_000_000;
        internal const long RequeryMicros = 2_000_000;
        private const int MaximumServers = 64;

        /// <summary>Sessions one sender address may hold in the list, on any port: enough for a machine running several peers.</summary>
        private const int MaximumServersPerSender = 8;
        private const int MaximumBeaconsPerTurn = 64;

        private readonly Socket socket;
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly byte[] buffer = new byte[LanBeacon.Size + 1];
        private readonly long expiryMicros;
        private IReadOnlyList<LanServer> servers = Array.Empty<LanServer>();
        private long nextExpiry = long.MaxValue;
        private long dropped;

        /// <param name="port">The discovery port; tests listen elsewhere.</param>
        /// <param name="expiryMicros">How long a silent session stays listed.</param>
        /// <param name="bindAddress">The IPv4 address to listen on, any by default; tests listen on loopback.</param>
        internal LanBrowser(int port = LanBeacon.DiscoveryPort, long expiryMicros = ExpiryMicros, IPAddress? bindAddress = null)
        {
            this.expiryMicros = expiryMicros;
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { Blocking = false };
            try
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                socket.Bind(new IPEndPoint(bindAddress ?? IPAddress.Any, port));
                LocalPort = ((IPEndPoint)socket.LocalEndPoint!).Port;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        internal int LocalPort { get; }

        /// <summary>Gets the sessions heard so far; safe to read from any thread.</summary>
        internal IReadOnlyList<LanServer> Servers => Volatile.Read(ref servers);

        /// <summary>
        /// Gets how many beacons were dropped because the list, or their sender's share of it, was full; safe to read from
        /// any thread, for tests and diagnostics.
        /// </summary>
        internal long Dropped => Interlocked.Read(ref dropped);

        public void Turn(NetworkStack stack, long now)
        {
            var changed = false;
            for (var count = 0; count < MaximumBeaconsPerTurn; count++)
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int length;
                try
                {
                    length = socket.ReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref from);
                }
                catch (SocketException exception) when (exception.SocketErrorCode == SocketError.WouldBlock)
                {
                    break;
                }
                catch (SocketException)
                {
                    continue;
                }

                if (!LanBeacon.TryDecode(buffer, 0, length, out var beacon)) continue;
                changed |= Heard(stack, beacon, ((IPEndPoint)from).Address, now);
            }

            if (now >= nextExpiry) changed |= Expire(now);
            if (changed) Publish();
        }

        public bool TryClaim(TransportInfoReply reply, long now)
        {
            // A reply from a host this browser asked is consumed here. Its info counts only for the beacon that named the
            // same server, so a beacon that names someone else at that address never borrows the reply.
            var claimed = false;
            var changed = false;
            SessionInfo? info = null;
            if (reply.IsCompatible) SessionInfo.TryDecode(reply.Info, out info, out _);
            foreach (var entry in entries.Values)
            {
                if (!entry.Path.Equals(reply.Path)) continue;
                claimed = true;
                if (!reply.IsCompatible)
                {
                    changed |= !entry.Incompatible;
                    entry.Incompatible = true;
                }
                else if (info != null && info.ServerId == entry.ServerId)
                {
                    entry.Info = info;
                    entry.Incompatible = false;
                    entry.RoundTripMicros = reply.RoundTripMicros;
                    changed = true;
                }
            }

            if (changed) Publish();
            return claimed;
        }

        public long NextTimeoutMicros() => nextExpiry;

        public void Dispose() => socket.Dispose();

        private bool Heard(NetworkStack stack, LanBeacon beacon, IPAddress from, long now)
        {
            var path = UdpPaths.For(new IPEndPoint(from, beacon.GamePort));
            var sender = UdpPaths.SourceOf(path)!;
            if (!entries.TryGetValue(beacon.ServerId, out var entry))
            {
                if (entries.Count >= MaximumServers || SessionsFrom(sender) >= MaximumServersPerSender) return Drop();
                entry = new Entry(beacon.ServerId, path, sender);
                entries.Add(beacon.ServerId, entry);
            }
            else if (!string.Equals(entry.Sender, sender, StringComparison.Ordinal) && SessionsFrom(sender) >= MaximumServersPerSender)
            {
                // A session moving here from another address would take a place in this sender's share. A move adds no
                // session, so a full list alone never stops one.
                return Drop();
            }

            var moved = !entry.Path.Equals(path);
            if (moved)
            {
                // What the old path answered says nothing about the new one: the entry waits for a reply from there
                // that names the same server.
                entry.Info = null;
                entry.Incompatible = false;
                entry.RoundTripMicros = 0;
            }

            entry.Path = path;
            entry.Sender = sender;
            entry.LastSeenMicros = now;
            nextExpiry = Math.Min(nextExpiry, now + expiryMicros);
            if (moved || entry.QueriedAt == long.MinValue || now - entry.QueriedAt >= RequeryMicros)
            {
                entry.QueriedAt = now;
                if (stack.Endpoint.Permits(path)) stack.Endpoint.QueryInfo(path, now);
            }

            return moved || entry.Info == null;
        }

        /// <summary>Counts the listed sessions last heard from <paramref name="sender"/>, on any port.</summary>
        private int SessionsFrom(string sender)
        {
            var count = 0;
            foreach (var entry in entries.Values)
            {
                if (string.Equals(entry.Sender, sender, StringComparison.Ordinal)) count++;
            }

            return count;
        }

        private bool Drop()
        {
            Interlocked.Increment(ref dropped);
            return false;
        }

        private bool Expire(long now)
        {
            var removed = false;
            nextExpiry = long.MaxValue;
            foreach (var entry in new List<Entry>(entries.Values))
            {
                if (now - entry.LastSeenMicros >= expiryMicros)
                {
                    entries.Remove(entry.ServerId);
                    removed = true;
                }
                else
                {
                    nextExpiry = Math.Min(nextExpiry, entry.LastSeenMicros + expiryMicros);
                }
            }

            return removed;
        }

        private void Publish()
        {
            var list = new List<LanServer>(entries.Count);
            foreach (var entry in entries.Values)
            {
                list.Add(new LanServer(entry.ServerId, entry.Info, entry.Incompatible, entry.RoundTripMicros, entry.LastSeenMicros, entry.Path));
            }

            list.Sort((left, right) => string.CompareOrdinal(left.ServerId, right.ServerId));
            Volatile.Write(ref servers, list.AsReadOnly());
        }

        private sealed class Entry
        {
            internal Entry(string serverId, TransportPath path, string sender)
            {
                ServerId = serverId;
                Path = path;
                Sender = sender;
            }

            internal string ServerId { get; }
            internal TransportPath Path { get; set; }

            /// <summary>Gets or sets the host the path reaches, without its port, as <see cref="UdpPaths.SourceOf"/> names it.</summary>
            internal string Sender { get; set; }
            internal SessionInfo? Info { get; set; }
            internal bool Incompatible { get; set; }
            internal long RoundTripMicros { get; set; }
            internal long LastSeenMicros { get; set; }
            internal long QueriedAt { get; set; } = long.MinValue;
        }
    }
}
