using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Multiplayer.Engine;
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

        /// <summary>How long closing waits for the last saves to land.</summary>
        private const int SaveFlushMilliseconds = 1_000;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly Func<ClientSessionEnd, SoloSessionStart?> endHook;
        private Task<(LocalIdentity Identity, BanList Bans)>? loading;
        private Task<byte[]?>? hostKey;
        private HostSettings? pendingHost;
        private LocalIdentity? identity;
        private BanList? bans;
        private INetworkControl? browserNetwork;
        private LanBrowser? browser;
        private int bansDirty;
        private int knownHostsDirty;
        private Task bansWrite = Task.CompletedTask;
        private Task knownHostsWrite = Task.CompletedTask;
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
            Func<Transport.TransportOptions>? transportOptions = null)
        {
            this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
            this.files = files ?? throw new ArgumentNullException(nameof(files));
            this.content = content ?? throw new ArgumentNullException(nameof(content));
            this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            this.unixSeconds = unixSeconds ?? throw new ArgumentNullException(nameof(unixSeconds));
            this.providerModId = providerModId ?? throw new ArgumentNullException(nameof(providerModId));
            this.transportOptions = transportOptions ?? (() => new Transport.TransportOptions());
            KnownHosts = new KnownHosts();
            this.resolver = resolver ?? new NetworkTargetResolver(() => browser, KnownHosts, MarkKnownHostsChanged, unixSeconds);
            endHook = OnClientSessionEnded;
            engine.Options.AfterClientSessionEnds = endHook;
        }

        internal HostPhase HostPhase { get; private set; }

        /// <summary>Gets why hosting failed, when it did.</summary>
        internal LiveFailure HostFailure { get; private set; }

        internal LiveHost? Host { get; private set; }

        internal LiveJoin? Join { get; private set; }

        internal KnownHosts KnownHosts { get; }

        /// <summary>Marks the known-hosts record for saving; the resolver calls it from its own thread after a change.</summary>
        internal void MarkKnownHostsChanged() => Interlocked.Exchange(ref knownHostsDirty, 1);

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
        }

        /// <summary>Stops hosting: every player's session ends, and this process plays on alone.</summary>
        internal void StopHosting()
        {
            pendingHost = null;
            hostKey = null;
            Host?.Stop();
            Host = null;
            HostPhase = HostPhase.Idle;
        }

        /// <summary>Starts joining <paramref name="target"/>. The solo session stays until the link to the host is up.</summary>
        internal LiveJoin StartJoin(JoinTarget target, JoinSettings settings)
        {
            ThrowIfDisposed();
            if (Host != null || pendingHost != null) throw new InvalidOperationException("Stop hosting before joining another session.");
            if (Join != null && Join.Phase != JoinPhase.Ended) throw new InvalidOperationException("A join is already in progress.");
            if (identity == null) throw new InvalidOperationException("The identity is not ready yet; call PrepareAsync first.");
            Join?.Dispose();
            Join = new LiveJoin(engine, identity, target, settings, resolver, content, factory, clock, NextSolo, transportOptions);
            return Join;
        }

        /// <summary>Loads the identity ahead of a join, so the join can start at once.</summary>
        internal Task PrepareAsync()
        {
            EnsureIdentity();
            return loading!;
        }

        /// <summary>Gets whether the identity has loaded.</summary>
        internal bool IsReady => identity != null;

        /// <summary>
        /// Gets the work running on other threads that lands at a later pump: the identity load, a password key being
        /// derived, the join's resolve and proof, and, with <paramref name="saves"/>, files still being saved. A test rig
        /// waits for it in real time, so it lands at the same point of a run however slow the machine is.
        /// </summary>
        internal List<Task> PendingBackgroundWork(bool saves)
        {
            var work = new List<Task>();
            if (identity == null && loading != null && !loading.IsCompleted) work.Add(loading);
            if (hostKey != null && !hostKey.IsCompleted) work.Add(hostKey);
            Join?.AddPendingBackgroundWork(work);
            if (saves && !bansWrite.IsCompleted) work.Add(bansWrite);
            if (saves && !knownHostsWrite.IsCompleted) work.Add(knownHostsWrite);
            return work;
        }

        /// <summary>
        /// Starts listening for LAN sessions, on its own socket and network. Browsing is always relay-only: LAN hosts are
        /// still asked for their info, but a beacon from any other address is never answered with this player's own.
        /// </summary>
        internal void StartBrowsing(int discoveryPort = LanBeacon.DiscoveryPort)
        {
            ThrowIfDisposed();
            if (browserNetwork != null) return;
            var options = transportOptions();
            options.RelayOnly = true;
            options.AcceptsInbound = false;
            var endpoint = new Transport.TransportEndpoint(X25519.GeneratePrivateKey(), options, clock());
            browserNetwork = factory.Start(0, new NetworkStack(endpoint));
            browser = factory.CreateBrowser(discoveryPort);
            if (browser != null) browserNetwork.Attach(browser);
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
            if (pendingHost != null && identity != null && hostKey != null && hostKey.IsCompleted) BeginHosting();
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

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            StopHosting();
            Join?.Dispose();
            Join = null;
            StopBrowsing();
            if (ReferenceEquals(engine.Options.AfterClientSessionEnds, endHook)) engine.Options.AfterClientSessionEnds = null;

            // Whatever changed is saved before the game closes, and the saves get a moment to land.
            Persist();
            try
            {
                Task.WaitAll(new[] { bansWrite, knownHostsWrite }, SaveFlushMilliseconds);
            }
            catch (AggregateException)
            {
                // A save that failed leaves the previous file; there is nothing more to do while closing.
            }

            lifetime.Cancel();
            identity?.Dispose();

            // An identity still loading is released the moment it arrives.
            loading?.ContinueWith(
                task =>
                {
                    if (task.Status == TaskStatus.RanToCompletion && !ReferenceEquals(task.Result.Identity, identity)) task.Result.Identity.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            lifetime.Dispose();
        }

        private void EnsureIdentity()
        {
            if (identity != null || loading != null) return;
            var store = new IdentityStore(files, unixSeconds);
            var token = lifetime.Token;
            loading = Task.Run(async () =>
            {
                var loaded = await store.LoadOrCreateAsync(token).ConfigureAwait(false);
                var handedOver = false;
                try
                {
                    // The ban list and known hosts are side records: one that cannot be read is treated as empty, the
                    // way a damaged one is, rather than keeping the player out of live play.
                    var banned = BanList.Parse(await ReadSideFileAsync(BanList.FilePath, token).ConfigureAwait(false));
                    var parsed = Network.KnownHosts.Parse(await ReadSideFileAsync(Network.KnownHosts.FilePath, token).ConfigureAwait(false));
                    lock (KnownHosts) KnownHosts.MergeFrom(parsed);
                    handedOver = true;
                    return (loaded, banned);
                }
                finally
                {
                    if (!handedOver) loaded.Dispose();
                }
            });
        }

        private void Loaded()
        {
            var task = loading!;
            if (task.IsFaulted || task.IsCanceled)
            {
                // The next attempt to host or prepare loads it again.
                loading = null;
                if (pendingHost != null) Fail(LiveFailure.IdentityUnavailable);
                return;
            }

            identity = task.Result.Identity;
            bans = task.Result.Bans;
        }

        private async Task<byte[]?> ReadSideFileAsync(string relativePath, CancellationToken token)
        {
            try
            {
                return await files.ReadAsync(relativePath, token).ConfigureAwait(false);
            }
            catch (Exception exception) when (!(exception is OperationCanceledException))
            {
                return null;
            }
        }

        private Task<byte[]?> DeriveHostKeyAsync(string password)
        {
            // The host's proof key is salted with its own transport key, which the identity holds.
            EnsureIdentity();
            var identityTask = loading!;
            return Task.Run(async () =>
            {
                var loaded = await identityTask.ConfigureAwait(false);
                return (byte[]?)PasswordProof.DeriveKey(password, loaded.Identity.TransportPublicKey);
            });
        }

        private void BeginHosting()
        {
            var settings = pendingHost!;
            var keyTask = hostKey!;
            pendingHost = null;
            hostKey = null;
            if (keyTask.IsFaulted || keyTask.IsCanceled)
            {
                Fail(LiveFailure.Rejected);
                return;
            }

            var host = new LiveHost(engine, identity!, bans!, settings, content, clock, unixSeconds, keyTask.Result, transportOptions);

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
            Interlocked.Exchange(ref bansDirty, 1);
            Persist();
            return true;
        }

        /// <summary>Ends a hosting attempt that could not start; the player may try again.</summary>
        private void Fail(LiveFailure failure)
        {
            pendingHost = null;
            hostKey = null;
            HostPhase = HostPhase.Failed;
            HostFailure = failure;
        }

        /// <summary>
        /// Saves what changed. Each file's writes run one after another, so a slow save can never land after, and
        /// overwrite, the newer one behind it.
        /// </summary>
        private void Persist()
        {
            if (Interlocked.Exchange(ref bansDirty, 0) == 1 && bans != null) bansWrite = After(bansWrite, BanList.FilePath, bans.Serialize());
            if (Interlocked.Exchange(ref knownHostsDirty, 0) == 1)
            {
                byte[] bytes;
                lock (KnownHosts) bytes = KnownHosts.Serialize();
                knownHostsWrite = After(knownHostsWrite, Network.KnownHosts.FilePath, bytes);
            }
        }

        /// <summary>
        /// Queues a save behind the previous one. Saves are never cancelled, even while closing: a cancelled write would
        /// lose the change, and each is small and bounded.
        /// </summary>
        private Task After(Task previous, string path, byte[] bytes) =>
            previous.ContinueWith(_ => files.WriteAsync(path, bytes, CancellationToken.None), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();

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
