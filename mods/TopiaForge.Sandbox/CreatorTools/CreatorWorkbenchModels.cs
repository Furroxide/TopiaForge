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

    internal sealed class CreatorRosterEntry : IDisposable
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

        public void Dispose()
        {
            RobotEdit?.Dispose();
            NativeEdit?.Dispose();
            TargetRegistration?.Dispose();
            cleanup?.Dispose();
            RobotEdit = null;
            NativeEdit = null;
            TargetRegistration = null;
            cleanup = null;
        }
    }
}
