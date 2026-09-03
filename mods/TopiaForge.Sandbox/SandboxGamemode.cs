using System;
using TopiaForge.Mods;

namespace TopiaForge.Sandbox
{
    /// <summary>
    /// The implementation owner named by
    /// <c>contributions.gamemodes[0].implementation.type</c> in Sandbox's manifest.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sandbox used to attach to a gamemode the Worlds provider declared on its behalf. That made the
    /// creator workbench and the world infrastructure share one identity, so neither could be enabled,
    /// disabled or launched without the other, and the manifest of the package that actually implements
    /// the gameplay said nothing about it at all. Sandbox now owns its own gamemode.
    /// </para>
    /// <para>
    /// A thin wrapper over the controller construction that already exists. The runtime does not call it
    /// yet; the session orchestrator that will is stage 3.
    /// </para>
    /// </remarks>
    public sealed class SandboxGamemode : IGamemodeFactory
    {
        private IDisposable? creatorHostRegistration;

        /// <inheritdoc />
        public string GamemodeId => SandboxMod.GamemodeId;

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
                    "RobotKit is unavailable, so Sandbox cannot create safe robot entities.");
            }

            if (!context.TryGetExtension<ICreatorContentService>(out var content)
                || !context.TryGetExtension<ICreatorToolHostService>(out var router))
            {
                return OperationResult<IGamemodeController>.Failure(
                    ModErrorCode.Unavailable,
                    "Creator Content is unavailable, so the shared F5 Sandbox workbench cannot start.");
            }

            var config = SandboxMod.ReadNormalizedConfig(context);
            var controller = new SandboxController(
                context,
                config,
                robots,
                content,
                router,
                session.WorldId);

            RegisterCreatorHost(context, router, config, controller);
            return OperationResult<IGamemodeController>.Success(controller);
        }

        /// <summary>
        /// Publishes the controller as the shared creator workbench host, replacing any host this factory
        /// registered for an earlier session.
        /// </summary>
        /// <remarks>
        /// Registration is best effort: a session whose workbench could not be published is still a
        /// playable sandbox, so this warns rather than failing the session. The handle is deferred onto
        /// the mod lifetime so unloading releases it even if no later session replaces it.
        /// </remarks>
        private void RegisterCreatorHost(
            IModContext context,
            ICreatorToolHostService router,
            SandboxConfig config,
            SandboxController controller)
        {
            creatorHostRegistration?.Dispose();
            creatorHostRegistration = null;

            var registered = router.RegisterHost(new CreatorToolHostRegistrationRequest(
                "sandbox",
                "Creator Sandbox",
                priority: 200,
                controller,
                toggleBinding: string.Equals(config.SpawnMenuKey, "F5", StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : config.SpawnMenuKey));
            if (!registered.TryGetValue(out var handle))
            {
                context.Logger.Warn("Sandbox F5 host registration failed: " + registered.ErrorMessage);
                return;
            }

            creatorHostRegistration = handle;
            context.Lifetime.Defer(() =>
            {
                creatorHostRegistration?.Dispose();
                creatorHostRegistration = null;
            });
        }
    }
}
