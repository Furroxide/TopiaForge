using System;
using System.Collections.Generic;
using System.Threading;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>Internal owner-aware arbitration retained as a loader safety net behind owner-bound scene facades.</summary>
    internal sealed class SceneCoordinator
    {
        private readonly object gate = new object();
        private readonly List<Claim> claims = new List<Claim>();
        private readonly Action<string> logInfo;
        private readonly ISceneTransitionAuthorityPolicy authorityPolicy;

        public SceneCoordinator(
            Action<string>? logInfo = null,
            ISceneTransitionAuthorityPolicy? authorityPolicy = null)
        {
            this.logInfo = logInfo ?? (_ => { });
            this.authorityPolicy = authorityPolicy ?? StandaloneSceneTransitionAuthorityPolicy.Instance;
        }

        public bool IsSceneBusy
        {
            get
            {
                lock (gate)
                {
                    return claims.Count > 0;
                }
            }
        }

        public IReadOnlyList<SceneClaimInfo> ActiveClaims
        {
            get
            {
                lock (gate)
                {
                    var view = new List<SceneClaimInfo>(claims.Count);
                    foreach (var claim in claims)
                    {
                        view.Add(claim.Info);
                    }

                    return view;
                }
            }
        }

        public SceneTransitionDecision RequestTransition(SceneTransitionRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var authority = authorityPolicy.Evaluate(request);
            if (!authority.Allowed)
            {
                var message = string.IsNullOrWhiteSpace(authority.Message)
                    ? "The current process is not authoritative for scene transitions."
                    : authority.Message;
                TryLog("Scene transition refused for '" + request.OwnerModId + "' -> '"
                    + request.SceneName + "': " + message);
                return SceneTransitionDecision.Refuse(ModErrorCode.NotAuthoritative, message);
            }

            SceneTransitionDecision decision;
            string? logMessage = null;
            lock (gate)
            {
                if (request.Priority == SceneTransitionPriority.Automatic && claims.Count > 0)
                {
                    var blocker = claims[claims.Count - 1].Info;
                    var message = "'" + blocker.OwnerModId + "' holds the scene"
                        + (string.IsNullOrEmpty(blocker.Reason) ? "" : " (" + blocker.Reason + ")")
                        + "; automatic transitions must yield.";
                    logMessage = "Scene transition refused for '" + request.OwnerModId + "' -> '"
                        + request.SceneName + "': " + message;
                    decision = SceneTransitionDecision.Refuse(ModErrorCode.Conflict, message);
                }
                else
                {
                    var claim = new Claim(this, new SceneClaimInfo(
                        request.OwnerModId,
                        request.SceneName,
                        request.Priority,
                        request.Reason,
                        DateTime.UtcNow));
                    if (claims.Count > 0)
                    {
                        logMessage = "Scene transition approved for '" + request.OwnerModId + "' -> '"
                            + request.SceneName + "' superseding " + claims.Count
                            + " active claim(s) (first: '" + claims[0].Info.OwnerModId + "').";
                    }

                    claims.Add(claim);
                    decision = SceneTransitionDecision.Approve(claim, "Approved for '" + request.SceneName + "'.");
                }
            }

            if (logMessage != null)
            {
                TryLog(logMessage);
            }

            return decision;
        }

        public void ReleaseOwner(string ownerModId)
        {
            if (string.IsNullOrWhiteSpace(ownerModId))
            {
                return;
            }

            lock (gate)
            {
                claims.RemoveAll(claim => string.Equals(
                    claim.Info.OwnerModId,
                    ownerModId,
                    StringComparison.OrdinalIgnoreCase));
            }
        }

        private void Release(Claim claim)
        {
            lock (gate)
            {
                claims.Remove(claim);
            }
        }

        private void TryLog(string message)
        {
            try
            {
                logInfo(message);
            }
            catch
            {
                // Correctness state must not depend on diagnostics.
            }
        }

        private sealed class Claim : IDisposable
        {
            private SceneCoordinator? owner;

            public Claim(SceneCoordinator owner, SceneClaimInfo info)
            {
                this.owner = owner;
                Info = info;
            }

            public SceneClaimInfo Info { get; }

            public void Dispose()
            {
                Interlocked.Exchange(ref owner, null)?.Release(this);
            }
        }
    }

    internal enum SceneTransitionPriority
    {
        Automatic = 0,
        UserInitiated = 1
    }

    internal sealed class SceneTransitionRequest
    {
        public SceneTransitionRequest(
            string ownerModId,
            string sceneName,
            SceneTransitionPriority priority,
            string reason = "")
        {
            OwnerModId = ownerModId;
            SceneName = sceneName;
            Priority = priority;
            Reason = reason;
        }

        public string OwnerModId { get; }
        public string SceneName { get; }
        public SceneTransitionPriority Priority { get; }
        public string Reason { get; }
    }

    internal sealed class SceneClaimInfo
    {
        public SceneClaimInfo(
            string ownerModId,
            string sceneName,
            SceneTransitionPriority priority,
            string reason,
            DateTime acquiredAtUtc)
        {
            OwnerModId = ownerModId;
            SceneName = sceneName;
            Priority = priority;
            Reason = reason;
            AcquiredAtUtc = acquiredAtUtc;
        }

        public string OwnerModId { get; }
        public string SceneName { get; }
        public SceneTransitionPriority Priority { get; }
        public string Reason { get; }
        public DateTime AcquiredAtUtc { get; }
    }

    internal sealed class SceneTransitionDecision
    {
        private SceneTransitionDecision(
            bool approved,
            IDisposable? claim,
            ModErrorCode errorCode,
            string message)
        {
            Approved = approved;
            Claim = claim;
            ErrorCode = errorCode;
            Message = message;
        }

        public bool Approved { get; }
        public IDisposable? Claim { get; }
        public ModErrorCode ErrorCode { get; }
        public string Message { get; }

        public static SceneTransitionDecision Approve(IDisposable claim, string message)
        {
            return new SceneTransitionDecision(true, claim, ModErrorCode.None, message);
        }

        public static SceneTransitionDecision Refuse(ModErrorCode errorCode, string message)
        {
            if (errorCode == ModErrorCode.None)
            {
                throw new ArgumentOutOfRangeException(nameof(errorCode));
            }

            return new SceneTransitionDecision(false, null, errorCode, message);
        }
    }

    /// <summary>
    /// Future networking providers can replace this policy to deny client-side world mutations. The V1 host is
    /// standalone, so all owner-bound requests are authorized before ordinary coordinator arbitration.
    /// </summary>
    internal interface ISceneTransitionAuthorityPolicy
    {
        SceneTransitionAuthorityDecision Evaluate(SceneTransitionRequest request);
    }

    internal sealed class StandaloneSceneTransitionAuthorityPolicy : ISceneTransitionAuthorityPolicy
    {
        public static readonly StandaloneSceneTransitionAuthorityPolicy Instance =
            new StandaloneSceneTransitionAuthorityPolicy();

        private StandaloneSceneTransitionAuthorityPolicy()
        {
        }

        public SceneTransitionAuthorityDecision Evaluate(SceneTransitionRequest request) =>
            SceneTransitionAuthorityDecision.Allow();
    }

    internal readonly struct SceneTransitionAuthorityDecision
    {
        private SceneTransitionAuthorityDecision(bool allowed, string message)
        {
            Allowed = allowed;
            Message = message;
        }

        public bool Allowed { get; }
        public string Message { get; }

        public static SceneTransitionAuthorityDecision Allow() =>
            new SceneTransitionAuthorityDecision(true, string.Empty);

        public static SceneTransitionAuthorityDecision Deny(string message) =>
            new SceneTransitionAuthorityDecision(false, message ?? string.Empty);
    }
}
