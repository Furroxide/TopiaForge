using System;
using TopiaForge.Mods;

// Recording backends for the real owner-facade sources; native adapters remain in their game assemblies.
namespace TopiaForge.Chronos
{
    internal sealed partial class TimeControlService
    {
        internal int Calls;
        internal Lease? LastLease;
        internal Scheduler? LastScheduler;
        internal ITimeControlService ForOwner(IModLifetime lifetime) => new OwnerFacade(this, "scope.mod", lifetime);
        public bool IsAvailable => true;
        public float WorldScale => 1;
        public float WorldDeltaTime => 1;
        public float WorldTime => 1;
        public float ControlDeltaTime => 1;
        public float ControlTime => 1;
        public bool IsFrozen => false;
        public TimeMode Mode => default;
        private ITimeLease Allocate() { Calls++; return LastLease = new Lease(); }
        public ITimeLease Freeze(string owner, string usage, bool suspendPlayer) => Allocate();
        public ITimeLease Slow(string owner, string usage, float scale) => Allocate();
        public ITimeLease ExemptPlayer(string owner, string usage) => Allocate();
        public ITimeLease SetDriver(string owner, string usage, ITimeDriver driver) => Allocate();
        public OperationResult<bool> Step(float seconds) => OperationResult<bool>.Success(true);
        public OperationResult<bool> StepFixed(int ticks) => OperationResult<bool>.Success(true);
        public ITurnScheduler BeginTurnBased(string owner, string usage, TurnSchedulerOptions options) => LastScheduler = new Scheduler();
        internal sealed class Scheduler : ITurnScheduler
        {
            internal int Calls;
            public TurnState State => default;
            public TurnActorId? CurrentActor => null;
            public int ActorCount => 0;
            public OperationResult<bool> Register(TurnActorId actor, float speed) { Calls++; return OperationResult<bool>.Success(true); }
            public OperationResult<bool> Unregister(TurnActorId actor) { Calls++; return OperationResult<bool>.Success(true); }
            public OperationResult<bool> BeginAction() { Calls++; return OperationResult<bool>.Success(true); }
            public OperationResult<bool> EndAction() { Calls++; return OperationResult<bool>.Success(true); }
            public void Tick(float value) { Calls++; }
            public void Dispose() { }
        }
        internal sealed class Lease : ITimeLease
        {
            public bool IsActive { get; private set; } = true;
            public void Release() => IsActive = false;
            public void Dispose() => Release();
        }
    }
}
namespace TopiaForge.Worlds
{
    internal sealed partial class PauseMenuBridge
    {
        internal int Calls;
        internal WorldPauseAction? Action;
        internal Func<WorldPauseExitContext, WorldPauseExitDecision>? Interceptor;
        public bool IsAvailable => true;
        internal IWorldPauseMenuService ForOwner(IModLifetime lifetime) => new OwnerFacade(this, "scope.mod", lifetime);
        public OperationResult<IDisposable> RegisterAction(WorldPauseAction action)
        { Calls++; Action = action; return OperationResult<IDisposable>.Success(new ActionRegistration(this, action)); }
        public OperationResult<IDisposable> InterceptExit(Func<WorldPauseExitContext, WorldPauseExitDecision> callback, string owner)
        { Calls++; Interceptor = callback; return OperationResult<IDisposable>.Success(new ExitInterceptorLease(this, callback)); }
        private void RemoveAction(ActionRegistration registration)
        { if (ReferenceEquals(Action, registration.Action)) Action = null; }
        private void RemoveExitInterceptor(Func<WorldPauseExitContext, WorldPauseExitDecision> callback)
        { if (ReferenceEquals(Interceptor, callback)) Interceptor = null; }
    }
}
