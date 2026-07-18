using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Deterministic player snapshot and reversible-control service.</summary>
    public sealed class FakePlayerService : IPlayerService
    {
        private readonly FakeModLifetime lifetime;
        private readonly List<FakePlayerControlLease> leases = new List<FakePlayerControlLease>();

        /// <summary>Creates a fake player service.</summary>
        public FakePlayerService(FakeModLifetime lifetime)
        {
            this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        }

        /// <summary>Gets or sets the snapshot returned by <see cref="TryGetSnapshot"/>.</summary>
        public PlayerSnapshot? Snapshot { get; set; }

        /// <summary>Gets the most recent damage request.</summary>
        public PlayerDamageRequest? LastDamageRequest { get; private set; }

        /// <summary>Gets or sets the health snapshot mutated by <see cref="Damage"/> and <see cref="Heal"/>.</summary>
        public PlayerHealthSnapshot? Health { get; set; }

        /// <summary>Gets or sets a stable error used to reject health mutations.</summary>
        public ModErrorCode HealthMutationErrorCode { get; set; }

        /// <summary>Gets or sets the diagnostic paired with <see cref="HealthMutationErrorCode"/>.</summary>
        public string HealthMutationErrorMessage { get; set; } = "Player health is unavailable in this test.";

        /// <summary>Gets or sets a stable error used to reject control acquisition.</summary>
        public ModErrorCode AcquireControlErrorCode { get; set; }

        /// <summary>Gets or sets the diagnostic paired with <see cref="AcquireControlErrorCode"/>.</summary>
        public string AcquireControlErrorMessage { get; set; } = "Player control is unavailable in this test.";

        /// <summary>Gets the number of active player-control leases.</summary>
        public int ActiveControlLeaseCount => leases.Count;

        /// <inheritdoc/>
        public bool TryGetSnapshot(out PlayerSnapshot? snapshot)
        {
            snapshot = Snapshot;
            return snapshot != null;
        }

        /// <inheritdoc/>
        public bool TryGetHealth(out PlayerHealthSnapshot? health)
        {
            health = Health;
            return health != null;
        }

        /// <inheritdoc/>
        public OperationResult<PlayerHealthSnapshot> Damage(PlayerDamageRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            LastDamageRequest = request;

            if (HealthMutationErrorCode != ModErrorCode.None)
            {
                return OperationResult<PlayerHealthSnapshot>.Failure(
                    HealthMutationErrorCode,
                    HealthMutationErrorMessage);
            }

            if (Health == null)
            {
                return OperationResult<PlayerHealthSnapshot>.Failure(
                    ModErrorCode.Unavailable,
                    HealthMutationErrorMessage);
            }

            Health = new PlayerHealthSnapshot(Health.Current - request.Amount, Health.Maximum);
            return OperationResult<PlayerHealthSnapshot>.Success(Health);
        }

        /// <inheritdoc/>
        public OperationResult<PlayerHealthSnapshot> Heal(float amount, string source)
        {
            if (amount <= 0f || float.IsNaN(amount) || float.IsInfinity(amount))
            {
                throw new ArgumentOutOfRangeException(nameof(amount));
            }

            if (string.IsNullOrWhiteSpace(source))
            {
                throw new ArgumentException("A diagnostic healing source is required.", nameof(source));
            }

            if (HealthMutationErrorCode != ModErrorCode.None)
            {
                return OperationResult<PlayerHealthSnapshot>.Failure(
                    HealthMutationErrorCode,
                    HealthMutationErrorMessage);
            }

            if (Health == null)
            {
                return OperationResult<PlayerHealthSnapshot>.Failure(
                    ModErrorCode.Unavailable,
                    HealthMutationErrorMessage);
            }

            Health = new PlayerHealthSnapshot(Health.Current + amount, Health.Maximum);
            return OperationResult<PlayerHealthSnapshot>.Success(Health);
        }

        /// <inheritdoc/>
        public OperationResult<IPlayerControlLease> AcquireControl(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                throw new ArgumentException("A control reason is required.", nameof(reason));
            }

            if (AcquireControlErrorCode != ModErrorCode.None)
            {
                return OperationResult<IPlayerControlLease>.Failure(
                    AcquireControlErrorCode,
                    AcquireControlErrorMessage);
            }

            var lease = new FakePlayerControlLease(reason, value => leases.Remove(value));
            leases.Add(lease);
            try
            {
                lease.AttachLifetimeLease(lifetime.Track(lease));
                return OperationResult<IPlayerControlLease>.Success(lease);
            }
            catch (ObjectDisposedException)
            {
                lease.Dispose();
                return OperationResult<IPlayerControlLease>.Failure(
                    ModErrorCode.Cancelled,
                    "The fake mod stopped before player control could be acquired.");
            }
        }

    }

    /// <summary>Inspectable fake player-control lease.</summary>
    public sealed class FakePlayerControlLease : IPlayerControlLease
    {
        private Action<FakePlayerControlLease>? release;
        private IDisposable? lifetimeLease;

        internal FakePlayerControlLease(string reason, Action<FakePlayerControlLease> release)
        {
            Reason = reason;
            this.release = release;
        }

        internal void AttachLifetimeLease(IDisposable lease)
        {
            lifetimeLease = lease ?? throw new ArgumentNullException(nameof(lease));
        }

        /// <inheritdoc/>
        public bool IsActive => release != null;

        /// <inheritdoc/>
        public string Reason { get; }

        /// <inheritdoc/>
        public void Dispose()
        {
            var callback = release;
            release = null;
            try
            {
                callback?.Invoke(this);
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
            }
        }
    }
}
