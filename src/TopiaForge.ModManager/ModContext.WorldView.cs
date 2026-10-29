using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class ModContext : IInternalWorldViewContext
    {
        public bool TryGetViewWorld(out WorldReadiness? world, out IModLifetime? lifetime)
        {
            UnityMainThreadGuard.AssertCurrent();
            world = null; lifetime = null;
            return !Lifetime.IsStopping && sessionOwner != null && sessionOwner.TryGetViewWorld(this, out world, out lifetime);
        }
    }
    internal sealed partial class GamemodeSessionOrchestrator
    {
        private static void ActivateView(SessionRecord record)
        {
            var context = record.Scopes[record.World.Package.Id].Context;
            if (context.Extensions.TryGet<IWorldView>(out var view) && view is IInternalWorldViewFacade facade)
                facade.Activate(record.Identity.SessionId);
        }
        internal bool TryGetViewWorld(ModContext caller, out WorldReadiness? world, out IModLifetime? lifetime)
        {
            world = null; lifetime = null;
            var record = current;
            if (shuttingDown || record == null || record.StopRequested || record.Readiness == null
                || !record.Snapshot.Contexts.TryGetValue(caller.Identity.Id, out var root) || !caller.IsWithin(root)
                || !record.Scopes.TryGetValue(record.World.Package.Id, out var scope) || scope.Context.Lifetime.IsStopping) return false;
            world = record.Readiness; lifetime = scope.Context.Lifetime;
            return true;
        }
    }
}
