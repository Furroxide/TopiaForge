using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal sealed class ContextWorldSessionService : IWorldSessionService
    {
        private readonly IWorldSessionService authority;
        private readonly GamemodeSessionOrchestrator orchestrator;
        private readonly IModLifetime lifetime;
        private readonly object gate = new object();
        private readonly List<Subscription> subscriptions = new List<Subscription>();
        internal ContextWorldSessionService(GamemodeSessionOrchestrator authority, IModLifetime lifetime)
        {
            this.authority = authority; orchestrator = authority; this.lifetime = lifetime;
            lifetime.Defer(DisposeSubscriptions);
        }
        public WorldSessionSnapshot Current => Bind(authority.Current);
        public event Action<WorldSessionSnapshot> StateChanged
        {
            add
            {
                if (value == null) return;
                lock (gate)
                {
                    if (lifetime.IsStopping) return;
                    var subscription = new Subscription(this, value);
                    subscriptions.Add(subscription);
                    authority.StateChanged += subscription.Invoke;
                }
            }
            remove
            {
                lock (gate)
                    for (var index = subscriptions.Count - 1; index >= 0; index--)
                        if (subscriptions[index].Handler == value)
                        {
                            authority.StateChanged -= subscriptions[index].Invoke;
                            subscriptions.RemoveAt(index);
                            break;
                        }
            }
        }
        private WorldSessionSnapshot Bind(WorldSessionSnapshot snapshot)
        {
            if (lifetime.IsStopping) return new WorldSessionSnapshot(WorldSessionPhase.Idle, null, null, snapshot.Sequence);
            return new WorldSessionSnapshot(snapshot.Phase,
                snapshot.Session == null ? null : new BoundSession(snapshot.Session, lifetime, orchestrator), snapshot.World, snapshot.Sequence);
        }
        private void DisposeSubscriptions()
        {
            lock (gate)
            {
                foreach (var subscription in subscriptions) authority.StateChanged -= subscription.Invoke;
                subscriptions.Clear();
            }
        }
        private sealed class Subscription
        {
            private readonly ContextWorldSessionService owner;
            internal Subscription(ContextWorldSessionService owner, Action<WorldSessionSnapshot> handler) { this.owner = owner; Handler = handler; }
            internal Action<WorldSessionSnapshot> Handler { get; }
            internal void Invoke(WorldSessionSnapshot snapshot) { if (!owner.lifetime.IsStopping) Handler(owner.Bind(snapshot)); }
        }
        private sealed class BoundSession : IWorldSession
        {
            private readonly IWorldSession session;
            private readonly GamemodeSessionOrchestrator orchestrator;
            private readonly IModLifetime lifetime;
            internal BoundSession(IWorldSession session, IModLifetime lifetime, GamemodeSessionOrchestrator orchestrator)
            { this.session = session; this.lifetime = lifetime; this.orchestrator = orchestrator; }
            public string SessionId => session.SessionId;
            public string TargetId => session.TargetId;
            public string GamemodeId => session.GamemodeId;
            public string WorldId => session.WorldId;
            public string? WorldFamilyId => session.WorldFamilyId;
            public Task<OperationResult<bool>> StopAsync(CancellationToken token = default) => Invoke(session.StopAsync, token);
            public Task<OperationResult<bool>> RestartAsync(CancellationToken token = default) => Invoke(session.RestartAsync, token);
            public Task<OperationResult<bool>> ReturnToMainMenuAsync(CancellationToken token = default) => Invoke(session.ReturnToMainMenuAsync, token);
            private Task<OperationResult<bool>> Invoke(Func<CancellationToken, Task<OperationResult<bool>>> operation, CancellationToken token)
            {
                if (lifetime.IsStopping || token.IsCancellationRequested)
                    return Task.FromResult(OperationResult<bool>.Failure(ModErrorCode.Cancelled, "The consuming session scope has stopped."));
                return orchestrator.InvokeContextOperationAsync(lifetime, operation, token);
            }
        }
    }
}
