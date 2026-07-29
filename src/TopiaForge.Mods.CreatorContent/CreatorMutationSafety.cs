using System;

namespace TopiaForge.Mods
{
    /// <summary>Reports whether global creator mutations can be isolated from persistent game state.</summary>
    public enum CreatorMutationSafetyState
    {
        /// <summary>No validated persistence-isolation bridge is available; mutation must remain disabled.</summary>
        Unavailable = 0,
        /// <summary>Isolation is available but this creator session still needs explicit user acknowledgement.</summary>
        RequiresAcknowledgement = 1,
        /// <summary>A validated bridge can isolate and roll back temporary creator mutations.</summary>
        Ready = 2
    }

    /// <summary>Immutable current state of the fail-closed global mutation gate.</summary>
    public sealed class CreatorMutationSafetySnapshot
    {
        /// <summary>Creates mutation safety status.</summary>
        public CreatorMutationSafetySnapshot(
            CreatorMutationSafetyState state,
            bool persistenceIsolationAvailable,
            string message)
        {
            if (!Enum.IsDefined(typeof(CreatorMutationSafetyState), state)) throw new ArgumentOutOfRangeException(nameof(state));
            if (state == CreatorMutationSafetyState.Ready && !persistenceIsolationAvailable)
            {
                throw new ArgumentException("Ready mutation safety requires persistence isolation.", nameof(persistenceIsolationAvailable));
            }
            State = state;
            PersistenceIsolationAvailable = persistenceIsolationAvailable;
            Message = message ?? string.Empty;
        }

        /// <summary>Gets the current gate state.</summary>
        public CreatorMutationSafetyState State { get; }
        /// <summary>Gets whether a validated persistence-isolation bridge is active.</summary>
        public bool PersistenceIsolationAvailable { get; }
        /// <summary>Gets a user-readable explanation or remediation.</summary>
        public string Message { get; }
    }

    /// <summary>Requests one explicitly acknowledged global-mutation lease.</summary>
    public sealed class CreatorMutationLeaseRequest
    {
        /// <summary>Creates a global mutation lease request.</summary>
        public CreatorMutationLeaseRequest(string purpose, bool userAcknowledgedTemporaryChanges)
        {
            if (string.IsNullOrWhiteSpace(purpose)) throw new ArgumentException("A purpose is required.", nameof(purpose));
            Purpose = purpose;
            UserAcknowledgedTemporaryChanges = userAcknowledgedTemporaryChanges;
        }

        /// <summary>Gets the bounded diagnostic purpose.</summary>
        public string Purpose { get; }
        /// <summary>Gets whether the user explicitly accepted temporary reversible mutation for this lease.</summary>
        public bool UserAcknowledgedTemporaryChanges { get; }
    }

    /// <summary>
    /// Short-lived proof that persistence isolation and one-time user acknowledgement were both established.
    /// Disposing the lease ends mutation authority but does not replace individual edit-lease rollback.
    /// </summary>
    public interface ICreatorMutationLease : IDisposable
    {
        /// <summary>Gets the diagnostic purpose.</summary>
        string Purpose { get; }
        /// <summary>Gets whether the lease remains authoritative.</summary>
        bool IsAlive { get; }
        /// <summary>Gets whether the underlying bridge still reports persistence isolation.</summary>
        bool IsPersistenceIsolated { get; }
    }

    /// <summary>Fail-closed gate used by global creator tools before enabling any scene mutation.</summary>
    public interface ICreatorMutationSafetyService
    {
        /// <summary>Gets current isolation availability without requiring mutation authority.</summary>
        CreatorMutationSafetySnapshot Status { get; }
        /// <summary>
        /// Acquires a short-lived mutation lease only after explicit acknowledgement and validated persistence
        /// isolation. Browsing and project authoring do not require this lease.
        /// </summary>
        OperationResult<ICreatorMutationLease> Acquire(CreatorMutationLeaseRequest request);
    }
}
