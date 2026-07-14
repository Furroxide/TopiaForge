using System;
using System.Collections.Generic;

namespace Robotopia.Mods
{
    /// <summary>
    /// Arbitrates scene transitions between mods so two mods cannot silently race single-mode scene loads
    /// (last-write-wins stomps the loser's world with no diagnosis). Published by the mod manager itself, so
    /// it is always available via <c>context.GetService&lt;ISceneCoordinator&gt;()</c>.
    /// </summary>
    /// <remarks>
    /// The contract: any mod that loads a scene calls <see cref="RequestTransition"/> first and only loads on
    /// approval. Automatic triggers (startup hooks, timers, file watchers) use
    /// <see cref="SceneTransitionPriority.Automatic"/> and are refused while any claim is active — they must
    /// degrade gracefully (defer, skip, or wait for the scene to arrive by other means). Direct user actions
    /// use <see cref="SceneTransitionPriority.UserInitiated"/> and are always approved; the superseded claim
    /// holder finds out through its own scene handling (e.g. <c>Robotopia.Worlds</c> ends its session with
    /// <see cref="WorldSessionEndReason.SceneReplaced"/> when an approved foreign transition lands).
    /// Dispose the returned claim when the transition resolves (the scene arrived or the load was abandoned);
    /// a session-scoped claim is held for the session's lifetime and disposed when the session ends.
    /// </remarks>
    public interface ISceneCoordinator
    {
        /// <summary>True while any claim is active (automatic transitions would be refused).</summary>
        bool IsSceneBusy { get; }

        /// <summary>The active claims, for diagnostics and for detecting who owns an arriving scene.</summary>
        IReadOnlyList<SceneClaimInfo> ActiveClaims { get; }

        /// <summary>
        /// Asks to transition to a scene. On approval, the decision carries a claim that must be disposed when
        /// the transition resolves; on refusal, do not load the scene (the message names the blocking owner).
        /// </summary>
        SceneTransitionDecision RequestTransition(SceneTransitionRequest request);

        /// <summary>Releases every claim held by a mod (safety valve; the manager calls this on mod unload).</summary>
        void ReleaseOwner(string ownerModId);
    }

    /// <summary>How a scene transition was triggered; decides who yields when transitions collide.</summary>
    public enum SceneTransitionPriority
    {
        /// <summary>
        /// Triggered without a direct user action (auto-load on start, timers, file watchers). Refused while
        /// any claim is active.
        /// </summary>
        Automatic,

        /// <summary>A direct user action (a menu/overlay button). Always approved; supersedes active claims.</summary>
        UserInitiated
    }

    /// <summary>Parameters for <see cref="ISceneCoordinator.RequestTransition"/>.</summary>
    public sealed class SceneTransitionRequest
    {
        public SceneTransitionRequest(string ownerModId, string sceneName, SceneTransitionPriority priority, string reason = "")
        {
            if (string.IsNullOrWhiteSpace(ownerModId))
            {
                throw new ArgumentException("Owner mod id is required.", nameof(ownerModId));
            }

            if (!Enum.IsDefined(typeof(SceneTransitionPriority), priority))
            {
                throw new ArgumentOutOfRangeException(nameof(priority));
            }

            OwnerModId = ownerModId;
            SceneName = sceneName ?? string.Empty;
            Priority = priority;
            Reason = reason ?? string.Empty;
        }

        /// <summary>The requesting mod's id (use <c>context.ModId</c>).</summary>
        public string OwnerModId { get; }

        /// <summary>The scene the requester intends to load (may be empty when not known up front).</summary>
        public string SceneName { get; }

        public SceneTransitionPriority Priority { get; }

        /// <summary>Short human-readable purpose, surfaced in logs and refusal messages.</summary>
        public string Reason { get; }
    }

    /// <summary>A live claim as surfaced by <see cref="ISceneCoordinator.ActiveClaims"/>.</summary>
    public sealed class SceneClaimInfo
    {
        public SceneClaimInfo(string ownerModId, string sceneName, SceneTransitionPriority priority, string reason, DateTime acquiredAtUtc)
        {
            if (string.IsNullOrWhiteSpace(ownerModId))
            {
                throw new ArgumentException("Owner mod id is required.", nameof(ownerModId));
            }

            if (!Enum.IsDefined(typeof(SceneTransitionPriority), priority))
            {
                throw new ArgumentOutOfRangeException(nameof(priority));
            }

            OwnerModId = ownerModId;
            SceneName = sceneName ?? string.Empty;
            Priority = priority;
            Reason = reason ?? string.Empty;
            AcquiredAtUtc = acquiredAtUtc;
        }

        public string OwnerModId { get; }
        public string SceneName { get; }
        public SceneTransitionPriority Priority { get; }
        public string Reason { get; }
        public DateTime AcquiredAtUtc { get; }
    }

    /// <summary>Outcome of <see cref="ISceneCoordinator.RequestTransition"/>.</summary>
    public sealed class SceneTransitionDecision
    {
        private SceneTransitionDecision(bool approved, IDisposable? claim, string message)
        {
            Approved = approved;
            Claim = claim;
            Message = message ?? string.Empty;
        }

        public bool Approved { get; }

        /// <summary>The claim to dispose when the transition resolves; null when refused.</summary>
        public IDisposable? Claim { get; }

        /// <summary>Human-readable detail; on refusal it names the blocking owner and its reason.</summary>
        public string Message { get; }

        public static SceneTransitionDecision Approve(IDisposable claim, string message)
        {
            return new SceneTransitionDecision(true, claim ?? throw new ArgumentNullException(nameof(claim)), message);
        }

        public static SceneTransitionDecision Refuse(string message)
        {
            return new SceneTransitionDecision(false, null, message);
        }
    }
}
