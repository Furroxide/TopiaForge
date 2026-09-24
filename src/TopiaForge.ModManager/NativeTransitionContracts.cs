using System;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed class NativeTransitionOwner
    {
        public NativeTransitionOwner(string packageId, string ownershipId, string? sessionId = null)
        {
            PackageId = string.IsNullOrWhiteSpace(packageId) ? throw new ArgumentException("An owner package is required.") : packageId;
            OwnershipId = string.IsNullOrWhiteSpace(ownershipId) ? throw new ArgumentException("A unique ownership id is required.") : ownershipId;
            SessionId = sessionId;
        }
        public string PackageId { get; }
        public string OwnershipId { get; }
        public string? SessionId { get; }
    }

    internal interface INativeTransitionExecutor
    {
        bool IsSceneBusy { get; }
        void SetSessionAdmissionGate(Func<bool> isBusy);
        Task<NativeDrainResult> WaitForIdleAsync();
        OperationResult<INativeTransitionReservation> TryReserve(NativeTransitionOwner owner, string operationId);
    }

    internal interface INativeTransitionReservation : IDisposable
    {
        INativeTransitionGrant BorrowFor(string packageId, string sessionId);
        Task<NativeDrainResult> CloseAsync();
    }

    internal interface INativeTransitionGrant : IDisposable
    {
        string SessionId { get; }
        IInternalSceneTransitionService SceneTransitions { get; }
    }

    internal sealed class NativeDrainResult
    {
        public bool Completed => true;
        public static NativeDrainResult Drained { get; } = new NativeDrainResult();
        private NativeDrainResult() { }
    }

    /// <summary>A scope-local access slot. Only a currently installed, unrevoked grant can join admission.</summary>
    internal sealed class NativeTransitionAccessSlot
    {
        private readonly Func<bool> isAlive;
        private INativeTransitionGrant? current;
        public NativeTransitionAccessSlot(string ownershipId, string sessionId, Func<bool> isAlive)
        {
            OwnershipId = ownershipId ?? throw new ArgumentNullException(nameof(ownershipId));
            SessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            this.isAlive = isAlive ?? throw new ArgumentNullException(nameof(isAlive));
        }
        public string OwnershipId { get; }
        public string SessionId { get; }
        public bool IsAlive => isAlive();
        internal IInternalSceneTransitionService? Borrowed => IsAlive ? current?.SceneTransitions : null;
        public IDisposable Install(INativeTransitionGrant grant)
        {
            if (!IsAlive) throw new ObjectDisposedException(nameof(NativeTransitionAccessSlot));
            if (current != null) throw new InvalidOperationException("A provider grant is already installed.");
            if (grant == null) throw new ArgumentNullException(nameof(grant));
            if (grant.SessionId != SessionId) throw new InvalidOperationException("A grant cannot cross session ownership.");
            current = grant;
            return new Binding(this, grant);
        }
        private sealed class Binding : IDisposable
        {
            private NativeTransitionAccessSlot? slot;
            private readonly INativeTransitionGrant grant;
            public Binding(NativeTransitionAccessSlot slot, INativeTransitionGrant grant) { this.slot = slot; this.grant = grant; }
            public void Dispose()
            {
                var previous = System.Threading.Interlocked.Exchange(ref slot, null);
                if (previous == null) return;
                if (ReferenceEquals(previous.current, grant)) previous.current = null;
                grant.Dispose();
            }
        }
    }
}
