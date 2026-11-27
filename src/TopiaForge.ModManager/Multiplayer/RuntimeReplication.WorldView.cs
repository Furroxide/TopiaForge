using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class RuntimeReplication
    {
        public void ActivateWorldView(IInternalWorldViewBinding binding)
        { UnityMainThreadGuard.AssertCurrent(); services.Views.Activate(binding); }
        public IViewControls WorldView(IInternalWorldViewRenderer renderer) => BindView(renderer, null);
        public IViewControls PlayerWorldView(IInternalWorldViewRenderer renderer, string player) => BindView(renderer, player);
        private IViewControls BindView(IInternalWorldViewRenderer renderer, string? player)
        {
            UnityMainThreadGuard.AssertCurrent();
            return new RoutedView(this, renderer, player);
        }
        private sealed class RoutedView : IViewControls
        {
            private readonly RuntimeReplication owner;
            private readonly IInternalWorldViewRenderer renderer;
            private readonly string? player;
            internal RoutedView(RuntimeReplication owner, IInternalWorldViewRenderer renderer, string? player)
            { this.owner = owner; this.renderer = renderer; this.player = player; }
            public OperationResult<IViewLease> Environment(string preset) => Change(v => v.Environment(preset));
            public OperationResult<IViewLease> Fog(VisionFog fog) => Change(v => v.Fog(fog));
            public OperationResult<IViewLease> Hide(IEntity entity) => Change(v => v.Hide(entity));
            private OperationResult<IViewLease> Change(Func<IViewControls, OperationResult<IViewLease>> change)
            {
                UnityMainThreadGuard.AssertCurrent();
                if (owner.ExecutionContext == InternalExecutionContext.Participant
                    || owner.Realm != InternalNetRealm.Offline && (owner.NetworkClass == InternalNetworkClass.ClientLocal || owner.NetworkClass == InternalNetworkClass.Standalone)
                    || owner.Realm == InternalNetRealm.Client && owner.CurrentIntent.Source != InternalIntent.None)
                    return OperationResult<IViewLease>.Failure(ModErrorCode.NotAuthoritative, "Only the host changes a player's view. Use the server controller.");
                if (owner.lifetime.IsStopping || !renderer.IsAlive)
                    return OperationResult<IViewLease>.Failure(ModErrorCode.Cancelled, "The view's scope or world is stopping.");
                if (owner.Realm == InternalNetRealm.Client)
                {
                    var shadow = new ShadowViewLease(owner.lifetime, renderer);
                    return OperationResult<IViewLease>.Success(shadow);
                }
                return change(owner.services.Views.For(renderer).Bind(owner.lifetime, () => ModErrorCode.None,
                    () => UnityMainThreadGuard.AssertCurrent(), player));
            }
        }
        private sealed class ShadowViewLease : IViewLease
        {
            private readonly IModLifetime lifetime;
            private readonly IInternalWorldViewRenderer renderer;
            private IDisposable? tracking;
            private bool disposed;
            internal ShadowViewLease(IModLifetime lifetime, IInternalWorldViewRenderer renderer)
            { this.lifetime = lifetime; this.renderer = renderer; tracking = lifetime.Track(this); renderer.Stopped += Dispose; }
            public bool IsActive => !disposed && !lifetime.IsStopping && renderer.IsAlive;
            public void Dispose()
            { if (disposed) return; disposed = true; renderer.Stopped -= Dispose; var hold = tracking; tracking = null; hold?.Dispose(); }
        }
    }
}
