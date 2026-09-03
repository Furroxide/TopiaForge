using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.Zombies
{
    /// <summary>
    /// The implementation owner named by
    /// <c>contributions.gamemodes[0].implementation.type</c> in Zombies' manifest.
    /// </summary>
    /// <remarks>
    /// <para>
    /// V5 could not express this. The manifest listed an id, a name and a description, and the code
    /// that actually ran was a lambda passed to <see cref="GamemodeHost{TController}"/> -- so the
    /// manifest could name a gamemode that nothing implemented, and nothing would have said so.
    /// </para>
    /// <para>
    /// A thin wrapper over the controller construction that already exists, and nothing more. The
    /// runtime does not call it yet; the session orchestrator that will is stage 3. It exists now so
    /// the V6 declaration never names a type that is not there.
    /// </para>
    /// <para>
    /// Public parameterless constructor on purpose: it mirrors how <c>entryType</c> is already
    /// instantiated (<c>ModRuntime.Loading.cs</c> uses <c>Activator.CreateInstance(type)</c>), so a
    /// declared implementation is constructed the same way a declared mod is. Everything the
    /// controller needs comes from the session instead.
    /// </para>
    /// </remarks>
    public sealed class ZombiesGamemode : IGamemodeFactory
    {
        /// <inheritdoc />
        public string GamemodeId => ZombiesMod.GamemodeId;

        /// <inheritdoc />
        public OperationResult<IGamemodeController> CreateController(IGamemodeSession session)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }

            var context = session.Mod;
            if (!context.TryGetExtension<IRobotAgentService>(out var robots))
            {
                return OperationResult<IGamemodeController>.Failure(
                    ModErrorCode.Unavailable,
                    "RobotKit is unavailable, so Zombies cannot create infected robot entities.");
            }

            var config = ZombiesMod.ReadNormalizedConfig(context);
            return OperationResult<IGamemodeController>.Success(new ZombiesController(
                context,
                config,
                robots,
                session.World,
                cancellationToken => ReturnToMenuAsync(session, cancellationToken)));
        }

        /// <summary>
        /// Ends the run by returning to the game's own menu, then closing the session -- but only if
        /// this session is still the current one, so a run that ended while the scene was loading
        /// cannot end a newer run that replaced it.
        /// </summary>
        private static async Task<OperationResult<SceneSnapshot>> ReturnToMenuAsync(
            IGamemodeSession session,
            CancellationToken cancellationToken)
        {
            var result = await session.Mod.Scenes.LoadAsync(
                new SceneLoadRequest(GameScenes.MainMenuSceneName, SceneLoadMode.Single),
                cancellationToken);
            if (result.Succeeded)
            {
                session.End(WorldSessionEndReason.EndedByGamemode);
            }

            return result;
        }
    }
}
