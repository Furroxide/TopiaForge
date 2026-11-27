using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal sealed partial class MultiplayerPlayers
    {
        private const int MaximumOverlaysPerScope = 4;
        private readonly ConditionalWeakTable<IModLifetime, List<OverlaySlot>> overlaySlots =
            new ConditionalWeakTable<IModLifetime, List<OverlaySlot>>();

        internal OperationResult<IUiSurface> CreateContextSurface(RuntimeReplication context, IUiService local, UiSurfaceRequest request)
        {
            var presentation = Presentation;
            // Keep eligible solo surfaces for promotion to a listen host without rebuilding their author trees.
            var mirror = presentation != null && PresentationRoutingPolicy.Route(NetRealm.ListenHost, context.Profile,
                PresentationKind.HudSurface) == PresentationRoute.MirrorToEveryone;
            if (request.Kind == UiSurfaceKind.Overlay)
                return CreateOverlay(context.Lifetime, request, kind => mirror && kind == UiOverlayContentKind.PresentationOnly
                    ? presentation!.CreateSharedSurface(context, local, request) : local.CreateSurface(request));

            return mirror && request.Kind == UiSurfaceKind.Hud
                ? presentation!.CreateSharedSurface(context, local, request) : local.CreateSurface(request);
        }

        private OperationResult<IUiSurface> CreateOverlay(IModLifetime lifetime, UiSurfaceRequest request,
            Func<UiOverlayContentKind, OperationResult<IUiSurface>> create)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (lifetime.IsStopping)
                return OperationResult<IUiSurface>.Failure(ModErrorCode.Cancelled, "The context is stopping.");

            var admission = UiOverlayAdmission.Check(request.Content, request.Dismissible);
            if (!admission.TryGetValue(out var kind))
                return OperationResult<IUiSurface>.Failure(admission.ErrorCode, admission.ErrorMessage);

            // Author scopes own their local, mirrored and targeted overlays together. A receiver canvas never
            // reserves another slot, and a hidden overlay still holds its slot.
            var slots = overlaySlots.GetOrCreateValue(lifetime);
            for (var index = slots.Count - 1; index >= 0; index--)
                if (!slots[index].IsLive) slots.RemoveAt(index);
            if (slots.Count >= MaximumOverlaysPerScope)
                return OperationResult<IUiSurface>.Failure(ModErrorCode.RateLimited, "A scope can have at most four live overlays.");

            var slot = new OverlaySlot();
            slots.Add(slot);
            var committed = false;
            try
            {
                var result = create(kind);
                if (!result.TryGetValue(out var surface)) return result;
                if (!(surface is MirroredHudPresenter || surface is RemoteUiSurface))
                {
                    var local = new LocalOverlay(surface, request, kind);
                    local.Tracking = lifetime.Track(local);
                    surface = local;
                    result = OperationResult<IUiSurface>.Success(surface);
                }

                slot.Handle = new WeakReference<IUiSurface>(surface);
                committed = true;
                return result;
            }
            finally
            {
                if (!committed) slots.Remove(slot);
            }
        }

        private sealed class OverlaySlot
        {
            internal WeakReference<IUiSurface>? Handle;
            internal bool IsLive => Handle == null || Handle.TryGetTarget(out var surface) && Alive(surface);

            private static bool Alive(IUiSurface surface) => surface switch
            {
                MirroredHudPresenter mirror => mirror.IsAlive,
                RemoteUiSurface targeted => !targeted.Closed && targeted.Target.IsPresent,
                _ => MultiplayerPresentation.SurfaceAlive(surface),
            };
        }

        private sealed class LocalOverlay : IUiSurface, IUiSurfaceDismissalSource, IPresentationSurfaceState
        {
            private readonly IUiSurface inner;
            private readonly bool dismissible;
            private readonly UiOverlayContentKind kind;
            private UiNode? content;
            private bool disposed;
            internal IDisposable? Tracking;

            internal LocalOverlay(IUiSurface inner, UiSurfaceRequest request, UiOverlayContentKind kind)
            {
                this.inner = inner;
                dismissible = request.Dismissible;
                this.kind = kind;
                content = request.Content;
            }

            public string Id => inner.Id;
            public bool IsVisible => !disposed && inner.IsVisible;
            public bool IsAlive => !disposed && MultiplayerPresentation.SurfaceAlive(inner);
            public event Action? Dismissed
            {
                add { if (inner is IUiSurfaceDismissalSource source) source.Dismissed += value; }
                remove { if (inner is IUiSurfaceDismissalSource source) source.Dismissed -= value; }
            }

            public void Show() => inner.Show();
            public void Hide() => inner.Hide();
            public void SetBody(string value) => inner.SetBody(value);

            public OperationResult<bool> SetContent(UiNode value)
            {
                if (ReferenceEquals(content, value)) return inner.SetContent(value);
                if (value == null) throw new ArgumentNullException(nameof(value));
                var admission = UiOverlayAdmission.Check(value, dismissible, kind);
                if (!admission.Succeeded) return OperationResult<bool>.Failure(admission.ErrorCode, admission.ErrorMessage);

                var result = inner.SetContent(value);
                if (result.Succeeded && !disposed) content = value;
                return result;
            }

            public void Dispose()
            {
                UnityMainThreadGuard.AssertCurrent();
                if (disposed) return;
                disposed = true;
                try { inner.Dispose(); }
                finally { Tracking?.Dispose(); Tracking = null; }
            }
        }
    }
}
