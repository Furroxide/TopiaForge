using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using TopiaForge.Multiplayer.NatTraversal.Signaling;

namespace TopiaForge.Multiplayer.ControlPlane
{
    /// <summary>
    /// What live sessions need to reach the TopiaForge service: its client, the signaling socket for a server and a
    /// credential, the lookup of the STUN and TURN names the service hands out, and this machine's own addresses for a
    /// session the player opened to direct connections. Live sessions hold one only while a service origin is configured;
    /// without it nothing here exists, nothing is contacted, and only LAN and direct play remain.
    /// </summary>
    internal sealed class InternetServices
    {
        internal InternetServices(
            ControlPlaneClient client,
            Func<string, string, ISignalingClient> openSignaling,
            Func<string, Task<IPAddress[]>> resolve,
            Func<IReadOnlyList<IPAddress>>? localAddresses = null,
            ControlPlaneOrigin? origin = null)
        {
            Client = client ?? throw new ArgumentNullException(nameof(client));
            OpenSignaling = openSignaling ?? throw new ArgumentNullException(nameof(openSignaling));
            Resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
            LocalAddresses = localAddresses ?? LocalAddressList.Read;
            Origin = origin;
        }

        internal ControlPlaneClient Client { get; }

        /// <summary>Gets the origin the game's services reach, or null for services a test composed.</summary>
        internal ControlPlaneOrigin? Origin { get; }

        /// <summary>Opens the signaling room of a server with a host token or a join ticket.</summary>
        internal Func<string, string, ISignalingClient> OpenSignaling { get; }

        /// <summary>Looks up a STUN or TURN server's name, off the network thread.</summary>
        internal Func<string, Task<IPAddress[]>> Resolve { get; }

        /// <summary>Lists this machine's usable addresses, for host candidates; called off the main thread, and only for direct play.</summary>
        internal Func<IReadOnlyList<IPAddress>> LocalAddresses { get; }

        /// <summary>The services of the game: the HTTPS client and the WebSocket signaling client to one origin, and the system's resolver.</summary>
        internal static InternetServices ForOrigin(ControlPlaneOrigin origin)
        {
            if (origin == null) throw new ArgumentNullException(nameof(origin));
            var room = origin.Uri;
            return new InternetServices(
                new ControlPlaneClient(new HttpsControlPlaneTransport(origin)),
                (serverId, credential) => WebSocketSignalingClient.Open(room, serverId, credential),
                Dns.GetHostAddressesAsync,
                origin: origin);
        }

        public override string ToString() => "TopiaForge service";
    }

    /// <summary>
    /// This machine's addresses for host candidates: every unicast address of every interface that is up, but loopback.
    /// It runs only for a session the player opened to direct connections; a relay-only session never lists them.
    /// </summary>
    internal static class LocalAddressList
    {
        private const int MaximumAddresses = 16;

        internal static IReadOnlyList<IPAddress> Read()
        {
            var addresses = new List<IPAddress>();
            try
            {
                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                    {
                        var address = unicast.Address;
                        if (address == null || IPAddress.IsLoopback(address)) continue;
                        if (address.AddressFamily != AddressFamily.InterNetwork && address.AddressFamily != AddressFamily.InterNetworkV6) continue;
                        if (addresses.Count < MaximumAddresses && !addresses.Contains(address)) addresses.Add(address);
                    }
                }
            }
            catch (NetworkInformationException)
            {
                // Interfaces cannot be listed here; the session gathers what its servers report instead.
            }
            catch (PlatformNotSupportedException)
            {
                // Likewise on a runtime without interface listing.
            }

            return addresses;
        }
    }
}
