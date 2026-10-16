using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class ModContext : IInternalWorldSessionContext
    {
        private GamemodeSessionOrchestrator? sessionOwner;
        private readonly ModContext? scopeParent;
        internal bool IsWithin(ModContext root)
        {
            for (ModContext? value = this; value != null; value = value.scopeParent)
                if (ReferenceEquals(value, root)) return true;
            return false;
        }
        private IInternalWorldSessionOperations? contentOperations;
        private IWorldSessionService? sessionService;
        internal void ConfigureSessions(GamemodeSessionOrchestrator owner)
        {
            if (sessionOwner != null) throw new InvalidOperationException("A context already has a session authority.");
            sessionOwner = owner ?? throw new ArgumentNullException(nameof(owner));
            contentOperations = new ContextContentOperations(owner, this);
            sessionService = new ContextWorldSessionService(owner, Lifetime);
        }
        public IWorldSessionService Sessions => sessionService ?? throw new InvalidOperationException("Session authority is unavailable.");
        public IInternalWorldSessionOperations ContentOperations => contentOperations
            ?? throw new InvalidOperationException("Session content authority is unavailable.");
        private sealed class ContextContentOperations : IInternalWorldSessionOperations
        {
            private readonly GamemodeSessionOrchestrator owner;
            private readonly ModContext context;
            internal ContextContentOperations(GamemodeSessionOrchestrator owner, ModContext context)
            { this.owner = owner; this.context = context; }
            public Task<OperationResult<bool>> RunContentOperationAsync(string sessionId,
                Func<IInternalSessionContentContext, CancellationToken, Task<OperationResult<IDisposable>>> operation,
                CancellationToken cancellationToken = default) => owner.RunContentOperationAsync(context, sessionId, operation, cancellationToken);
        }
    }
}
