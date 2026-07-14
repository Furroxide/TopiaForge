using System;
using System.Collections.Generic;
using System.Threading;
using Robotopia.Mods;

namespace Robotopia.ModManager
{
    /// <summary>
    /// The manager-owned <see cref="ISceneCoordinator"/>: pure claim bookkeeping, no Unity types (the file is
    /// also compiled into the net8.0 test assembly). Automatic-priority requests are refused while any claim
    /// is active; user-initiated requests are always approved and simply stack — the superseded claim holder
    /// learns about the takeover through its own scene handling (e.g. Robotopia.Worlds ends its session with
    /// <see cref="WorldSessionEndReason.SceneReplaced"/> when the foreign scene arrives).
    /// </summary>
    public sealed class SceneCoordinator : ISceneCoordinator
    {
        private readonly object gate = new object();
        private readonly List<Claim> claims = new List<Claim>();
        private readonly Action<string> logInfo;

        public SceneCoordinator(Action<string>? logInfo = null)
        {
            this.logInfo = logInfo ?? (_ => { });
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
                    decision = SceneTransitionDecision.Refuse(message);
                }
                else
                {
                    var claim = new Claim(this, new SceneClaimInfo(
                        request.OwnerModId, request.SceneName, request.Priority, request.Reason, DateTime.UtcNow));
                    if (claims.Count > 0)
                    {
                        // A user-initiated takeover: allowed, but say so — this is the trace that explains a
                        // SceneReplaced session end.
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
                claims.RemoveAll(claim =>
                    string.Equals(claim.Info.OwnerModId, ownerModId, StringComparison.OrdinalIgnoreCase));
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
                // Arbitration is correctness state; diagnostics are best-effort and never alter a decision.
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
                var coordinator = Interlocked.Exchange(ref owner, null);
                coordinator?.Release(this);
            }
        }
    }
}
