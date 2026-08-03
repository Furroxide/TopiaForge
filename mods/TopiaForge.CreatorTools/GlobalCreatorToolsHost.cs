using System;
using TopiaForge.CreatorTools.Shared;
using TopiaForge.Mods;

namespace TopiaForge.CreatorTools
{
    internal sealed class GlobalCreatorToolsHost : ICreatorToolHost, IDisposable
    {
        private readonly ICreatorToolHostService router;
        private readonly IModContext context;
        private readonly IWorldGamemodeService? worlds;
        private readonly IMultiplayerSession? multiplayer;
        private readonly CreatorWorkbench workbench;
        private readonly IDisposable eligibilitySubscription;
        private readonly IDisposable? multiplayerSubscription;
        private bool disposed;

        public GlobalCreatorToolsHost(
            IModContext context,
            CreatorToolsConfig config,
            ICreatorContentService content,
            ICreatorToolHostService router,
            IRobotAgentService robots)
        {
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            this.router = router ?? throw new ArgumentNullException(nameof(router));
            context.Extensions.TryGet(out worlds);
            context.Extensions.TryGet(out multiplayer);
            workbench = new CreatorWorkbench(
                context,
                new CreatorWorkbenchOptions(
                    "global-creator",
                    "CREATOR TOOLS",
                    CreatorProjectScope.Global,
                    config.MaximumInstances,
                    config.ShowSessionHud,
                    config.ConversationEnabled,
                    config.ChatMaxTurns,
                    config.ChatTemperature,
                    worldId: string.Empty,
                    acceptanceChallenge: config.AcceptanceChallenge),
                content,
                robots,
                RequestHide,
                EndExplicitSession);
            if (worlds != null) worlds.SessionChanged += OnWorldSessionChanged;
            multiplayerSubscription = multiplayer?.SubscribeChanged(_ => EnforceEligibility());
            eligibilitySubscription = context.Events.SubscribeUpdate(_ => EnforceEligibility());
        }

        public bool IsOpen => workbench.IsVisible;

        public bool CanOpen(CreatorToolOpenContext context)
        {
            if (disposed || worlds == null || worlds.CurrentSession != null
                || string.IsNullOrWhiteSpace(context.ActiveSceneName)
                || GameScenes.IsNonGameplayScene(context.ActiveSceneName)
                || HasRemoteMultiplayer()) return false;
            return !(worlds is IWorldTransitionState transition) || !transition.IsTransitionInFlight;
        }

        public OperationResult<bool> Open(CreatorToolOpenContext context)
        {
            if (CanOpen(context)) return workbench.Open();
            // An observed refusal outside stable standalone gameplay is itself
            // required evidence for creator.f5-routing-and-session.
            workbench.Recorder?.Observe(
                CreatorObservation.BlockedOutsideStandaloneGameplay);
            return OperationResult<bool>.Failure(ModErrorCode.Unavailable, "Global Creator Tools is unavailable in this scene.");
        }

        public OperationResult<bool> Close(CreatorToolCloseReason reason)
        {
            if (reason == CreatorToolCloseReason.UserToggle || reason == CreatorToolCloseReason.Requested)
            {
                return workbench.Hide();
            }
            return workbench.EndSession();
        }

        public OperationResult<bool> EndSession()
        {
            var result = workbench.EndSession();
            if (router.ActiveHost != null) router.CloseActive(CreatorToolCloseReason.Requested);
            return result;
        }
        public string DescribeStatus() => workbench.DescribeStatus();

        public void Dispose()
        {
            if (disposed) return;
            if (workbench.IsSessionActive)
            {
                workbench.Recorder?.Observe(
                    CreatorObservation.ModUnloadClosedWorkbench);
            }
            disposed = true;
            if (worlds != null) worlds.SessionChanged -= OnWorldSessionChanged;
            multiplayerSubscription?.Dispose();
            eligibilitySubscription.Dispose();
            workbench.Dispose();
        }

        private void EndExplicitSession()
        {
            EndSession();
        }

        private void RequestHide()
        {
            router.CloseActive(CreatorToolCloseReason.Requested);
        }

        private void OnWorldSessionChanged(WorldSession session)
        {
            if (!workbench.IsSessionActive) return;
            workbench.Recorder?.Observe(
                CreatorObservation.WorldsTransitionClosedWorkbench);
            EndSession();
        }

        private void EnforceEligibility()
        {
            if (!workbench.IsSessionActive) return;
            var sceneName = context.Scenes.TryGetActive(out var scene) && scene != null ? scene.Name : string.Empty;
            // Attribute the closure to the condition that actually caused it so
            // scene replacement and remote multiplayer are distinct evidence.
            var remoteMultiplayer = HasRemoteMultiplayer();
            var worldsTransition = worlds == null || worlds.CurrentSession != null
                || worlds is IWorldTransitionState transition && transition.IsTransitionInFlight;
            var sceneReplaced = string.IsNullOrWhiteSpace(sceneName)
                || GameScenes.IsNonGameplayScene(sceneName);
            if (!worldsTransition && !sceneReplaced && !remoteMultiplayer) return;
            if (remoteMultiplayer)
            {
                workbench.Recorder?.Observe(
                    CreatorObservation.RemoteMultiplayerClosedWorkbench);
            }
            else if (sceneReplaced)
            {
                workbench.Recorder?.Observe(
                    CreatorObservation.SceneReplacementClosedWorkbench);
            }
            else
            {
                workbench.Recorder?.Observe(
                    CreatorObservation.WorldsTransitionClosedWorkbench);
            }
            EndSession();
        }

        private bool HasRemoteMultiplayer()
        {
            if (multiplayer == null) return false;
            var snapshot = multiplayer.Snapshot;
            return !CreatorToolsMultiplayerPolicy.Allows(snapshot);
        }
    }
}
