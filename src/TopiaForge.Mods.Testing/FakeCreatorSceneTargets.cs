using System;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Inspectable provider-approved native target for creator-session tests.</summary>
    public sealed class FakeCreatorSceneTarget : ICreatorSceneTarget
    {
        private FakeCreatorTemporaryEdit? activeEdit;

        /// <summary>Creates a live fake scene target backed by a mutable fake entity.</summary>
        public FakeCreatorSceneTarget(
            string id,
            string displayName,
            string adapterId,
            FakeEntity entity,
            CreatorContentKind kind = CreatorContentKind.Other,
            CreatorSceneTargetCapabilities capabilities = CreatorSceneTargetCapabilities.Transform,
            string catalogContentId = "",
            bool hidden = false)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A target id is required.", nameof(id));
            if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("A display name is required.", nameof(displayName));
            if (string.IsNullOrWhiteSpace(adapterId)) throw new ArgumentException("An adapter id is required.", nameof(adapterId));
            if (!Enum.IsDefined(typeof(CreatorContentKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
            Id = id;
            DisplayName = displayName;
            AdapterId = adapterId;
            FakeEntity = entity ?? throw new ArgumentNullException(nameof(entity));
            Kind = kind;
            Capabilities = capabilities;
            CatalogContentId = catalogContentId ?? string.Empty;
            Hidden = hidden;
        }

        /// <inheritdoc />
        public string Id { get; }
        /// <inheritdoc />
        public string DisplayName { get; }
        /// <inheritdoc />
        public CreatorContentKind Kind { get; }
        /// <inheritdoc />
        public CreatorSceneTargetCapabilities Capabilities { get; }
        /// <inheritdoc />
        public string CatalogContentId { get; }
        /// <inheritdoc />
        public string AdapterId { get; }
        /// <inheritdoc />
        public IEntity Entity => FakeEntity;
        /// <inheritdoc />
        public bool IsAlive => FakeEntity.IsAlive;

        /// <summary>Gets the mutable entity backing this target.</summary>
        public FakeEntity FakeEntity { get; }
        /// <summary>Gets or sets fake native visibility for reversible-edit tests.</summary>
        public bool Hidden { get; set; }
        /// <summary>Gets the current complete fake transform.</summary>
        public TransformState Transform => new TransformState(FakeEntity.Position, FakeEntity.Rotation, FakeEntity.Scale);
        /// <summary>Gets whether an exclusive edit lease currently owns this target.</summary>
        public bool HasActiveEdit => activeEdit?.IsAlive == true;

        internal bool TryAcquire(FakeCreatorTemporaryEdit edit)
        {
            if (!IsAlive || HasActiveEdit) return false;
            activeEdit = edit;
            return true;
        }

        internal void Release(FakeCreatorTemporaryEdit edit)
        {
            if (ReferenceEquals(activeEdit, edit)) activeEdit = null;
        }

        internal void SetTransform(TransformState transform)
        {
            FakeEntity.Position = transform.Position;
            FakeEntity.Rotation = transform.Rotation;
            FakeEntity.Scale = transform.Scale;
        }
    }

    /// <summary>
    /// Exclusive fake edit lease that restores position, rotation, scale, and visibility independently, preserving
    /// any property that another test actor changed after the lease last wrote it.
    /// </summary>
    public sealed class FakeCreatorTemporaryEdit : ICreatorTemporaryEdit
    {
        private readonly FakeCreatorSceneTarget target;
        private readonly TransformState originalTransform;
        private readonly bool originalHidden;
        private readonly Func<ModErrorCode> transformError;
        private readonly Func<ModErrorCode> visibilityError;
        private readonly Func<ModErrorCode> restoreError;
        private Action<FakeCreatorTemporaryEdit>? release;
        private TransformState expectedTransform;
        private bool expectedHidden;

        internal FakeCreatorTemporaryEdit(
            FakeCreatorSceneTarget target,
            Func<ModErrorCode> transformError,
            Func<ModErrorCode> visibilityError,
            Func<ModErrorCode> restoreError,
            Action<FakeCreatorTemporaryEdit> release)
        {
            this.target = target;
            this.transformError = transformError;
            this.visibilityError = visibilityError;
            this.restoreError = restoreError;
            this.release = release;
            originalTransform = target.Transform;
            originalHidden = target.Hidden;
            expectedTransform = originalTransform;
            expectedHidden = originalHidden;
        }

        /// <inheritdoc />
        public ICreatorSceneTarget Target => target;
        /// <inheritdoc />
        public CreatorSceneTargetCapabilities Capabilities => target.Capabilities;
        /// <inheritdoc />
        public bool IsAlive => release != null && target.IsAlive;
        /// <summary>Gets whether the final restore preserved at least one externally changed property.</summary>
        public bool LastRestoreHadConflict { get; private set; }

        /// <inheritdoc />
        public bool TryGetTransform(out TransformState transform)
        {
            if (!IsAlive)
            {
                transform = TransformState.Identity;
                return false;
            }
            transform = target.Transform;
            return true;
        }

        /// <inheritdoc />
        public OperationResult<TransformState> SetTransform(TransformState transform)
        {
            if (!IsAlive) return OperationResult<TransformState>.Failure(ModErrorCode.InvalidState, "The fake edit lease is not alive.");
            var configured = transformError();
            if (configured != ModErrorCode.None)
            {
                return OperationResult<TransformState>.Failure(configured, "The fake rejected a temporary transform edit.");
            }

            var current = target.Transform;
            if (current.Position != transform.Position && (Capabilities & CreatorSceneTargetCapabilities.Position) == 0)
            {
                return OperationResult<TransformState>.Failure(ModErrorCode.InvalidArgument, "The fake target does not support position edits.");
            }
            if (current.Rotation != transform.Rotation && (Capabilities & CreatorSceneTargetCapabilities.Rotation) == 0)
            {
                return OperationResult<TransformState>.Failure(ModErrorCode.InvalidArgument, "The fake target does not support rotation edits.");
            }
            if (current.Scale != transform.Scale && (Capabilities & CreatorSceneTargetCapabilities.Scale) == 0)
            {
                return OperationResult<TransformState>.Failure(ModErrorCode.InvalidArgument, "The fake target does not support scale edits.");
            }

            target.SetTransform(transform);
            expectedTransform = transform;
            return OperationResult<TransformState>.Success(transform);
        }

        /// <inheritdoc />
        public OperationResult<bool> SetTemporarilyHidden(bool hidden)
        {
            if (!IsAlive) return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "The fake edit lease is not alive.");
            if ((Capabilities & CreatorSceneTargetCapabilities.TemporaryVisibility) == 0)
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "The fake target does not support visibility edits.");
            }
            var configured = visibilityError();
            if (configured != ModErrorCode.None)
            {
                return OperationResult<bool>.Failure(configured, "The fake rejected a temporary visibility edit.");
            }
            target.Hidden = hidden;
            expectedHidden = hidden;
            return OperationResult<bool>.Success(true);
        }

        /// <inheritdoc />
        public OperationResult<bool> Restore() => RestoreCore(ignoreConfiguredFailure: false);

        /// <inheritdoc />
        public void Dispose() => _ = RestoreCore(ignoreConfiguredFailure: true);

        internal void Abandon()
        {
            release = null;
        }

        private OperationResult<bool> RestoreCore(bool ignoreConfiguredFailure)
        {
            if (release == null) return OperationResult<bool>.Success(false);
            var configured = restoreError();
            if (!ignoreConfiguredFailure && configured != ModErrorCode.None)
            {
                return OperationResult<bool>.Failure(configured, "The fake rejected temporary-edit restoration.");
            }

            var callback = release;
            release = null;
            if (!target.IsAlive)
            {
                target.Release(this);
                callback(this);
                return OperationResult<bool>.Failure(ModErrorCode.NotFound, "The fake target disappeared before restoration.");
            }

            var current = target.Transform;
            var restorePosition = current.Position == expectedTransform.Position;
            var restoreRotation = current.Rotation == expectedTransform.Rotation;
            var restoreScale = current.Scale == expectedTransform.Scale;
            var restoreVisibility = target.Hidden == expectedHidden;
            LastRestoreHadConflict = !restorePosition || !restoreRotation || !restoreScale || !restoreVisibility;
            target.SetTransform(new TransformState(
                restorePosition ? originalTransform.Position : current.Position,
                restoreRotation ? originalTransform.Rotation : current.Rotation,
                restoreScale ? originalTransform.Scale : current.Scale));
            if (restoreVisibility) target.Hidden = originalHidden;
            target.Release(this);
            callback(this);
            return OperationResult<bool>.Success(true);
        }
    }
}
