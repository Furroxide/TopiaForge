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

        public void OnLoad(IModContext context)
        {
            this.context = context;

            service = new RobotAgentService(context.Logger);
            brainService = new RobotBrainQueryService(context.Logger);
            conversationService = new RobotConversationService(brainService, context.Logger);
            dialogueInputService = new PlayerDialogueInputService(context.Logger);

            var registry = context.GetService<IModServiceRegistry>();
            registry?.Register<IRobotAgentService>(context.ModId, service);
            registry?.Register<IRobotBrainQueryService>(context.ModId, brainService);
            registry?.Register<IRobotConversationService>(context.ModId, conversationService);
            registry?.Register<IPlayerDialogueInputService>(context.ModId, dialogueInputService);

            context.Update += OnUpdate;
            context.SceneLoaded += OnSceneLoaded;
            context.Logger.Info("Robotopia RobotKit loaded; IRobotAgentService + IRobotBrainQueryService + IRobotConversationService + IPlayerDialogueInputService registered (poll IsAvailable once a level is loaded).");
        }

        public void OnUnload()
        {
            if (context != null)
            {
                context.Update -= OnUpdate;
                context.SceneLoaded -= OnSceneLoaded;
                context.GetService<IModServiceRegistry>()?.UnregisterOwner(context.ModId);
            }

            service?.Dispose();
            service = null;
            brainService?.Dispose();
            brainService = null;
            conversationService?.Dispose();
            conversationService = null;
            dialogueInputService?.Dispose();
            dialogueInputService = null;
            context = null;
        }

        private void OnUpdate(float deltaTime)
        {
            service?.Tick(deltaTime);
            brainService?.Tick(deltaTime);
            // After the brain service, so a conversation turn that completed this frame is observed this frame.
            conversationService?.Tick(deltaTime);
            dialogueInputService?.Tick(deltaTime);
        }

        private void OnSceneLoaded(string sceneName)
        {
            service?.OnSceneChanged();
            brainService?.OnSceneChanged();
            conversationService?.OnSceneChanged();
            dialogueInputService?.OnSceneChanged();
        }
    }
}
