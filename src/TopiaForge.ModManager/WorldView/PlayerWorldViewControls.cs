using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed class PlayerWorldViewControls : IViewControls
    {
        private readonly IModContext? context;
        private readonly ParticipantId player;
        internal PlayerWorldViewControls(IModContext? context, ParticipantId player) { this.context = context; this.player = player; }
        public OperationResult<IViewLease> Environment(string preset) => Change(v => v.Environment(preset));
        public OperationResult<IViewLease> Fog(VisionFog fog) => Change(v => v.Fog(fog));
        public OperationResult<IViewLease> Hide(IEntity entity) => Change(v => v.Hide(entity));
        private OperationResult<IViewLease> Change(Func<IViewControls, OperationResult<IViewLease>> change)
        {
            if (context?.Lifetime.IsStopping == true)
                return OperationResult<IViewLease>.Failure(ModErrorCode.Cancelled, "The view's scope is stopping.");
            return context != null && context.Extensions.TryGet<IWorldView>(out var view) && view is IInternalWorldViewFacade facade
                ? change(facade.ForPlayer(player.Value))
                : OperationResult<IViewLease>.Failure(ModErrorCode.Unavailable, "No active world view is available.");
        }
    }
}
