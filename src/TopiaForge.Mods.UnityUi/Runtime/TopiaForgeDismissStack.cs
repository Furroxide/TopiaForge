using System;
using UnityEngine;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// The Back stack: one Back press, Escape or a controller's B, dismisses only the top-most visible surface: modals beat
    /// windows, later registrations beat earlier ones. TopiaForge takes that press from the game, so the game sees nothing
    /// of it, and keeps the key until it is released; the next press with no surface left reaches the game as before. While
    /// they are on the stack, surfaces also take what they claim from the game: kit modals, fullscreen tools and the manager
    /// overlay take menu navigation, a window takes it while one of its controls is selected, and a key capture takes the
    /// keyboard (see TopiaForgeInputOwnership). Unity's legacy input cannot be filtered, so code reading it still sees the key.
    /// </summary>
    public static class TopiaForgeDismissStack
    {
        private static readonly TopiaForgeDismissOrder Order =
            new TopiaForgeDismissOrder(TopiaForgeInputOwnership.IsLiveRoot, TopiaForgeInputOwnership.HoldsSelection);

        /// <summary>Gets count.</summary>
        public static int Count => Order.Count;

        /// <summary>Pushes a dismissible surface; while it is on the stack, Back is TopiaForge's.</summary>
        public static void Push(ITopiaForgeDismissable entry)
        {
            Push(entry, TopiaForgeInputClaim.Back, null);
        }

        /// <summary>Removes the specified state entry.</summary>
        public static void Remove(ITopiaForgeDismissable entry)
        {
            Order.Remove(entry);
        }

        /// <summary>Picks the entry to dismiss: highest band wins, then latest pushed.</summary>
        public static ITopiaForgeDismissable? Top()
        {
            return Order.Top();
        }

        /// <summary>
        /// Pushes a surface showing in <paramref name="root"/> that also takes <paramref name="claim"/> from the game while it
        /// is on the stack. Once the root is destroyed or hidden, the surface takes nothing and is never dismissed.
        /// </summary>
        internal static void Push(ITopiaForgeDismissable entry, TopiaForgeInputClaim claim, GameObject? root)
        {
            if (Order.Push(entry, claim, root))
            {
                TopiaForgeRuntime.Ensure();
            }
        }

        /// <summary>Gets what the surfaces on the stack take from the game now.</summary>
        internal static TopiaForgeInputClaim Claims => Order.Claims();

        /// <summary>Gets the root of the topmost surface if it takes navigation now, or null.</summary>
        internal static GameObject? FocusRoot => Order.FocusRoot() as GameObject;

        /// <summary>Gets whether a surface holds the focus outright: the manager overlay, a modal, a fullscreen tool or a key capture.</summary>
        internal static bool HoldsFocus => Order.HoldsFocus();

        internal static void TickEscape()
        {
            // Every frame, the stack empty or not: a key still held when a surface opens never counts as a press. The surface
            // leaves the stack before it closes, so one that throws as it closes has still given Back up.
            var top = Order.TakeOnBack(TopiaForgeInput.BackHeld(), TopiaForgeInput.BackPressedThisFrame());
            if (top == null)
            {
                return;
            }

            // The player's Escape dismisses the surface as that surface's owner's action.
            using (TopiaForgeIntent.PlayerAction(OwnerOf(top)))
            {
                try
                {
                    top.Dismiss();
                }
                catch (Exception exception)
                {
                    TopiaForgeLog.Error(exception, "A TopiaForge surface failed to close on Back.");
                }
            }
        }

        private static string? OwnerOf(ITopiaForgeDismissable entry)
        {
            switch (entry)
            {
                case TopiaForgeWidget widget:
                    return widget.Host.OwnerId;
                case TopiaForgeModalInstance modal:
                    return modal.OwnerId;
                default:
                    return null;
            }
        }

        internal static void Reset()
        {
            Order.Clear();
        }
    }
}
