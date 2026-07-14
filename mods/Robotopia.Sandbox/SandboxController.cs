using System;
using Robotopia.Mods;
using Robotopia.Mods.UnityUi;
using UnityEngine;

namespace Robotopia.Sandbox
{
    /// <summary>
    /// Session-scoped orchestrator of the sandbox gameplay layer: owns the UI host, the spawn systems, and
    /// the hotkeys for exactly one world session, and tears all of it (including everything spawned) down
    /// on Dispose. Created per matching SessionChanged, disposed on SessionEnded/unload (Zombies pattern).
    /// </summary>
    internal sealed class SandboxController : IDisposable
    {
        // Marker pads: stable named places a robot program can reference ("go to the red marker").
        private static readonly (string Label, Color Color)[] MarkerOptions =
        {
            ("Red Marker", new Color(0.9f, 0.25f, 0.2f)),
            ("Blue Marker", new Color(0.3f, 0.5f, 0.95f)),
            ("Green Marker", new Color(0.35f, 0.85f, 0.35f)),
            ("Gold Marker", new Color(0.95f, 0.8f, 0.25f)),
        };

        private readonly IModContext context;
        private readonly SandboxConfig config;
        private readonly SpawnRegistry registry;
        private readonly PropCatalog catalog;
        private readonly PropSpawner spawner;
        private readonly IRobotAgentService? robots;
        private readonly IRobotConversationService? conversations;
        private readonly IRobotObjectiveService? objectives;
        private readonly IPlayerDialogueInputService? dialogueInput;
        private readonly IRobotBrainQueryService? brains;
        private readonly System.Collections.Generic.Dictionary<string, int> targetNameCounts =
            new System.Collections.Generic.Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private UiHost? ui;
        private Ui.SpawnMenuWindow? menu;
        private Ui.SandboxHud? hud;
        private RobotChat? chat;
        private RobotAmbience? ambience;
        private object? menuHotkey;
        private object? undoHotkey;
        private object? freezeHotkey;
        private bool disposed;

        public SandboxController(IModContext context, SandboxConfig config)
        {
            this.context = context;
            this.config = config;
            registry = new SpawnRegistry(config.MaxSpawnedObjects, context.Logger);
            robots = context.GetService<IRobotAgentService>();
            // Robots are not props: the catalog filters robot prefabs out of the UGC list (they live in the
            // NPC spawner instead). No RobotKit -> no filter; the catalog then lists everything as before.
            catalog = new PropCatalog(context.Logger, robots != null ? robots.IsRobotPrefab : (Func<object, bool>?)null);
            spawner = new PropSpawner(catalog, config, context.Logger);
            conversations = context.GetService<IRobotConversationService>();
            objectives = context.GetService<IRobotObjectiveService>();
            dialogueInput = context.GetService<IPlayerDialogueInputService>();
            brains = context.GetService<IRobotBrainQueryService>();
            registry.EntryRemoved += OnSpawnRemoved;
        }

        public bool RobotsAvailable => robots != null && robots.IsAvailable;

        /// <summary>True when the player opted into remote programming and RobotKit chat services are present.</summary>
        public bool ProgrammingAvailable => chat != null;

        internal static string[] MarkerLabels
        {
            get
            {
                var labels = new string[MarkerOptions.Length];
                for (var index = 0; index < MarkerOptions.Length; index++)
                {
                    labels[index] = MarkerOptions[index].Label;
                }

                return labels;
            }
        }

        public SandboxConfig Config => config;

        public PropCatalog Catalog => catalog;

        internal IModLogger Logger => context.Logger;

        public int PropCount => registry.PropCount;

        public int RobotCount => registry.RobotCount;

        /// <summary>The spawnable robot types the current level exposes (empty until RobotKit's prefab scan runs).</summary>
        public System.Collections.Generic.IReadOnlyList<RobotTypeDescriptor> RobotTypes =>
            robots?.RobotTypes ?? Array.Empty<RobotTypeDescriptor>();

        // The chosen type's display name, as the default label for unnamed spawns ("WORKER ROBOT 2" beats "ROBOT 2"
        // when the operator later refers to robots by what they are).
        private string? RobotTypeLabel(string? robotTypeId)
        {
            if (string.IsNullOrWhiteSpace(robotTypeId) || robots == null)
            {
                return null;
            }

            foreach (var type in robots.RobotTypes)
            {
                if (string.Equals(type.Id, robotTypeId, StringComparison.OrdinalIgnoreCase))
                {
                    return type.DisplayName;
                }
            }

            return null;
        }

        public void Start(WorldSession session)
        {
            ui = QwUi.For(context);
            menu = new Ui.SpawnMenuWindow(ui, this);
            if (config.ShowHud)
            {
                hud = new Ui.SandboxHud(ui, config);
            }

            if (robots != null && objectives != null)
            {
                // The operator is always a valid program target ("follow me") — for both the chat and the roster
                // quick actions, so it registers whenever objectives exist, even with the chat backend absent.
                objectives.RegisterTarget("PLAYER", RobotTargetKind.Player, () =>
                {
                    if (robots.TryGetPlayerObject(out var playerObject) && robots.TryGetPlayerPosition(out var position))
                    {
                        return new RobotTargetSnapshot(position, playerObject);
                    }

                    return null;
                });

                if (config.ConversationEnabled && conversations != null)
                {
                    chat = new RobotChat(context, config, ui, robots, conversations, objectives,
                        config.VoiceInputEnabled ? dialogueInput : null,
                        registry.FindRobotByTargetName);
                }

                // Robots reacting to each other: proximity greetings, opt-in banter, courier-delivery toasts.
                ambience = new RobotAmbience(config, ui, registry, objectives, brains,
                    () => chat != null && chat.IsOpen, context.Logger);
            }

            // Sandbox hotkeys stay quiet while a robot chat is up (the chat owns the keyboard).
            menuHotkey = ui.Hotkey(ParseKey(config.SpawnMenuKey, QwKey.Q), () => WhenNotChatting(() => menu?.Toggle()));
            undoHotkey = ui.Hotkey(ParseKey(config.UndoKey, QwKey.Z), () => WhenNotChatting(Undo));
            freezeHotkey = ui.Hotkey(ParseKey(config.FreezeKey, QwKey.F), () => WhenNotChatting(ToggleFreezeUnderCrosshair));

            context.Logger.Info("Sandbox session started in '" + session.SceneName + "' — press "
                + config.SpawnMenuKey + " for the spawn menu.");
            ui.Toast("Sandbox ready — press " + config.SpawnMenuKey + " for the spawn menu.", QwTone.Success);
        }

        public void Update(float deltaTime)
        {
            if (disposed)
            {
                return;
            }

            // The UGC catalog only exists once the sandbox scene has finished coming up; retry cheaply until
            // it loads, then refresh the menu's list once.
            if (!catalog.UgcAvailable && catalog.TryLoadUgcCatalog())
            {
                menu?.RefreshProps();
            }

            menu?.Update();
            hud?.Update(registry.PropCount, registry.RobotCount);
            chat?.Update();
            ambience?.Update();
        }

        public void SpawnProp(SandboxPropDefinition definition)
        {
            if (!registry.HasCapacity)
            {
                ui?.Toast("Spawn limit reached (" + config.MaxSpawnedObjects + ") — undo or clean up first.", QwTone.Warning);
                return;
            }

            var camera = ResolveCamera();
            if (camera == null)
            {
                ui?.Toast("No camera to place against.", QwTone.Danger);
                return;
            }

            var instance = spawner.Spawn(definition, camera);
            if (instance == null)
            {
                ui?.Toast("Could not spawn " + definition.DisplayName + ".", QwTone.Danger);
                return;
            }

            var targetName = RegisterPropTarget(instance, definition.DisplayName, RobotTargetKind.Prop);
            registry.PushProp(instance, definition.DisplayName, targetName);
            ui?.Toast("Spawned " + definition.DisplayName + ".", QwTone.Success);
        }

        /// <summary>Drops a named, frozen marker pad where the camera looks — a stable place robot programs can target.</summary>
        public void SpawnMarker(int markerIndex)
        {
            if (markerIndex < 0 || markerIndex >= MarkerOptions.Length)
            {
                return;
            }

            if (!registry.HasCapacity)
            {
                ui?.Toast("Spawn limit reached (" + config.MaxSpawnedObjects + ") — undo or clean up first.", QwTone.Warning);
                return;
            }

            var camera = ResolveCamera();
            if (camera == null)
            {
                ui?.Toast("No camera to place against.", QwTone.Danger);
                return;
            }

            var (label, color) = MarkerOptions[markerIndex];
            var pad = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            pad.name = "Sandbox Marker: " + label;
            pad.transform.localScale = new Vector3(1.6f, 0.05f, 1.6f);
            pad.transform.position = spawner.ResolveSpawnPoint(camera) + Vector3.up * 0.06f;

            var shader = Shader.Find("HDRP/Lit") ?? Shader.Find("Standard");
            if (shader != null)
            {
                pad.GetComponent<Renderer>().sharedMaterial = new Material(shader)
                {
                    name = "Sandbox Marker " + label,
                    color = color,
                };
            }

            // Markers are places, not toys: kinematic so physics (and the Gravity Gun) leaves them where placed.
            var body = pad.AddComponent<Rigidbody>();
            body.isKinematic = true;

            var targetName = RegisterPropTarget(pad, label, RobotTargetKind.Marker);
            registry.PushProp(pad, label, targetName);
            ui?.Toast("Placed " + label + (targetName != null ? " — robots know it as " + targetName + "." : "."),
                QwTone.Success);
        }

        public void SpawnRobot(RobotBrainMode brainMode, RobotColor? tint, float scale, string name, string? robotTypeId = null)
        {
            if (robots == null || !robots.IsAvailable)
            {
                ui?.Toast("Robots aren't ready in this level yet.", QwTone.Warning);
                return;
            }

            if (!registry.HasCapacity)
            {
                ui?.Toast("Spawn limit reached (" + config.MaxSpawnedObjects + ") — undo or clean up first.", QwTone.Warning);
                return;
            }

            var camera = ResolveCamera();
            if (camera == null)
            {
                ui?.Toast("No camera to place against.", QwTone.Danger);
                return;
            }

            var point = spawner.ResolveSpawnPoint(camera);
            var toCamera = camera.transform.position - point;
            var agent = robots.Spawn(new RobotAgentSpawnRequest(
                new Vec3(point.x, point.y, point.z),
                new Vec3(toCamera.x, 0f, toCamera.z))
            {
                BrainMode = brainMode,
                Tint = tint,
                Scale = scale,
                Name = string.IsNullOrWhiteSpace(name) ? null : name,
                RobotTypeId = robotTypeId,
            });

            if (agent == null)
            {
                ui?.Toast("Robot spawn failed (no prefab yet — try again in a moment).", QwTone.Warning);
                return;
            }

            var label = string.IsNullOrWhiteSpace(name)
                ? (RobotTypeLabel(robotTypeId)
                    ?? (brainMode == RobotBrainMode.Autonomous ? "Autonomous robot" : "Robot"))
                : name;

            // Robots are program targets too ("follow the red robot"), and every spawned robot gets the chat
            // verb — walk up, interact, and talk a program into it. On an autonomous robot the verb reads
            // REPROGRAM and overrides its native brain (the chat forces it dormant); the AUTONOMOUS decision
            // hands the brain back. Config can keep native talk on autonomous robots instead.
            var targetName = RegisterRobotTarget(agent, label);
            var reprogrammable = chat != null
                && (brainMode == RobotBrainMode.Dormant || config.ReprogramAutonomousRobots);
            if (reprogrammable)
            {
                var chatLabel = label;
                var chatTargetName = targetName ?? string.Empty;
                var verb = brainMode == RobotBrainMode.Autonomous ? "REPROGRAM" : "PROGRAM";
                agent.SetInteraction(RobotInteractionOptions.Custom(
                    new RobotCustomInteraction(verb, ctx => chat?.Begin(ctx.Agent, chatLabel, chatTargetName))
                    {
                        Distance = 3.5f,
                        CanInteract = _ => chat != null && !chat.IsOpen,
                    }));
            }

            registry.PushRobot(agent, label, targetName);
            ui?.Toast("Spawned " + label + ".", QwTone.Success);
        }

        public void Undo()
        {
            var undone = registry.Undo();
            ui?.Toast(undone == null ? "Nothing to undo." : "Undid " + undone + ".",
                undone == null ? QwTone.Neutral : QwTone.Success);
        }

        public void ToggleFreezeUnderCrosshair()
        {
            var camera = ResolveCamera();
            if (camera == null)
            {
                return;
            }

            var result = registry.ToggleFreezeUnderCrosshair(camera, config.SpawnDistanceMax);
            if (result != null)
            {
                ui?.Toast("Prop " + result + ".", QwTone.Neutral);
            }
        }

        public void FreezeAll(bool frozen)
        {
            var changed = registry.SetAllFrozen(frozen);
            ui?.Toast(changed + (frozen ? " props frozen." : " props unfrozen."), QwTone.Neutral);
        }

        /// <summary>Confirmed bulk reset — used by the Tools tab and the vanilla pause-menu action.</summary>
        public void CleanUpEverything()
        {
            if (ui == null)
            {
                return;
            }

            var live = registry.LiveCount;
            if (live == 0)
            {
                ui.Toast("Nothing to clean up.", QwTone.Neutral);
                return;
            }

            ui.Modal.Destructive(
                "CLEAN UP EVERYTHING",
                "Remove all " + live + " spawned objects? This cannot be undone.",
                "CLEAN UP",
                () =>
                {
                    var removed = registry.DestroyAll();
                    ui.Toast("Cleaned up " + removed + " objects.", QwTone.Success);
                });
        }

        // ------------------------------------------------------------------------------------------------------
        // ROBOTS roster tab support: the window renders rows and forwards clicks; all state and policy live here.

        /// <summary>True when the roster's quick actions (FOLLOW ME / IDLE) can program robots at all.</summary>
        internal bool ObjectivesAvailable => objectives != null;

        /// <summary>Fills the buffer with every live spawned robot, in spawn order (roster rows bind to these).</summary>
        internal void CollectRobots(System.Collections.Generic.List<SpawnRegistry.SpawnedEntry> buffer)
        {
            registry.CollectRobots(buffer);
        }

        /// <summary>The roster's program badge: AUTONOMOUS, the objective description, or NONE.</summary>
        internal string RobotProgramBadge(IRobotAgent robot)
        {
            if (robot.BrainMode == RobotBrainMode.Autonomous)
            {
                return "AUTONOMOUS";
            }

            var handle = objectives?.GetObjective(robot);
            return handle == null ? "NONE" : handle.Objective.Describe();
        }

        /// <summary>The roster's state badge ("MOVING", "PAUSING", …); empty for autonomous/unprogrammed robots.</summary>
        internal string RobotStateBadge(IRobotAgent robot)
        {
            if (robot.BrainMode == RobotBrainMode.Autonomous)
            {
                return string.Empty;
            }

            var handle = objectives?.GetObjective(robot);
            if (handle == null)
            {
                return string.Empty;
            }

            switch (handle.State)
            {
                case RobotObjectiveState.Seeking:
                    return "MOVING";
                case RobotObjectiveState.Arrived:
                    return "ARRIVED";
                case RobotObjectiveState.Dwelling:
                    return "PAUSING";
                case RobotObjectiveState.TargetMissing:
                    return "NO TARGET";
                case RobotObjectiveState.Delivered:
                    return "DELIVERED";
                case RobotObjectiveState.Idle:
                    return "IDLE";
                default:
                    return string.Empty;
            }
        }

        /// <summary>Roster PROGRAM: opens the chat with the robot remotely (no walk-up needed), closing the menu.</summary>
        internal void ProgramRobotFromRoster(SpawnRegistry.SpawnedEntry entry)
        {
            var robot = entry.Robot;
            if (robot == null || !robot.IsAlive || chat == null)
            {
                return;
            }

            if (chat.IsOpen)
            {
                ui?.Toast("Finish the current chat first.", QwTone.Warning);
                return;
            }

            if (!MayOverride(robot))
            {
                return;
            }

            if (chat.Begin(robot, entry.DisplayName, entry.TargetName ?? string.Empty))
            {
                menu?.Hide();
            }
        }

        /// <summary>Roster FOLLOW ME: deterministic follow-the-player program, no LLM involved.</summary>
        internal void FollowMeFromRoster(SpawnRegistry.SpawnedEntry entry)
        {
            QuickProgram(entry, RobotObjective.Follow("PLAYER"), "is now following you");
        }

        /// <summary>Roster IDLE: deterministic stand-down, no LLM involved.</summary>
        internal void IdleFromRoster(SpawnRegistry.SpawnedEntry entry)
        {
            QuickProgram(entry, RobotObjective.Idle(), "is standing down");
        }

        private void QuickProgram(SpawnRegistry.SpawnedEntry entry, RobotObjective objective, string toastVerb)
        {
            var robot = entry.Robot;
            if (robot == null || !robot.IsAlive || objectives == null)
            {
                return;
            }

            if (chat != null && chat.IsOpen)
            {
                ui?.Toast("Finish the current chat first.", QwTone.Warning);
                return;
            }

            if (!MayOverride(robot))
            {
                return;
            }

            robot.SetBrainMode(RobotBrainMode.Dormant);
            objectives.SetObjective(robot, objective);
            ui?.Toast(entry.DisplayName + " " + toastVerb + ".", QwTone.Success);
        }

        // The same policy the interaction verb applies at spawn time: an autonomous robot's native brain is only
        // overridden when the config allows it.
        private bool MayOverride(IRobotAgent robot)
        {
            if (robot.BrainMode == RobotBrainMode.Autonomous && !config.ReprogramAutonomousRobots)
            {
                ui?.Toast("Autonomous robots are off-limits (reprogramAutonomousRobots is off).", QwTone.Neutral);
                return false;
            }

            return true;
        }

        internal enum SandboxHotkey
        {
            SpawnMenu,
            Undo,
            Freeze
        }

        internal QwKey HotkeyValue(SandboxHotkey kind)
        {
            return kind switch
            {
                SandboxHotkey.SpawnMenu => ParseKey(config.SpawnMenuKey, QwKey.Q),
                SandboxHotkey.Undo => ParseKey(config.UndoKey, QwKey.Z),
                _ => ParseKey(config.FreezeKey, QwKey.F)
            };
        }

        /// <summary>Rebinds one of the sandbox hotkeys live and persists the choice.</summary>
        internal void Rebind(SandboxHotkey kind, QwKey key)
        {
            if (key == QwKey.None)
            {
                return;
            }

            var handle = kind switch
            {
                SandboxHotkey.SpawnMenu => menuHotkey,
                SandboxHotkey.Undo => undoHotkey,
                _ => freezeHotkey
            };
            if (handle != null)
            {
                QwHotkeys.Rebind(handle, key);
            }

            switch (kind)
            {
                case SandboxHotkey.SpawnMenu: config.SpawnMenuKey = key.ToString(); break;
                case SandboxHotkey.Undo: config.UndoKey = key.ToString(); break;
                default: config.FreezeKey = key.ToString(); break;
            }

            context.SaveConfig(config);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            ambience?.Dispose();
            ambience = null;
            chat?.Dispose();
            chat = null;
            registry.DestroyAll();
            registry.EntryRemoved -= OnSpawnRemoved;
            objectives?.UnregisterTarget("PLAYER");
            menu?.Dispose();
            menu = null;
            hud = null;
            menuHotkey = null;
            undoHotkey = null;
            freezeHotkey = null;
            ui?.Dispose();
            ui = null;
        }

        private void WhenNotChatting(Action action)
        {
            if (chat == null || !chat.IsOpen)
            {
                action();
            }
        }

        // Register a spawned prop as a named objective target: "Crate" -> CRATE, a second one -> CRATE 2. Null when
        // the objective service is absent (older RobotKit) — everything else keeps working without targets.
        private string? RegisterPropTarget(GameObject instance, string displayLabel, RobotTargetKind kind)
        {
            if (objectives == null)
            {
                return null;
            }

            var name = UniqueTargetName(displayLabel);
            objectives.RegisterTarget(name, kind, () =>
            {
                if (instance == null)
                {
                    return null; // destroyed (Unity fake-null) — the objective waits/retries
                }

                var position = instance.transform.position;
                return new RobotTargetSnapshot(new Vec3(position.x, position.y, position.z), instance);
            });
            return name;
        }

        private string? RegisterRobotTarget(IRobotAgent agent, string displayLabel)
        {
            if (objectives == null)
            {
                return null;
            }

            var name = UniqueTargetName(displayLabel);
            objectives.RegisterTarget(name, RobotTargetKind.Robot, () =>
                agent.IsAlive ? new RobotTargetSnapshot(agent.Position, agent.GameObject) : (RobotTargetSnapshot?)null);
            return name;
        }

        private string UniqueTargetName(string displayLabel)
        {
            var baseName = displayLabel.Trim().ToUpperInvariant();
            targetNameCounts.TryGetValue(baseName, out var seen);
            targetNameCounts[baseName] = seen + 1;
            return seen == 0 ? baseName : baseName + " " + (seen + 1);
        }

        private void OnSpawnRemoved(SpawnRegistry.SpawnedEntry entry)
        {
            if (entry.TargetName != null)
            {
                objectives?.UnregisterTarget(entry.TargetName);
            }
        }

        // GravityGun's camera idiom: the main camera when tagged, else the first live camera.
        internal static Camera? ResolveCamera()
        {
            var camera = Camera.main;
            if (camera != null)
            {
                return camera;
            }

            var all = Camera.allCameras;
            return all.Length > 0 ? all[0] : null;
        }

        private static QwKey ParseKey(string value, QwKey fallback)
        {
            return Enum.TryParse<QwKey>(value, ignoreCase: true, out var parsed) && parsed != QwKey.None
                ? parsed
                : fallback;
        }
    }
}
