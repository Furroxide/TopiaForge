using System.Collections.Generic;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>A surface Escape can dismiss (windows, modals).</summary>
    public interface ITopiaForgeDismissable
    {
        /// <summary>Gets band.</summary>
        TopiaForgeLayerBand Band { get; }

        /// <summary>Dismisses the active UI element.</summary>
        void Dismiss();
    }

    /// <summary>
    /// The Escape-close stack: one Escape press dismisses only the top-most visible
    /// surface — modals beat windows, later registrations beat earlier ones. The game
    /// still sees the key (documented limitation: BepInEx UI cannot consume input
    /// before the game).
    /// </summary>
    public static class TopiaForgeDismissStack
    {
        private static readonly List<ITopiaForgeDismissable> Entries = new List<ITopiaForgeDismissable>();

        /// <summary>Gets count.</summary>
        public static int Count => Entries.Count;

        /// <summary>Pushes a dismissible layer and returns its ownership token.</summary>
        public static void Push(ITopiaForgeDismissable entry)
        {
            if (!Entries.Contains(entry))
            {
                Entries.Add(entry);
                TopiaForgeRuntime.Ensure();
            }
        }

        /// <summary>Removes the specified state entry.</summary>
        public static void Remove(ITopiaForgeDismissable entry)
        {
            Entries.Remove(entry);
        }

        /// <summary>Picks the entry to dismiss: highest band wins, then latest pushed.</summary>
        public static ITopiaForgeDismissable? Top()
        {
            ITopiaForgeDismissable? top = null;
            for (var index = 0; index < Entries.Count; index++)
            {
                var entry = Entries[index];
                if (top == null || entry.Band >= top.Band)
                {
                    top = entry;
                }
            }

            return top;
        }

        internal static void TickEscape()
        {
            if (Entries.Count == 0 || !TopiaForgeInput.EscapePressedThisFrame())
            {
                return;
            }

            var top = Top();
            if (top == null)
            {
                return;
            }

            // The player's Escape dismisses the surface as that surface's owner's action.
            using (TopiaForgeIntent.PlayerAction(OwnerOf(top)))
            {
                top.Dismiss();
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
            Entries.Clear();
        }
    }
}
