using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;

namespace TopiaForge.ModManager
{
    internal sealed partial class OwnerUiService
    {
        private sealed partial class UnityUiSurface : IUiSurface, IUiSurfaceDismissalSource
        {
            private TopiaForgeWidget? widget;
            private TopiaForgeWindow? window;
            private TopiaForgeFullscreenTool? fullscreen;
            private TopiaForgeLabel? body;
            private TopiaForgeContainer? compositionParent;
            private TopiaForgeContainer? compositionRoot;
            private IReadOnlyDictionary<string, TopiaForgeGraphCanvas> retainedGraphs =
                new Dictionary<string, TopiaForgeGraphCanvas>(StringComparer.Ordinal);
            private readonly IModLifetime lifetime;
            private readonly IModLogger logger;
            private readonly UiCallbackGate callbacks;
            private readonly UiSurfacePresence presence;
            private Action<UnityUiSurface>? release;
            private Action? nativeClosed;
            private IDisposable? lifetimeLease;
            private UnityUiHudPlacement? placement;
            private int dockHandle;
            private int disposed;

            private UnityUiSurface(
                string id,
                GameObject canvasRoot,
                TopiaForgeWidget widget,
                TopiaForgeWindow? window,
                TopiaForgeFullscreenTool? fullscreen,
                TopiaForgeLabel body,
                TopiaForgeContainer compositionParent,
                UiSurfacePresence presence,
                IModLifetime lifetime,
                IModLogger logger,
                Action<UnityUiSurface> release)
            {
                Id = id;
                CanvasRoot = canvasRoot;
                this.widget = widget;
                this.window = window;
                this.fullscreen = fullscreen;
                this.body = body;
                this.compositionParent = compositionParent;
                this.presence = presence;
                this.lifetime = lifetime;
                this.logger = logger;
                this.release = release;
                callbacks = new UiCallbackGate(lifetime, logger);
                if (window != null)
                {
                    nativeClosed = HandleNativeClosed;
                    window.Closed += nativeClosed;
                }
                else if (fullscreen != null)
                {
                    nativeClosed = HandleNativeClosed;
                    fullscreen.Closed += nativeClosed;
                }
            }

            public string Id { get; }

            /// <summary>Gets the kit canvas the surface draws on, by which a canvas destroyed outside TopiaForge is matched.</summary>
            internal GameObject CanvasRoot { get; }

            /// <summary>Gets whether the surface is a HUD that hides outside gameplay.</summary>
            internal bool FollowsGameplay => presence.FollowsGameplay;

            public bool IsVisible => presence.IsVisible;

            public event Action? Dismissed;

            public static UnityUiSurface ForWindow(
                string id,
                TopiaForgeWindow window,
                TopiaForgeLabel body,
                TopiaForgeContainer compositionParent,
                IModLifetime lifetime,
                IModLogger logger,
                Action<UnityUiSurface> release)
            {
                return new UnityUiSurface(id, window.CanvasRoot, window, window, null, body, compositionParent,
                    new UiSurfacePresence(followsGameplay: false, gameplay: true), lifetime, logger, release);
            }

            public static UnityUiSurface ForFullscreen(
                string id,
                TopiaForgeFullscreenTool fullscreen,
                TopiaForgeLabel body,
                TopiaForgeContainer compositionParent,
                IModLifetime lifetime,
                IModLogger logger,
                Action<UnityUiSurface> release)
            {
                return new UnityUiSurface(id, fullscreen.CanvasRoot, fullscreen, null, fullscreen, body, compositionParent,
                    new UiSurfacePresence(followsGameplay: false, gameplay: true), lifetime, logger, release);
            }

            public static UnityUiSurface ForHud(
                string id,
                TopiaForgeHudLayer layer,
                TopiaForgeLabel body,
                TopiaForgeContainer compositionParent,
                bool followsGameplay,
                bool gameplay,
                IModLifetime lifetime,
                IModLogger logger,
                Action<UnityUiSurface> release)
            {
                return new UnityUiSurface(id, layer.Go, layer, null, null, body, compositionParent,
                    new UiSurfacePresence(followsGameplay, gameplay), lifetime, logger, release);
            }

            public void AttachLifetimeLease(IDisposable lease)
            {
                lifetimeLease = lease ?? throw new ArgumentNullException(nameof(lease));
            }

            /// <summary>Docks the HUD's panel through the process-wide placement, which stacks HUDs sharing a corner.</summary>
            public void AttachDock(UnityUiHudPlacement hudPlacement, int handle)
            {
                placement = hudPlacement;
                dockHandle = handle;
            }

            public void SetBody(string value)
            {
                UnityMainThreadGuard.AssertCurrent();
                var label = body ?? throw new ObjectDisposedException(nameof(UnityUiSurface));
                if (presence.IsLost)
                {
                    return;
                }

                label.SetText(value ?? string.Empty);
            }

            public OperationResult<bool> SetContent(UiNode content)
            {
                UnityMainThreadGuard.AssertCurrent();
                if (content == null) throw new ArgumentNullException(nameof(content));
                var parent = compositionParent;
                if (lifetime.IsStopping)
                {
                    return OperationResult<bool>.Failure(
                        ModErrorCode.Cancelled,
                        "The mod UI surface is stopping.");
                }

                if (presence.IsLost)
                {
                    return OperationResult<bool>.Failure(ModErrorCode.InvalidState, LostMessage);
                }

                if (parent == null || widget == null)
                {
                    return OperationResult<bool>.Failure(
                        ModErrorCode.InvalidState,
                        "The mod UI surface is disposed.");
                }

                TopiaForgeContainer? next = null;
                UiGraphRetentionTransaction? graphTransaction = null;
                try
                {
                    UiComposition.Validate(content);
                    next = parent.Column(TopiaForgeGap.Sm, TopiaForgeGap.None);
                    graphTransaction = new UiGraphRetentionTransaction(retainedGraphs);
                    RenderNode(content, next, callbacks, graphTransaction);
                }
                catch (ArgumentException exception)
                {
                    var cleanup = RollbackFailedRender(graphTransaction, parent, next);
                    return OperationResult<bool>.Failure(
                        ModErrorCode.InvalidArgument,
                        exception.Message + cleanup);
                }
                catch (Exception exception)
                {
                    var cleanup = RollbackFailedRender(graphTransaction, parent, next);
                    return OperationResult<bool>.Failure(
                        ModErrorCode.External,
                        "TopiaForgeUi could not render the composition: " + exception.Message + cleanup);
                }

                var previous = compositionRoot;
                compositionRoot = next;
                retainedGraphs = graphTransaction!.Commit();
                try
                {
                    previous?.Destroy();
                }
                catch (Exception exception)
                {
                    return OperationResult<bool>.Failure(
                        ModErrorCode.External,
                        "TopiaForgeUi replaced the composition, but could not clean up stale UI: " +
                        exception.Message);
                }

                return OperationResult<bool>.Success(true);
            }

            private static string RollbackFailedRender(
                UiGraphRetentionTransaction? transaction,
                TopiaForgeContainer fallbackParent,
                TopiaForgeContainer? failedRoot)
            {
                Exception? cleanupFailure = null;
                try
                {
                    cleanupFailure = transaction?.Rollback(fallbackParent);
                }
                catch (Exception exception)
                {
                    cleanupFailure = exception;
                }

                try
                {
                    failedRoot?.Destroy();
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                }

                return cleanupFailure == null
                    ? string.Empty
                    : " UI rollback also failed: " + cleanupFailure.Message;
            }
        }
    }
}
