using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>One admission owner shared by every native route; ownership survives caller cancellation.</summary>
    internal sealed class SceneCoordinator : INativeTransitionExecutor
    {
        private readonly object gate = new object();
        private Action<string> logInfo;
        private ISceneTransitionAuthorityPolicy authorityPolicy;
        private readonly HashSet<string> revokedOwnership = new HashSet<string>(StringComparer.Ordinal);
        private Func<bool> sessionAdmissionBusy = () => false;
        internal readonly IHostDispatcher? Dispatcher;
        private NativeTransitionReservation? active;

        public SceneCoordinator(Action<string>? logInfo = null,
            ISceneTransitionAuthorityPolicy? authorityPolicy = null, IHostDispatcher? dispatcher = null)
        {
            this.logInfo = logInfo ?? (_ => { });
            this.authorityPolicy = authorityPolicy ?? StandaloneSceneTransitionAuthorityPolicy.Instance;
            Dispatcher = dispatcher;
        }

        public bool IsSceneBusy { get { lock (gate) return active != null; } }
        public IReadOnlyList<SceneClaimInfo> ActiveClaims
        {
            get { lock (gate) return active == null ? Array.Empty<SceneClaimInfo>() : new[] { active.Info }; }
        }

        public OperationResult<INativeTransitionReservation> TryReserve(NativeTransitionOwner owner, string operationId)
        {
            var request = new SceneTransitionRequest(owner.PackageId, operationId, SceneTransitionPriority.UserInitiated);
            return Reserve(owner, request, lifecycle: true);
        }

        internal OperationResult<INativeTransitionReservation> Reserve(NativeTransitionOwner owner, SceneTransitionRequest request, bool lifecycle = false)
        {
            AssertCurrent();
            var refused = CheckAdmission(owner, lifecycle);
            if (refused != null) return refused;
            var denied = CheckAuthority(request);
            if (denied != null) return OperationResult<INativeTransitionReservation>.Failure(ModErrorCode.NotAuthoritative, denied);
            // The authority provider may synchronously close admission or revoke this owner.
            refused = CheckAdmission(owner, lifecycle);
            if (refused != null) return refused;
            lock (gate)
            {
                if (active != null)
                {
                    var message = "'" + active.Info.OwnerModId + "' holds native transition admission; competing requests are Busy.";
                    TryLog(message);
                    return OperationResult<INativeTransitionReservation>.Failure(ModErrorCode.Conflict, message);
                }
                active = new NativeTransitionReservation(this, owner, request);
                return OperationResult<INativeTransitionReservation>.Success(active);
            }
        }

        private OperationResult<INativeTransitionReservation>? CheckAdmission(NativeTransitionOwner owner, bool lifecycle)
        {
            foreach (var prefix in revokedOwnership)
                if (owner.OwnershipId == prefix || owner.OwnershipId.StartsWith(prefix + ":", StringComparison.Ordinal))
                    return OperationResult<INativeTransitionReservation>.Failure(ModErrorCode.InvalidState, "The native transition owner was revoked.");
            if (!lifecycle && sessionAdmissionBusy())
                return OperationResult<INativeTransitionReservation>.Failure(ModErrorCode.Conflict, "The session lifecycle is Busy.");
            return null;
        }

        public void SetSessionAdmissionGate(Func<bool> isBusy)
        {
            AssertCurrent();
            sessionAdmissionBusy = isBusy ?? throw new ArgumentNullException(nameof(isBusy));
        }

        internal void UpdateLogSink(Action<string> sink) { AssertCurrent(); logInfo = sink; }

        internal void UpdateAuthorityPolicy(ISceneTransitionAuthorityPolicy policy)
        {
            AssertCurrent();
            authorityPolicy = policy ?? throw new ArgumentNullException(nameof(policy));
        }

        public Task<NativeDrainResult> WaitForIdleAsync()
        {
            lock (gate) return active?.DrainTask ?? Task.FromResult(NativeDrainResult.Drained);
        }

        public SceneTransitionDecision RequestTransition(SceneTransitionRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var result = Reserve(new NativeTransitionOwner(request.OwnerModId, request.OwnerModId), request);
            return result.TryGetValue(out var reservation)
                ? SceneTransitionDecision.Approve(reservation, "Approved for '" + request.SceneName + "'.")
                : SceneTransitionDecision.Refuse(result.ErrorCode, result.ErrorMessage);
        }

        internal string? CheckAuthority(SceneTransitionRequest request)
        {
            try
            {
                var decision = authorityPolicy.Evaluate(request);
                return decision.Allowed ? null : string.IsNullOrWhiteSpace(decision.Message)
                    ? "The process is not authoritative for this native transition." : decision.Message;
            }
            catch (Exception error) { return "Scene authority could not be established: " + error.Message; }
        }

        public void ReleaseOwner(string ownerModId)
        {
            NativeTransitionReservation? held;
            lock (gate) held = active;
            if (held != null && held.OwnsPackage(ownerModId))
                held.RevokeOwner();
        }

        public void RevokeOwnership(string ownershipId)
        {
            AssertCurrent();
            revokedOwnership.Add(ownershipId);
            NativeTransitionReservation? held;
            lock (gate) held = active;
            if (held != null && (held.Owner.OwnershipId == ownershipId
                || held.Owner.OwnershipId.StartsWith(ownershipId + ":", StringComparison.Ordinal)))
                held.RevokeOwner();
        }

        internal void Release(NativeTransitionReservation reservation)
        {
            AssertCurrent();
            lock (gate) if (ReferenceEquals(active, reservation)) active = null;
        }

        public void NotifySceneArrived(SceneSnapshot scene)
        {
            AssertCurrent();
            NativeTransitionReservation? held;
            lock (gate) held = active;
            held?.ObserveScene(scene);
        }

        public void CheckTimeout(DateTime nowUtc, TimeSpan timeout)
        {
            AssertCurrent();
            NativeTransitionReservation? held;
            lock (gate) held = active;
            held?.CheckTimeout(nowUtc, timeout);
        }

        internal void Post(Action action)
        {
            if (Dispatcher != null && !Dispatcher.IsCurrent) Dispatcher.Post(action);
            else action();
        }

        internal void AssertCurrent()
        {
            if (Dispatcher != null && !Dispatcher.IsCurrent)
                throw new InvalidOperationException("Native transition admission requires the host thread.");
        }

        private void TryLog(string message)
        {
            try { logInfo(message); } catch { }
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
