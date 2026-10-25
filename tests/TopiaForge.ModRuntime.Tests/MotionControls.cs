using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.ModRuntime.Tests
{
    /// <summary>Physical input, aim and collision queries beneath real context decorators.</summary>
    internal sealed class MotionControls : IInputService, ILocalPlayerService, IPhysicsService
    {
        private readonly Dictionary<string, MotionAction> actions = new Dictionary<string, MotionAction>();
        internal PlayerSnapshot Snapshot { get; set; } = new PlayerSnapshot(Vec3.Zero, new Ray(Vec3.Zero, new Vec3(0, 0, 1)));
        internal PhysicsHit? Hit { get; set; }
        internal void Hold(string action, bool down) => actions[action].Down = down;
        internal void Sample() { foreach (var action in actions.Values) action.Sample(); }
        public bool IsUiFocused => false;
        public IReadOnlyList<InputConflict> GetConflicts() => Array.Empty<InputConflict>();
        public OperationResult<IInputAction> RegisterAction(InputActionDefinition definition)
        {
            var action = new MotionAction(definition.Name);
            actions[definition.Name] = action;
            return OperationResult<IInputAction>.Success(action);
        }
        public bool TryGetSnapshot(out PlayerSnapshot? snapshot) { snapshot = Snapshot; return true; }
        public bool TryGetHealth(out PlayerHealthSnapshot? health) { health = null; return false; }
        public OperationResult<PlayerHealthSnapshot> Damage(PlayerDamageRequest request) => OperationResult<PlayerHealthSnapshot>.Failure(ModErrorCode.Unavailable, "No health.");
        public OperationResult<PlayerHealthSnapshot> Heal(float amount, string source) => OperationResult<PlayerHealthSnapshot>.Failure(ModErrorCode.Unavailable, "No health.");
        public OperationResult<IPlayerControlLease> AcquireControl(string reason) => OperationResult<IPlayerControlLease>.Failure(ModErrorCode.Unavailable, "No controls.");
        public bool TryRaycast(Ray ray, float maximumDistance, out PhysicsHit? hit) { hit = Hit; return hit?.Entity.IsAlive == true; }
        public bool TrySphereCast(Ray ray, float radius, float maximumDistance, out PhysicsHit? hit) => TryRaycast(ray, maximumDistance, out hit);
        public IReadOnlyList<IEntity> Overlap(Bounds bounds, int maximumResults = 64) => Array.Empty<IEntity>();

        private sealed class MotionAction : IInputAction
        {
            internal MotionAction(string name) { Name = name; }
            internal bool Down { get; set; }
            public string Name { get; }
            public IReadOnlyList<InputBinding> Bindings => Array.Empty<InputBinding>();
            public float Value => IsHeld ? 1 : 0;
            public bool IsHeld { get; private set; }
            public bool WasPressed { get; private set; }
            public bool WasReleased { get; private set; }
            internal void Sample()
            {
                WasPressed = Down && !IsHeld;
                WasReleased = !Down && IsHeld;
                IsHeld = Down;
            }
            public OperationResult<bool> Rebind(IEnumerable<InputBinding> bindings) => OperationResult<bool>.Success(true);
            public OperationResult<bool> ResetBindings() => OperationResult<bool>.Success(true);
            public void Dispose() => Down = false;
        }
    }
}
