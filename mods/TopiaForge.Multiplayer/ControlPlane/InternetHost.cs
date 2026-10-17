using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Multiplayer.NatTraversal;
using TopiaForge.Multiplayer.NatTraversal.Turn;
using TopiaForge.Multiplayer.Network;

namespace TopiaForge.Multiplayer.ControlPlane
{
    internal enum InternetListingPhase
    {
        Off = 0,
        Registering = 1,
        Online = 2,
        Retrying = 3,
        Failed = 4
    }

    /// <summary>
    /// A hosted session's life with the TopiaForge service, on the main thread. It reads the service's status and
    /// degrades from it, registers through a challenge and the Ed25519 proof of the host's identity key, keeps the listing
    /// with a heartbeat, publishes a newer descriptor, holds an invite, and answers joiners through signaling and the NAT
    /// layer on the game socket. A lapsed session (a refused heartbeat, or none that landed for the listing's lifetime)
    /// is registered again, with fresh signaling and a fresh invite. Stopping deletes the listing. Requests run one at a
    /// time off the main thread and land at a later pump; failures back off, honour the service's Retry-After, and are
    /// reported as the live sessions' failures. Nothing here logs, and nothing it keeps names an address.
    /// </summary>
    internal sealed partial class InternetHost : IDisposable
    {
        internal const long FirstRetryMicros = 5_000_000;
        internal const long LongestRetryMicros = 300_000_000;
        internal const long DisabledRetryMicros = 60_000_000;
        internal const long BlockedRetryMicros = 900_000_000;

        /// <summary>The service allows at most six registrations a minute; registering again waits at least this long.</summary>
        internal const long ReregisterSpacingMicros = 60_000_000;

        internal const int InviteSeconds = ControlPlaneClient.MaximumInviteSeconds;

        private readonly InternetServices services;
        private readonly LocalIdentity identity;
        private readonly TraversalNetwork network;
        private readonly bool relayOnly;
        private readonly bool mapPorts;
        private readonly bool inviteOnly;
        private readonly Func<int> players;
        private readonly Func<long> clock;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly Queue<Func<Task<Action>>> requests = new Queue<Func<Task<Action>>>();
        private Task<Action>? inFlight;
        private Task<ServiceResult<HostRegistration>>? registering;
        private Task<IReadOnlyList<IPAddress>>? listing;
        private IReadOnlyList<IPAddress> localAddresses = Array.Empty<IPAddress>();
        private SignedServerDescriptor descriptor;
        private SignedServerDescriptor? published;
        private Step step = Step.Status;
        private HostChallenge? challenge;
        private string? token;
        private long nextAt;
        private long heartbeatAt;
        private long descriptorAt;
        private long mintAt;
        private long renewedAt;
        private long registeredAt = long.MinValue / 2;
        private long heartbeatMicros = 30_000_000;
        private long listingTtlMicros = 90_000_000;
        private long backoff = FirstRetryMicros;
        private long mintBackoff = 30_000_000;
        private bool relayAvailable = true;
        private NatHost? nat;
        private bool stopped;

        internal InternetHost(InternetServices services, LocalIdentity identity, TraversalNetwork network, SignedServerDescriptor descriptor, HostSettings settings, Func<int> players, Func<long> clock)
        {
            this.services = services ?? throw new ArgumentNullException(nameof(services));
            this.identity = identity ?? throw new ArgumentNullException(nameof(identity));
            this.network = network ?? throw new ArgumentNullException(nameof(network));
            this.descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            this.players = players ?? throw new ArgumentNullException(nameof(players));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            relayOnly = !settings.AllowDirect;
            mapPorts = settings.MapPorts && settings.AllowDirect;
            inviteOnly = settings.Visibility == ServerVisibility.Invite;
            Phase = InternetListingPhase.Registering;
            nextAt = clock();

            // Host candidates exist only for a host that shows its address; a relay-only host never lists this machine's.
            if (!relayOnly) listing = Task.Run(services.LocalAddresses);
        }

        private enum Step
        {
            Status,
            Challenge,
            Register,
            Online
        }

        internal InternetListingPhase Phase { get; private set; }

        /// <summary>Gets why the listing is retrying or failed; none while it registers or is online.</summary>
        internal LiveFailure Failure { get; private set; }

        /// <summary>Gets the delay the service named with its last refusal, in seconds, or zero.</summary>
        internal int RetryAfterSeconds { get; private set; }

        /// <summary>Gets the invite joiners use now: the registration's first, or the newest the host made.</summary>
        internal ServiceInvite? Invite { get; private set; }

        /// <summary>Gets whether the server is in the public browser.</summary>
        internal bool Listed { get; private set; }

        /// <summary>Gets why the last invite request failed, if it did.</summary>
        internal LiveFailure InviteFailure { get; private set; }

        /// <summary>Gets the NAT layer's latest status, enums and counts only: the relay, and the players on each route.</summary>
        internal NatStatus NatStatus => nat?.Status ?? NatStatus.Idle;

        /// <summary>
        /// Gets whether this relay-only host is registered but cannot be reached over the Internet: the service gave it no
        /// relay, and it never offers a direct path instead. It keeps asking.
        /// </summary>
        internal bool RelayRefused { get; private set; }

        /// <summary>Gets whether the service holds a registration this host can act on now.</summary>
        internal bool IsOnline => token != null && step == Step.Online && !stopped;

        /// <summary>Gets the listing's deletion once stopping started it; the provider waits for it while closing.</summary>
        internal Task? Ending { get; private set; }

        /// <summary>Main thread, every frame.</summary>
        internal void Pump()
        {
            if (stopped) return;
            var now = clock();
            if (listing != null && listing.IsCompleted)
            {
                // A listing that failed gives no addresses; its exception is observed and never read.
                if (listing.IsFaulted) _ = listing.Exception;
                localAddresses = listing.Status == TaskStatus.RanToCompletion && listing.Result != null ? listing.Result : Array.Empty<IPAddress>();
                listing = null;
            }

            if (inFlight != null)
            {
                if (!inFlight.IsCompleted) return;
                var apply = inFlight.Status == TaskStatus.RanToCompletion ? inFlight.Result : null;
                inFlight = null;
                apply?.Invoke();
                if (stopped) return;
            }

            if (step == Step.Online && token != null && now - renewedAt >= listingTtlMicros)
            {
                // No heartbeat landed for the listing's lifetime: the service ended the session, so it is registered again.
                Lapse(now);
            }

            if (now < nextAt) return;
            switch (step)
            {
                case Step.Status:
                    Begin(services.Client.GetStatusAsync(lifetime.Token), StatusLanded);
                    break;
                case Step.Challenge:
                    Begin(services.Client.RequestChallengeAsync(identity.SigningPublicKey, lifetime.Token), ChallengeLanded);
                    break;
                case Step.Register:
                    Register();
                    break;
                case Step.Online:
                    Serve(now);
                    break;
            }
        }

        /// <summary>Publishes a newer descriptor, which the next free moment sends to the service.</summary>
        internal void Republish(SignedServerDescriptor updated) => descriptor = updated ?? throw new ArgumentNullException(nameof(updated));

        /// <summary>Asks the service for another invite, which becomes the one shown once it lands. False while not online.</summary>
        internal bool CreateInvite()
        {
            if (!IsOnline) return false;
            InviteFailure = LiveFailure.None;
            requests.Enqueue(() => Settle(services.Client.CreateInviteAsync(descriptor.Descriptor.ServerId, token!, InviteSeconds, lifetime.Token), InviteCreated));
            return true;
        }

        /// <summary>Withdraws an invite of this session. False while not online.</summary>
        internal bool RevokeInvite(string code)
        {
            if (!IsOnline || !InviteCode.IsCanonical(code)) return false;
            InviteFailure = LiveFailure.None;
            requests.Enqueue(() => Settle(services.Client.RevokeInviteAsync(descriptor.Descriptor.ServerId, token!, code, lifetime.Token), result => InviteRevoked(result, code)));
            return true;
        }

        /// <summary>
        /// Stops hosting through the service: the listing is deleted, and a registration still in flight is deleted as soon
        /// as it lands, so no listing outlives the host; stopping cancels every other request, but never a registration,
        /// which the service may already hold. The NAT layer is left to the network, which the host stops next: its
        /// links close first, so the message ending each player's session still leaves through the relay, and then the
        /// network disposes the NAT layer, which says goodbye and releases the relay.
        /// </summary>
        internal void Stop()
        {
            if (stopped) return;
            stopped = true;
            Phase = InternetListingPhase.Off;
            nat = null;
            var serverId = descriptor.Descriptor.ServerId;
            var current = token;
            token = null;
            Invite = null;
            Listed = false;
            if (current != null)
            {
                Ending = services.Client.EndHostAsync(serverId, current, CancellationToken.None);
            }
            else if (registering != null)
            {
                Ending = registering.ContinueWith(
                    task => task.Status == TaskStatus.RanToCompletion && task.Result.Succeeded
                        ? services.Client.EndHostAsync(serverId, task.Result.Value!.SessionToken, CancellationToken.None)
                        : Task.FromResult(ServiceResult<ServiceDone>.Fail(ServiceFailure.Cancelled)),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default).Unwrap();
            }

            lifetime.Cancel();
        }

        public void Dispose() => Stop();

        /// <summary>Adds the requests still running elsewhere, which land at a later pump.</summary>
        internal void AddPendingBackgroundWork(List<Task> work)
        {
            if (inFlight != null && !inFlight.IsCompleted) work.Add(inFlight);
            if (listing != null && !listing.IsCompleted) work.Add(listing);
            if (Ending != null && !Ending.IsCompleted) work.Add(Ending);
        }

        public override string ToString() => "Internet listing (" + Phase + ")";

        private void Register()
        {
            // The proof is signed before the request leaves, so the seed is wiped at once. Stopping never cancels a
            // registration: the service may already hold one whose answer is still on its way, and only that answer's
            // token can delete it. The request's own deadline bounds it instead, and Stop deletes the listing once it
            // lands.
            var seed = identity.CopySigningSeed();
            try
            {
                registering = services.Client.RegisterAsync(challenge!, descriptor, relayOnly, seed, CancellationToken.None);
            }
            finally
            {
                Array.Clear(seed, 0, seed.Length);
            }

            var registered = descriptor;
            Begin(registering, result => Registered(result, registered));
        }

        /// <summary>The online host's next request, by priority: the heartbeat when due, the player's invite requests, a newer descriptor, then the relay.</summary>
        private void Serve(long now)
        {
            if (now >= heartbeatAt)
            {
                Begin(services.Client.HeartbeatAsync(descriptor.Descriptor.ServerId, token!, Math.Min(players(), descriptor.Descriptor.MaxPlayers), lifetime.Token), Beat);
                return;
            }

            if (requests.Count > 0)
            {
                inFlight = requests.Dequeue()();
                return;
            }

            if (!ReferenceEquals(published, descriptor) && now >= descriptorAt)
            {
                var update = descriptor;
                Begin(services.Client.UpdateDescriptorAsync(update.Descriptor.ServerId, token!, update, lifetime.Token), result => Updated(result, update));
                return;
            }

            if (nat != null || now < mintAt || listing != null) return;
            if (!relayAvailable && !relayOnly)
            {
                // The service has no relay to give, and this host shows its address: it answers joiners on direct paths alone.
                StartNat(NoServers());
                return;
            }

            Begin(services.Client.MintRelayCredentialsAsync(descriptor.Descriptor.ServerId, token!, lifetime.Token), Minted);
        }

        private static IceServerSet NoServers() => IceServerSet.Create(Array.Empty<IceServerAddress>(), Array.Empty<IceServerAddress>(), null, null, 600);

        private void Begin<T>(Task<ServiceResult<T>> request, Action<ServiceResult<T>> landed) where T : class => inFlight = Settle(request, landed);

        /// <summary>Carries a request's result back to the main thread as the work that applies it, so nothing runs elsewhere.</summary>
        private static Task<Action> Settle<T>(Task<ServiceResult<T>> request, Action<ServiceResult<T>> landed) where T : class =>
            request.ContinueWith(
                task =>
                {
                    var result = task.Status == TaskStatus.RanToCompletion ? task.Result : ServiceResult<T>.Fail(task.IsCanceled ? ServiceFailure.Cancelled : ServiceFailure.Unreachable);
                    return (Action)(() => landed(result));
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }
}
