using System;
using TopiaForge.Mods;

namespace TopiaForge.Worlds
{
    /// <summary>
    /// A gamemode that imposes no rules: the player is in the declared world, and nothing else happens.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists so that shipping a world is not the same as shipping a gamemode. Before it, a world
    /// package had to name someone else's gamemode to be launchable at all, and the world template named
    /// the Sandbox creator mode -- a package it did not depend on, dragging in robot spawning and the
    /// creator toolchain for a mod whose whole content is a prefab. A world can now be played on its own
    /// terms by naming a neutral mode the world provider itself owns.
    /// </para>
    /// <para>
    /// The controller is deliberately empty. Under the V6 model the world's own declaration carries its
    /// content binding and its spawn policy, and placing the player is the session orchestrator's job,
    /// not the gamemode's. Free play is the absence of rules, so a controller that did anything would be
    /// doing something free play is not.
    /// </para>
    /// <para>
    /// Nothing calls this yet; the orchestrator that will is stage 3.
    /// </para>
    /// </remarks>
    public sealed class FreePlayGamemode : IGamemodeFactory
    {
        /// <summary>Gets the free-play gamemode id.</summary>
        public const string FreePlayGamemodeId = "io.github.furroxide.topiaforge.worlds.freeplay";

        internal const string MenuEntryId = "io.github.furroxide.topiaforge.worlds.freeplay.menu";

        /// <inheritdoc />
        public string GamemodeId => FreePlayGamemodeId;

        /// <inheritdoc />
        public OperationResult<IGamemodeController> CreateController(IGamemodeSession session)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }

            return OperationResult<IGamemodeController>.Success(new FreePlaySession(session.WorldId));
        }

        /// <summary>
        /// A session that runs no rules. It holds the world it was started in so a diagnostic can say
        /// which one, and releases nothing on disposal because it claimed nothing.
        /// </summary>
        private sealed class FreePlaySession : IGamemodeController
        {
            public FreePlaySession(string worldId) => WorldId = worldId;

            public string WorldId { get; }

            public void Dispose()
            {
                // Nothing to release. Stated rather than left blank so a later change that gives free
                // play something to own has an obvious place to release it.
            }
        }
    }
}
