using System;
using TopiaForge.CreatorTools.Shared;
using TopiaForge.Mods;

namespace TopiaForge.Sandbox
{
    /// <summary>Hosts the shared F5 creator workbench while the managed Sandbox gamemode is active.</summary>
    internal sealed class SandboxController : ICreatorToolHost, IDisposable
    {
        private readonly ICreatorToolHostService router;
        private readonly CreatorWorkbench workbench;
        private bool disposed;

        public SandboxController(
            IModContext context,
            SandboxConfig config,
            IRobotAgentService robots,
            ICreatorContentService content,
            ICreatorToolHostService router,
            string worldId)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (string.IsNullOrWhiteSpace(worldId)) throw new ArgumentException("A Sandbox world id is required.", nameof(worldId));
            this.router = router ?? throw new ArgumentNullException(nameof(router));
            workbench = new CreatorWorkbench(
                context,
                new CreatorWorkbenchOptions(
                    "sandbox-creator",
                    "CREATOR SANDBOX",
                    CreatorProjectScope.Sandbox,
                    config.MaxSpawnedObjects,
                    config.ShowHud,
                    config.ConversationEnabled,
                    config.ChatMaxTurns,
                    config.ChatTemperature,
                    worldId),
                content ?? throw new ArgumentNullException(nameof(content)),
                robots ?? throw new ArgumentNullException(nameof(robots)),
                RequestHide,
                EndExplicitSession);
        }

        public bool IsOpen => workbench.IsVisible;

        public bool CanOpen(CreatorToolOpenContext context) => !disposed;

        public OperationResult<bool> Open(CreatorToolOpenContext context) =>
            disposed
                ? OperationResult<bool>.Failure(ModErrorCode.InvalidState, "The Sandbox creator host is disposed.")
                : workbench.Open();

        public OperationResult<bool> Close(CreatorToolCloseReason reason)
        {
            if (reason == CreatorToolCloseReason.UserToggle || reason == CreatorToolCloseReason.Requested)
            {
                return workbench.Hide();
            }
            return workbench.EndSession();
        }

        public OperationResult<string> SpawnRobot() => workbench.SpawnRobot();
        public OperationResult<string> Undo() => workbench.Undo();
        public OperationResult<string> CleanUpEverything() => workbench.CleanUpEverything();
        public OperationResult<string> ToggleRobotSimulation() => workbench.ToggleRobotSimulation();
        public string DescribeStatus() => workbench.DescribeStatus();

        public OperationResult<bool> EndSession()
        {
            var result = workbench.EndSession();
            if (router.ActiveHost != null) router.CloseActive(CreatorToolCloseReason.Requested);
            return result;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
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
    }
}
