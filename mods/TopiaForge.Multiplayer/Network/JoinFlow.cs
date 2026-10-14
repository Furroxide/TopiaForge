using System;
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
        private ResolvedTarget(LiveFailure failure, string detail, TransportPath? path, SignedServerDescriptor? descriptor)
        {
            Failure = failure;
            Detail = detail;
            Path = path;
            Descriptor = descriptor;
        }

        internal LiveFailure Failure { get; }
        internal string Detail { get; }
        internal TransportPath? Path { get; }
        internal SignedServerDescriptor? Descriptor { get; }

        internal static ResolvedTarget Found(TransportPath path, SignedServerDescriptor descriptor) =>
            new ResolvedTarget(LiveFailure.None, string.Empty, path ?? throw new ArgumentNullException(nameof(path)), descriptor ?? throw new ArgumentNullException(nameof(descriptor)));

        internal static ResolvedTarget Failed(LiveFailure failure, string detail) => new ResolvedTarget(failure, detail, null, null);
    }

    /// <summary>Finds where a target is and fetches what it says it is, off the main thread.</summary>
    internal interface IJoinTargetResolver
    {
        /// <summary>
        /// Resolves <paramref name="target"/>. Unless <paramref name="allowDirect"/>, nothing beyond the local network
        /// is contacted: a host there fails with <see cref="LiveFailure.DirectNotAllowed"/> before anything is sent.
        /// </summary>
        Task<ResolvedTarget> ResolveAsync(JoinTarget target, bool allowDirect, bool trustChangedIdentity, CancellationToken cancellationToken);
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
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private Task<ResolvedTarget>? resolving;
        private Task<byte[]>? deriving;
        private ResolvedTarget? resolved;
        private string? password;
        private byte[]? passwordKey;
        private INetworkControl? network;
        private ConnectAttempt? attempt;
        private long reconnectDeadline;
        private long nextAttemptAt;
        private bool engineJoined;

        internal LiveJoin(
            SessionEngine engine,
            LocalIdentity identity,
            JoinTarget target,
            JoinSettings settings,
            IJoinTargetResolver resolver,
            ILiveSessionContent content,
            INetworkFactory factory,
            Func<long> clock,
            Func<SoloSessionStart> soloStart)
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
            password = settings.Password;
            resolving = resolver.ResolveAsync(target, settings.AllowDirect, settings.TrustChangedIdentity, cancellation.Token);
        }

        internal JoinPhase Phase { get; private set; } = JoinPhase.Resolving;

        internal LiveFailure Failure { get; private set; }

        /// <summary>Gets what went wrong, for the player, when the join failed. It never names an address.</summary>
        internal string Detail { get; private set; } = string.Empty;

        /// <summary>Gets the host's admission report when it refused the join, decoded.</summary>
        internal AdmissionCode RejectionCode { get; private set; }

        /// <summary>Gets the packages the host listed that are missing or differ, when content stopped the join.</summary>
        internal ContentCheck? Content { get; private set; }

        /// <summary>Gets the host's verified descriptor once resolved, for the interface to show.</summary>
        internal ServerDescriptor? Descriptor => resolved?.Descriptor?.Descriptor;

        /// <summary>Gives the password the session asked for, when the join is waiting for it.</summary>
        internal void SupplyPassword(string value)
        {
            if (Phase != JoinPhase.AwaitingPassword) throw new InvalidOperationException("The join is not waiting for a password.");
            if (!PasswordProof.IsValidPassword(value)) throw new ArgumentException("A password is 1 to 64 characters with no control characters.", nameof(value));
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
                _ => LiveFailure.Rejected
            };
            RejectionCode = AdmissionReport.TryDecode(end.Report, out var code, out _) ? code : AdmissionCode.Other;
            Detail = end.Message;
            engineJoined = false;
            Phase = JoinPhase.Ended;
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
                End(resolved.Failure, resolved.Detail);
                return;
            }

            Phase = JoinPhase.Checking;
            var descriptor = resolved.Descriptor!.Descriptor;
            if (descriptor.GameBuild != content.GameBuild)
            {
                End(LiveFailure.WrongGameBuild, "The host runs Robotopia build " + descriptor.GameBuild + "; this game runs " + content.GameBuild + ".");
                return;
            }

            if (Major(descriptor.ProtocolVersion) != Major(SessionEngine.ProtocolVersion))
            {
                End(LiveFailure.ProtocolMismatch, "The host speaks multiplayer protocol " + descriptor.ProtocolVersion + ".");
                return;
            }

            if (resolved.Path!.Kind == TransportPathKind.Direct && !settings.AllowDirect)
            {
                End(LiveFailure.DirectNotAllowed, "Joining directly would show the host your address; allow it for this session to continue.");
                return;
            }

            Content = content.Check(descriptor);
            if (!Content.IsReady)
            {
                End(LiveFailure.ContentMismatch, "This game is missing packages the host runs.");
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
            var endpoint = new TransportEndpoint(identity.CopyTransportPrivateKey(), new TransportOptions { RelayOnly = !settings.AllowDirect }, clock());
            network = factory.Start(0, new NetworkStack(endpoint));
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

                End(failure == TransportCloseReason.HandshakeFailed ? LiveFailure.HandshakeFailed : LiveFailure.Unreachable,
                    failure == TransportCloseReason.HandshakeFailed ? "The host did not prove the identity it announced." : "The host did not answer.");
                return;
            }

            var link = attempt.Link!;
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
        }

        private void Watch()
        {
            if (!engineJoined) return;
            var state = engine.Snapshot.State;
            if (state == MultiplayerSessionState.Ready)
            {
                Phase = JoinPhase.InSession;
                return;
            }

            if (Phase == JoinPhase.InSession && state == MultiplayerSessionState.Connecting)
            {
                // The link went away under a joined session: try again until the host's grace period runs out.
                Phase = JoinPhase.Reconnecting;
                reconnectDeadline = clock() + ReconnectWindowMicros;
                nextAttemptAt = clock();
            }
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

            if (passwordKey != null) Array.Clear(passwordKey, 0, passwordKey.Length);
            StopNetwork();
        }

        internal void StopNetwork()
        {
            var current = network;
            network = null;
            current?.Stop(StopGraceMicros);
        }

        private static string Major(string version)
        {
            var dot = version.IndexOf('.');
            return dot < 0 ? version : version.Substring(0, dot);
        }
    }
}
