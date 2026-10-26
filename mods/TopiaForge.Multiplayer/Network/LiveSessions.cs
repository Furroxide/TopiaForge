using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Multiplayer.ControlPlane;
using TopiaForge.Multiplayer.Engine;
using TopiaForge.Multiplayer.NatTraversal;
using TopiaForge.Multiplayer.Transport.Crypto;

namespace TopiaForge.Multiplayer.Network
{
    internal enum HostPhase
    {
        Idle = 0,
        Preparing = 1,
        Hosting = 2,
        Failed = 3
    }

    /// <summary>
    /// Live sessions for one provider: at most one host or one join at a time, LAN browsing, and the identity, bans and
    /// known hosts they share. Everything runs on the main thread through <see cref="Pump"/>; slow work (loading the
    /// identity, deriving a password key) runs on background tasks and lands at the next pump. It owns the engine's
    /// client-end hook, so a joined session that ends returns the process to solo play.
    /// </summary>
    internal sealed class LiveSessions : IDisposable
    {
        private readonly SessionEngine engine;
        private readonly INetworkDataFiles files;
        private readonly ILiveSessionContent content;
        private readonly INetworkFactory factory;
        private readonly IJoinTargetResolver resolver;
        private readonly Func<long> clock;
        private readonly Func<long> unixSeconds;
        private readonly string providerModId;
        private readonly Func<Transport.TransportOptions> transportOptions;

        /// <summary>How long the last saves, and the listing's deletion, are given to land, as the runtime detaches and at closing.</summary>
        private const int SaveFlushMilliseconds = 2_000;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly Func<ClientSessionEnd, SoloSessionStart?> endHook;
        private Task<LocalIdentity>? loading;
        private Task<byte[]?>? hostKey;

        /// <summary>The read of the ban list a pending host waits for: nobody is admitted against a list that was never read.</summary>
        private Task<bool>? bansRead;
        private HostSettings? pendingHost;
        private LocalIdentity? identity;
        private INetworkControl? browserNetwork;
        private LanBrowser? browser;
        private readonly BanStore bans;
        private readonly KnownHostsStore knownHosts;
        private Task bansWrite = Task.CompletedTask;
        private Task knownHostsWrite = Task.CompletedTask;
        private readonly List<Task> endings = new List<Task>();
        private readonly Func<NatAgentOptions> natOptions;
        private bool disposed;

        internal LiveSessions(
            SessionEngine engine,
            INetworkDataFiles files,
            ILiveSessionContent content,
            INetworkFactory factory,
            Func<long> clock,
            Func<long> unixSeconds,
            string providerModId,
            IJoinTargetResolver? resolver = null,
            Func<Transport.TransportOptions>? transportOptions = null,
            Func<NatAgentOptions>? natOptions = null)
        {
            this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
            this.files = files ?? throw new ArgumentNullException(nameof(files));
            this.content = content ?? throw new ArgumentNullException(nameof(content));
            this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            this.unixSeconds = unixSeconds ?? throw new ArgumentNullException(nameof(unixSeconds));
            this.providerModId = providerModId ?? throw new ArgumentNullException(nameof(providerModId));
            this.transportOptions = transportOptions ?? (() => new Transport.TransportOptions());
            this.natOptions = natOptions ?? (() => new NatAgentOptions());
            bans = new BanStore(files, clock);
            knownHosts = new KnownHostsStore(files, clock);

            // Invites and listed servers go to the TopiaForge service, while one is configured; every other target to the network.
            this.resolver = new ServiceAwareResolver(resolver ?? new NetworkTargetResolver(() => browser, knownHosts, unixSeconds), () => Services);
            endHook = OnClientSessionEnded;
            engine.Options.AfterClientSessionEnds = endHook;
        }

        internal HostPhase HostPhase { get; private set; }

        /// <summary>Gets why hosting failed, when it did.</summary>
        internal LiveFailure HostFailure { get; private set; }

        internal LiveHost? Host { get; private set; }

        internal LiveJoin? Join { get; private set; }

        /// <summary>
        /// Gets whether a game socket these sessions opened, browsing, hosting or joining, runs over IPv4 alone because this
        /// computer has no IPv6. It names no address.
        /// </summary>
        internal bool NetworkRunsIPv4Only => factory.OpenedIPv4Only;

        /// <summary>Gets the trust-on-first-use record; every use locks it.</summary>
        internal KnownHosts KnownHosts => knownHosts.Record;

        /// <summary>Gets the record's file: whether it was read, and what is saved when.</summary>
        internal KnownHostsStore KnownHostsFile => knownHosts;

        /// <summary>
        /// Gets or sets how this provider reaches the TopiaForge service, or null while no service origin is configured:
        /// then nothing is contacted, invites and listed servers fail as not configured, and hosting is LAN or direct only.
        /// Set it before hosting or joining; a session already running keeps what it started with.
        /// </summary>
        internal InternetServices? Services { get; set; }

        /// <summary>Marks the known-hosts record for saving, from any thread; nothing is saved before its file was read.</summary>
        internal void MarkKnownHostsChanged() => knownHosts.MarkChanged();

        /// <summary>Gets the sessions heard on the LAN while browsing; empty otherwise.</summary>
        internal IReadOnlyList<LanServer> LanServers => browser?.Servers ?? Array.Empty<LanServer>();

        /// <summary>Opens this process's session to other players. It starts at a later pump, once the identity is ready.</summary>
        internal void StartHosting(HostSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            ThrowIfDisposed();
            if (Host != null || pendingHost != null) throw new InvalidOperationException("The session is already hosted.");
            if (Join != null && Join.Phase != JoinPhase.Ended) throw new InvalidOperationException("Leave the joined session before hosting.");
            if (engine.Role != EngineRole.Host) throw new InvalidOperationException("Only a solo session can be opened to others.");
            pendingHost = settings;
            HostPhase = HostPhase.Preparing;
            HostFailure = LiveFailure.None;
            EnsureIdentity();
            hostKey = settings.Password == null ? Task.FromResult<byte[]?>(null) : DeriveHostKeyAsync(settings.Password);

            // Hosting starts only once the ban list has been read, so nobody is admitted unchecked; a list the identity's
            // load could not read is read again now.
            bansRead = bans.EnsureReadAsync();
        }

        /// <summary>Stops hosting: every player's session ends, and this process plays on alone.</summary>
        internal void StopHosting()
        {
            pendingHost = null;
            ReleaseHostKey();
            bansRead = null;
            var internet = Host?.Internet;
            Host?.Stop();
            Host = null;
            HostPhase = HostPhase.Idle;

            // The listing's deletion runs on; closing waits for it as it waits for the last saves.
            var ending = internet?.Ending;
            if (ending != null)
            {
                endings.RemoveAll(task => task.IsCompleted);
                endings.Add(ending);
            }
        }

        /// <summary>Starts joining <paramref name="target"/>. The solo session stays until the link to the host is up.</summary>
        internal LiveJoin StartJoin(JoinTarget target, JoinSettings settings)
        {
            ThrowIfDisposed();
            if (Host != null || pendingHost != null) throw new InvalidOperationException("Stop hosting before joining another session.");
            if (Join != null && Join.Phase != JoinPhase.Ended) throw new InvalidOperationException("A join is already in progress.");
            if (identity == null) throw new InvalidOperationException("The identity is not ready yet; call PrepareAsync first.");
            Join?.Dispose();
            Join = new LiveJoin(engine, identity, target, settings, resolver, content, factory, clock, NextSolo, transportOptions, Services, natOptions);
            Join.Timings = JoinTimings;
            return Join;
        }

        /// <summary>Gets or sets how long joins wait for their hosts; tests shorten it.</summary>
        internal LiveJoinTimings JoinTimings { get; set; } = LiveJoinTimings.Default;

        /// <summary>Loads the identity ahead of a join, so the join can start at once.</summary>
        internal Task PrepareAsync()
        {
            EnsureIdentity();
            return loading!;
        }

        /// <summary>Gets whether the identity has loaded.</summary>
        internal bool IsReady => identity != null;

        /// <summary>Gets whether the last attempt to load or create the identity failed; the next one tries again.</summary>
        internal bool IdentityFailed { get; private set; }

        /// <summary>Gets whether the LAN browser is listening.</summary>
        internal bool IsBrowsing => browserNetwork != null;

        /// <summary>
        /// Gets this host's bans, oldest first: once the list was read, the file's and every ban since; before that, only the
        /// bans made meanwhile.
        /// </summary>
        internal IReadOnlyList<BanRecord> Bans => bans.Record.Records();

        /// <summary>Gets the ban list's file: whether it was read, and what is saved when.</summary>
        internal BanStore BansFile => bans;

        /// <summary>Lifts the ban on <paramref name="identity"/> and saves the list; false when it was not banned.</summary>
        internal bool Unban(string identity)
        {
            ThrowIfDisposed();
            if (!bans.Record.Remove(identity)) return false;
            bans.MarkChanged();
            Persist();
            return true;
        }

        /// <summary>Looks <paramref name="target"/> up without joining it; the identity is not needed.</summary>
        internal LiveQuery StartQuery(JoinTarget target, bool allowDirect)
        {
            ThrowIfDisposed();
            return new LiveQuery(resolver, target, allowDirect, content);
        }

        /// <summary>
        /// Gets the work running on other threads that lands at a later pump: the identity load, a password key being
        /// derived, the join's resolve and proof, and, with <paramref name="saves"/>, files still being saved. A test rig
        /// waits for it in real time, so it lands at the same point of a run however slow the machine is.
        /// Operation names are fixed labels for test failures, never task results, paths, addresses or exception text.
        /// </summary>
        internal List<(string Operation, Task Task)> PendingBackgroundWork(bool saves)
        {
            var work = new List<(string Operation, Task Task)>();
            if (identity == null && loading != null && !loading.IsCompleted) work.Add(("identity load", loading));
            if (hostKey != null && !hostKey.IsCompleted) work.Add(("host password derivation", hostKey));
            if (bansRead != null && !bansRead.IsCompleted) work.Add(("ban-list read", bansRead));
            Host?.Internet?.AddPendingBackgroundWork(work);
            Join?.AddPendingBackgroundWork(work);
            foreach (var ending in endings)
            {
                if (!ending.IsCompleted) work.Add(("host listing removal", ending));
            }

            if (saves && !bansWrite.IsCompleted) work.Add(("ban-list save", bansWrite));
            if (saves && !knownHostsWrite.IsCompleted) work.Add(("known-hosts save", knownHostsWrite));
            return work;
        }

        /// <summary>
        /// Starts listening for LAN sessions, on its own socket and network. Browsing is always relay-only: LAN hosts are
        /// still asked for their info, but a beacon from any other address is never answered with this player's own.
        /// </summary>
        /// <returns>False when the network or the discovery port could not be opened; browsing is then off.</returns>
        internal bool StartBrowsing(int discoveryPort = LanBeacon.DiscoveryPort)
        {
            ThrowIfDisposed();
            if (browserNetwork != null) return true;
            var options = transportOptions();
            options.RelayOnly = true;
            options.AcceptsInbound = false;
            var endpoint = new Transport.TransportEndpoint(X25519.GeneratePrivateKey(), options, clock());
            var stack = new NetworkStack(endpoint);
            INetworkControl started;
            try
            {
                started = factory.Start(0, stack);
            }
            catch (System.Net.Sockets.SocketException)
            {
                stack.Dispose();
                return false;
            }

            LanBrowser? listening;
            try
            {
                listening = factory.CreateBrowser(discoveryPort);
            }
            catch (System.Net.Sockets.SocketException)
            {
                started.Stop(0);
                return false;
            }

            browserNetwork = started;
            browser = listening;
            if (listening != null) started.Attach(listening);
            return true;
        }

        internal void StopBrowsing()
        {
            var current = browserNetwork;
            browserNetwork = null;
            browser = null;
            current?.Stop(0);
        }

        /// <summary>Main thread, every frame.</summary>
        internal void Pump()
        {
            if (disposed) return;
            if (identity == null && loading != null && loading.IsCompleted) Loaded();
            if (pendingHost != null && identity != null && Done(hostKey) && Done(bansRead)) BeginHosting();
            Host?.Pump();
            if (Host != null && Host.Failure != LiveFailure.None)
            {
                Fail(Host.Failure);
                Host = null;
            }

            Join?.Pump();
            DrainBrowserEvents();
            Persist();
        }

        /// <summary>
        /// Saves now what closing would: a change not yet saved, and a save that failed, or fails meanwhile, once more, and
        /// waits up to <see cref="SaveFlushMilliseconds"/> for them to land. The provider calls it when TopiaForge's
        /// runtime detaches, as shutdown begins and before any mod stops. It has to: a stopping mod's files refuse every
        /// operation, so nothing these sessions write as they close can land.
        /// </summary>
        internal void SaveBeforeClosing()
        {
            if (disposed) return;
            bansWrite = bans.SaveOnClose(bansWrite);
            knownHostsWrite = knownHosts.SaveOnClose(knownHostsWrite);
            AwaitBriefly(new List<Task> { bansWrite, knownHostsWrite });
        }

        /// <summary>Gives the last saves, and the listing's deletion, up to <see cref="SaveFlushMilliseconds"/> to land.</summary>
        private static void AwaitBriefly(List<Task> work)
        {
            try
            {
                Task.WaitAll(work.ToArray(), SaveFlushMilliseconds);
            }
            catch (AggregateException)
            {
                // A save that failed leaves the previous file; there is nothing more to do while closing.
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            StopHosting();
            Join?.Dispose();
            Join = null;
            StopBrowsing();
            if (ReferenceEquals(engine.Options.AfterClientSessionEnds, endHook)) engine.Options.AfterClientSessionEnds = null;

            // Closing saves what is left, and everything when no runtime saved it first as it detached: a change not yet
            // saved, and a save that failed, once more. The saves, and the listing's deletion, get a moment to land.
            bansWrite = bans.SaveOnClose(bansWrite);
            knownHostsWrite = knownHosts.SaveOnClose(knownHostsWrite);
            var waits = new List<Task> { bansWrite, knownHostsWrite };
            waits.AddRange(endings);
            AwaitBriefly(waits);

            lifetime.Cancel();
            identity?.Dispose();

            // An identity still loading is released the moment it arrives.
            loading?.ContinueWith(
                task =>
                {
                    if (task.Status == TaskStatus.RanToCompletion && !ReferenceEquals(task.Result, identity)) task.Result.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            lifetime.Dispose();
        }

        private void EnsureIdentity()
        {
            if (identity != null || loading != null) return;
            IdentityFailed = false;
            var store = new IdentityStore(files, unixSeconds);
            var token = lifetime.Token;
            loading = Task.Run(async () =>
            {
                var loaded = await store.LoadOrCreateAsync(token).ConfigureAwait(false);
                var handedOver = false;
                try
                {
                    // Neither side record is ever taken for an empty one when it cannot be read: each stays unread, so
                    // nothing is written over its file, and is read again when it is next needed. Hosting does not start
                    // while the bans are unread, and typed addresses are refused with the reason while the known hosts are.
                    await bans.EnsureReadAsync().ConfigureAwait(false);
                    await knownHosts.EnsureReadAsync().ConfigureAwait(false);
                    handedOver = true;
                    return loaded;
                }
                finally
                {
                    if (!handedOver) loaded.Dispose();
                }
            });

            // A failed load is let go once a frame sees it, or at closing. It is observed here, so the file service's
            // text, which can name a local file, never reaches the global unobserved-exception event.
            NetworkSaveTasks.Observe(loading);
        }

        private void Loaded()
        {
            var task = loading!;
            if (task.IsFaulted || task.IsCanceled)
            {
                // The next attempt to host or prepare loads it again.
                loading = null;
                IdentityFailed = true;
                if (pendingHost != null) Fail(LiveFailure.IdentityUnavailable);
                return;
            }

            identity = task.Result;
        }

        private Task<byte[]?> DeriveHostKeyAsync(string password)
        {
            // The host's proof key is salted with its own transport key, which the identity holds.
            EnsureIdentity();
            var identityTask = loading!;
            var derived = Task.Run(async () =>
            {
                var loaded = await identityTask.ConfigureAwait(false);
                return (byte[]?)PasswordProof.DeriveKey(password, loaded.TransportPublicKey);
            });

            // A failed load fails the key with the same text, and hosting that fails, stops or closes lets the key go,
            // so it is observed as the load is.
            NetworkSaveTasks.Observe(derived);
            return derived;
        }

        private void BeginHosting()
        {
            var settings = pendingHost!;
            var keyTask = hostKey!;
            var listRead = bansRead!;
            pendingHost = null;
            hostKey = null;
            bansRead = null;
            if (keyTask.IsFaulted || keyTask.IsCanceled)
            {
                Fail(LiveFailure.Rejected);
                return;
            }

            if (listRead.Status != TaskStatus.RanToCompletion || !listRead.Result)
            {
                // A host that could not read its bans would admit every player it banned, so it does not open; hosting again
                // reads the list again. The derived key is wiped, as it is once a host has taken its copy.
                if (keyTask.Result != null) Array.Clear(keyTask.Result, 0, keyTask.Result.Length);
                Fail(LiveFailure.BansUnreadable);
                return;
            }

            var host = new LiveHost(engine, identity!, bans.Record, settings, content, clock, unixSeconds, keyTask.Result, transportOptions, Services, natOptions);

            // The host's policy keeps its own copy of the key, so the derived one is wiped at once.
            if (keyTask.Result != null) Array.Clear(keyTask.Result, 0, keyTask.Result.Length);
            var failure = host.Start(factory);
            if (failure != LiveFailure.None)
            {
                Fail(failure);
                return;
            }

            Host = host;
            HostPhase = HostPhase.Hosting;
        }

        /// <summary>Bans the player behind <paramref name="participant"/> from this host, and saves the list.</summary>
        internal bool Ban(ParticipantId participant, string displayName)
        {
            if (Host == null || !Host.Ban(participant, displayName)) return false;

            // The save starts now rather than at the next frame, so a ban survives the game closing right away.
            bans.MarkChanged();
            Persist();
            return true;
        }

        /// <summary>Ends a hosting attempt that could not start; the player may try again.</summary>
        private void Fail(LiveFailure failure)
        {
            pendingHost = null;
            hostKey = null;
            bansRead = null;
            HostPhase = HostPhase.Failed;
            HostFailure = failure;
        }

        /// <summary>
        /// Saves what changed. Each file's writes run one after another, so a slow save can never land after, and
        /// overwrite, the newer one behind it. The bans and the known hosts are each saved only once their file was read.
        /// </summary>
        private void Persist()
        {
            bansWrite = bans.SaveAfter(bansWrite);
            knownHostsWrite = knownHosts.SaveAfter(knownHostsWrite);
        }

        private static bool Done(Task? task) => task != null && task.IsCompleted;

        /// <summary>
        /// Lets go of the password key this hosting attempt derived, wiped: at once when it is done, and the moment it is
        /// done when it is still being derived. A derivation that completes after hosting stopped, or after a newer attempt
        /// began, so wipes its own result and starts nothing, since only the key of the attempt in hand ever opens a host.
        /// The wipe is bound to this attempt's own task, so it never reaches a newer attempt's key.
        /// </summary>
        private void ReleaseHostKey()
        {
            var pending = hostKey;
            hostKey = null;
            pending?.ContinueWith(
                task =>
                {
                    if (task.Status == TaskStatus.RanToCompletion && task.Result != null) Array.Clear(task.Result, 0, task.Result.Length);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        /// <summary>The browser accepts no peers and claims its own replies, so anything else it reports is let go.</summary>
        private void DrainBrowserEvents()
        {
            var current = browserNetwork;
            if (current == null) return;
            while (current.TryTakeEvent(out var networkEvent)) networkEvent.Link?.Close();
        }

        /// <summary>Runs inside the engine when a joined session ends: the join hears why, and the process returns to solo play.</summary>
        private SoloSessionStart? OnClientSessionEnded(ClientSessionEnd end)
        {
            Join?.OnSessionEnded(end);
            return NextSolo();
        }

        private SoloSessionStart NextSolo()
        {
            var seed = new byte[8];
            CryptoRandom.Fill(seed);
            return new SoloSessionStart(
                MultiplayerProvider.SoloSessionId(providerModId),
                new SessionSeed(BitConverter.ToUInt64(seed, 0)),
                new ParticipantId(MultiplayerProvider.LocalParticipantId),
                MultiplayerProvider.LocalDisplayName);
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(LiveSessions));
        }
    }
}
