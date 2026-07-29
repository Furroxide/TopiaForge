using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager
{
    internal sealed partial class OwnerUiService
    {
        private sealed class UnityUiSurface : IUiSurface, IUiSurfaceDismissalSource
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
            private readonly UiCallbackGate callbacks;
            private Action<string>? releaseId;
            private Action? nativeClosed;
            private IDisposable? lifetimeLease;
            private int visible;
            private int disposed;

            private UnityUiSurface(
                string id,
                TopiaForgeWidget widget,
                TopiaForgeWindow? window,
                TopiaForgeFullscreenTool? fullscreen,
                TopiaForgeLabel body,
                TopiaForgeContainer compositionParent,
                IModLifetime lifetime,
                IModLogger logger,
                Action<string> releaseId)
            {
                Id = id;
                this.widget = widget;
                this.window = window;
                this.fullscreen = fullscreen;
                this.body = body;
                this.compositionParent = compositionParent;
                this.lifetime = lifetime;
                this.releaseId = releaseId;
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
            public bool IsVisible => Volatile.Read(ref visible) != 0 && widget != null;

            public event Action? Dismissed;

            public static UnityUiSurface ForWindow(
                string id,
                TopiaForgeWindow window,
                TopiaForgeLabel body,
                TopiaForgeContainer compositionParent,
                IModLifetime lifetime,
                IModLogger logger,
                Action<string> releaseId)
            {
                return new UnityUiSurface(id, window, window, null, body, compositionParent, lifetime, logger, releaseId);
            }

            public static UnityUiSurface ForFullscreen(
                string id,
                TopiaForgeFullscreenTool fullscreen,
                TopiaForgeLabel body,
                TopiaForgeContainer compositionParent,
                IModLifetime lifetime,
                IModLogger logger,
                Action<string> releaseId)
            {
                return new UnityUiSurface(
                    id,
                    fullscreen,
                    null,
                    fullscreen,
                    body,
                    compositionParent,
                    lifetime,
                    logger,
                    releaseId);
            }

            public static UnityUiSurface ForWidget(
                string id,
                TopiaForgeWidget widget,
                TopiaForgeLabel body,
                TopiaForgeContainer compositionParent,
                IModLifetime lifetime,
                IModLogger logger,
                Action<string> releaseId)
            {
                return new UnityUiSurface(id, widget, null, null, body, compositionParent, lifetime, logger, releaseId);
            }

            public void AttachLifetimeLease(IDisposable lease)
            {
                lifetimeLease = lease ?? throw new ArgumentNullException(nameof(lease));
            }

            public void Show()
            {
                UnityMainThreadGuard.AssertCurrent();
                var current = widget ?? throw new ObjectDisposedException(nameof(UnityUiSurface));
                if (Interlocked.Exchange(ref visible, 1) != 0)
                {
                    return;
                }

                if (window != null)
                {
                    window.Show();
                }
                else if (fullscreen != null)
                {
                    fullscreen.Show();
                }
                else
                {
                    current.SetVisible(true);
                }
            }

            public void Hide()
            {
                UnityMainThreadGuard.AssertCurrent();
                var current = widget ?? throw new ObjectDisposedException(nameof(UnityUiSurface));
                if (Interlocked.Exchange(ref visible, 0) == 0)
                {
                    return;
                }

                if (window != null)
                {
                    window.Close();
                }
                else if (fullscreen != null)
                {
                    fullscreen.Close();
                }
                else
                {
                    current.SetVisible(false);
                }

                RaiseDismissed();
            }

            public void SetBody(string value)
            {
                UnityMainThreadGuard.AssertCurrent();
                var label = body ?? throw new ObjectDisposedException(nameof(UnityUiSurface));
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

            public void Dispose()
            {
                UnityMainThreadGuard.AssertCurrent();
                if (Interlocked.Exchange(ref disposed, 1) != 0)
                {
                    return;
                }

                Interlocked.Exchange(ref visible, 0);
                callbacks.Close();
                compositionRoot = null;
                compositionParent = null;
                retainedGraphs = new Dictionary<string, TopiaForgeGraphCanvas>(StringComparer.Ordinal);
                body = null;
                var currentWindow = window;
                var currentFullscreen = fullscreen;
                var closed = Interlocked.Exchange(ref nativeClosed, null);
                if (currentWindow != null && closed != null)
                {
                    currentWindow.Closed -= closed;
                }
                else if (currentFullscreen != null && closed != null)
                {
                    currentFullscreen.Closed -= closed;
                }

                window = null;
                fullscreen = null;
                Dismissed = null;
                var currentWidget = Interlocked.Exchange(ref widget, null);
                var release = Interlocked.Exchange(ref releaseId, null);
                var lease = Interlocked.Exchange(ref lifetimeLease, null);
                try
                {
                    currentWidget?.Destroy();
                }
                finally
                {
                    try
                    {
                        release?.Invoke(Id);
                    }
                    finally
                    {
                        lease?.Dispose();
                    }
                }
            }

            private void HandleNativeClosed()
            {
                if (Interlocked.Exchange(ref visible, 0) != 0)
                {
                    RaiseDismissed();
                }
            }

            private void RaiseDismissed()
            {
                var handlers = Dismissed;
                if (handlers != null)
                {
                    callbacks.Invoke(handlers, "surface '" + Id + "' dismissal");
                }
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
