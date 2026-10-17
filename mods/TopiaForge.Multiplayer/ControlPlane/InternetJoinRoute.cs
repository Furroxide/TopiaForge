using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Multiplayer.NatTraversal;
using TopiaForge.Multiplayer.NatTraversal.Turn;
using TopiaForge.Multiplayer.Network;
using TopiaForge.Multiplayer.Transport;

namespace TopiaForge.Multiplayer.ControlPlane
{
    /// <summary>
    /// The way a join reaches a host the TopiaForge service found, on the main thread: a join ticket carrying the player's
    /// privacy choice, relay credentials minted with it, a network whose game socket the NAT layer shares, and a NAT join
    /// that finds the path and fills in the connection attempt the join polls. Relay-only, the player's default, is honoured
    /// whatever it costs: without relay credentials such a join fails as <see cref="LiveFailure.RelayUnavailable"/> and never
    /// tries a direct path, and its agent and transport are both relay-only, so neither could. A player who allowed direct
    /// play for this session goes on without the relay, on direct paths alone. Reconnecting starts a fresh NAT join on the
    /// same network with the same ticket, minting again only when the credential is about to expire.
    /// </summary>
    internal sealed class InternetJoinRoute : IDisposable
    {
        /// <summary>A reconnection mints afresh when the credential has less than this left.</summary>
        internal const long RemintMarginMicros = 120_000_000;

        private const long StopGraceMicros = 100_000;

        private readonly InternetServices services;
        private readonly ServiceRoute route;
        private readonly byte[] hostKey;
        private readonly bool allowDirect;
        private readonly Func<bool, TraversalNetwork?> startNetwork;
        private readonly Func<long> clock;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private Task<ServiceResult<ServiceJoinTicket>>? ticketing;
        private Task<ServiceResult<RelayCredentials>>? minting;
        private Task<IReadOnlyList<IPAddress>>? listing;
        private ServiceJoinTicket? ticket;
        private IceServerSet? servers;
        private long serversExpireAt;
        private IReadOnlyList<IPAddress> localAddresses = Array.Empty<IPAddress>();
        private TraversalNetwork? network;
        private NatJoin? join;
        private ConnectAttempt? fresh;
        private bool reconnectWanted;
        private bool disposed;

        /// <param name="startNetwork">Starts the join's network, relay-only unless the argument says direct play is allowed; null when it cannot open.</param>
        internal InternetJoinRoute(InternetServices services, ServiceRoute route, byte[] hostKey, bool allowDirect, Func<bool, TraversalNetwork?> startNetwork, Func<long> clock)
        {
            this.services = services ?? throw new ArgumentNullException(nameof(services));
            this.route = route ?? throw new ArgumentNullException(nameof(route));
            this.hostKey = (byte[])(hostKey ?? throw new ArgumentNullException(nameof(hostKey))).Clone();
            this.allowDirect = allowDirect;
            this.startNetwork = startNetwork ?? throw new ArgumentNullException(nameof(startNetwork));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        /// <summary>Gets why the route failed before it could make an attempt; none while it works or once an attempt exists.</summary>
        internal LiveFailure Failure { get; private set; }

        internal string Detail { get; private set; } = string.Empty;

        /// <summary>Gets the delay the service named when it refused the ticket or the relay credential, in seconds, or zero.</summary>
        internal int RetryAfterSeconds { get; private set; }

        /// <summary>Gets the NAT layer's latest status, enums and counts only, once the join runs.</summary>
        internal NatStatus Status => join?.Status ?? NatStatus.Idle;

        /// <summary>Gets whether the host accepts direct connections, as its ticket or its listing says.</summary>
        internal bool HostAllowsDirect => !(ticket?.HostRelayOnly ?? route.HostRelayOnly);

        /// <summary>Gets whether this route's own side may take direct paths: the player allowed it for this session.</summary>
        internal bool AllowsDirect => allowDirect;

        /// <summary>Asks the service for a join ticket; the rest follows at later pumps.</summary>
        internal void Begin()
        {
            if (ticketing != null || disposed) return;
            ticketing = services.Client.CreateJoinTicketAsync(route.ServerId, route.InviteCode, !allowDirect, lifetime.Token);
        }

        /// <summary>Takes the attempt the latest NAT join fills in, once; null until one has started.</summary>
        internal ConnectAttempt? TakeAttempt()
        {
            var attempt = fresh;
            fresh = null;
            return attempt;
        }

        /// <summary>Main thread, every frame: lands the requests that finished and starts what they made possible.</summary>
        internal void Pump()
        {
            if (disposed || Failure != LiveFailure.None) return;
            DrainEvents();
            if (ticketing != null && ticketing.IsCompleted) TicketLanded();
            if (minting != null && minting.IsCompleted) CredentialsLanded();
            if (listing != null && listing.IsCompleted) AddressesLanded();
            if (reconnectWanted && servers != null && minting == null && listing == null) StartJoin();
        }

        /// <summary>
        /// Starts again after the link was lost in session: a fresh NAT join on the same network with the same ticket. A
        /// credential about to expire is minted again first. It is harmless while a join is still being prepared.
        /// </summary>
        internal void Reconnect()
        {
            if (disposed || Failure != LiveFailure.None || ticket == null) return;
            reconnectWanted = true;
            if (minting == null && clock() >= serversExpireAt - RemintMarginMicros) Mint();
        }

        /// <summary>
        /// Gets the failure for an attempt that failed: a pinned key the host did not prove, or what the NAT layer says. The
        /// network thread fails the attempt a moment before it publishes why, so until the reason shows, for at most
        /// <see cref="ReasonWaitMicros"/>, this says to wait; after that an unexplained failure is no route.
        /// </summary>
        internal bool TryExplain(ConnectAttempt attempt, out LiveFailure failure)
        {
            if (attempt == null) throw new ArgumentNullException(nameof(attempt));
            failure = LiveFailure.None;
            if (attempt.Failure == TransportCloseReason.HandshakeFailed)
            {
                failure = LiveFailure.HandshakeFailed;
                return true;
            }

            var nat = Status.Failure;
            if (nat != NatFailure.None)
            {
                failure = InternetFailures.OfNat(nat);
                return true;
            }

            var now = clock();
            if (!ReferenceEquals(explaining, attempt))
            {
                explaining = attempt;
                explainSince = now;
            }

            if (now - explainSince < ReasonWaitMicros) return false;
            failure = LiveFailure.NoRoute;
            return true;
        }

        /// <summary>How long a failed attempt waits for the NAT layer to publish why.</summary>
        internal const long ReasonWaitMicros = 500_000;

        private ConnectAttempt? explaining;
        private long explainSince;

        /// <summary>Adds the requests still running elsewhere, which land at a later pump.</summary>
        internal void AddPendingBackgroundWork(List<Task> work)
        {
            if (ticketing != null && !ticketing.IsCompleted) work.Add(ticketing);
            if (minting != null && !minting.IsCompleted) work.Add(minting);
            if (listing != null && !listing.IsCompleted) work.Add(listing);
        }

        /// <summary>
        /// Stops the network: its links close gracefully first, so the last frames still leave through the relay, and then
        /// the network disposes the NAT layer, which says goodbye and releases the relay. The NAT join is not stopped
        /// before that, since its release would cut the relay under the closing frames.
        /// </summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            lifetime.Cancel();
            join = null;
            network?.Network.Stop(StopGraceMicros);
            network = null;
        }

        /// <summary>
        /// The relay credential the agent asks for before the current one expires: minted with the same ticket, off the
        /// main thread. A mint that fails gives nothing, and the agent asks again later.
        /// </summary>
        internal async Task<IceServerSet?> RenewAsync()
        {
            var current = ticket;
            if (current == null || disposed) return null;
            var minted = await services.Client.MintRelayCredentialsAsync(route.ServerId, current.Ticket, lifetime.Token).ConfigureAwait(false);
            return minted.Succeeded ? minted.Value!.Servers : null;
        }

        private void TicketLanded()
        {
            var result = Take(ref ticketing);
            if (!result.Succeeded)
            {
                RetryAfterSeconds = result.RetryAfterSeconds ?? 0;
                Fail(InternetFailures.OfService(result.Failure, route.InviteCode != null));
                return;
            }

            ticket = result.Value!;
            Mint();
        }

        private void Mint() => minting = services.Client.MintRelayCredentialsAsync(route.ServerId, ticket!.Ticket, lifetime.Token);

        private void CredentialsLanded()
        {
            var result = Take(ref minting);
            var reconnecting = join != null;
            if (result.Succeeded)
            {
                servers = result.Value!.Servers;
                serversExpireAt = clock() + (servers.TtlSeconds * 1_000_000L);
            }
            else if (reconnecting && servers != null)
            {
                // A reconnection keeps the credential it has; the agent renews it once the session is back.
            }
            else if (IsRelayRefusal(result.Failure) && allowDirect && HostAllowsDirect)
            {
                // Direct play was allowed for this session and the host takes it: go on without the relay, on direct paths alone.
                servers = IceServerSet.Create(Array.Empty<IceServerAddress>(), Array.Empty<IceServerAddress>(), null, null, 600);
                serversExpireAt = long.MaxValue;
            }
            else
            {
                // Relay-only is honoured whatever it costs: no relay, no join, and never a direct path instead.
                RetryAfterSeconds = result.RetryAfterSeconds ?? 0;
                Fail(IsRelayRefusal(result.Failure) ? LiveFailure.RelayUnavailable : InternetFailures.OfService(result.Failure, route.InviteCode != null));
                return;
            }

            if (reconnecting)
            {
                reconnectWanted = true;
                return;
            }

            // Host candidates only ever exist for direct play; a relay-only join never lists this machine's addresses.
            listing = allowDirect ? Task.Run(services.LocalAddresses) : Task.FromResult<IReadOnlyList<IPAddress>>(Array.Empty<IPAddress>());
            reconnectWanted = true;
        }

        private void AddressesLanded()
        {
            var task = listing!;
            listing = null;

            // A listing that failed gives no addresses; its exception is observed and never read.
            if (task.IsFaulted) _ = task.Exception;
            localAddresses = task.Status == TaskStatus.RanToCompletion && task.Result != null ? task.Result : Array.Empty<IPAddress>();
        }

        private void StartJoin()
        {
            reconnectWanted = false;
            if (network == null)
            {
                network = startNetwork(allowDirect);
                if (network == null)
                {
                    Fail(LiveFailure.Unreachable, "This game could not open a network connection.");
                    return;
                }
            }

            join?.Dispose();
            var serverId = route.ServerId;
            var credential = ticket!.Ticket;
            var plan = new NatSessionPlan(servers!, () => services.OpenSignaling(serverId, credential), services.Resolve)
            {
                Renew = RenewAsync,
                LocalAddresses = localAddresses,
                LocalPort = network.Network.LocalPort
            };
            join = NatJoin.Start(network.Network, network.Agent, plan, hostKey);
            fresh = join.Attempt;
        }

        /// <summary>The join accepts nobody, so a link its network reports is a stray, closed at once; nothing else means anything here.</summary>
        private void DrainEvents()
        {
            var current = network?.Network;
            if (current == null) return;
            while (current.TryTakeEvent(out var networkEvent)) networkEvent.Link?.Close();
        }

        private void Fail(LiveFailure failure, string? detail = null)
        {
            Failure = failure;
            Detail = detail ?? InternetFailures.Detail(failure, HostAllowsDirect);
        }

        private static bool IsRelayRefusal(ServiceFailure failure) =>
            failure == ServiceFailure.RelayUnavailable || failure == ServiceFailure.TicketLimit || failure == ServiceFailure.UpstreamFailed;

        private static ServiceResult<T> Take<T>(ref Task<ServiceResult<T>>? task) where T : class
        {
            var done = task!;
            task = null;
            return done.Status == TaskStatus.RanToCompletion ? done.Result : ServiceResult<T>.Fail(done.IsCanceled ? ServiceFailure.Cancelled : ServiceFailure.Unreachable);
        }
    }
}
