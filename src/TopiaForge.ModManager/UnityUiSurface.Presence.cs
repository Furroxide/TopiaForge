using System;
using System.Collections.Generic;
using System.Threading;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager
{
    internal sealed partial class OwnerUiService
    {
        private sealed partial class UnityUiSurface
        {
            private const string LostMessage =
                "The UI surface's canvas was destroyed outside TopiaForge, by the game or another mod, so it no longer shows. "
                + "Create the surface again to show it.";

            /// <summary>Puts a new surface on screen, or keeps a gameplay HUD hidden until gameplay.</summary>
            public void Present()
            {
                if (presence.IsVisible)
                {
                    ShowNativeView();
                }
                else
                {
                    HideNativeView();
                }
            }

            public void Show()
            {
                UnityMainThreadGuard.AssertCurrent();
                ThrowIfDisposed();
                Apply(presence.Show());
            }

            public void Hide()
            {
                UnityMainThreadGuard.AssertCurrent();
                ThrowIfDisposed();
                Apply(presence.Hide());
            }

            /// <summary>The game entered or left gameplay; a gameplay HUD the mod shows appears or hides with it.</summary>
            public void ApplyGameplay(bool gameplay)
            {
                if (Volatile.Read(ref disposed) != 0)
                {
                    return;
                }

                Apply(presence.SetGameplay(gameplay));
            }

            /// <summary>
            /// The surface's canvas was destroyed outside TopiaForge. The surface reports itself hidden for good, gives its id
            /// back so the mod can create it again, says so in the mod's log, and raises Dismissed if it was showing.
            /// </summary>
            public void HandleLost()
            {
                if (Volatile.Read(ref disposed) != 0 || presence.IsLost)
                {
                    return;
                }

                var effects = presence.Lose();
                DetachNativeClosed();
                placement?.Remove(dockHandle);
                placement = null;
                Interlocked.Exchange(ref release, null)?.Invoke(this);
                try
                {
                    logger.Warn("UI surface '" + Id + "': " + LostMessage);
                }
                catch
                {
                }

                if ((effects & UiPresenceEffects.Dismissed) != 0)
                {
                    RaiseDismissed();
                }
            }

            public void Dispose()
            {
                UnityMainThreadGuard.AssertCurrent();
                if (Interlocked.Exchange(ref disposed, 1) != 0)
                {
                    return;
                }

                presence.Dispose();
                callbacks.Close();
                compositionRoot = null;
                compositionParent = null;
                retainedGraphs = new Dictionary<string, TopiaForgeGraphCanvas>(StringComparer.Ordinal);
                body = null;
                DetachNativeClosed();
                window = null;
                fullscreen = null;
                Dismissed = null;
                placement?.Remove(dockHandle);
                placement = null;
                var currentWidget = Interlocked.Exchange(ref widget, null);
                var releaseOwner = Interlocked.Exchange(ref release, null);
                var lease = Interlocked.Exchange(ref lifetimeLease, null);
                try
                {
                    currentWidget?.Destroy();
                }
                finally
                {
                    try
                    {
                        releaseOwner?.Invoke(this);
                    }
                    finally
                    {
                        lease?.Dispose();
                    }
                }
            }

            private void HandleNativeClosed()
            {
                if (Volatile.Read(ref disposed) != 0)
                {
                    return;
                }

                if ((presence.ClosedByPlayer() & UiPresenceEffects.Dismissed) != 0)
                {
                    RaiseDismissed();
                }
            }

            private void Apply(UiPresenceEffects effects)
            {
                if ((effects & UiPresenceEffects.ShowNative) != 0)
                {
                    ShowNativeView();
                }
                else if ((effects & UiPresenceEffects.HideNative) != 0)
                {
                    HideNativeView();
                }

                if ((effects & UiPresenceEffects.Dismissed) != 0)
                {
                    RaiseDismissed();
                }
            }

            private void ShowNativeView()
            {
                if (window != null)
                {
                    window.Show();
                }
                else if (fullscreen != null)
                {
                    fullscreen.Show();
                }
                else if (widget != null)
                {
                    widget.SetShown(true);
                    placement?.SetShown(dockHandle, true);
                }
            }

            private void HideNativeView()
            {
                if (window != null)
                {
                    window.Close();
                }
                else if (fullscreen != null)
                {
                    fullscreen.Close();
                }
                else if (widget != null)
                {
                    widget.SetShown(false);
                    placement?.SetShown(dockHandle, false);
                }
            }

            private void DetachNativeClosed()
            {
                var closed = Interlocked.Exchange(ref nativeClosed, null);
                if (closed == null)
                {
                    return;
                }

                if (window != null)
                {
                    window.Closed -= closed;
                }
                else if (fullscreen != null)
                {
                    fullscreen.Closed -= closed;
                }
            }

            private void ThrowIfDisposed()
            {
                if (Volatile.Read(ref disposed) != 0)
                {
                    throw new ObjectDisposedException(nameof(UnityUiSurface));
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
        }
    }
}
