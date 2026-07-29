using System;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Configurable fail-closed mutation safety gate for creator-tool tests.</summary>
    public sealed class FakeCreatorMutationSafetyService : ICreatorMutationSafetyService
    {
        private readonly FakeModLifetime lifetime;
        private CreatorMutationSafetyState state;
        private string message;

        /// <summary>Creates a fake mutation safety gate.</summary>
        public FakeCreatorMutationSafetyService(
            FakeModLifetime lifetime,
            CreatorMutationSafetyState state = CreatorMutationSafetyState.Unavailable,
            string message = "Fake persistence isolation is unavailable.")
        {
            this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
            this.state = state;
            this.message = message ?? string.Empty;
        }

        /// <summary>Gets the number of active mutation leases.</summary>
        public int ActiveLeaseCount { get; private set; }

        /// <summary>Changes the simulated persistence-isolation state.</summary>
        public void SetState(CreatorMutationSafetyState newState, string newMessage = "")
        {
            state = newState;
            message = newMessage ?? string.Empty;
        }

        /// <inheritdoc />
        public CreatorMutationSafetySnapshot Status => new CreatorMutationSafetySnapshot(
            state,
            state != CreatorMutationSafetyState.Unavailable,
            message);

        /// <inheritdoc />
        public OperationResult<ICreatorMutationLease> Acquire(CreatorMutationLeaseRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (lifetime.IsStopping)
            {
                return OperationResult<ICreatorMutationLease>.Failure(ModErrorCode.Cancelled, "The fake lifetime is stopping.");
            }
            if (!request.UserAcknowledgedTemporaryChanges)
            {
                return OperationResult<ICreatorMutationLease>.Failure(ModErrorCode.Conflict, "The fake requires acknowledgement.");
            }
            if (state == CreatorMutationSafetyState.Unavailable)
            {
                return OperationResult<ICreatorMutationLease>.Failure(ModErrorCode.Unavailable, message);
            }
            ActiveLeaseCount++;
            var lease = new Lease(this, request.Purpose);
            return lifetime.TrackResult<ICreatorMutationLease>(lease, "The fake lifetime stopped during mutation lease creation.");
        }

        private sealed class Lease : ICreatorMutationLease
        {
            private FakeCreatorMutationSafetyService? service;

            public Lease(FakeCreatorMutationSafetyService service, string purpose)
            {
                this.service = service;
                Purpose = purpose;
            }

            public string Purpose { get; }
            public bool IsAlive => service != null;
            public bool IsPersistenceIsolated => service != null && service.state != CreatorMutationSafetyState.Unavailable;
            public void Dispose()
            {
                var current = service;
                service = null;
                if (current != null) current.ActiveLeaseCount--;
            }
        }
    }
}
