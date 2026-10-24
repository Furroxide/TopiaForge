using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// The dismiss stack's order and what its surfaces take from the game, Unity-free. One Back press dismisses the topmost
    /// live surface: the highest band, then the latest pushed. Every live surface claims Back, plus what it was pushed with.
    /// A surface's root is what it shows in: a surface whose root is gone (destroyed with a scene, or by the game or another
    /// mod) or hidden claims nothing and is never dismissed, so no way out of a surface leaves the game's input taken. A
    /// surface pushed without a root is always live.
    /// </summary>
    internal sealed class TopiaForgeDismissOrder
    {
        private readonly List<ITopiaForgeDismissable> surfaces = new List<ITopiaForgeDismissable>();
        private readonly List<TopiaForgeInputClaim> claims = new List<TopiaForgeInputClaim>();
        private readonly List<object?> roots = new List<object?>();
        private readonly TopiaForgeBackPress back = new TopiaForgeBackPress();
        private readonly Func<object, bool> isLive;
        private readonly Func<object, bool> holdsSelection;

        /// <summary>
        /// Creates an empty order. <paramref name="isLive"/> says whether a root still exists and shows;
        /// <paramref name="holdsSelection"/> whether the EventSystem selection is one of a root's controls.
        /// </summary>
        public TopiaForgeDismissOrder(Func<object, bool> isLive, Func<object, bool> holdsSelection)
        {
            this.isLive = isLive ?? throw new ArgumentNullException(nameof(isLive));
            this.holdsSelection = holdsSelection ?? throw new ArgumentNullException(nameof(holdsSelection));
        }

        /// <summary>Gets how many surfaces are on the stack, live or not.</summary>
        public int Count => surfaces.Count;

        /// <summary>
        /// Puts a surface on top of the stack, or gives one already on it, in its place, the claim and root it is pushed with
        /// now. Returns whether the surface was new.
        /// </summary>
        public bool Push(ITopiaForgeDismissable surface, TopiaForgeInputClaim claim, object? root)
        {
            if (surface == null)
            {
                throw new ArgumentNullException(nameof(surface));
            }

            var index = surfaces.IndexOf(surface);
            if (index >= 0)
            {
                claims[index] = claim;
                roots[index] = root;
                return false;
            }

            surfaces.Add(surface);
            claims.Add(claim);
            roots.Add(root);
            return true;
        }

        /// <summary>Takes a surface off the stack. Returns whether it was on it.</summary>
        public bool Remove(ITopiaForgeDismissable surface)
        {
            var index = surfaces.IndexOf(surface);
            if (index < 0)
            {
                return false;
            }

            RemoveAt(index);
            return true;
        }

        /// <summary>Gets whether a surface is on the stack.</summary>
        public bool Contains(ITopiaForgeDismissable surface) => surfaces.IndexOf(surface) >= 0;

        /// <summary>Gets the surface one Back press dismisses: the topmost live one, or null.</summary>
        public ITopiaForgeDismissable? Top()
        {
            var index = TopIndex();
            return index < 0 ? null : surfaces[index];
        }

        /// <summary>
        /// One frame of Back. <paramref name="held"/> is whether any way of reading Back holds it now, and
        /// <paramref name="pressedThisFrame"/> whether any reported a press this frame. A press that starts while nothing held
        /// Back takes the topmost live surface off the stack and returns it to be dismissed, so one that throws as it closes
        /// has still given Back up; otherwise this returns null. Call it every frame, the stack empty or not, so a key still
        /// held when a surface opens never counts as a press.
        /// </summary>
        public ITopiaForgeDismissable? TakeOnBack(bool held, bool pressedThisFrame)
        {
            if (!back.Tick(held, pressedThisFrame))
            {
                return null;
            }

            var index = TopIndex();
            if (index < 0)
            {
                return null;
            }

            var surface = surfaces[index];
            RemoveAt(index);
            return surface;
        }

        /// <summary>Gets what the live surfaces take from the game now, resolved: Back, Navigation and Keyboard only.</summary>
        public TopiaForgeInputClaim Claims()
        {
            var result = TopiaForgeInputClaim.None;
            for (var index = 0; index < surfaces.Count; index++)
            {
                if (IsLive(index))
                {
                    result |= Resolve(index);
                }
            }

            return result;
        }

        /// <summary>
        /// Gets whether a live surface holds the player's focus outright: one that takes navigation whatever is selected (the
        /// manager overlay, a modal, a fullscreen tool) or the keyboard (a key capture). A window holds it only in part.
        /// </summary>
        public bool HoldsFocus()
        {
            for (var index = 0; index < surfaces.Count; index++)
            {
                if (IsLive(index) && (claims[index] & (TopiaForgeInputClaim.Navigation | TopiaForgeInputClaim.Keyboard)) != 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Gets the root of the topmost live navigation owner. A Back-only window does not block it, but a keyboard capture
        /// above it leaves the keys to itself, and so does any surface in the modal band or above, whatever it claims: nothing
        /// beneath a modal moves or submits. The kit moves and submits only inside the returned root.
        /// </summary>
        public object? FocusRoot()
        {
            var top = -1;
            for (var index = 0; index < surfaces.Count; index++)
            {
                if (IsLive(index)
                    && (surfaces[index].Band >= TopiaForgeLayerBand.Modal
                        || (Resolve(index) & (TopiaForgeInputClaim.Navigation | TopiaForgeInputClaim.Keyboard)) != 0)
                    && (top < 0 || surfaces[index].Band >= surfaces[top].Band))
                {
                    top = index;
                }
            }

            return top >= 0 && (Resolve(top) & TopiaForgeInputClaim.Navigation) != 0 ? roots[top] : null;
        }

        /// <summary>Empties the stack and forgets a held Back, as the kit's teardown does.</summary>
        public void Clear()
        {
            surfaces.Clear();
            claims.Clear();
            roots.Clear();
            back.Reset();
        }

        private TopiaForgeInputClaim Resolve(int index)
        {
            var claim = claims[index];
            var resolved = TopiaForgeInputClaim.Back | (claim & (TopiaForgeInputClaim.Navigation | TopiaForgeInputClaim.Keyboard));
            var root = roots[index];
            if ((claim & TopiaForgeInputClaim.NavigationWhileSelected) != 0 && root != null && holdsSelection(root))
            {
                resolved |= TopiaForgeInputClaim.Navigation;
            }

            return resolved;
        }

        private bool IsLive(int index)
        {
            var root = roots[index];
            return root == null || isLive(root);
        }

        private int TopIndex()
        {
            var top = -1;
            for (var index = 0; index < surfaces.Count; index++)
            {
                if (IsLive(index) && (top < 0 || surfaces[index].Band >= surfaces[top].Band))
                {
                    top = index;
                }
            }

            return top;
        }

        private void RemoveAt(int index)
        {
            surfaces.RemoveAt(index);
            claims.RemoveAt(index);
            roots.RemoveAt(index);
        }
    }
}
