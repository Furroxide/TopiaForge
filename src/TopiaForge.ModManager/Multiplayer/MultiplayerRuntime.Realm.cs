using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// The realm: this process's <see cref="NetRealm"/>, the realm rules every request to go online or to leave passes
    /// through, the network class of each loaded mod, the spawns recorded for a later promotion, and the census seam the
    /// network world fills. See docs/internal/MultiplayerRuntimeIntegration.md, section 10.
    /// </summary>
    internal sealed partial class MultiplayerRuntime
    {
        internal const string ProviderMissing = "Multiplayer is unavailable: the multiplayer provider is not loaded.";
        internal const string LiveSwitchedOff = "Live multiplayer is switched off in this build.";
        internal const string LeaveGameFirst = "Leave the current game before joining another session.";
        internal const string StopHostingFirst = "Stop hosting before joining another session.";
        internal const string LeaveSessionFirst = "Leave the joined session before hosting.";
        internal const string AlreadyHosting = "This game is already open to other players.";
        internal const string AlreadyJoined = "This game is already in a joined session; leave it before joining another.";
        internal const string SessionBusy = "A game is starting or stopping; try again once it has settled.";

        private static readonly Func<SessionPhase> IdleSession = () => SessionPhase.Idle;

        private Func<SessionPhase> sessionPhaseSource = IdleSession;
        private InternalSessionRole realmRole;
        private bool censusFailureLogged;
        private bool phaseFailureLogged;
        private bool leaveFailureLogged;

        /// <summary>Gets this process's realm as of the last pump: offline in single player, or online as a host or a joined player.</summary>
        internal NetRealm Realm => RealmOf(role);

        /// <summary>
        /// Gets the spawns recorded at spawn time, in every realm and above all offline, so that opening the running session
        /// to other players later can baseline them.
        /// </summary>
        internal MultiplayerSpawnLedger Spawns { get; } = new MultiplayerSpawnLedger();

        /// <summary>
        /// Gets or sets where the realm rules read the local session's phase. ModRuntime points it at its session
        /// orchestrator; without one the session is idle.
        /// </summary>
        internal Func<SessionPhase> SessionPhaseSource
        {
            get => sessionPhaseSource;
            set => sessionPhaseSource = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>
        /// The census seam: the network world (WS3.6) keys the scene and baselines the recorded spawns whenever this process
        /// becomes a host. Until a slice fills it, promotions take no census and the records stay.
        /// </summary>
        internal IMultiplayerPromotionCensus? PromotionCensus { get; set; }

        /// <summary>Gets the names of the loaded standalone mods, sorted; while any is loaded, every session is refused.</summary>
        internal IReadOnlyList<string> StandaloneMods
        {
            get
            {
                List<string>? names = null;
                foreach (var package in Host.Inventory.Packages)
                {
                    if (package.NetworkClass != ModNetworkClass.Standalone) continue;
                    (names ??= new List<string>()).Add(package.DisplayName);
                }

                if (names == null) return Array.Empty<string>();
                names.Sort(StringComparer.Ordinal);
                return names.AsReadOnly();
            }
        }

        /// <summary>
        /// Gets the network class a manifest gives its mod: no multiplayer block is shared implicitly, and a mode that cannot
        /// be read is standalone. Each mod context's network profile starts from it.
        /// </summary>
        internal static ModNetworkClass NetworkClassOf(ModManifest manifest) => ModNetworkClasses.Classify(manifest);

        /// <summary>Gets a loaded mod's network class.</summary>
        internal bool TryGetNetworkClass(string modId, out ModNetworkClass networkClass)
        {
            if (modId == null) throw new ArgumentNullException(nameof(modId));
            foreach (var package in Host.Inventory.Packages)
            {
                if (!string.Equals(package.Manifest.Id, modId, StringComparison.OrdinalIgnoreCase)) continue;
                networkClass = package.NetworkClass;
                return true;
            }

            networkClass = ModNetworkClass.Standalone;
            return false;
        }

        /// <summary>
        /// Asks the realm rules whether this process may move to <paramref name="target"/> now, without acting. The interface
        /// asks before it offers a choice, so it can ask "Leave current game?" first, and asks every frame its views bind a
        /// choice, so it allocates nothing.
        /// </summary>
        internal NetRealmTransition EvaluateRealmChange(NetRealm target) =>
            NetRealms.EvaluateTransition(Realm, target, ReadSessionPhase(), HasStandaloneMod());

        /// <summary>
        /// Opens this game to other players. In single player with a world running, the running session is promoted in place
        /// ("Open to multiplayer"); from the menu, this game starts as a host. A joined player must leave first, a game that
        /// is starting or stopping must settle first, and a loaded standalone mod refuses it. Hosting starts at a later pump;
        /// the provider's status says how it went.
        /// </summary>
        internal MultiplayerRealmChange OpenToMultiplayer(InternalHostSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            var current = control;
            var transition = EvaluateRealmChange(NetRealm.ListenHost);
            var refused = Unavailable(current, transition) ?? Refuse(transition, NetRealm.ListenHost);
            return refused ?? new MultiplayerRealmChange(transition, current!.StartHosting(settings));
        }

        /// <summary>
        /// Joins another player's session. Joining replaces the local session, so it needs an idle one: while a game runs the
        /// runtime refuses with <see cref="NetRealmTransitionVerdict.RequiresLeave"/>, and the interface asks "Leave current
        /// game?" before it tries again from the menu. A host must stop hosting first, and a loaded standalone mod refuses it.
        /// </summary>
        internal MultiplayerRealmChange Join(IInternalJoinTarget target, InternalJoinSettings settings)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            var current = control;
            var transition = EvaluateRealmChange(NetRealm.Client);
            var refused = Unavailable(current, transition) ?? Refuse(transition, NetRealm.Client);
            return refused ?? new MultiplayerRealmChange(transition, current!.Join(target, settings));
        }

        /// <summary>
        /// Leaves whatever this process does online: a joined session, hosting, or either on its way. Leaving is never
        /// refused, whatever the realm or the local session's phase; a host plays on alone in the same session.
        /// </summary>
        internal MultiplayerRealmChange Leave()
        {
            var transition = NetRealms.EvaluateTransition(Realm, NetRealm.Offline, ReadSessionPhase(), false);
            var current = control;
            if (current == null) return new MultiplayerRealmChange(transition, OperationResult<bool>.Success(true));
            Exception? failure = null;
            try
            {
                // Stopping hosting only when it runs or starts keeps an earlier hosting failure on record for the interface.
                var hostPhase = current.GetStatus().HostPhase;
                if (hostPhase == InternalHostPhase.Hosting || hostPhase == InternalHostPhase.Preparing) current.StopHosting();
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            try
            {
                current.Leave();
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }

            if (failure == null)
            {
                leaveFailureLogged = false;
                return new MultiplayerRealmChange(transition, OperationResult<bool>.Success(true));
            }

            if (!leaveFailureLogged) log.Error("Multiplayer: leaving failed in the provider: " + Describe(failure));
            leaveFailureLogged = true;
            return new MultiplayerRealmChange(transition, OperationResult<bool>.Failure(ModErrorCode.External,
                "The multiplayer provider failed while leaving (" + failure.GetType().Name + ")."));
        }

        /// <summary>Follows the role the last pump read; a move from single player to hosting takes the census. It allocates nothing unless the realm changed.</summary>
        private void FollowRealm()
        {
            var next = role;
            if (next == realmRole) return;
            var previous = realmRole;
            realmRole = next;
            Intents.FollowRealm(RealmOf(next));
            log.Info("Multiplayer: this game is now " + Describe(RealmOf(next)) + " (it was " + Describe(RealmOf(previous)) + ").");

            // Observers end what the old realm held, and a joined player starts, before a new host takes its census.
            for (var index = 0; index < observers.Count; index++) Tell(observers[index], ObserverEvent.RealmChanged, null, RealmOf(previous), RealmOf(next));
            if (previous == InternalSessionRole.Solo && next == InternalSessionRole.ListenHost) TakeCensus();
        }

        private void TakeCensus()
        {
            var census = PromotionCensus;
            if (census == null) return;
            var phase = ReadSessionPhase();
            var effect = phase == SessionPhase.Running ? NetRealmTransitionEffect.PromoteInPlace : NetRealmTransitionEffect.StartAsHost;
            try
            {
                census.TakeCensus(new MultiplayerPromotion(effect, phase, Spawns.Live()));
                censusFailureLogged = false;
            }
            catch (Exception exception)
            {
                if (!censusFailureLogged) log.Error("Multiplayer: the census at promotion failed: " + Describe(exception));
                censusFailureLogged = true;
            }
        }

        /// <summary>Gets whether a loaded mod is standalone, by index: enumerating the list through its interface would allocate.</summary>
        private bool HasStandaloneMod()
        {
            var packages = Host.Inventory.Packages;
            for (var index = 0; index < packages.Count; index++)
            {
                if (packages[index].NetworkClass == ModNetworkClass.Standalone) return true;
            }

            return false;
        }

        /// <summary>Reads the local session's phase; one that cannot be read counts as busy, so nothing goes online on a guess.</summary>
        private SessionPhase ReadSessionPhase()
        {
            SessionPhase phase;
            try
            {
                phase = sessionPhaseSource();
            }
            catch (Exception exception)
            {
                if (!phaseFailureLogged) log.Error("Multiplayer: the local session's phase could not be read: " + Describe(exception));
                phaseFailureLogged = true;
                return SessionPhase.Stopping;
            }

            phaseFailureLogged = false;
            return phase >= SessionPhase.Idle && phase <= SessionPhase.Stopping ? phase : SessionPhase.Stopping;
        }

        private static MultiplayerRealmChange? Unavailable(IInternalMultiplayerControl? current, NetRealmTransition transition)
        {
            if (current == null) return new MultiplayerRealmChange(transition, OperationResult<bool>.Failure(ModErrorCode.Unavailable, ProviderMissing));
            return current.IsLiveAvailable
                ? null
                : new MultiplayerRealmChange(transition, OperationResult<bool>.Failure(ModErrorCode.Unavailable, LiveSwitchedOff));
        }

        private MultiplayerRealmChange? Refuse(NetRealmTransition transition, NetRealm target)
        {
            string message;
            var code = ModErrorCode.InvalidState;
            switch (transition.Verdict)
            {
                case NetRealmTransitionVerdict.Allowed:
                    return null;
                case NetRealmTransitionVerdict.Unchanged:
                    message = target == NetRealm.ListenHost ? AlreadyHosting : AlreadyJoined;
                    break;
                case NetRealmTransitionVerdict.RequiresLeave:
                    message = Realm == NetRealm.Offline ? LeaveGameFirst : Realm == NetRealm.ListenHost ? StopHostingFirst : LeaveSessionFirst;
                    break;
                case NetRealmTransitionVerdict.Busy:
                    code = ModErrorCode.Conflict;
                    message = SessionBusy;
                    break;
                default:
                    message = StandaloneRefusal(StandaloneMods, target == NetRealm.ListenHost ? "host" : "join");
                    break;
            }

            return new MultiplayerRealmChange(transition, OperationResult<bool>.Failure(code, message));
        }

        /// <summary>
        /// Names the standalone mods that refuse a session, three at most: "Solo refuses multiplayer; disable it to host.",
        /// or "Solo, Story, Tutor and 2 more refuse multiplayer; disable them to join.".
        /// </summary>
        internal static string StandaloneRefusal(IReadOnlyList<string> names, string verb)
        {
            if (names == null) throw new ArgumentNullException(nameof(names));
            if (names.Count == 0) return "A standalone mod refuses multiplayer; disable it to " + verb + ".";
            if (names.Count == 1) return names[0] + " refuses multiplayer; disable it to " + verb + ".";
            var list = names.Count == 2 ? names[0] + " and " + names[1]
                : names.Count == 3 ? names[0] + ", " + names[1] + " and " + names[2]
                : names[0] + ", " + names[1] + ", " + names[2] + " and " + (names.Count - 3) + " more";
            return list + " refuse multiplayer; disable them to " + verb + ".";
        }

        private static NetRealm RealmOf(InternalSessionRole value) =>
            value == InternalSessionRole.ListenHost ? NetRealm.ListenHost
            : value == InternalSessionRole.Client ? NetRealm.Client
            : NetRealm.Offline;

        private static string Describe(NetRealm realm) =>
            realm == NetRealm.ListenHost ? "a listen host"
            : realm == NetRealm.Client ? "joined to a host"
            : "offline";
    }
}
