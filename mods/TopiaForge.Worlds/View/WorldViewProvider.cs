using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Worlds
{
    internal sealed class WorldViewProvider : IWorldView, IOwnerContextBoundExtensionFactory
    {
        private readonly ConditionalWeakTable<WorldReadiness, IInternalWorldViewRenderer> worlds = new ConditionalWeakTable<WorldReadiness, IInternalWorldViewRenderer>();
        private readonly IModContext provider;
        private readonly Func<WorldReadiness, IModContext, IInternalWorldViewRenderer> createRenderer;
        internal WorldViewProvider(IModContext provider, Func<WorldReadiness, IModContext, IInternalWorldViewRenderer> createRenderer)
        { this.provider = provider; this.createRenderer = createRenderer; }
        public object CreateOwnerFacade(Type contractType, IModContext context) => new Facade(this, context);
        public bool IsAvailable => false;
        public IReadOnlyList<string> EnvironmentPresets => Array.Empty<string>();
        public OperationResult<IViewLease> Environment(string preset) => Unavailable();
        public OperationResult<IViewLease> Fog(VisionFog fog) => Unavailable();
        public OperationResult<IViewLease> Hide(IEntity entity) => Unavailable();
        private static OperationResult<IViewLease> Unavailable() => OperationResult<IViewLease>.Failure(ModErrorCode.Unavailable, "No active world view is available.");
        private sealed class Facade : IWorldView
        {
            private readonly WorldViewProvider owner;
            private readonly IModContext context;
            internal Facade(WorldViewProvider owner, IModContext context) { this.owner = owner; this.context = context; }
            private IInternalWorldViewRenderer? Renderer()
            {
                if (context.Lifetime.IsStopping || owner.provider.Lifetime.IsStopping || !(context is IInternalWorldViewContext current)
                    || !current.TryGetViewWorld(out var world, out var lifetime) || world == null || lifetime == null) return null;
                if (owner.worlds.TryGetValue(world, out var found)) return found;
                IInternalWorldViewRenderer renderer;
                try { renderer = owner.createRenderer(world, owner.provider); }
                catch { return null; }
                IDisposable? worldTracking = null, providerTracking = null;
                void End()
                {
                    renderer.Stopped -= End;
                    var worldHold = worldTracking; worldTracking = null;
                    var providerHold = providerTracking; providerTracking = null;
                    try { worldHold?.Dispose(); } finally { providerHold?.Dispose(); }
                }
                renderer.Stopped += End;
                try { worldTracking = lifetime.Track(renderer); providerTracking = owner.provider.Lifetime.Track(renderer); }
                catch { try { renderer.Dispose(); } catch { } return null; }
                owner.worlds.Add(world, renderer);
                return renderer;
            }
            public bool IsAvailable => context is IInternalReplicationContext runtime
                && runtime.Replication.ExecutionContext != InternalExecutionContext.Participant
                && runtime.Replication.Realm == InternalNetRealm.Offline && Renderer()?.IsAlive == true;
            public IReadOnlyList<string> EnvironmentPresets => Renderer()?.Presets ?? Array.Empty<string>();
            private OperationResult<IViewLease> Change(Func<IViewControls, OperationResult<IViewLease>> change)
            {
                if (!(context is IInternalReplicationContext runtime)) return Unavailable();
                if (runtime.Replication.ExecutionContext == InternalExecutionContext.Participant)
                    return OperationResult<IViewLease>.Failure(ModErrorCode.NotAuthoritative, "Move world view changes to the gamemode's server controller.");
                if (context.Lifetime.IsStopping || owner.provider.Lifetime.IsStopping)
                    return OperationResult<IViewLease>.Failure(ModErrorCode.Cancelled, "The view's scope or provider is stopping.");
                if (runtime.Replication.Realm != InternalNetRealm.Offline)
                    return OperationResult<IViewLease>.Failure(ModErrorCode.Unavailable, "World view changes currently require single player.");
                var renderer = Renderer();
                return renderer == null ? Unavailable() : change(runtime.Replication.WorldView(renderer));
            }
            public OperationResult<IViewLease> Environment(string preset) => Change(view => view.Environment(preset));
            public OperationResult<IViewLease> Fog(VisionFog fog) => Change(view => view.Fog(fog));
            public OperationResult<IViewLease> Hide(IEntity entity) => Change(view => view.Hide(entity));
        }
    }
}
