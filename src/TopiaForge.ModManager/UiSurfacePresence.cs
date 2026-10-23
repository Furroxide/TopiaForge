using System;

namespace TopiaForge.ModManager
{
    /// <summary>What a change in a surface's presence asks of its native view and of its owner.</summary>
    [Flags]
    internal enum UiPresenceEffects
    {
        /// <summary>Nothing to do.</summary>
        None = 0,

        /// <summary>Put the native view on screen.</summary>
        ShowNative = 1,

        /// <summary>Take the native view off screen.</summary>
        HideNative = 2,

        /// <summary>Raise the surface's <c>Dismissed</c> event: a visible surface became hidden for the mod's own reasons.</summary>
        Dismissed = 4,
    }

    /// <summary>
    /// Whether one UI surface shows, and why not. The mod shows or hides it; a gameplay HUD is also kept hidden while the
    /// game is not in gameplay; and a surface whose canvas was destroyed outside TopiaForge, or that was disposed, never
    /// shows again. <see cref="IsVisible"/> is the truth a mod reads through <c>IUiSurface.IsVisible</c>. Each change
    /// returns what the native view and the owner must do. Unity-free and unit-tested.
    /// </summary>
    internal sealed class UiSurfacePresence
    {
        /// <summary>Creates the presence of a surface the mod has just shown.</summary>
        /// <param name="followsGameplay">Whether the surface is a HUD that shows only in gameplay.</param>
        /// <param name="gameplay">Whether the game is in gameplay now.</param>
        public UiSurfacePresence(bool followsGameplay, bool gameplay)
        {
            FollowsGameplay = followsGameplay;
            Gameplay = gameplay;
            Requested = true;
        }

        /// <summary>Gets whether the surface hides outside gameplay.</summary>
        public bool FollowsGameplay { get; }

        /// <summary>Gets whether the mod asked for the surface to show.</summary>
        public bool Requested { get; private set; }

        /// <summary>Gets whether the game was in gameplay at the last change TopiaForge reported.</summary>
        public bool Gameplay { get; private set; }

        /// <summary>Gets whether the surface's canvas was destroyed outside TopiaForge.</summary>
        public bool IsLost { get; private set; }

        /// <summary>Gets whether the surface was disposed.</summary>
        public bool IsDisposed { get; private set; }

        /// <summary>Gets whether the surface shows on screen now.</summary>
        public bool IsVisible => Requested && !IsLost && !IsDisposed && (!FollowsGameplay || Gameplay);

        /// <summary>Gets whether the native view can still be shown, hidden or written to.</summary>
        public bool IsLive => !IsLost && !IsDisposed;

        /// <summary>The mod shows the surface.</summary>
        public UiPresenceEffects Show()
        {
            if (!IsLive)
            {
                return UiPresenceEffects.None;
            }

            var before = IsVisible;
            Requested = true;
            return before == IsVisible ? UiPresenceEffects.None : UiPresenceEffects.ShowNative;
        }

        /// <summary>The mod hides the surface. Only a surface that was showing raises <c>Dismissed</c>.</summary>
        public UiPresenceEffects Hide()
        {
            if (!IsLive)
            {
                return UiPresenceEffects.None;
            }

            var before = IsVisible;
            Requested = false;
            return before ? UiPresenceEffects.HideNative | UiPresenceEffects.Dismissed : UiPresenceEffects.None;
        }

        /// <summary>The player closed the native view, as a window's close button or Escape does; it is already off screen.</summary>
        public UiPresenceEffects ClosedByPlayer()
        {
            if (!IsLive || !Requested)
            {
                return UiPresenceEffects.None;
            }

            var before = IsVisible;
            Requested = false;
            return before ? UiPresenceEffects.Dismissed : UiPresenceEffects.None;
        }

        /// <summary>
        /// The game entered or left gameplay. A gameplay HUD the mod shows appears or hides with it, without
        /// <c>Dismissed</c>: TopiaForge hid it, not the mod or the player.
        /// </summary>
        public UiPresenceEffects SetGameplay(bool gameplay)
        {
            var before = IsVisible;
            Gameplay = gameplay;
            if (!IsLive || before == IsVisible)
            {
                return UiPresenceEffects.None;
            }

            return IsVisible ? UiPresenceEffects.ShowNative : UiPresenceEffects.HideNative;
        }

        /// <summary>
        /// The surface's canvas was destroyed outside TopiaForge, by the game or another mod. It never shows again, and a
        /// surface that was showing raises <c>Dismissed</c>; the native view is already gone.
        /// </summary>
        public UiPresenceEffects Lose()
        {
            if (!IsLive)
            {
                return UiPresenceEffects.None;
            }

            var before = IsVisible;
            IsLost = true;
            return before ? UiPresenceEffects.Dismissed : UiPresenceEffects.None;
        }

        /// <summary>The surface was disposed. Disposal never raises <c>Dismissed</c>.</summary>
        public void Dispose()
        {
            IsDisposed = true;
        }
    }
}
