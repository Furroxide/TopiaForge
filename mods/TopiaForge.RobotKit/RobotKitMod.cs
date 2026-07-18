using System;
using TopiaForge.Mods;

namespace TopiaForge.RobotKit
{
    // Framework mod that publishes IRobotAgentService so gameplay mods can spawn standard-agent robots — clones
    // that come up native (body, animation, native locomotion) and are driven by the game's own pathing — then
    // override only the behaviour and visuals they need, without re-deriving the GameCode reflection themselves.
    // Mirrors the TopiaForge.Worlds lifecycle discipline.
    public sealed class RobotKitMod : TopiaForgeMod
    {
        private IModLogger? logger;
        private RobotAgentService? service;
        private RobotBrainQueryService? brainService;
        private RobotConversationService? conversationService;
        private PlayerDialogueInputService? dialogueInputService;
        private RobotObjectiveService? objectiveService;
        private bool agentTickFailed;
        private bool objectiveTickFailed;
        private bool brainTickFailed;
        private bool conversationTickFailed;
        private bool dialogueTickFailed;

        protected override void OnLoad()
        {
            logger = Context.Logger;

            service = new RobotAgentService(logger);
            brainService = new RobotBrainQueryService(logger);
            conversationService = new RobotConversationService(brainService, logger);
            dialogueInputService = new PlayerDialogueInputService(logger);
            // The objective service resolves Reprogram courier recipients back to agent handles via the agent
            // service (live-object reference -> IRobotAgent), staying Unity-free itself.
            objectiveService = new RobotObjectiveService(
                logger,
                null,
                entity => service?.FindAgentByEntity(entity),
                null,
                Context.Identity.Id);

            Context.Lifetime.Track(service);
            Context.Lifetime.Track(brainService);
            Context.Lifetime.Track(objectiveService);
            Context.Lifetime.Track(dialogueInputService);
            Context.Lifetime.Track(conversationService);
            RegisterExtension<IRobotAgentService>(service);
            RegisterExtension<IRobotBrainQueryService>(brainService);
            RegisterExtension<IRobotConversationService>(conversationService);
            RegisterExtension<IPlayerDialogueInputService>(dialogueInputService);
            RegisterExtension<IRobotObjectiveService>(objectiveService);

            Context.Events.SubscribeUpdate(OnUpdate);
            Context.Events.SubscribeSceneLoaded(OnSceneLoaded);
            logger.Info("TopiaForge RobotKit loaded; safe robot extensions registered.");
        }

        protected override void OnUnload()
        {
            // Runtime lifetime releases subscriptions, registrations, then services in reverse order.
        }

        private void OnUpdate(float deltaTime)
        {
            try
            {
                service?.Tick(deltaTime);
                agentTickFailed = false;
            }
            catch (Exception exception)
            {
                ReportTickFailure(ref agentTickFailed, "agent service", exception);
            }

            // After the agent service, so objectives react to this frame's reached/moving state.
            try
            {
                objectiveService?.Tick(deltaTime);
                objectiveTickFailed = false;
            }
            catch (Exception exception)
            {
                ReportTickFailure(ref objectiveTickFailed, "objective service", exception);
            }

            try
            {
                brainService?.Tick(deltaTime);
                brainTickFailed = false;
            }
            catch (Exception exception)
            {
                ReportTickFailure(ref brainTickFailed, "brain service", exception);
            }

            // After the brain service, so a conversation turn that completed this frame is observed this frame.
            try
            {
                conversationService?.Tick(deltaTime);
                conversationTickFailed = false;
            }
            catch (Exception exception)
            {
                ReportTickFailure(ref conversationTickFailed, "conversation service", exception);
            }

            try
            {
                dialogueInputService?.Tick(deltaTime);
                dialogueTickFailed = false;
            }
            catch (Exception exception)
            {
                ReportTickFailure(ref dialogueTickFailed, "dialogue input service", exception);
            }
        }

        private void OnSceneLoaded(string sceneName)
        {
            // Consumers release handles before providers clear their underlying agents/queries.
            RunLifecycle(() => conversationService?.OnSceneChanged(), "conversation scene cleanup");
            RunLifecycle(() => dialogueInputService?.OnSceneChanged(), "dialogue scene cleanup");
            RunLifecycle(() => objectiveService?.OnSceneChanged(), "objective scene cleanup");
            RunLifecycle(() => brainService?.OnSceneChanged(), "brain scene cleanup");
            RunLifecycle(() => service?.OnSceneChanged(), "agent scene cleanup");
        }

        private void ReportTickFailure(ref bool alreadyReported, string component, Exception exception)
        {
            if (!alreadyReported)
            {
                logger?.Error(exception, "RobotKit " + component
                    + " tick failed; other RobotKit services will continue.");
            }

            alreadyReported = true;
        }

        private void RunLifecycle(Action action, string operation)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                logger?.Error(exception, "RobotKit failed during " + operation + ".");
            }
        }

        private void DisposeSafely(IDisposable? component, string name)
        {
            try
            {
                component?.Dispose();
            }
            catch (Exception exception)
            {
                logger?.Error(exception, "RobotKit failed to dispose its " + name + ".");
            }
        }

        private void RegisterExtension<T>(T provider) where T : class
        {
            var registration = Context.Extensions.Register(provider);
            if (!registration.Succeeded)
            {
                throw new InvalidOperationException(registration.ErrorMessage);
            }
        }
    }
}
