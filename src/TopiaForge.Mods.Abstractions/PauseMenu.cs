using System;

namespace TopiaForge.Mods
{
    /// <summary>
    /// Session-scoped customization of the game's vanilla pause menu while a world/gamemode session is
    /// active. Lets a gamemode add its own actions ("RESTART RUN") and decide what happens when the player
    /// picks the vanilla exit-to-menu option, so the vanilla menu can never strand a modded session in a
    /// broken state. Everything here is best-effort: the provider resolves the game's pause UI reflectively
    /// and degrades gracefully (see <see cref="IsAvailable"/>) — the provider's scene-load session teardown
    /// remains the correctness backstop when the pause UI cannot be reached.
    /// </summary>
    public interface IWorldPauseMenuService
    {
        /// <summary>True once the game's pause UI has been resolved for the current session.</summary>
        bool IsAvailable { get; }

        /// <summary>
        /// Adds an action to the TopiaForgeUi pause companion while a world session is active. Returns a handle;
        /// dispose it to remove the action. Never throws on provider-side failures. Destructive actions are
        /// confirmed by the provider before their callback runs.
        /// </summary>
        IDisposable RegisterAction(WorldPauseAction action);

        /// <summary>
        /// Optional hook consulted when the player picks the vanilla exit-to-menu option during a session.
        /// Null (the default) behaves as <see cref="WorldPauseExitDecision.EndSessionAndExit"/>. A throwing
        /// interceptor is treated as the default decision so it can never eat the vanilla button.
        /// </summary>
        void SetExitInterceptor(Func<WorldPauseExitContext, WorldPauseExitDecision>? interceptor);
    }

    /// <summary>A gamemode-supplied pause-menu action.</summary>
    public sealed class WorldPauseAction
    {
        public WorldPauseAction(
            string id,
            string label,
            Action callback,
            bool closePauseMenu = true,
            int order = 0,
            bool destructive = false)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("A pause action needs a non-empty id.", nameof(id));
            }

            if (string.IsNullOrWhiteSpace(label))
            {
                throw new ArgumentException("A pause action needs a non-empty label.", nameof(label));
            }

            Id = id;
            Label = label;
            Callback = callback ?? throw new ArgumentNullException(nameof(callback));
            ClosePauseMenu = closePauseMenu;
            Order = order;
            Destructive = destructive;
        }

        public string Id { get; }
        public string Label { get; }
        public Action Callback { get; }

        /// <summary>Close the vanilla pause menu after the callback runs (default true).</summary>
        public bool ClosePauseMenu { get; }

        /// <summary>Sort key among registered actions; lower renders first.</summary>
        public int Order { get; }

        /// <summary>Require a TopiaForgeUi destructive confirmation before invoking this action.</summary>
        public bool Destructive { get; }
    }

    /// <summary>Context handed to the exit interceptor when the vanilla exit-to-menu option is picked.</summary>
    public sealed class WorldPauseExitContext
    {
        public WorldPauseExitContext(WorldSession session)
        {
            Session = session ?? throw new ArgumentNullException(nameof(session));
        }

        public WorldSession Session { get; }
    }

    public enum WorldPauseExitDecision
    {
        /// <summary>End the session cleanly, then let the vanilla exit handler run (the default).</summary>
        EndSessionAndExit,

        /// <summary>Run the vanilla exit handler without ending the session (the provider's scene-load
        /// teardown will still end it once the menu loads).</summary>
        ExitWithoutEnding,

        /// <summary>Swallow the click — the gamemode shows its own confirmation UI.</summary>
        Block
    }
}
