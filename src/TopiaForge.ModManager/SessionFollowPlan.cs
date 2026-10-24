using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>The stages a joined player's follow of its host's world runs, in order (WS3.8).</summary>
    internal enum SessionFollowStage
    {
        /// <summary>The session's scopes are made, each a world owner, as a joined player's are.</summary>
        Preparing = 0,

        /// <summary>The host's world loads, through the same provider's <c>LoadAsync</c> as on the host.</summary>
        LoadingWorld = 1,

        /// <summary>The host's world arrives: the network world's baseline is applied and its replicas built.</summary>
        Synchronizing = 2,

        /// <summary>This game's own player's participant controller starts.</summary>
        Participant = 3,

        /// <summary>The session runs.</summary>
        Running = 4
    }

    /// <summary>
    /// What a follow needs: the host's world as it announced it, which this game resolves against its own packages, and how
    /// to wait for the host's world to arrive. Its ids were checked as the runtime checks its own before it was made.
    /// </summary>
    internal sealed class SessionFollowPlan
    {
        internal SessionFollowPlan(string hostSessionId, LaunchRequest request, string gamemodeId, string worldId, string? worldFamilyId,
            string transition, string? baseScene, TransformState? baseSpawn, Func<CancellationToken, Task<OperationResult<bool>>> synchronize)
        {
            HostSessionId = LaunchContractValues.Token(hostSessionId, nameof(hostSessionId));
            Request = request ?? throw new ArgumentNullException(nameof(request));
            GamemodeId = LaunchContractValues.Identifier(gamemodeId, nameof(gamemodeId));
            WorldId = LaunchContractValues.Identifier(worldId, nameof(worldId));
            WorldFamilyId = LaunchContractValues.OptionalIdentifier(worldFamilyId, nameof(worldFamilyId));
            Transition = LaunchContractValues.Choice(transition, nameof(transition), ModTransitions.ByPrecedence);
            if (baseScene != null && Transition != ModTransitions.AdditiveArena)
                throw new ArgumentException("Only an additive arena is built over a level of its own.", nameof(baseScene));
            BaseScene = baseScene;
            BaseSpawn = baseSpawn;
            Synchronize = synchronize ?? throw new ArgumentNullException(nameof(synchronize));
        }

        /// <summary>Gets the host's world session id, which this game's session takes, so every consumer of the world session sees the host's.</summary>
        internal string HostSessionId { get; }

        /// <summary>Gets the launch the host made: its target, and the world and transition it chose, if it chose them.</summary>
        internal LaunchRequest Request { get; }

        internal string GamemodeId { get; }

        internal string WorldId { get; }

        internal string? WorldFamilyId { get; }

        internal string Transition { get; }

        /// <summary>Gets the level the host's additive arena is built over, which this game loads first; null for any other world.</summary>
        internal string? BaseScene { get; }

        /// <summary>Gets where the host's player stood as its arena was built, so this game builds the arena in the same place.</summary>
        internal TransformState? BaseSpawn { get; }

        /// <summary>Waits until the host's world has arrived here; the follow's cancellation ends the wait.</summary>
        internal Func<CancellationToken, Task<OperationResult<bool>>> Synchronize { get; }

        /// <summary>Gets whether this game's own resolution of <see cref="Request"/> starts what the host started.</summary>
        internal bool Matches(LaunchPlanDescriptor resolved) => resolved != null
            && Same(resolved.TargetId, Request.TargetId) && Same(resolved.GamemodeId, GamemodeId) && Same(resolved.WorldId, WorldId)
            && Same(resolved.WorldFamilyId, WorldFamilyId) && string.Equals(resolved.Transition, Transition, StringComparison.Ordinal);

        private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// How a follow ended. A failure names its kind, the stage it stopped at and its error code, which choose the words the
    /// player reads, and keeps the reason the runtime, a provider or a mod gave for the manager log alone.
    /// </summary>
    internal sealed class SessionFollowResult
    {
        private SessionFollowResult(bool succeeded, bool busy, FollowFailureKind failure, SessionFollowStage stage, ModErrorCode code, string reason)
        {
            Succeeded = succeeded;
            IsBusy = busy;
            Failure = failure;
            Stage = stage;
            Code = code;
            Reason = reason ?? string.Empty;
        }

        internal static SessionFollowResult Success { get; } = new SessionFollowResult(true, false, FollowFailureKind.None, SessionFollowStage.Running, ModErrorCode.None, string.Empty);

        internal bool Succeeded { get; }

        /// <summary>Gets whether this game was busy starting or stopping something else, so the follow did not begin and may be tried again.</summary>
        internal bool IsBusy { get; }

        /// <summary>Gets whether the follow was cancelled: the player left, the host moved on, or the game shut down.</summary>
        internal bool IsCancelled => !Succeeded && !IsBusy && Failure == FollowFailureKind.None;

        internal FollowFailureKind Failure { get; }

        internal SessionFollowStage Stage { get; }

        internal ModErrorCode Code { get; }

        /// <summary>Gets the reason as it was given, for the manager log only, never for the player's screen.</summary>
        internal string Reason { get; }

        internal static SessionFollowResult Busy(string reason) =>
            new SessionFollowResult(false, true, FollowFailureKind.None, SessionFollowStage.Preparing, ModErrorCode.Conflict, reason);

        internal static SessionFollowResult Cancelled(SessionFollowStage stage) =>
            new SessionFollowResult(false, false, FollowFailureKind.None, stage, ModErrorCode.Cancelled, "The follow was cancelled.");

        internal static SessionFollowResult Failed(FollowFailureKind failure, SessionFollowStage stage, ModErrorCode code, string reason) =>
            new SessionFollowResult(false, false, failure == FollowFailureKind.None ? FollowFailureKind.PrepareFailed : failure, stage,
                code == ModErrorCode.None ? ModErrorCode.External : code, reason);

        public override string ToString() => Succeeded ? "followed" : IsBusy ? "busy" : IsCancelled ? "cancelled at " + Stage : Failure + " at " + Stage + " (" + Code + ")";
    }

    /// <summary>
    /// The session follower, as the orchestrator sees it: whether this game follows a host now, and how a joined player's
    /// own request to go to the main menu leaves the session.
    /// </summary>
    internal interface ISessionFollowOwner
    {
        /// <summary>Gets whether this game is joined to a host and follows its world, so a local launch is refused.</summary>
        bool IsFollowing { get; }

        /// <summary>Leaves the joined session, because the player asked to go to the main menu from inside the host's world.</summary>
        void LeaveForMainMenu();
    }
}
