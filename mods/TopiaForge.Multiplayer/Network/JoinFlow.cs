using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Multiplayer.Engine;
using TopiaForge.Multiplayer.Transport;

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>A join target resolved to a path and its host's verified descriptor, or the reason it could not be.</summary>
    internal sealed class ResolvedTarget
    {
        private ResolvedTarget(LiveFailure failure, string detail, TransportPath? path, SignedServerDescriptor? descriptor, string? servedServerId)
        {
            Failure = failure;
            Detail = detail;
            Path = path;
            Descriptor = descriptor;
            ServedServerId = servedServerId;
        }

        internal LiveFailure Failure { get; }
        internal string Detail { get; }
        internal TransportPath? Path { get; }
        internal SignedServerDescriptor? Descriptor { get; }

        /// <summary>Gets, for a changed identity, the server the address serves now: the one the player may choose to trust.</summary>
        internal string? ServedServerId { get; }

        internal static ResolvedTarget Found(TransportPath path, SignedServerDescriptor descriptor) =>
            new ResolvedTarget(LiveFailure.None, string.Empty, path ?? throw new ArgumentNullException(nameof(path)), descriptor ?? throw new ArgumentNullException(nameof(descriptor)), null);

        internal static ResolvedTarget Failed(LiveFailure failure, string detail) => new ResolvedTarget(failure, detail, null, null, null);

        internal static ResolvedTarget IdentityChanged(string servedServerId) =>
            new ResolvedTarget(LiveFailure.HostIdentityChanged, "This address served a different server last time.", null, null, servedServerId);
    }

    /// <summary>Finds where a target is and fetches what it says it is, off the main thread.</summary>
    internal interface IJoinTargetResolver
    {
        /// <summary>
        /// Resolves <paramref name="target"/>. Unless <paramref name="allowDirect"/>, nothing beyond the local network
        /// is contacted, a name server included: such a host fails with <see cref="LiveFailure.DirectNotAllowed"/>
        /// before anything is sent. A typed address that served another server before is trusted only when the server
        /// it serves now is <paramref name="trustedServerId"/>.
        /// </summary>
        Task<ResolvedTarget> ResolveAsync(JoinTarget target, bool allowDirect, string? trustedServerId, CancellationToken cancellationToken);
    }

    /// <summary>
    /// One join, from target to session, on the main thread. It resolves the target and verifies the host's descriptor,
    /// checks the game build, the protocol and the content, asks for the password when the session needs one and
    /// derives its proof off the main thread, then connects with the descriptor's transport key pinned. Only once the
    /// link is up does the engine leave solo play for the host's session, so any failure before that leaves the
    /// player's own session untouched. A lost link is retried for the engine's grace period with a fresh proof; after
    /// that, or when the host ends the session, the engine returns to solo play.
    /// </summary>
    internal sealed class LiveJoin : IDisposable
    {
        internal const long ReconnectWindowMicros = 30_000_000;
        internal const long ReconnectIntervalMicros = 2_000_000;
        private const long StopGraceMicros = 100_000;

        private readonly SessionEngine engine;
        private readonly LocalIdentity identity;
        private readonly JoinTarget target;
        private readonly JoinSettings settings;
        private readonly IJoinTargetResolver resolver;
        private readonly ILiveSessionContent content;
        private readonly INetworkFactory factory;
        private readonly Func<long> clock;
        private readonly Func<SoloSessionStart> soloStart;
        private readonly Func<TransportOptions> transportOptions;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private Task<ResolvedTarget>? resolving;
        private Task<byte[]>? deriving;
        private ResolvedTarget? resolved;
        private string? password;
        private byte[]? passwordKey;
        private INetworkControl? network;
        private ConnectAttempt? attempt;
        private NetworkLink? link;
        private long reconnectDeadline;
        private long nextAttemptAt;
        private JoinReadiness? readiness;
        private bool engineJoined;
        private bool everReady;

        internal LiveJoin(
            SessionEngine engine,
            LocalIdentity identity,
            JoinTarget target,
            JoinSettings settings,
            IJoinTargetResolver resolver,
            ILiveSessionContent content,
            INetworkFactory factory,
            Func<long> clock,
            Func<SoloSessionStart> soloStart,
            Func<TransportOptions>? transportOptions = null)
        {
            this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
            this.identity = identity ?? throw new ArgumentNullException(nameof(identity));
            this.target = target ?? throw new ArgumentNullException(nameof(target));
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
            this.content = content ?? throw new ArgumentNullException(nameof(content));
            this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            this.soloStart = soloStart ?? throw new ArgumentNullException(nameof(soloStart));
            this.transportOptions = transportOptions ?? (() => new TransportOptions());
            password = settings.Password;
            resolving = resolver.ResolveAsync(target, settings.AllowDirect, settings.TrustedServerId, cancellation.Token);
        }

        internal JoinPhase Phase { get; private set; } = JoinPhase.Resolving;

        internal LiveFailure Failure { get; private set; }

        /// <summary>Gets what went wrong, for the player, in this game's own words. It never names an address.</summary>
        internal string Detail { get; private set; } = string.Empty;

        /// <summary>
        /// Gets what the host said when it ended, refused or removed this player, such as a kick message. Host-authored:
        /// it may hold anything, so it is shown only as the host's words and never logged.
        /// </summary>
        internal string HostMessage { get; private set; } = string.Empty;

        /// <summary>Gets or sets how long this join waits for its host. Set before the first pump.</summary>
        internal LiveJoinTimings Timings { get; set; } = LiveJoinTimings.Default;

        private JoinReadiness Readiness => readiness ??= new JoinReadiness(Timings);

        /// <summary>Gets the host's admission report when it refused the join, decoded.</summary>
        internal AdmissionCode RejectionCode { get; private set; }

        /// <summary>Gets the problems the host's admission report listed, such as the packages a content refusal names.</summary>
        internal IReadOnlyList<string> RejectionProblems { get; private set; } = Array.Empty<string>();

        /// <summary>Gets the packages the host listed that are missing or differ, when content stopped the join.</summary>
        internal ContentCheck? Content { get; private set; }

        /// <summary>Gets the host's verified descriptor once resolved, for the interface to show.</summary>
        internal ServerDescriptor? Descriptor => resolved?.Descriptor?.Descriptor;

        /// <summary>
        /// Gets, when a typed address now serves a different server, that server's id. A new join that sets it as
        /// <see cref="JoinSettings.TrustedServerId"/> trusts exactly that server there.
        /// </summary>
        internal string? ChangedServerId { get; private set; }

        /// <summary>Gets whether a password key is still held, unwiped; tests check every way a join ends wipes it.</summary>
        internal bool HoldsPasswordKey
        {
            get
            {
                if (passwordKey == null) return false;
                foreach (var value in passwordKey)
                {
                    if (value != 0) return true;
                }

                return false;
            }
        }

        /// <summary>Adds the work this join runs on other threads, which lands at a later pump: its resolve and its proof key.</summary>
        internal void AddPendingBackgroundWork(List<Task> work)
        {
            if (resolving != null && !resolving.IsCompleted) work.Add(resolving);
            if (deriving != null && !deriving.IsCompleted) work.Add(deriving);
        }

        /// <summary>Gives the password the session asked for, when the join is waiting for it.</summary>
        internal void SupplyPassword(string value)
        {
            if (Phase != JoinPhase.AwaitingPassword) throw new InvalidOperationException("The join is not waiting for a password.");
            if (!PasswordProof.IsValidPassword(value)) throw new ArgumentException(PasswordProof.Rule, nameof(value));
            password = value;
            BeginProof();
        }

        /// <summary>Main thread, every frame.</summary>
        internal void Pump()
        {
            if (Phase == JoinPhase.Ended)
            {
                // A session the host ended is noticed inside the engine, where the network cannot be stopped.
                StopNetwork();
                return;
            }

            DrainEvents();

            switch (Phase)
            {
                case JoinPhase.Resolving:
                    if (resolving != null && resolving.IsCompleted) Resolved();
                    break;
                case JoinPhase.PreparingProof:
                    if (deriving != null && deriving.IsCompleted) Derived();
                    break;
                case JoinPhase.Connecting:
                case JoinPhase.Reconnecting:
                    Connecting();
                    break;
                case JoinPhase.Synchronizing:
                case JoinPhase.InSession:
                    Watch();
                    break;
            }
        }

        /// <summary>Called inside the engine when the joined session ends; the engine is returning to solo play.</summary>
        internal void OnSessionEnded(ClientSessionEnd end)
        {
            if (Phase == JoinPhase.Ended) return;
            Failure = end.Reason switch
            {
                MultiplayerSessionEndReason.Kicked => LiveFailure.Kicked,
                MultiplayerSessionEndReason.Banned => LiveFailure.Banned,
                MultiplayerSessionEndReason.HostEnded => LiveFailure.HostEnded,
                MultiplayerSessionEndReason.ProtocolMismatch => LiveFailure.ProtocolMismatch,
                MultiplayerSessionEndReason.Left => LiveFailure.Left,
                MultiplayerSessionEndReason.TimedOut => LiveFailure.TimedOut,
                _ => LiveFailure.Rejected
            };
            if (AdmissionReport.TryDecode(end.Report, out var code, out var problems))
            {
                RejectionCode = code;
                RejectionProblems = problems;
            }
            else
            {
                RejectionCode = AdmissionCode.Other;
                RejectionProblems = Array.Empty<string>();
            }

            HostMessage = end.Message;
            Detail = EndDetail(Failure, RejectionCode);
            engineJoined = false;
            link = null;
            Phase = JoinPhase.Ended;
            WipePasswordKey();
        }

        /// <summary>Leaves: a joined session tells the host and returns to solo play; an unfinished join just stops.</summary>
        internal void Leave()
        {
            if (Phase == JoinPhase.Ended) return;
            var wasJoined = engineJoined;
            End(wasJoined ? LiveFailure.Left : LiveFailure.Cancelled, string.Empty);
        }

        public void Dispose()
        {
            Leave();
            WipePasswordKey();
            StopNetwork();
            cancellation.Dispose();
        }

        private void Resolved()
        {
            var task = resolving!;
            resolving = null;
            if (task.IsCanceled || task.IsFaulted)
            {
                End(task.IsCanceled ? LiveFailure.Cancelled : LiveFailure.Unreachable, "The host could not be reached.");
                return;
            }

            resolved = task.Result;
            if (resolved.Failure != LiveFailure.None)
            {
                ChangedServerId = resolved.ServedServerId;
                End(resolved.Failure, resolved.Detail);
                return;
            }

            Phase = JoinPhase.Checking;
            var descriptor = resolved.Descriptor!.Descriptor;
            var failure = JoinChecks.Evaluate(resolved, settings.AllowDirect, content, out var detail, out var check);
            Content = check;
            if (failure != LiveFailure.None)
            {
                End(failure, detail);
                return;
            }

            if (!descriptor.PasswordRequired)
            {
                StartConnecting();
                return;
            }

            if (password == null) Phase = JoinPhase.AwaitingPassword;
            else BeginProof();
        }

        private void BeginProof()
        {
            Phase = JoinPhase.PreparingProof;
            var secret = password!;
            var hostKey = resolved!.Descriptor!.Descriptor.TransportKey;
            password = null;
            deriving = Task.Run(() => PasswordProof.DeriveKey(secret, hostKey));
        }

        private void Derived()
        {
            var task = deriving!;
            deriving = null;
            if (task.IsFaulted || task.IsCanceled)
            {
                End(LiveFailure.Rejected, "The password could not be prepared.");
                return;
            }

            passwordKey = task.Result;
            StartConnecting();
        }

        private void StartConnecting()
        {
            Phase = JoinPhase.Connecting;

            // A joining player connects out and accepts nobody, so no one else can reach it or learn its key.
            var options = transportOptions();
            options.RelayOnly = !settings.AllowDirect;
            options.AcceptsInbound = false;
            var endpoint = new TransportEndpoint(identity.CopyTransportPrivateKey(), options, clock());
            var stack = new NetworkStack(endpoint);
            try
            {
                network = factory.Start(0, stack);
            }
            catch (System.Net.Sockets.SocketException)
            {
                stack.Dispose();
                End(LiveFailure.Unreachable, "This game could not open a network connection.");
                return;
            }

            attempt = network.Connect(resolved!.Path!, resolved.Descriptor!.Descriptor.TransportKey);
        }

        private void Connecting()
        {
            var now = clock();
            if (attempt == null)
            {
                if (Phase == JoinPhase.Reconnecting && now >= nextAttemptAt) attempt = network!.Connect(resolved!.Path!, resolved.Descriptor!.Descriptor.TransportKey);
                else if (Phase == JoinPhase.Reconnecting && now >= reconnectDeadline) End(LiveFailure.ConnectionLost, "The connection to the host was lost.");
                return;
            }

            if (attempt.State == ConnectAttemptState.Pending) return;
            if (attempt.State == ConnectAttemptState.Failed)
            {
                var failure = attempt.Failure;
                attempt = null;
                if (Phase == JoinPhase.Reconnecting)
                {
                    nextAttemptAt = now + ReconnectIntervalMicros;
                    if (now >= reconnectDeadline) End(LiveFailure.ConnectionLost, "The connection to the host was lost.");
                    return;
                }

                switch (failure)
                {
                    case TransportCloseReason.HandshakeFailed:
                        End(LiveFailure.HandshakeFailed, "The host did not prove the identity it announced.");
                        break;
                    case TransportCloseReason.HandshakeTimedOut:
                        End(LiveFailure.TimedOut, "The host did not answer in time.");
                        break;
                    default:
                        End(LiveFailure.Unreachable, "The host did not answer.");
                        break;
                }

                return;
            }

            link = attempt.Link!;
            attempt = null;
            var proof = passwordKey == null ? null : PasswordProof.Prove(passwordKey, link.ChannelBinding);
            var joinContent = JoinContent.Encode(proof, content.ClientContent());
            if (!engineJoined)
            {
                // The link is up and pinned: only now does the solo session give way to the host's.
                engine.SwitchToClient(new MultiplayerSessionId("joining:" + resolved!.Descriptor!.Descriptor.ServerId.Substring(0, 16)));
                engineJoined = true;
            }

            engine.Connect(link, settings.DisplayName, joinContent);
            Phase = JoinPhase.Synchronizing;
            Readiness.LinkUp(now, engine.SessionReplacementsReceived);
        }

        private void Watch()
        {
            if (!engineJoined) return;
            var now = clock();
            var state = engine.SessionState;
            if (state == MultiplayerSessionState.Ready)
            {
                Phase = JoinPhase.InSession;
                everReady = true;
                Readiness.Ready();
                return;
            }

            if (link != null && link.State == EngineLinkState.Closed)
            {
                // The link went away before or during the session. A player who was in the session tries again until the
                // host's grace period runs out, keeping the window that started when the first link was lost; a first
                // join that never got in ends, back in solo play.
                link = null;
                if (!everReady)
                {
                    End(LiveFailure.ConnectionLost, "The connection to the host was lost before the session started.");
                    return;
                }

                if (Phase == JoinPhase.InSession) reconnectDeadline = now + ReconnectWindowMicros;
                Phase = JoinPhase.Reconnecting;
                nextAttemptAt = now;
                return;
            }

            // A host that withholds the world, or replaces it again and again, cannot keep the player for ever.
            var timedOut = Readiness.Check(
                now,
                state == MultiplayerSessionState.Synchronizing,
                engine.IsAwaitingLocalReadiness,
                engine.ReadinessHeldSinceMicros,
                engine.ReadinessProgressMicros,
                engine.SessionReplacementsReceived);
            if (timedOut != null) End(LiveFailure.TimedOut, timedOut);
        }

        /// <summary>
        /// Says, in this game's own words, why the host ended or refused the session, from the failure and the report's
        /// code; the host's own words are <see cref="HostMessage"/>.
        /// </summary>
        private static string EndDetail(LiveFailure failure, AdmissionCode code)
        {
            switch (failure)
            {
                case LiveFailure.Kicked:
                    return "The host removed this player from the session.";
                case LiveFailure.Banned:
                    return "The host banned this player from the session.";
                case LiveFailure.HostEnded:
                    return "The host ended the session.";
                case LiveFailure.ProtocolMismatch:
                    return "The host and this game could not agree on the session protocol.";
                case LiveFailure.TimedOut:
                    return "The host stopped waiting for this game to finish joining.";
                case LiveFailure.Left:
                    return string.Empty;
            }

            switch (code)
            {
                case AdmissionCode.Full:
                    return "The session is full.";
                case AdmissionCode.PasswordRequired:
                    return "The session needs a password.";
                case AdmissionCode.PasswordWrong:
                    return "The password is wrong.";
                case AdmissionCode.TooManyAttempts:
                    return "The host is turning joins away after too many wrong passwords; try again shortly.";
                case AdmissionCode.Banned:
                    return "The host banned this player from the session.";
                case AdmissionCode.Content:
                    return "This game's packages do not match the session's.";
                case AdmissionCode.NotReady:
                    return "The host is not ready for players yet; try again shortly.";
                default:
                    return "The host refused the join.";
            }
        }

        /// <summary>
        /// Takes whatever the join's network reports. It accepts no peers, so a link here is a stray, closed at once;
        /// info replies and faults mean nothing to a join.
        /// </summary>
        private void DrainEvents()
        {
            var current = network;
            if (current == null) return;
            while (current.TryTakeEvent(out var networkEvent)) networkEvent.Link?.Close();
        }

        /// <summary>Wipes the password key, including one still being derived, which is wiped the moment it is done.</summary>
        private void WipePasswordKey()
        {
            if (passwordKey != null) Array.Clear(passwordKey, 0, passwordKey.Length);
            var pending = deriving;
            deriving = null;
            pending?.ContinueWith(
                task =>
                {
                    if (task.Status == TaskStatus.RanToCompletion) Array.Clear(task.Result, 0, task.Result.Length);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void End(LiveFailure failure, string detail)
        {
            if (Phase == JoinPhase.Ended) return;
            cancellation.Cancel();
            Failure = failure;
            Detail = detail;
            Phase = JoinPhase.Ended;
            if (engineJoined && engine.Role == EngineRole.Client)
            {
                engineJoined = false;
                engine.SwitchToSolo(soloStart());
            }

            link = null;
            WipePasswordKey();
            StopNetwork();
        }

        internal void StopNetwork()
        {
            var current = network;
            network = null;
            current?.Stop(StopGraceMicros);
        }
    }
}
