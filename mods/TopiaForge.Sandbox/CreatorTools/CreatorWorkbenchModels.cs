using System;
using TopiaForge.Mods;

namespace TopiaForge.CreatorTools.Shared
{
    internal sealed class CreatorCatalogEntry
    {
        public CreatorCatalogEntry(string id, string displayName, string description, CreatorContentKind kind)
        {
            Id = id;
            DisplayName = displayName;
            Description = description;
            Kind = kind;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public string Description { get; }
        public CreatorContentKind Kind { get; }
        public bool IsRobotKit => Id.StartsWith("robotkit:", StringComparison.OrdinalIgnoreCase);
        public string SourceId => IsRobotKit ? Id.Substring("robotkit:".Length) : Id.Substring("content:".Length);
    }

    internal sealed class CreatorRosterEntry
    {
        private IDisposable? cleanup;

        public CreatorRosterEntry(
            string id,
            string displayName,
            CreatorContentKind kind,
            bool owned,
            IDisposable? cleanup = null)
        {
            Id = id;
            DisplayName = displayName;
            Kind = kind;
            Owned = owned;
            this.cleanup = cleanup;
        }

        public string Id { get; set; }
        public string DisplayName { get; set; }
        public CreatorContentKind Kind { get; }
        public bool Owned { get; }
        public string SourceId { get; set; } = string.Empty;
        public string TargetName { get; set; } = string.Empty;
        public ICreatorSpawnHandle? Spawn { get; set; }
        public ICreatorSceneTarget? NativeTarget { get; set; }
        public ICreatorTemporaryEdit? NativeEdit { get; set; }
        public IRobotAgent? Robot { get; set; }
        public IRobotEditTarget? RobotTarget { get; set; }
        public IRobotEditLease? RobotEdit { get; set; }
        public IRobotTargetRegistration? TargetRegistration { get; set; }
        public bool NativeHidden { get; set; }

        public IEntity? Entity => (IEntity?)Robot ?? Spawn?.Entity ?? NativeTarget?.Entity;
        public bool IsAlive => Robot?.IsAlive ?? Spawn?.IsAlive ?? NativeTarget?.IsAlive ?? RobotTarget?.IsAlive ?? false;
        public bool IsRobot => Robot != null || RobotTarget != null || Kind == CreatorContentKind.Robot;

        public OperationResult<bool> RestoreEdits()
        {
            var robotEdit = RobotEdit;
            var nativeEdit = NativeEdit;
            RobotEdit = null;
            NativeEdit = null;
            NativeHidden = false;
            var result = OperationResult<bool>.Success(robotEdit != null || nativeEdit != null);
            Restore(robotEdit, robotEdit == null ? null : new Func<OperationResult<bool>>(robotEdit.Restore));
            Restore(nativeEdit, nativeEdit == null ? null : new Func<OperationResult<bool>>(nativeEdit.Restore));
            return result;

            void Restore(IDisposable? lease, Func<OperationResult<bool>>? restore)
            {
                if (lease == null || restore == null) return;
                try { result = Merge(result, restore()); }
                catch (Exception exception) { result = Merge(result, OperationResult<bool>.Failure(ModErrorCode.External, exception.Message)); }
                try { lease.Dispose(); }
                catch (Exception exception) { result = Merge(result, OperationResult<bool>.Failure(ModErrorCode.External, exception.Message)); }
            }
        }

        public OperationResult<bool> RestoreAndDispose()
        {
            // Detach first: providers may re-enter cleanup synchronously.
            var registration = TargetRegistration;
            var owned = cleanup;
            TargetRegistration = null;
            cleanup = null;
            var result = RestoreEdits();
            Release(registration);
            Release(owned);
            return result;

            void Release(IDisposable? resource)
            {
                try { resource?.Dispose(); }
                catch (Exception exception) { result = Merge(result, OperationResult<bool>.Failure(ModErrorCode.External, exception.Message)); }
            }
        }

        private static OperationResult<bool> Merge(OperationResult<bool> current, OperationResult<bool> next)
        {
            if (next.Succeeded) return current;
            if (current.Succeeded) return next;
            return OperationResult<bool>.Failure(
                current.ErrorCode == ModErrorCode.Conflict ? next.ErrorCode : current.ErrorCode,
                current.ErrorMessage + " " + next.ErrorMessage);
        }

    }
}
