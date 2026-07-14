using System;
using Robotopia.Mods;

namespace Robotopia.RobotKit
{
    // Framework mod that publishes IRobotAgentService so gameplay mods can spawn standard-agent robots — clones
    // that come up native (body, animation, native locomotion) and are driven by the game's own pathing — then
    // override only the behaviour and visuals they need, without re-deriving the GameCode reflection themselves.
    // Mirrors the Robotopia.Worlds lifecycle discipline.
    public sealed class RobotKitMod : IRobotopiaMod
    {
        private IModContext? context;
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

        public void OnLoad(IModContext context)
        {
            this.context = context;

            service = new RobotAgentService(context.Logger);
            brainService = new RobotBrainQueryService(context.Logger);
            conversationService = new RobotConversationService(brainService, context.Logger);
            dialogueInputService = new PlayerDialogueInputService(context.Logger);
            // The objective service resolves Reprogram courier recipients back to agent handles via the agent
            // service (live-object reference -> IRobotAgent), staying Unity-free itself.
            objectiveService = new RobotObjectiveService(context.Logger, null, obj => service?.FindAgentByGameObject(obj));

            var registry = context.GetService<IModServiceRegistry>();
            registry?.Register<IRobotAgentService>(context.ModId, service);
            registry?.Register<IRobotBrainQueryService>(context.ModId, brainService);
            registry?.Register<IRobotConversationService>(context.ModId, conversationService);
            registry?.Register<IPlayerDialogueInputService>(context.ModId, dialogueInputService);
            registry?.Register<IRobotObjectiveService>(context.ModId, objectiveService);

            context.Update += OnUpdate;
            context.SceneLoaded += OnSceneLoaded;
            context.Logger.Info("Robotopia RobotKit loaded; IRobotAgentService + IRobotBrainQueryService + IRobotConversationService + IPlayerDialogueInputService + IRobotObjectiveService registered (poll IsAvailable once a level is loaded).");
        }

        public void OnUnload()
        {
            if (context != null)
            {
                context.Update -= OnUpdate;
                context.SceneLoaded -= OnSceneLoaded;
                context.GetService<IModServiceRegistry>()?.UnregisterOwner(context.ModId);
            }

            // Dispose consumers before their providers, and isolate teardown so one broken adapter cannot strand
            // the remaining services or their Unity objects.
            DisposeSafely(conversationService, "conversation service");
            conversationService = null;
            DisposeSafely(dialogueInputService, "dialogue input service");
            dialogueInputService = null;
            DisposeSafely(objectiveService, "objective service");
            objectiveService = null;
            DisposeSafely(brainService, "brain service");
            brainService = null;
            DisposeSafely(service, "agent service");
            service = null;
            context = null;
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
                context?.Logger.Error(exception, "RobotKit " + component
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
                context?.Logger.Error(exception, "RobotKit failed during " + operation + ".");
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
                context?.Logger.Error(exception, "RobotKit failed to dispose its " + name + ".");
            }
        }
    }
}
