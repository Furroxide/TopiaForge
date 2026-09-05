using System;

namespace TopiaForge.Worlds
{
    /// <summary>
    /// Thread-safe, Unity-free lifecycle for one provisional scene dispatch. Async loader continuations may report
    /// failure off the main thread; the Worlds update loop consumes it and performs all Unity/session cleanup.
    /// Generation tokens prevent a late fault from an old load from ending a newer session.
    /// </summary>
    internal sealed class SceneTransitionTracker
    {
        private readonly object gate = new object();
        private int generation;
        private bool pending;
        private bool quarantined;
        private bool terminalFailurePending;
        private float startedAt;
        private string expectedScene = string.Empty;
        private string failure = string.Empty;

        public bool BlocksAdmission
        {
            get
            {
                lock (gate)
                {
                    return pending || quarantined || terminalFailurePending;
                }
            }
        }

        public bool IsQuarantined
        {
            get
            {
                lock (gate)
                {
                    return quarantined;
                }
            }
        }

        public int Begin(float now, string expectedSceneName)
        {
            if (string.IsNullOrWhiteSpace(expectedSceneName))
            {
                throw new ArgumentException("An expected scene name is required.", nameof(expectedSceneName));
            }

            lock (gate)
            {
                if (pending || quarantined || terminalFailurePending)
                {
                    throw new InvalidOperationException(
                        "A previous scene dispatch must be resolved before another can begin.");
                }

                generation = generation == int.MaxValue ? 1 : generation + 1;
                pending = true;
                startedAt = now;
                expectedScene = expectedSceneName;
                failure = string.Empty;
                return generation;
            }
        }

        public void ReportFailure(int token, string message)
        {
            lock (gate)
            {
                if ((!pending && !quarantined) || token != generation)
                {
                    return;
                }

                // A reflected Task/UniTask failure ends local session bookkeeping, but does not prove native
                // work drained. The process-owned executor retains admission and late-arrival tracking
                // independently while the Unity-thread update performs this session cleanup.
                pending = false;
                quarantined = false;
                terminalFailurePending = true;
                startedAt = 0f;
                expectedScene = string.Empty;
                failure = string.IsNullOrWhiteSpace(message)
                    ? "The scene loader failed after dispatch."
                    : message;
            }
        }

        public void Cancel(int token)
        {
            lock (gate)
            {
                if (pending && token == generation)
                {
                    ClearToIdle();
                }
            }
        }

        /// <summary>
        /// Records a single-scene arrival. Only the dispatch's expected target resolves or retires it; an
        /// unrelated/menu scene preserves quarantine because the uncancelled target may still arrive later.
        /// </summary>
        public bool ResolveSceneArrival(string sceneName)
        {
            lock (gate)
            {
                if ((!pending && !quarantined) || terminalFailurePending)
                {
                    return false;
                }

                if (!string.Equals(sceneName, expectedScene, StringComparison.OrdinalIgnoreCase))
                {
                    // An unrelated/menu scene may supersede the current session, but it does not prove the
                    // uncancelled dispatch cannot still arrive. Preserve its expected target in quarantine.
                    if (pending)
                    {
                        Quarantine();
                    }
                    return false;
                }

                ClearToIdle();
                return true;
            }
        }

        /// <summary>
        /// Ends the owning session without pretending its asynchronous scene operation was canceled. Until that
        /// operation produces a scene arrival, admission remains quarantined so a late arrival cannot be mistaken
        /// for a retry's completion.
        /// </summary>
        public void Abandon()
        {
            lock (gate)
            {
                if (pending)
                {
                    Quarantine();
                }
            }
        }

        public bool IsInFlight(float now, float timeoutSeconds)
        {
            lock (gate)
            {
                return quarantined
                    || (pending
                        && failure.Length == 0
                        && now - startedAt < timeoutSeconds);
            }
        }

        public string? ConsumeFailure(float now, float timeoutSeconds)
        {
            lock (gate)
            {
                if (terminalFailurePending)
                {
                    var terminalFailure = failure;
                    ClearToIdle();
                    return terminalFailure;
                }

                if (!pending)
                {
                    return null;
                }

                string? result = null;
                if (failure.Length > 0)
                {
                    result = failure;
                }
                else if (now - startedAt >= timeoutSeconds)
                {
                    result = "The scene load did not complete within " + timeoutSeconds + " seconds.";
                }

                if (result != null)
                {
                    // The scene API offers no cancellation handle and sceneLoaded has no dispatch token. Keep
                    // admission closed until a late arrival retires this dispatch; otherwise that arrival could
                    // resolve a retry, especially when both loads target UgcPlay.
                    Quarantine();
                }

                return result;
            }
        }

        private void Quarantine()
        {
            pending = false;
            quarantined = true;
            terminalFailurePending = false;
            startedAt = 0f;
            failure = string.Empty;
        }

        private void ClearToIdle()
        {
            pending = false;
            quarantined = false;
            terminalFailurePending = false;
            startedAt = 0f;
            expectedScene = string.Empty;
            failure = string.Empty;
        }
    }
}
