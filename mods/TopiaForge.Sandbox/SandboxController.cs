using System;
using System.Collections.Generic;
using System.Globalization;
using TopiaForge.Mods;

namespace TopiaForge.Sandbox
{
    /// <summary>Runs one safe-SDK sandbox session without exposing engine objects.</summary>
    internal sealed partial class SandboxController : IDisposable
    {
        private sealed class RobotEntry
        {
            public RobotEntry(IRobotAgent agent, string targetName, IDisposable cleanup)
            {
                Agent = agent;
                TargetName = targetName;
                Cleanup = cleanup;
            }

            public IRobotAgent Agent { get; }
            public string TargetName { get; }
            public IDisposable Cleanup { get; }
            public bool FollowingPlayer { get; set; }
        }

        private readonly IModContext context;
        private readonly SandboxConfig config;
        private readonly IRobotAgentService robots;
        private readonly IRobotObjectiveService? objectives;
        private readonly List<RobotEntry> spawned = new List<RobotEntry>();
        private readonly IDisposable updateSubscription;
        private readonly IInputAction? menuAction;
        private readonly IInputAction? undoAction;
        private readonly IInputAction? pauseAction;
        private readonly IDisposable? playerTargetCleanup;
        private IUiSurface? menu;
        private IUiSurface? hud;
        private IUiModal? confirmation;
        private string hudText = string.Empty;
        private int nextRobotNumber = 1;
        private bool robotsPaused;
        private bool disposed;

        public SandboxController(IModContext context, SandboxConfig config, IRobotAgentService robots)
        {
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            this.config = config ?? throw new ArgumentNullException(nameof(config));
            this.robots = robots ?? throw new ArgumentNullException(nameof(robots));
            context.Extensions.TryGet(out objectives);

            menuAction = RegisterAction(new InputActionDefinition(
                "creator-menu",
                "Open creator menu",
                new[] { InputBinding.Key(config.SpawnMenuKey) }));
            undoAction = RegisterAction(new InputActionDefinition(
                "undo-spawn",
                "Undo latest sandbox spawn",
                new[] { InputBinding.Key(config.UndoKey) }));
            pauseAction = RegisterAction(new InputActionDefinition(
                "toggle-robots",
                "Pause or resume sandbox robots",
                new[] { InputBinding.Key(config.FreezeKey) }));

            updateSubscription = context.Events.SubscribeUpdate(Update);
            if (objectives != null)
            {
                objectives.RegisterTarget("PLAYER", RobotTargetKind.Player, ResolvePlayerTarget)
                    .TryGetValue(out var playerTarget);
                playerTargetCleanup = playerTarget;
            }

            if (config.ShowHud)
            {
                var result = context.Ui.CreateSurface(new UiSurfaceRequest(
                    "sandbox-status",
                    "CREATOR SANDBOX",
                    string.Empty,
                    UiSurfaceKind.Hud,
                    360f,
                    130f));
                if (result.TryGetValue(out var surface))
                {
                    hud = surface;
                    hud.Show();
                }
            }

            RefreshHud(force: true);
        }

        private IInputAction? RegisterAction(InputActionDefinition definition)
        {
            var result = context.Input.RegisterAction(definition);
            if (result.TryGetValue(out var action))
            {
                return action;
            }

            context.Logger.Warn(
                "Sandbox input '" + definition.Name + "' is unavailable (" +
                result.ErrorCode + "): " + result.ErrorMessage);
            return null;
        }

        public OperationResult<string> SpawnRobot()
        {
            if (disposed)
            {
                return OperationResult<string>.Failure(ModErrorCode.InvalidState, "The sandbox session is not active.");
            }

            RemoveDeadEntries();
            if (spawned.Count >= config.MaxSpawnedObjects)
            {
                return OperationResult<string>.Failure(
                    ModErrorCode.Conflict,
                    "The configured sandbox spawn limit has been reached.");
            }

            if (!robots.IsAvailable)
            {
                return OperationResult<string>.Failure(ModErrorCode.Unavailable, "RobotKit cannot spawn in this scene.");
            }

            if (!context.LocalPlayer.TryGetSnapshot(out var player) || player == null)
            {
                return OperationResult<string>.Failure(ModErrorCode.Unavailable, "A gameplay player and camera are required.");
            }

            var spawnDistance = Math.Min(config.SpawnDistanceMax, 8f);
            var position = player.AimRay.GetPoint(spawnDistance);
            if (context.Physics.TryRaycast(player.AimRay, config.SpawnDistanceMax, out var hit) && hit != null)
            {
                position = hit.Point + (hit.Normal * 0.5f);
            }

            var number = nextRobotNumber++;
            var request = new RobotAgentSpawnRequest(
                position,
                -player.AimRay.Direction,
                brainMode: string.Equals(
                    config.DefaultRobotBrainMode,
                    "Autonomous",
                    StringComparison.OrdinalIgnoreCase)
                    ? RobotBrainMode.Autonomous
                    : RobotBrainMode.Dormant,
                name: "Sandbox Robot " + number.ToString(CultureInfo.InvariantCulture),
                interaction: RobotInteractionOptions.Custom(new RobotCustomInteraction(
                    "TOGGLE FOLLOW",
                    interaction => ToggleFollow(interaction.Agent))));

            var spawnResult = robots.Spawn(request);
            if (!spawnResult.TryGetValue(out var agent) || agent == null)
            {
                return OperationResult<string>.Failure(spawnResult.ErrorCode, spawnResult.ErrorMessage);
            }

            var targetName = "ROBOT " + number.ToString(CultureInfo.InvariantCulture);
            IRobotTargetRegistration? targetRegistration = null;
            if (objectives != null)
            {
                objectives.RegisterTarget(
                    targetName,
                    RobotTargetKind.Robot,
                    () => agent.IsAlive ? new RobotTargetSnapshot(agent.Position, agent) : (RobotTargetSnapshot?)null)
                    .TryGetValue(out targetRegistration);
            }

            var cleanup = context.Lifetime.Defer(() =>
            {
                targetRegistration?.Dispose();
                agent.Despawn();
            });
            spawned.Add(new RobotEntry(agent, targetName, cleanup));
            RefreshHud(force: true);
            context.Ui.ShowToast(targetName + " spawned.", UiTone.Success);
            return OperationResult<string>.Success(targetName + " spawned.");
        }

        public OperationResult<string> Undo()
        {
            RemoveDeadEntries();
            if (spawned.Count == 0)
            {
                return OperationResult<string>.Failure(ModErrorCode.NotFound, "There is nothing to undo.");
            }

            var index = spawned.Count - 1;
            var entry = spawned[index];
            spawned.RemoveAt(index);
            entry.Cleanup.Dispose();
            RefreshHud(force: true);
            context.Ui.ShowToast(entry.TargetName + " removed.");
            return OperationResult<string>.Success(entry.TargetName + " removed.");
        }

        public OperationResult<string> CleanUpEverything()
        {
            for (var index = spawned.Count - 1; index >= 0; index--)
            {
                spawned[index].Cleanup.Dispose();
            }

            var removed = spawned.Count;
            spawned.Clear();
            RefreshHud(force: true);
            context.Ui.ShowToast("Sandbox cleared.", UiTone.Success);
            return OperationResult<string>.Success(
                removed.ToString(CultureInfo.InvariantCulture) + " sandbox robots removed.");
        }

        public OperationResult<string> ToggleRobotSimulation()
        {
            robotsPaused = !robotsPaused;
            foreach (var entry in spawned)
            {
                if (!entry.Agent.IsAlive)
                {
                    continue;
                }

                if (robotsPaused)
                {
                    entry.Agent.Stop();
                }
                else if (entry.FollowingPlayer)
                {
                    FollowPlayer(entry);
                }
            }

            RefreshHud(force: true);
            var state = robotsPaused ? "paused" : "running";
            context.Ui.ShowToast("Sandbox robots " + state + ".");
            return OperationResult<string>.Success("Sandbox robots are " + state + ".");
        }

        public string DescribeStatus()
        {
            RemoveDeadEntries();
            return "robots=" + spawned.Count.ToString(CultureInfo.InvariantCulture)
                + ", simulation=" + (robotsPaused ? "paused" : "running")
                + ", navigation=" + (robots.IsNavigationAvailable ? "available" : "unavailable");
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            updateSubscription.Dispose();
            menuAction?.Dispose();
            undoAction?.Dispose();
            pauseAction?.Dispose();
            playerTargetCleanup?.Dispose();
            confirmation?.Dispose();
            menu?.Dispose();
            hud?.Dispose();
            confirmation = null;
            menu = null;
            hud = null;
            for (var index = spawned.Count - 1; index >= 0; index--)
            {
                spawned[index].Cleanup.Dispose();
            }

            spawned.Clear();
        }

        private void Update(float deltaTime)
        {
            if (disposed)
            {
                return;
            }

            if (menuAction?.WasPressed == true)
            {
                ToggleMenu();
            }

            if (undoAction?.WasPressed == true)
            {
                Undo();
            }

            if (pauseAction?.WasPressed == true)
            {
                ToggleRobotSimulation();
            }

            RemoveDeadEntries();
            RefreshHud(force: false);
        }

        private void ToggleFollow(IRobotAgent agent)
        {
            var entry = Find(agent.Id);
            if (entry == null || !entry.Agent.IsAlive)
            {
                return;
            }

            entry.FollowingPlayer = !entry.FollowingPlayer;
            if (entry.FollowingPlayer && !robotsPaused)
            {
                FollowPlayer(entry);
            }
            else
            {
                objectives?.ClearObjective(entry.Agent);
                entry.Agent.Stop();
            }

            context.Ui.ShowToast(
                entry.TargetName + (entry.FollowingPlayer ? " follows PLAYER." : " is idle."),
                UiTone.Neutral);
        }

        private void FollowPlayer(RobotEntry entry)
        {
            if (objectives != null)
            {
                objectives.SetObjective(entry.Agent, RobotObjective.Follow("PLAYER"));
                return;
            }

            if (context.LocalPlayer.TryGetSnapshot(out var player) && player != null)
            {
                entry.Agent.MoveTo(player.Position);
            }
        }

        private RobotTargetSnapshot? ResolvePlayerTarget()
        {
            return context.LocalPlayer.TryGetSnapshot(out var player) && player != null
                ? new RobotTargetSnapshot(player.Position)
                : (RobotTargetSnapshot?)null;
        }

        private RobotEntry? Find(string id)
        {
            for (var index = 0; index < spawned.Count; index++)
            {
                if (string.Equals(spawned[index].Agent.Id, id, StringComparison.Ordinal))
                {
                    return spawned[index];
                }
            }

            return null;
        }

        private void RemoveDeadEntries()
        {
            var changed = false;
            for (var index = spawned.Count - 1; index >= 0; index--)
            {
                if (spawned[index].Agent.IsAlive)
                {
                    continue;
                }

                spawned[index].Cleanup.Dispose();
                spawned.RemoveAt(index);
                changed = true;
            }

            if (changed)
            {
                RefreshHud(force: true);
            }
        }

        private void RefreshHud(bool force)
        {
            if (hud == null)
            {
                return;
            }

            var next = "ROBOTS  " + spawned.Count.ToString(CultureInfo.InvariantCulture)
                + " / " + config.MaxSpawnedObjects.ToString(CultureInfo.InvariantCulture)
                + "\nSIMULATION  " + (robotsPaused ? "PAUSED" : "RUNNING")
                + "\n" + config.SpawnMenuKey + " TOOLS   " + config.UndoKey + " UNDO   "
                + config.FreezeKey + " PAUSE";
            if (force || !string.Equals(next, hudText, StringComparison.Ordinal))
            {
                hudText = next;
                hud.SetBody(next);
                menu?.SetBody(BuildMenuStatus());
            }
        }
    }
}
