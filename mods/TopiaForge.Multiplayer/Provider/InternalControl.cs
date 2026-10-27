using System;
using System.Collections.Generic;
using System.Text;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Multiplayer.Engine;
using TopiaForge.Multiplayer.Network;

namespace TopiaForge.Multiplayer
{
    /// <summary>
    /// The provider's <see cref="IInternalMultiplayerControl"/>, over its session engine and live sessions. ModManager's
    /// composition root attaches to it and drives the frame; until one attaches, the provider pumps itself from its
    /// update subscription. Every member runs on the main thread, the engine's owner. See
    /// docs/internal/MultiplayerRuntimeIntegration.md.
    /// </summary>
    internal sealed partial class InternalControl : IInternalMultiplayerControl, IDisposable
    {
        private const string LiveOff = "Live multiplayer is switched off in this build.";
        private const string NoRuntime = "The TopiaForge runtime has not attached to multiplayer yet.";

        private readonly MultiplayerProvider provider;
        private readonly SessionEngine engine;
        private readonly LiveSessions? live;
        private readonly RuntimeContent content;
        private readonly RoutedDiagnostics diagnostics;
        private readonly DeveloperSessions? developer;
        private readonly ProviderVoiceService? voice;
        private IInternalMultiplayerRuntimeHost? runtime;
        private PendingJoin? pendingJoin;
        private InternalLiveFailure pendingJoinFailure;
        private InternalSessionRole lastRole;
        private MultiplayerSessionId lastSession;
        private HostPhase lastHostPhase;
        private LiveJoin? observedJoin;
        private JoinPhase observedJoinPhase;
        private string? appliedName;
        private bool displayNameFailed;
        private bool reportedIPv4Only;
        private bool disposed;

        internal InternalControl(MultiplayerProvider provider, RuntimeContent content, RoutedDiagnostics diagnostics, DeveloperSessions? developer = null)
        {
            this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
            this.content = content ?? throw new ArgumentNullException(nameof(content));
            this.diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            this.developer = developer;
            engine = provider.Engine;
            live = provider.Live;
            voice = provider.Voice == null ? null : new ProviderVoiceService(engine, provider.Voice);
            lastSession = engine.CurrentSessionId;
        }

        public bool IsLiveAvailable => live != null;

        public InternalSessionRole Role =>
            engine.Role == EngineRole.Client ? InternalSessionRole.Client
            : live != null && live.Host != null && live.HostPhase == HostPhase.Hosting ? InternalSessionRole.ListenHost
            : InternalSessionRole.Solo;

        public bool IsReadinessHeld => engine.ReadinessHolds > 0;

        /// <summary>Gets the engine's session snapshot, built again only when the session changed.</summary>
        public MultiplayerSessionSnapshot SessionSnapshot
        {
            get
            {
                engine.RequireOwnerThread();
                return engine.Snapshot;
            }
        }

        /// <summary>
        /// Gets the provider's voice service, or null while it has none: the provider runs voice only with live multiplayer
        /// switched on, and none once it has stopped. It hands out the service's own playback streams.
        /// </summary>
        public IInternalVoiceService? Voice => disposed ? null : voice;

        /// <summary>Gets the attached runtime, or null.</summary>
        internal IInternalMultiplayerRuntimeHost? Runtime => runtime;

        public bool AttachRuntime(IInternalMultiplayerRuntimeHost runtime)
        {
            if (runtime == null) throw new ArgumentNullException(nameof(runtime));
            engine.RequireOwnerThread();
            if (disposed) return false;
            if (this.runtime != null) return ReferenceEquals(this.runtime, runtime);
            this.runtime = runtime;
            content.Runtime = runtime;
            content.Report = diagnostics.Report;
            diagnostics.Runtime = runtime;
            ConfigureService(runtime);

            // The identity loads off the main thread, so a join the player starts later can begin at once.
            live?.PrepareAsync();
            ApplyDisplayName();
            SetRunInBackground(Role != InternalSessionRole.Solo);
            return true;
        }

        public void DetachRuntime(IInternalMultiplayerRuntimeHost runtime)
        {
            if (runtime == null || !ReferenceEquals(this.runtime, runtime)) return;
            this.runtime = null;
            content.Runtime = null;
            diagnostics.Runtime = null;

            // The runtime detaches as shutdown begins, before any mod stops, while this mod's files still take writes:
            // once it stops they refuse every one, so the bans and known hosts are saved now. A detach as this control
            // closes leaves that to the provider's own closing.
            if (!disposed) live?.SaveBeforeClosing();
        }

        public void Pump()
        {
            if (disposed) return;
            provider.Pump();
            developer?.Pump();
            StartPendingJoin();
            Observe();
        }

        /// <summary>The provider's own update subscription: it pumps only while no runtime drives the frame.</summary>
        internal void PumpUnattached()
        {
            if (runtime == null) Pump();
        }

        public void Flush()
        {
            if (disposed) return;
            provider.FlushOutbound();
        }

        public OperationResult<bool> StartHosting(InternalHostSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            engine.RequireOwnerThread();
            var unavailable = RequireLive();
            if (unavailable != null) return unavailable;

            // A join still waiting for the identity is joining too, though the live sessions do not know of it yet.
            if (pendingJoin != null) return Fail(ModErrorCode.InvalidState, "Leave the joined session before hosting.");
            if (settings.Password != null && !PasswordProof.IsValidPassword(settings.Password))
            {
                return Fail(ModErrorCode.InvalidArgument, "A password is 1 to 64 characters with no control characters.");
            }

            // This game's packages are read, and hashed off the main thread where they have no receipt, before anything opens:
            // hosting waits while that runs, and packages that cannot be read stop hosting here, with the failure reported.
            if (content.IsLocalContentPending) return Fail(ModErrorCode.Unavailable, RuntimeContent.ContentNotReady);
            var local = content.ReadLocalContent();
            if (local == null) return Fail(ModErrorCode.Io, RuntimeContent.ContentUnreadable);
            foreach (var descriptor in local)
            {
                if (descriptor.Mode == InternalContentMode.Standalone)
                {
                    return Fail(ModErrorCode.InvalidState, descriptor.DisplayName + " refuses multiplayer; disable it to host.");
                }
            }

            // Public and invite-only sessions are found through the TopiaForge service; without one, only the LAN can find them.
            if (LacksService(settings.Visibility)) return Fail(ModErrorCode.Unavailable, NoService);
            HostSettings host;
            try
            {
                host = new HostSettings(settings.Name, settings.MaxPlayers, ToVisibility(settings.Visibility))
                {
                    Password = settings.Password,
                    AllowDirect = settings.AllowDirect,
                    MapPorts = settings.RouterForwarding,
                    Voice = settings.Voice,
                    Gamemode = settings.Gamemode,
                    World = settings.World
                };
            }
            catch (ArgumentException exception)
            {
                return Fail(ModErrorCode.InvalidArgument, exception.Message);
            }

            // A session launch's own settings open with its game port and its privacy, whatever was set on them since.
            ApplyLaunchPlan(settings, host);
            try
            {
                live!.StartHosting(host);
            }
            catch (InvalidOperationException exception)
            {
                return Fail(ModErrorCode.InvalidState, exception.Message);
            }

            return OperationResult<bool>.Success(true);
        }

        public void StopHosting()
        {
            engine.RequireOwnerThread();

            // Stopping closes every connection, and the engine forgets the moderation it kept, server mutes included.
            live?.StopHosting();
        }

        public bool TryCreateJoinTarget(string text, out IInternalJoinTarget? target)
        {
            var parsed = ProviderJoinTarget.TryParse(text, out var created);
            target = created;
            return parsed;
        }

        public OperationResult<bool> Join(IInternalJoinTarget target, InternalJoinSettings settings)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            engine.RequireOwnerThread();
            var unavailable = RequireLive();
            if (unavailable != null) return unavailable;
            if (live!.Host != null || live.HostPhase == HostPhase.Preparing) return Fail(ModErrorCode.InvalidState, "Stop hosting before joining another session.");
            if (pendingJoin != null || (live.Join != null && live.Join.Phase != JoinPhase.Ended)) return Fail(ModErrorCode.InvalidState, "A join is already in progress.");
            if (!(target is ProviderJoinTarget providerTarget)) return Fail(ModErrorCode.InvalidArgument, "The join target was not made by this provider.");
            if (providerTarget.NetworkTarget.NeedsService && live.Services == null) return Fail(ModErrorCode.Unavailable, NoService);
            var name = settings.DisplayName ?? runtime!.DisplayName;
            if (!WireText.IsDisplayText(name, InternalJoinSettings.MaximumDisplayNameLength))
            {
                return Fail(ModErrorCode.InvalidArgument, "A display name is 1 to 24 printable characters.");
            }

            if (settings.Password != null && !PasswordProof.IsValidPassword(settings.Password))
            {
                return Fail(ModErrorCode.InvalidArgument, "A password is 1 to 64 characters with no control characters.");
            }

            var join = JoinSettingsFor(providerTarget, settings, name);
            pendingJoinFailure = InternalLiveFailure.None;
            pendingJoin = new PendingJoin(providerTarget.NetworkTarget, join);

            // A new join tries the identity again even when an earlier load failed.
            live.PrepareAsync();
            StartPendingJoin();
            return OperationResult<bool>.Success(true);
        }

        public OperationResult<bool> SupplyPassword(string password)
        {
            if (password == null) throw new ArgumentNullException(nameof(password));
            engine.RequireOwnerThread();
            var join = live?.Join;
            if (join == null || join.Phase != JoinPhase.AwaitingPassword) return Fail(ModErrorCode.InvalidState, "The join is not waiting for a password.");
            if (!PasswordProof.IsValidPassword(password)) return Fail(ModErrorCode.InvalidArgument, "A password is 1 to 64 characters with no control characters.");
            join.SupplyPassword(password);
            return OperationResult<bool>.Success(true);
        }

        public void Leave()
        {
            engine.RequireOwnerThread();
            if (pendingJoin != null)
            {
                // A join left before it started ends the way a live join left before it joined does.
                pendingJoin = null;
                pendingJoinFailure = InternalLiveFailure.Cancelled;
                return;
            }

            live?.Join?.Leave();
        }

        public OperationResult<bool> Kick(ParticipantId participant, string message)
        {
            engine.RequireOwnerThread();
            var host = live?.Host;
            if (host == null) return Fail(ModErrorCode.NotAuthoritative, "Only a listen host removes players.");
            if (IsLocal(participant)) return Fail(ModErrorCode.InvalidArgument, "The host cannot remove its own player.");
            var text = WireText.IsDisplayText(message, 256) ? message : "The host removed you from the session.";
            return host.Kick(participant, text) ? OperationResult<bool>.Success(true) : Fail(ModErrorCode.NotFound, "No connected player has that id.");
        }

        public OperationResult<bool> Ban(ParticipantId participant)
        {
            engine.RequireOwnerThread();
            if (live?.Host == null) return Fail(ModErrorCode.NotAuthoritative, "Only a listen host bans players.");
            if (IsLocal(participant)) return Fail(ModErrorCode.InvalidArgument, "The host cannot ban its own player.");
            var name = engine.Snapshot.TryGetParticipant(participant, out var record) ? record!.DisplayName : "Player";
            return live.Ban(participant, name) ? OperationResult<bool>.Success(true) : Fail(ModErrorCode.NotFound, "No player in this session has that id.");
        }

        public OperationResult<bool> Unban(string banId)
        {
            if (banId == null) throw new ArgumentNullException(nameof(banId));
            engine.RequireOwnerThread();
            if (live == null) return Fail(ModErrorCode.Unavailable, LiveOff);
            foreach (var record in live.Bans)
            {
                if (string.Equals(BanIdOf(record.Identity), banId, StringComparison.Ordinal) && live.Unban(record.Identity))
                {
                    return OperationResult<bool>.Success(true);
                }
            }

            return Fail(ModErrorCode.NotFound, "No ban has that id.");
        }

        public IReadOnlyList<InternalBanEntry> GetBans()
        {
            engine.RequireOwnerThread();
            if (live == null) return Array.Empty<InternalBanEntry>();
            var records = live.Bans;
            var entries = new InternalBanEntry[records.Count];
            for (var index = 0; index < entries.Length; index++)
            {
                entries[index] = new InternalBanEntry(BanIdOf(records[index].Identity), records[index].Name, records[index].BannedAt);
            }

            return entries;
        }

        /// <summary>
        /// Mutes or unmutes a player for everyone through the engine's voice moderation, the one set that drives the voice
        /// relay and the chat ingress alike. It follows the player's identity until hosting stops; unmuting also ends an
        /// automatic mute.
        /// </summary>
        public OperationResult<bool> SetServerMute(ParticipantId participant, bool muted)
        {
            engine.RequireOwnerThread();
            if (live?.Host == null || engine.Role != EngineRole.Host) return Fail(ModErrorCode.NotAuthoritative, "Only a listen host mutes players for everyone.");
            if (IsLocal(participant)) return Fail(ModErrorCode.InvalidArgument, "The host cannot mute its own player for everyone.");
            if (!engine.IsVoiceEnabled) return SetChatMute(participant, muted);
            if (engine.ParticipantIdentityOf(participant) == null || !engine.SetServerMute(participant, muted))
            {
                return Fail(ModErrorCode.NotFound, "No player in this session has that id.");
            }

            return OperationResult<bool>.Success(true);
        }

        /// <summary>
        /// Gets whether the host muted <paramref name="participant"/>, by hand or automatically for abusing voice chat: a host
        /// answers from its moderation, and a joined player from the host's voice roster.
        /// </summary>
        public bool IsServerMuted(ParticipantId participant)
        {
            engine.RequireOwnerThread();
            return participant.IsValid && (engine.IsVoiceEnabled ? engine.IsServerMuted(participant) : IsChatMuted(participant));
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            pendingJoin = null;
            var attached = runtime;
            if (attached != null) DetachRuntime(attached);
        }

        /// <summary>
        /// Names a ban without the key it bans: the first 128 bits of a labelled SHA-256 of the key, so the interface can
        /// lift a ban by id while the key itself stays in the provider.
        /// </summary>
        internal static string BanIdOf(string identity) =>
            WireText.ToHex(WireText.Sha256(Encoding.UTF8.GetBytes("TopiaForge ban id v1\n" + identity))).Substring(0, 32);

        private OperationResult<bool>? RequireLive()
        {
            if (disposed) return Fail(ModErrorCode.InvalidState, "The multiplayer provider has stopped.");
            if (live == null) return Fail(ModErrorCode.Unavailable, LiveOff);
            if (runtime == null) return Fail(ModErrorCode.Unavailable, NoRuntime);
            return null;
        }

        private bool IsLocal(ParticipantId participant) =>
            engine.LocalParticipantId.HasValue && engine.LocalParticipantId.Value.Equals(participant);

        /// <summary>
        /// Starts a join the player asked for once the identity it proves has loaded, or ends it if the identity cannot load.
        /// It also waits while the runtime identifies this game's packages off the main thread, so the join's own content
        /// check sees them identified, or unreadable.
        /// </summary>
        private void StartPendingJoin()
        {
            var pending = pendingJoin;
            if (pending == null || live == null) return;
            if (!live.IsReady)
            {
                if (live.IdentityFailed)
                {
                    pendingJoin = null;
                    pendingJoinFailure = InternalLiveFailure.IdentityUnavailable;
                    Warn("live.identity-failed", "The multiplayer identity could not be read or created, so the join ended.");
                }
                else
                {
                    live.PrepareAsync();
                }

                return;
            }

            if (content.IsLocalContentPending) return;
            pendingJoin = null;
            try
            {
                live.StartJoin(pending.Target, pending.Settings);
            }
            catch (InvalidOperationException)
            {
                // Hosting started in the meantime: the join cannot run, as if refused at once.
                pendingJoinFailure = InternalLiveFailure.Cancelled;
            }
        }

        /// <summary>Follows role, session and failures from frame to frame without allocating.</summary>
        private void Observe()
        {
            var role = Role;
            if (role != lastRole)
            {
                lastRole = role;
                SetRunInBackground(role != InternalSessionRole.Solo);
            }

            // A new session, or a new name from the runtime's settings, renames this player; the check reads two fields.
            var session = engine.CurrentSessionId;
            if (!session.Equals(lastSession) || NameChanged())
            {
                lastSession = session;
                ApplyDisplayName();
            }

            if (live != null)
            {
                ObserveFailures(live);
                ObserveNetwork(live);
            }
        }

        /// <summary>
        /// Says once, for the diagnostics bundle, that the multiplayer network runs over IPv4 alone, the first time a game
        /// socket opens that way. Where IPv6 exists it says nothing. It names no address.
        /// </summary>
        private void ObserveNetwork(LiveSessions sessions)
        {
            if (reportedIPv4Only || !sessions.NetworkRunsIPv4Only) return;
            reportedIPv4Only = true;
            diagnostics.Report(new InternalMultiplayerDiagnostic(InternalDiagnosticSeverity.Info, "live.network-ipv4-only",
                "The multiplayer network runs over IPv4 only, because this computer has no IPv6."));
        }

        private bool NameChanged()
        {
            var current = runtime;
            if (current == null || displayNameFailed) return false;
            try
            {
                return !ReferenceEquals(current.DisplayName, appliedName);
            }
            catch (Exception)
            {
                // ApplyDisplayName reports it, once.
                return true;
            }
        }

        private void ObserveFailures(LiveSessions sessions)
        {
            if (sessions.HostPhase != lastHostPhase)
            {
                lastHostPhase = sessions.HostPhase;
                if (lastHostPhase == HostPhase.Failed) Warn("live.host-failed", "Hosting stopped: " + sessions.HostFailure + ".");
            }

            var join = sessions.Join;
            if (!ReferenceEquals(join, observedJoin))
            {
                // A new join counts from its start, so one that already ended is still reported.
                observedJoin = join;
                observedJoinPhase = JoinPhase.Resolving;
            }

            if (join == null || join.Phase == observedJoinPhase) return;
            observedJoinPhase = join.Phase;

            // The failure's name only: a kick's detail is the host's own text.
            if (join.Phase == JoinPhase.Ended && join.Failure != LiveFailure.Left && join.Failure != LiveFailure.Cancelled)
            {
                Warn("live.join-ended", "The joined session ended: " + join.Failure + ".");
            }
        }

        /// <summary>
        /// Names this player after the runtime's display name whenever it hosts, solo play included. A joined player's name
        /// went to its host in the join request; it takes effect here again once the player is back in solo play.
        /// </summary>
        private void ApplyDisplayName()
        {
            var current = runtime;
            if (current == null) return;
            string name;
            try
            {
                name = current.DisplayName;
                displayNameFailed = false;
            }
            catch (Exception exception)
            {
                if (!displayNameFailed)
                {
                    diagnostics.Report(new InternalMultiplayerDiagnostic(InternalDiagnosticSeverity.Error, "runtime.display-name-failed", "The runtime's display name could not be read.", null, exception));
                }

                displayNameFailed = true;
                return;
            }

            appliedName = name;
            if (engine.Role != EngineRole.Host || engine.IsEnded) return;
            if (WireText.IsDisplayText(name, InternalJoinSettings.MaximumDisplayNameLength)) engine.SetLocalDisplayName(name);
        }

        private void SetRunInBackground(bool enabled)
        {
            var current = runtime;
            if (current == null) return;
            try
            {
                current.SetRunInBackground(enabled);
            }
            catch (Exception exception)
            {
                diagnostics.Report(new InternalMultiplayerDiagnostic(InternalDiagnosticSeverity.Error, "runtime.background-failed", "The runtime could not change whether the game runs in the background.", null, exception));
            }
        }

        private void Warn(string code, string message) =>
            diagnostics.Report(new InternalMultiplayerDiagnostic(InternalDiagnosticSeverity.Warning, code, message));

        private static OperationResult<bool> Fail(ModErrorCode code, string message) => OperationResult<bool>.Failure(code, message);

        private static ServerVisibility ToVisibility(InternalSessionVisibility visibility) =>
            visibility == InternalSessionVisibility.Public ? ServerVisibility.Public
            : visibility == InternalSessionVisibility.Invite ? ServerVisibility.Invite
            : ServerVisibility.Lan;

        private sealed class PendingJoin
        {
            internal PendingJoin(JoinTarget target, JoinSettings settings)
            {
                Target = target;
                Settings = settings;
            }

            internal JoinTarget Target { get; }

            internal JoinSettings Settings { get; }
        }
    }
}
