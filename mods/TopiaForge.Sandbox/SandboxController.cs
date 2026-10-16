using System;
using System.Collections.Generic;
using TopiaForge.CreatorTools.Shared;
using TopiaForge.Mods;

namespace TopiaForge.Sandbox
{
    /// <summary>Hosts the shared F5 creator workbench while the managed Sandbox gamemode is active.</summary>
    internal sealed class SandboxController : ICreatorToolHost, IGamemodeController
    {
        private readonly ICreatorToolHostService router;
        private readonly CreatorWorkbench workbench;
        private readonly string ownerId;
        private readonly Func<bool> isActive;
        private bool disposed;
        private readonly List<IDisposable> registrations = new List<IDisposable>();
        internal bool IsDisposed => disposed;
        internal void Own(IDisposable registration) => registrations.Add(registration);

        public SandboxController(
            IModContext context,
            SandboxConfig config,
            IRobotAgentService robots,
            ICreatorContentService content,
            ICreatorToolHostService router,
            string worldId, Func<bool>? isActive = null)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (string.IsNullOrWhiteSpace(worldId)) throw new ArgumentException("A Sandbox world id is required.", nameof(worldId));
            this.router = router ?? throw new ArgumentNullException(nameof(router));
            ownerId = context.Identity.Id;
            this.isActive = isActive ?? (() => true);
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

        public bool CanOpen(CreatorToolOpenContext context) => !disposed && isActive();

        public OperationResult<bool> Open(CreatorToolOpenContext context) =>
            !CanOpen(context)
                ? OperationResult<bool>.Failure(ModErrorCode.InvalidState, "The Sandbox creator session is not running.")
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
            CloseOwnedHost();
            return result;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            var failures = new List<Exception>();
            for (var index = registrations.Count - 1; index >= 0; index--)
                try { registrations[index].Dispose(); } catch (Exception exception) { failures.Add(exception); }
            registrations.Clear();
            try { workbench.Dispose(); } catch (Exception exception) { failures.Add(exception); }
            if (failures.Count != 0) throw new AggregateException("Sandbox cleanup failed.", failures);
        }

        private void EndExplicitSession()
        {
            EndSession();
        }

        private void CloseOwnedHost()
        {
            var active = router.ActiveHost;
            if (active != null && string.Equals(active.SourceId, ownerId, StringComparison.Ordinal)
                && string.Equals(active.LocalId, "sandbox", StringComparison.Ordinal))
                router.CloseActive(CreatorToolCloseReason.Requested);
        }

        private void RequestHide()
        {
            CloseOwnedHost();
        }
    }
}
