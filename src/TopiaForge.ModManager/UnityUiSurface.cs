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
        private sealed class UnityUiSurface : IUiSurface
        {
            private TopiaForgeWidget? widget;
            private TopiaForgeWindow? window;
            private TopiaForgeLabel? body;
            private TopiaForgeContainer? compositionParent;
            private TopiaForgeContainer? compositionRoot;
            private readonly IModLifetime lifetime;
            private readonly UiCallbackGate callbacks;
            private Action<string>? releaseId;
            private Action? windowClosed;
            private IDisposable? lifetimeLease;
            private int visible;
            private int disposed;

            private UnityUiSurface(
                string id,
                TopiaForgeWidget widget,
                TopiaForgeWindow? window,
                TopiaForgeLabel body,
                TopiaForgeContainer compositionParent,
                IModLifetime lifetime,
                IModLogger logger,
                Action<string> releaseId)
            {
                Id = id;
                this.widget = widget;
                this.window = window;
                this.body = body;
                this.compositionParent = compositionParent;
                this.lifetime = lifetime;
                this.releaseId = releaseId;
                callbacks = new UiCallbackGate(lifetime, logger);
                if (window != null)
                {
                    windowClosed = HandleWindowClosed;
                    window.Closed += windowClosed;
                }
            }

            public string Id { get; }
            public bool IsVisible => Volatile.Read(ref visible) != 0 && widget != null;

            public static UnityUiSurface ForWindow(
                string id,
                TopiaForgeWindow window,
                TopiaForgeLabel body,
                TopiaForgeContainer compositionParent,
                IModLifetime lifetime,
                IModLogger logger,
                Action<string> releaseId)
            {
                return new UnityUiSurface(id, window, window, body, compositionParent, lifetime, logger, releaseId);
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
                return new UnityUiSurface(id, widget, null, body, compositionParent, lifetime, logger, releaseId);
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
                else
                {
                    current.SetVisible(false);
                }
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
                try
                {
                    UiComposition.Validate(content);
                    next = parent.Column(TopiaForgeGap.Sm, TopiaForgeGap.None);
                    RenderNode(content, next, callbacks);
                    var previous = compositionRoot;
                    compositionRoot = next;
                    previous?.Destroy();
                    return OperationResult<bool>.Success(true);
                }
                catch (ArgumentException exception)
                {
                    next?.Destroy();
                    return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, exception.Message);
                }
                catch (Exception exception)
                {
                    next?.Destroy();
                    return OperationResult<bool>.Failure(
                        ModErrorCode.External,
                        "TopiaForgeUi could not render the composition: " + exception.Message);
                }
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
                body = null;
                var currentWindow = window;
                var closed = Interlocked.Exchange(ref windowClosed, null);
                if (currentWindow != null && closed != null)
                {
                    currentWindow.Closed -= closed;
                }

                window = null;
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

            private void HandleWindowClosed()
            {
                Interlocked.Exchange(ref visible, 0);
            }
        }
    }
}
