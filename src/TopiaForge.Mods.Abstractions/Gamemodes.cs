using System;
using System.Collections.Generic;

namespace TopiaForge.Mods
{
    /// <summary>
    /// The object a manifest's <c>contributions.gamemodes[].implementation</c> binds to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Manifest V5 could describe a gamemode but not bind one. A <c>worldGamemodes</c> entry was an id, a
    /// name and a description, and the code that actually ran was reached through a different mechanism
    /// entirely -- so the two could disagree and nothing noticed. A V6 declaration names a type, and this
    /// is the interface that type implements.
    /// </para>
    /// <para>
    /// Contrast <see cref="GamemodeHost{TController}"/>, which starts a session by observing a
    /// <c>SessionChanged</c> event and matching an id. That works, but it makes starting a gamemode a
    /// side effect of a notification: a mod that fails to subscribe simply never runs, and nothing can
    /// tell that apart from a mod that chose not to. Here the runtime asks a named type for a controller
    /// and gets an answer, so "declared but not bound" is a reportable state rather than silence.
    /// </para>
    /// <para>
    /// Nothing calls this yet. The session orchestrator that will is stage 3; this is the contract the
    /// V6 manifests are written against, and the first-party gamemodes implement it now so a declaration
    /// never names a type that does not exist.
    /// </para>
    /// </remarks>
    public interface IGamemodeFactory
    {
        /// <summary>Gets the declared gamemode id this factory implements.</summary>
        /// <remarks>
        /// Must equal the <c>id</c> of a <c>contributions.gamemodes</c> entry in the owning package's
        /// manifest. The runtime matches on this rather than on the type name, so a rename inside the
        /// assembly is not a contract change.
        /// </remarks>
        string GamemodeId { get; }

        /// <summary>Creates the controller that runs one session of this gamemode.</summary>
        /// <param name="session">The session being started.</param>
        /// <returns>
        /// The controller, or a typed failure. A failure ends the session with a reported diagnostic
        /// instead of leaving the player in a world with nothing running in it.
        /// </returns>
        OperationResult<IGamemodeController> CreateController(IGamemodeSession session);
    }

    /// <summary>
    /// One running session of a gamemode. Disposed when the session ends, the active gamemode changes,
    /// or the owning mod unloads.
    /// </summary>
    public interface IGamemodeController : IDisposable
    {
    }

    /// <summary>What a gamemode is handed when one of its sessions starts.</summary>
    /// <remarks>
    /// Deliberately small. It carries what the first-party gamemodes actually read today and nothing
    /// speculative: a context with fields nobody consumes is the same defect as a manifest option the
    /// runtime never reads.
    /// </remarks>
    public interface IGamemodeSession
    {
        /// <summary>Gets the gamemode being run.</summary>
        string GamemodeId { get; }

        /// <summary>Gets the world this session was resolved into.</summary>
        string WorldId { get; }

        /// <summary>Gets the launch target the player picked, or empty when started without one.</summary>
        string LaunchTargetId { get; }

        /// <summary>Gets the owning mod's context.</summary>
        IModContext Mod { get; }

        /// <summary>Gets the underlying world session.</summary>
        WorldSession World { get; }

        /// <summary>Offers a pause-menu action for as long as this session runs.</summary>
        /// <remarks>
        /// Best effort: a session that cannot reach the game's pause UI reports a failure rather than
        /// failing the gamemode. The handle releases the action; ending the session releases it anyway.
        /// </remarks>
        /// <param name="action">The action to offer.</param>
        OperationResult<IDisposable> AddPauseAction(WorldPauseAction action);

        /// <summary>Ends this session.</summary>
        /// <param name="reason">Why the session ended.</param>
        void End(WorldSessionEndReason reason);
    }
}
