using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>
    /// Resolves join targets over real networks. A LAN target comes from the browser's list; a direct target's host name
    /// is looked up, IPv4 addresses first. Either way the host's descriptor is fetched from its endpoint and verified,
    /// against the server id the target named when it named one. A hand-typed address names none, so the first server it
    /// served is remembered, and a different one later is reported as a changed identity unless the player trusts it.
    /// A relay-only player's resolve never contacts an address beyond the local network, not even for the descriptor.
    /// </summary>
    internal sealed class NetworkTargetResolver : IJoinTargetResolver
    {
        private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(5);
        private const string DirectNotAllowed = "Joining directly would show the host your address; allow it for this session to continue.";

        private readonly Func<LanBrowser?> browser;
        private readonly KnownHosts knownHosts;
        private readonly Action knownHostsChanged;
        private readonly Func<long> unixSeconds;
        private readonly Func<IPEndPoint, string?, CancellationToken, Task<DescriptorFetch>> fetch;

        /// <param name="browser">Gets the LAN browser, when the player is browsing.</param>
        /// <param name="knownHosts">The trust-on-first-use record; every use locks it.</param>
        /// <param name="knownHostsChanged">Called after the record changed, so it can be saved.</param>
        /// <param name="fetch">Fetches a descriptor; tests watch what is contacted. Null uses <see cref="DescriptorClient"/>.</param>
        internal NetworkTargetResolver(
            Func<LanBrowser?> browser,
            KnownHosts knownHosts,
            Action knownHostsChanged,
            Func<long> unixSeconds,
            Func<IPEndPoint, string?, CancellationToken, Task<DescriptorFetch>>? fetch = null)
        {
            this.browser = browser ?? throw new ArgumentNullException(nameof(browser));
            this.knownHosts = knownHosts ?? throw new ArgumentNullException(nameof(knownHosts));
            this.knownHostsChanged = knownHostsChanged ?? throw new ArgumentNullException(nameof(knownHostsChanged));
            this.unixSeconds = unixSeconds ?? throw new ArgumentNullException(nameof(unixSeconds));
            this.fetch = fetch ?? ((endpoint, serverId, token) => DescriptorClient.FetchAsync(endpoint, serverId, FetchTimeout, token));
        }

        public async Task<ResolvedTarget> ResolveAsync(JoinTarget target, bool allowDirect, string? trustedServerId, CancellationToken cancellationToken)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            return target.Kind == JoinTargetKind.Lan
                ? await ResolveLanAsync(target, allowDirect, cancellationToken).ConfigureAwait(false)
                : await ResolveDirectAsync(target, allowDirect, trustedServerId, cancellationToken).ConfigureAwait(false);
        }

        private async Task<ResolvedTarget> ResolveLanAsync(JoinTarget target, bool allowDirect, CancellationToken cancellationToken)
        {
            LanServer? found = null;
            foreach (var server in browser()?.Servers ?? Array.Empty<LanServer>())
            {
                if (server.ServerId == target.ServerId) found = server;
            }

            if (found == null || !UdpPaths.TryGetEndPoint(found.Path, out var endpoint))
            {
                return ResolvedTarget.Failed(LiveFailure.NotFound, "The session is no longer on the local network.");
            }

            // A beacon can come from beyond the local network; such a host is joined directly or not at all.
            if (found.Path.Kind != Transport.TransportPathKind.Lan && !allowDirect) return ResolvedTarget.Failed(LiveFailure.DirectNotAllowed, DirectNotAllowed);
            var fetched = await fetch(endpoint, target.ServerId, cancellationToken).ConfigureAwait(false);

            // The listing the player picked from showed the session's info; a session it showed without voice stays so.
            return fetched.Outcome == DescriptorFetchOutcome.Fetched
                ? ResolvedTarget.Found(found.Path, fetched.Descriptor!, listedWithoutVoice: found.Info != null && !found.Info.Voice)
                : Failed(fetched);
        }

        private async Task<ResolvedTarget> ResolveDirectAsync(JoinTarget target, bool allowDirect, string? trustedServerId, CancellationToken cancellationToken)
        {
            IPAddress[] addresses;
            if (IsCanonicalLiteral(target.Host!) && IPAddress.TryParse(target.Host!, out var literal))
            {
                addresses = new[] { literal };
            }
            else if (IPAddress.TryParse(target.Host!, out _))
            {
                // The link grammar reads 0x7f000001, 0x7f.1 and other hexadecimal and shortened forms as names, but .NET,
                // and the system's own resolver, read them as addresses. They are never resolved, as the launcher never
                // resolves them: relay-only refuses them as names beyond the local network, and direct play as names
                // that are not names at all.
                return allowDirect
                    ? ResolvedTarget.Failed(LiveFailure.Unreachable, "This host name reads as an address in an unusual form; type the address itself.")
                    : ResolvedTarget.Failed(LiveFailure.DirectNotAllowed, DirectNotAllowed);
            }
            else if (!allowDirect && !IsLocalName(target.Host!))
            {
                // Looking a name up asks the name's own servers, which can learn this player's resolver, network or
                // address from the query. A relay-only player therefore resolves only names that stay on this machine
                // or the local network.
                return ResolvedTarget.Failed(LiveFailure.DirectNotAllowed, DirectNotAllowed);
            }
            else
            {
                try
                {
                    addresses = await Dns.GetHostAddressesAsync(target.Host!).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                    return ResolvedTarget.Failed(LiveFailure.Unreachable, "The host name could not be found.");
                }
            }

            var ordered = new List<IPAddress>();
            foreach (var address in addresses)
            {
                if (address.AddressFamily == AddressFamily.InterNetwork) ordered.Add(address);
            }

            foreach (var address in addresses)
            {
                if (address.AddressFamily == AddressFamily.InterNetworkV6) ordered.Add(address);
            }

            // A relay-only player contacts only addresses on the local network; the lookup itself asks no host anything.
            if (!allowDirect) ordered.RemoveAll(address => UdpPaths.Classify(address) != Transport.TransportPathKind.Lan);
            if (ordered.Count == 0 && addresses.Length > 0 && !allowDirect) return ResolvedTarget.Failed(LiveFailure.DirectNotAllowed, DirectNotAllowed);

            DescriptorFetch? last = null;
            foreach (var address in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var endpoint = new IPEndPoint(address, target.Port);
                last = await fetch(endpoint, target.ServerId, cancellationToken).ConfigureAwait(false);
                if (last.Outcome != DescriptorFetchOutcome.Fetched) continue;
                var descriptor = last.Descriptor!;
                if (target.ServerId == null && !Trust(target, descriptor.Descriptor.ServerId, trustedServerId))
                {
                    return ResolvedTarget.IdentityChanged(descriptor.Descriptor.ServerId);
                }

                return ResolvedTarget.Found(UdpPaths.For(endpoint), descriptor);
            }

            return last == null ? ResolvedTarget.Failed(LiveFailure.Unreachable, "The host name has no usable address.") : Failed(last);
        }

        /// <summary>
        /// Whether a normalized host is an address in the one spelling links use: an RFC 5952 IPv6 address, or dotted
        /// decimal, since the grammar refuses any name of digits and dots alone.
        /// </summary>
        private static bool IsCanonicalLiteral(string host)
        {
            if (host.IndexOf(':') >= 0) return true;
            foreach (var character in host)
            {
                if (character != '.' && (character < '0' || character > '9')) return false;
            }

            return true;
        }

        /// <summary>Whether a name resolves without asking anyone beyond this machine and the local network.</summary>
        private static bool IsLocalName(string host) =>
            string.Equals(host, "localhost", StringComparison.Ordinal) || host.EndsWith(".localhost", StringComparison.Ordinal) ||
            host.EndsWith(".local", StringComparison.Ordinal);

        private bool Trust(JoinTarget target, string serverId, string? trustedServerId)
        {
            lock (knownHosts)
            {
                var check = knownHosts.Check(target.Host!, target.Port, serverId);
                if (check == KnownHostCheck.Match) return true;
                if (check == KnownHostCheck.Mismatch && !string.Equals(serverId, trustedServerId, StringComparison.Ordinal)) return false;
                knownHosts.Remember(target.Host!, target.Port, serverId, unixSeconds());
            }

            knownHostsChanged();
            return true;
        }

        private static ResolvedTarget Failed(DescriptorFetch fetch) =>
            fetch.Outcome switch
            {
                DescriptorFetchOutcome.TimedOut => ResolvedTarget.Failed(LiveFailure.TimedOut, fetch.Detail),
                DescriptorFetchOutcome.Invalid => ResolvedTarget.Failed(LiveFailure.DescriptorInvalid, fetch.Detail),
                _ => ResolvedTarget.Failed(LiveFailure.Unreachable, fetch.Detail)
            };
    }
}
