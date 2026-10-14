using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Multiplayer.NatTraversal.PortMapping
{
    internal enum PortMappingState
    {
        /// <summary>Not tried: the session joins, hosts relay-only, or mapping was not asked for.</summary>
        Off = 0,
        Mapping = 1,
        Mapped = 2,

        /// <summary>No gateway mapped the port; direct joins rely on hole punching and the relay instead.</summary>
        Unavailable = 3,
        Stopped = 4
    }

    internal enum PortMappingProtocol
    {
        None = 0,
        NatPmp = 1,
        Upnp = 2
    }

    /// <summary>
    /// Maps the game port on the home router while hosting, best effort: NAT-PMP first, UPnP otherwise, for one-hour
    /// leases renewed at half time, deleted when hosting stops. A gateway that only grants permanent mappings gets one,
    /// deleted all the same; a conflicting mapping moves to the next port, at most three times. The external address it
    /// learns becomes a host candidate's server-reflexive twin, and is never shown or logged.
    /// </summary>
    internal sealed class PortMapper
    {
        internal const int LeaseSeconds = 3600;
        internal const int Alternatives = 3;

        private readonly NatPmpClient? natPmp;
        private readonly UpnpIgdClient? upnp;
        private readonly Func<TimeSpan, CancellationToken, Task> delay;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private Task? running;
        private UpnpGateway? gateway;
        private int internalPort;
        private int externalPort;
        private int state;

        /// <param name="delay">Waits between renewals; tests pass one that records the wait and returns at once.</param>
        internal PortMapper(NatPmpClient? natPmp, UpnpIgdClient? upnp, Func<TimeSpan, CancellationToken, Task>? delay = null)
        {
            this.natPmp = natPmp;
            this.upnp = upnp;
            this.delay = delay ?? Task.Delay;
        }

        internal PortMappingState State => (PortMappingState)Volatile.Read(ref state);

        internal PortMappingProtocol Protocol { get; private set; }

        /// <summary>Gets the task that runs the mapping and its renewals, for tests to await.</summary>
        internal Task Running => running ?? Task.CompletedTask;

        /// <summary>The production mapper: NAT-PMP against the default IPv4 gateway, and UPnP over SSDP multicast.</summary>
        internal static PortMapper CreateDefault()
        {
            var gateway = DefaultGateway();
            return new PortMapper(gateway == null ? null : new NatPmpClient(new IPEndPoint(gateway, NatPmpClient.GatewayPort)), new UpnpIgdClient());
        }

        /// <summary>Starts mapping <paramref name="port"/>; <paramref name="mapped"/> hears the external endpoint once, on a pool thread.</summary>
        internal void Start(int port, Action<IPEndPoint> mapped)
        {
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            if (mapped == null) throw new ArgumentNullException(nameof(mapped));
            if (Interlocked.CompareExchange(ref state, (int)PortMappingState.Mapping, (int)PortMappingState.Off) != (int)PortMappingState.Off) return;
            internalPort = port;
            running = Task.Run(() => RunAsync(mapped, cancellation.Token));
        }

        /// <summary>Stops renewing and deletes the mapping; returns once the deletion was tried.</summary>
        internal Task Stop()
        {
            if (Interlocked.Exchange(ref state, (int)PortMappingState.Stopped) == (int)PortMappingState.Stopped) return Task.CompletedTask;
            cancellation.Cancel();
            return Task.Run(async () =>
            {
                try
                {
                    if (running != null) await running.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The mapping loop ends this way.
                }

                await DeleteAsync().ConfigureAwait(false);
            });
        }

        private async Task RunAsync(Action<IPEndPoint> mapped, CancellationToken token)
        {
            try
            {
                if (natPmp != null && await TryNatPmpAsync(mapped, token).ConfigureAwait(false)) return;
                if (upnp != null && await TryUpnpAsync(mapped, token).ConfigureAwait(false)) return;
                Interlocked.CompareExchange(ref state, (int)PortMappingState.Unavailable, (int)PortMappingState.Mapping);
            }
            catch (OperationCanceledException)
            {
                // Stopped while mapping or renewing.
            }
        }

        private async Task<bool> TryNatPmpAsync(Action<IPEndPoint> mapped, CancellationToken token)
        {
            var external = await natPmp!.GetExternalAddressAsync(token).ConfigureAwait(false);
            if (external == null || !external.Value.Succeeded || !NatAddresses.IsPublic(external.Value.ExternalAddress)) return false;
            var answer = await natPmp.MapUdpAsync(internalPort, internalPort, LeaseSeconds, token).ConfigureAwait(false);
            if (answer == null || !answer.Value.Succeeded || answer.Value.Lifetime == 0 || answer.Value.ExternalPort == 0) return false;
            Protocol = PortMappingProtocol.NatPmp;
            externalPort = answer.Value.ExternalPort;
            Announce(mapped, new IPEndPoint(external.Value.ExternalAddress!, externalPort));
            var lifetime = answer.Value.Lifetime;
            while (!token.IsCancellationRequested)
            {
                await delay(TimeSpan.FromSeconds(Math.Max(30, lifetime / 2)), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var renewal = await natPmp.MapUdpAsync(internalPort, externalPort, LeaseSeconds, token).ConfigureAwait(false);
                if (renewal != null && renewal.Value.Succeeded && renewal.Value.Lifetime > 0) lifetime = renewal.Value.Lifetime;
            }

            return true;
        }

        private async Task<bool> TryUpnpAsync(Action<IPEndPoint> mapped, CancellationToken token)
        {
            var found = await upnp!.DiscoverAsync(token).ConfigureAwait(false);
            if (found == null) return false;
            var external = await upnp.GetExternalAddressAsync(found, token).ConfigureAwait(false);
            if (!NatAddresses.IsPublic(external)) return false;
            var lease = LeaseSeconds;
            for (var attempt = 0; attempt <= Alternatives && !token.IsCancellationRequested; attempt++)
            {
                var port = internalPort + attempt;
                if (port > 65535) break;
                var code = await upnp.AddPortMappingAsync(found, port, internalPort, lease, token).ConfigureAwait(false);
                if (code == 725 && lease != 0)
                {
                    lease = 0;
                    code = await upnp.AddPortMappingAsync(found, port, internalPort, lease, token).ConfigureAwait(false);
                }

                if (code == 718) continue;
                if (code != 0) return false;
                gateway = found;
                externalPort = port;
                Protocol = PortMappingProtocol.Upnp;
                Announce(mapped, new IPEndPoint(external!, port));
                while (!token.IsCancellationRequested && lease != 0)
                {
                    await delay(TimeSpan.FromSeconds(lease / 2), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    await upnp.AddPortMappingAsync(found, port, internalPort, lease, token).ConfigureAwait(false);
                }

                return true;
            }

            return false;
        }

        private void Announce(Action<IPEndPoint> mapped, IPEndPoint endpoint)
        {
            if (Interlocked.CompareExchange(ref state, (int)PortMappingState.Mapped, (int)PortMappingState.Mapping) != (int)PortMappingState.Mapping) return;
            mapped(endpoint);
        }

        private async Task DeleteAsync()
        {
            using (var budget = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            {
                try
                {
                    if (Protocol == PortMappingProtocol.NatPmp && natPmp != null) await natPmp.DeleteUdpAsync(internalPort, budget.Token).ConfigureAwait(false);
                    else if (Protocol == PortMappingProtocol.Upnp && upnp != null && gateway != null) await upnp.DeletePortMappingAsync(gateway, externalPort, budget.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Best effort: the lease lapses on its own within the hour.
                }
            }
        }

        /// <summary>The first IPv4 gateway of an interface that is up, or null.</summary>
        private static IPAddress? DefaultGateway()
        {
            try
            {
                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var gateway in adapter.GetIPProperties().GatewayAddresses)
                    {
                        var address = gateway.Address;
                        if (address != null && address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.Any.Equals(address)) return address;
                    }
                }
            }
            catch (NetworkInformationException)
            {
                // Interfaces cannot be listed here; UPnP may still find the gateway.
            }

            return null;
        }
    }
}
