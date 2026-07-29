using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.SdkAcceptance
{
    public sealed partial class SdkAcceptanceMod : TopiaForgeMod
    {
        private const string Prefix = "TF-ACCEPT";
        private const string MissingOptionalProviderId = "dev.topiaforge.sdk-acceptance.missing-provider";
        private const string AcceptanceWorldId = "dev.topiaforge.sdk-acceptance.world";
        private const string AcceptanceGamemodeId = "dev.topiaforge.sdk-acceptance.mode";
        private const string AcceptanceMenuEntryId = "dev.topiaforge.sdk-acceptance.menu";
        private readonly HashSet<string> completed = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> failed = new HashSet<string>(StringComparer.Ordinal);
        private IInputAction? keyboard;
        private IInputAction? mouse;
        private IInputAction? gamepad;
        private IInputAction? voice;
        private IInteractableRegistration? interaction;
        private IRobotAgentService? robotAgents;
        private IRobotObjectiveService? robotObjectives;
        private IRobotBrainQueryService? robotBrain;
        private IRobotConversationService? robotConversations;
        private IPlayerDialogueInputService? dialogueInput;
        private IRobotAgent? acceptanceRobot;
        private IVoiceCapture? voiceCapture;
        private ITimeControlService? timeControl;
        private ICreatorContentService? creatorContent;
        private IPromptOverrideRegistry? promptOverrides;
        private IUgcLiveSyncService? ugcLiveSync;
        private IWorldGamemodeService? worlds;
        private int mainThreadId;
        private bool activeSceneSeen;
        private bool sceneCallbackSeen;
        private bool frameSeen;
        private bool fixedSeen;
        private bool lateSeen;
        private bool keyboardPressed;
        private bool keyboardReleased;
        private bool mousePressed;
        private bool mouseReleased;
        private bool gamepadPressed;
        private bool gamepadReleased;
        private bool uiFocusSeen;
        private bool itemProbeRunning;
        private bool robotProbeRunning;
        private bool robotCorePassed;
        private bool robotObjectivePassed;
        private bool robotInteractionPassed;
        private bool robotBrainPassed;
        private bool robotConversationPassed;
        private bool robotVoicePassed;
        private bool acceptanceWorldSessionSeen;
        private bool lifecycleProbeRunning;

        protected override void OnLoad()
        {
            mainThreadId = Thread.CurrentThread.ManagedThreadId;
            Context.Logger.Info(Prefix + "|START|" + Context.Identity.Id + "|" + Context.Identity.Version);
            RunAuthoringChecks();
            RegisterInput();
            RegisterEvents();
            RunUiChecks();
            RegisterProviderChecks();
            RunMultiplayerLoopbackChecks();
            var scheduled = Context.Scheduler.NextFrame(() => _ = RunAsyncChecks());
            if (!scheduled.Succeeded)
            {
                Fail("content.prefab-spawn-destroy", scheduled.ErrorMessage);
                Fail("compat.graceful-unavailable", scheduled.ErrorMessage);
            }
        }

        protected override void OnUnload()
        {
            Context.Logger.Info(Prefix + "|STOP|completed=" + completed.Count);
        }

        private void RunAuthoringChecks()
        {
            var definition = new ConfigDefinition<AcceptanceConfig>(
                2,
                () => new AcceptanceConfig(),
                value => value.UiScale >= 0.75f && value.UiScale <= 1.5f
                    ? OperationResult<bool>.Success(true)
                    : OperationResult<bool>.Failure(
                        ModErrorCode.InvalidArgument,
                        "UI scale must be between 0.75 and 1.5."),
                (_, value) =>
                {
                    value.MigratedFromSchema1 = true;
                    return OperationResult<AcceptanceConfig>.Success(value);
                });
            var config = Context.Config.Load(definition);
            if (!config.TryGetValue(out var value) || !Context.Config.Save(definition, value).Succeeded)
            {
                Fail("authoring.config-storage-localization-commands", config.ErrorMessage);
                return;
            }

            var state = Context.LocalStorage.Load<AcceptanceState>("acceptance-state");
            var current = state.TryGetValue(out var stored) ? stored : new AcceptanceState();
            current.LoadCount++;
            if (!Context.LocalStorage.Save("acceptance-state", current).Succeeded
                || !Context.LocalStorage.Contains("acceptance-state"))
            {
                Fail("authoring.config-storage-localization-commands", "installation-local typed storage failed");
                return;
            }

            var localization = Context.Localization.Register(new LocalizationCatalog(
                "en",
                new Dictionary<string, string> { ["acceptance.ready"] = "SDK acceptance ready" }));
            var command = Context.Commands.Register(
                new CommandDefinition("status", "Reports live SDK acceptance progress."),
                _ => OperationResult<string>.Success(completed.Count + " acceptance cases completed"));
            Context.Diagnostics.Report(new DiagnosticEntry(
                "TFACCEPT0001",
                "Live SDK acceptance started.",
                DiagnosticSeverity.Info,
                "load=" + current.LoadCount));
            if (!localization.Succeeded || !command.Succeeded ||
                Context.Localization.Get("acceptance.ready", "") != "SDK acceptance ready")
            {
                Fail("authoring.config-storage-localization-commands", "localization or command registration failed");
                return;
            }

            if (!value.MigratedFromSchema1)
            {
                Fail("authoring.config-storage-localization-commands", "schema-1 fixture was not migrated; rerun the acceptance harness");
                return;
            }

            Pass("authoring.config-storage-localization-commands", "load=" + current.LoadCount);
        }

        private void RegisterInput()
        {
            if (!TryRegisterInput(new InputActionDefinition(
                "accept-keyboard",
                "Acceptance keyboard",
                new[] { InputBinding.Key("F6") }), out keyboard)
                || !TryRegisterInput(new InputActionDefinition(
                "accept-mouse",
                "Acceptance mouse",
                new[] { InputBinding.MouseButton(InputMouseButton.Middle) }), out mouse)
                || !TryRegisterInput(new InputActionDefinition(
                "accept-gamepad",
                "Acceptance gamepad",
                new[] { InputBinding.GamepadButton(InputGamepadButton.South), InputBinding.GamepadAxis(InputGamepadAxis.LeftX) }), out gamepad)
                || !TryRegisterInput(new InputActionDefinition(
                "accept-voice",
                "Acceptance push to talk",
                new[] { InputBinding.Key("F9") }), out voice))
            {
                return;
            }

            var conflictResult = Context.Input.RegisterAction(new InputActionDefinition(
                "accept-conflict",
                "Acceptance conflict probe",
                new[] { InputBinding.Key("F6") }));
            if (!conflictResult.TryGetValue(out var conflict))
            {
                Fail("input.devices-and-focus", "conflict action registration failed: " + conflictResult.ErrorMessage);
                return;
            }

            var rebound = conflict.Rebind(new[] { InputBinding.Key("F7") });
            var reset = conflict.ResetBindings();
            if (!rebound.Succeeded || !reset.Succeeded || Context.Input.GetConflicts().Count == 0)
            {
                Fail("input.devices-and-focus", "rebind, reset, or conflict reporting failed");
            }
        }

        private bool TryRegisterInput(InputActionDefinition definition, out IInputAction? action)
        {
            var result = Context.Input.RegisterAction(definition);
            if (result.TryGetValue(out var registered))
            {
                action = registered;
                return true;
            }

            action = null;
            Fail(
                "input.devices-and-focus",
                definition.Name + " registration failed (" + result.ErrorCode + "): " + result.ErrorMessage);
            return false;
        }

        private void RegisterEvents()
        {
            if (Context.Scenes.TryGetActive(out var active) && active != null)
            {
                activeSceneSeen = true;
                Context.Logger.Info(Prefix + "|OBSERVE|initial-scene|" + Sanitize(active.Name));
            }

            Context.Events.SubscribeSceneLoaded(scene =>
            {
                if (!CheckMainThread("lifecycle.initial-scene", "scene-loaded"))
                {
                    return;
                }

                sceneCallbackSeen = true;
                Context.Logger.Info(Prefix + "|OBSERVE|scene-loaded|" + scene);
                TryPassInitialScene(scene);
            });
            Context.Events.SubscribeUpdate(_ =>
            {
                if (!CheckMainThread("lifecycle.frame-fixed-late", "frame"))
                {
                    return;
                }

                frameSeen = true;
                TryPassInitialScene("active-and-callback");
                ObserveInput();
                ObservePlayerAndWorld();
                if (frameSeen && fixedSeen && lateSeen)
                {
                    Pass("lifecycle.frame-fixed-late");
                }
            });
            Context.Events.SubscribeFixedUpdate(_ =>
            {
                if (CheckMainThread("lifecycle.frame-fixed-late", "fixed"))
                {
                    fixedSeen = true;
                }
            });
            Context.Events.SubscribeLateUpdate(_ =>
            {
                if (CheckMainThread("lifecycle.frame-fixed-late", "late"))
                {
                    lateSeen = true;
                }
            });
        }

        private void TryPassInitialScene(string detail)
        {
            if (activeSceneSeen && sceneCallbackSeen)
            {
                Pass("lifecycle.initial-scene", detail + ";thread=" + mainThreadId);
            }
        }

        private void RunUiChecks()
        {
            var accessibility = Context.Ui.ApplyAccessibility(
                new UiAccessibilityPreferences(true, 1.15f, true, 0f));
            var surface = Context.Ui.CreateSurface(new UiSurfaceRequest(
                "live-acceptance",
                "TOPIAFORGE SDK ACCEPTANCE",
                "Close the confirmation; press F6, middle mouse, and gamepad A. Hold an item, interact with the acceptance robot, hold F9 to speak, and launch SDK Acceptance World from the Worlds menu.",
                UiSurfaceKind.Hud,
                520f,
                180f));
            var toast = Context.Ui.ShowToast("SDK live acceptance started", UiTone.Warning);
            var modal = Context.Ui.ShowModal(
                new UiModalRequest("SDK ACCEPTANCE", "Confirm that the paper-scheme modal is readable."),
                confirmed =>
                {
                    if (confirmed && uiFocusSeen)
                    {
                        Pass("ui.accessibility-and-focus");
                    }
                    else
                    {
                        Fail("ui.accessibility-and-focus", "modal was cancelled or UI focus was not observed");
                    }
                });
            if (!accessibility.Succeeded || !surface.Succeeded || !toast.Succeeded || !modal.Succeeded)
            {
                Fail("ui.accessibility-and-focus", "TopiaForgeUi host operation failed");
            }
        }

        private void RegisterProviderChecks()
        {
            if (!RunProviderContractChecks() || worlds == null)
            {
                return;
            }

            worlds.SessionChanged += session =>
            {
                CheckMainThread("worlds.session-teardown", "session-started");
                if (string.Equals(session.WorldId, AcceptanceWorldId, StringComparison.Ordinal))
                {
                    acceptanceWorldSessionSeen = true;
                    Context.Logger.Info(Prefix + "|OBSERVE|world-session-started|" + session.Mode);
                    RegisterPauseAcceptance();
                }
            };
            worlds.SessionEnded += ended =>
            {
                CheckMainThread("worlds.session-teardown", "session-ended");
                if (acceptanceWorldSessionSeen
                    && string.Equals(ended.Session.WorldId, AcceptanceWorldId, StringComparison.Ordinal))
                {
                    Pass("worlds.session-teardown", ended.Reason.ToString());
                }
            };

            var content = new BundleWorldContent(
                Context.Assets,
                "third_party/sdk-acceptance-world.bundle",
                "Assets/World/World.prefab",
                new TransformState(Vec3.Zero, Quat.Identity, new Vec3(1f, 1f, 1f)));
            var worldRegistration = worlds.RegisterWorld(new WorldDefinition(
                AcceptanceWorldId,
                "SDK Acceptance World",
                "Automated package-prefab world used by the V1 launch gate.",
                supportsAdditiveArena: true), content);
            var gamemodeRegistration = worlds.RegisterGamemode(new GamemodeDefinition(
                AcceptanceGamemodeId,
                "SDK Acceptance",
                "Exercises world registration, loading, pause actions, and teardown."));
            var menuRegistration = worlds.RegisterMenuEntry(new GamemodeMenuEntry(
                AcceptanceMenuEntryId,
                "SDK Acceptance World",
                "Launch, wait for the ready toast, then exit the session.",
                AcceptanceGamemodeId,
                AcceptanceWorldId));
            Context.Commands.Register(
                new CommandDefinition("run-world", "Launches the registered SDK acceptance world."),
                invocation =>
                {
                    _ = RunWorldAcceptanceAsync();
                    return OperationResult<string>.Success("SDK acceptance world launch requested.");
                });
            if (!worldRegistration.Succeeded || !gamemodeRegistration.Succeeded || !menuRegistration.Succeeded)
            {
                Fail("worlds.session-teardown", "world, gamemode, or menu registration failed");
            }
        }

        private void RegisterPauseAcceptance()
        {
            if (!Context.TryGetExtension<IWorldPauseMenuService>(out var pause) || pause == null)
            {
                Fail("worlds.session-teardown", "pause-menu provider was not dependency-scoped");
                return;
            }

            var action = pause.RegisterAction(new WorldPauseAction(
                "finish-acceptance",
                "FINISH SDK ACCEPTANCE",
                () => worlds?.EndSession(WorldSessionEndReason.EndedByGamemode),
                destructive: true));
            var intercept = pause.InterceptExit(_ => WorldPauseExitDecision.EndSessionAndExit);
            if (!action.Succeeded || !intercept.Succeeded)
            {
                Fail("worlds.session-teardown", action.ErrorMessage + " " + intercept.ErrorMessage);
            }
        }

        private async Task RunWorldAcceptanceAsync()
        {
            if (worlds == null)
            {
                Fail("worlds.session-teardown", "world provider is unavailable");
                return;
            }

            var loaded = await worlds.LaunchMenuEntryAsync(
                AcceptanceMenuEntryId,
                Context.Lifetime.StoppingToken);
            if (!CheckMainThread("worlds.session-teardown", "load-completion") || !loaded.Succeeded)
            {
                Fail("worlds.session-teardown", loaded.ErrorMessage);
                return;
            }

            Context.Ui.ShowToast("SDK acceptance world loaded. Use the pause action or run the session-end command.");
            var delay = await Context.Scheduler.DelayAsync(
                TimeSpan.FromSeconds(2),
                Context.Lifetime.StoppingToken);
            if (delay.Succeeded && CheckMainThread("worlds.session-teardown", "scheduled-end"))
            {
                var ended = worlds.EndSession(WorldSessionEndReason.EndedByGamemode);
                if (!ended.Succeeded)
                {
                    Fail("worlds.session-teardown", ended.ErrorMessage);
                }
            }
        }

        private async Task RunAsyncChecks()
        {
            try
            {
                var missing = await Context.Assets.LoadBundleAsync(
                    "third_party/does-not-exist.bundle",
                    Context.Lifetime.StoppingToken);
                if (!CheckMainThread("compat.graceful-unavailable", "missing-asset-completion"))
                {
                    return;
                }
                if (!missing.Succeeded && missing.ErrorCode == ModErrorCode.NotFound
                    && !Context.Runtime.TryGetUnavailableCapability("does-not-exist", out _))
                {
                    Pass("compat.graceful-unavailable");
                }
                else
                {
                    Fail("compat.graceful-unavailable", "expected stable NotFound and capability-query results");
                }

                var bundle = await Context.Assets.LoadBundleAsync(
                    "third_party/sdk-acceptance-world.bundle",
                    Context.Lifetime.StoppingToken);
                if (!CheckMainThread("content.prefab-spawn-destroy", "bundle-completion"))
                {
                    return;
                }
                if (!bundle.TryGetValue(out var bundleHandle))
                {
                    Fail("content.prefab-spawn-destroy", bundle.ErrorMessage);
                    return;
                }

                var prefab = await Context.Assets.LoadPrefabAsync(
                    bundleHandle,
                    "Assets/World/World.prefab",
                    Context.Lifetime.StoppingToken);
                if (!CheckMainThread("content.prefab-spawn-destroy", "prefab-completion"))
                {
                    return;
                }
                if (!prefab.TryGetValue(out var prefabHandle))
                {
                    Fail("content.prefab-spawn-destroy", prefab.ErrorMessage);
                    return;
                }

                var position = Context.LocalPlayer.TryGetSnapshot(out var player) && player != null
                    ? player.Position + new Vec3(0f, -1000f, 0f)
                    : new Vec3(0f, -1000f, 0f);
                var spawned = Context.Assets.Spawn(new AssetSpawnRequest(
                    prefabHandle,
                    new TransformState(position, Quat.Identity, new Vec3(1f, 1f, 1f))));
                if (!spawned.TryGetValue(out var entity) || !Context.Entities.Destroy(entity).Succeeded)
                {
                    Fail("content.prefab-spawn-destroy", spawned.ErrorMessage);
                    return;
                }

                prefabHandle.Dispose();
                bundleHandle.Dispose();
                Pass("content.prefab-spawn-destroy");
            }
            catch (Exception exception)
            {
                Fail("content.prefab-spawn-destroy", exception.Message);
            }
        }

        private void ObserveInput()
        {
            uiFocusSeen |= Context.Input.IsUiFocused;
            ObserveAction(keyboard, ref keyboardPressed, ref keyboardReleased);
            ObserveAction(mouse, ref mousePressed, ref mouseReleased);
            ObserveAction(gamepad, ref gamepadPressed, ref gamepadReleased);
            if (voice?.WasPressed == true)
            {
                BeginRobotVoiceCapture();
            }
            if (voice?.WasReleased == true && voiceCapture != null)
            {
                var capture = voiceCapture;
                voiceCapture = null;
                _ = CompleteRobotDialogueAsync(capture);
            }
            if (keyboardPressed && keyboardReleased && mousePressed && mouseReleased
                && gamepadPressed && gamepadReleased && uiFocusSeen)
            {
                Pass("input.devices-and-focus");
            }
        }

        private static void ObserveAction(IInputAction? action, ref bool pressed, ref bool released)
        {
            if (action == null) return;
            pressed |= action.WasPressed || action.IsHeld;
            released |= pressed && action.WasReleased;
        }

        private async Task RunRobotCoreChecksAsync(PlayerSnapshot player)
        {
            try
            {
                var agents = robotAgents;
                if (agents == null)
                {
                    Fail("robots.objectives-dialogue-voice", "robot-agent provider is unavailable");
                    return;
                }

                var reachable = await agents.FindReachableSpawnAsync(
                    new ReachableSpawnRequest(
                        player.Position,
                        player.Position,
                        minRadius: 3f,
                        maxRadius: 6f,
                        maxCandidates: 12),
                    Context.Lifetime.StoppingToken);
                if (!CheckMainThread("robots.objectives-dialogue-voice", "reachable-spawn-completion"))
                {
                    return;
                }

                var position = reachable.TryGetValue(out var found)
                    ? found.Position
                    : player.AimRay.GetPoint(4f);
                var spawned = agents.Spawn(new RobotAgentSpawnRequest(
                    position,
                    name: "SDK ACCEPTANCE ROBOT",
                    tint: new RobotColor(0.3f, 0.8f, 1f),
                    interaction: RobotInteractionOptions.Custom(new RobotCustomInteraction(
                        "VERIFY SDK ROBOT",
                        _ =>
                        {
                            if (CheckMainThread("robots.objectives-dialogue-voice", "robot-interaction"))
                            {
                                robotInteractionPassed = true;
                                TryPassRobotAcceptance();
                            }
                        },
                        distance: 4f))));
                if (!spawned.TryGetValue(out var robot))
                {
                    Fail("robots.objectives-dialogue-voice", spawned.ErrorMessage);
                    return;
                }

                acceptanceRobot = robot;
                var bodyAndMovement = robot.IsAlive
                    && robot.HeadPosition.IsFinite
                    && robot.SetBrainMode(RobotBrainMode.Dormant).Succeeded
                    && robot.ConfigureMovement(new RobotMovementSettings(RobotGait.Walk, stopDistance: 1f)).Succeeded
                    && robot.SetName("SDK ACCEPTANCE ROBOT").Succeeded
                    && robot.SetScale(1f).Succeeded
                    && robot.SetTint(new RobotColor(0.3f, 0.8f, 1f)).Succeeded
                    && robot.SetEmote(":wave:").Succeeded
                    && robot.MoveTo(robot.Position).Succeeded
                    && robot.Stop().Succeeded
                    && robot.ApplyDamage(0.01f, RobotDamageType.Normal, "topiaforge-acceptance").Succeeded;
                if (!bodyAndMovement)
                {
                    Fail("robots.objectives-dialogue-voice", "robot body, health, visual, or movement operation failed");
                    return;
                }

                robotCorePassed = true;
                var objectives = robotObjectives;
                if (objectives == null)
                {
                    Fail("robots.objectives-dialogue-voice", "objective provider is unavailable");
                    return;
                }

                var target = objectives.RegisterTarget(
                    "PLAYER",
                    RobotTargetKind.Player,
                    () => Context.LocalPlayer.TryGetSnapshot(out var current) && current != null
                        ? new RobotTargetSnapshot(current.Position)
                        : (RobotTargetSnapshot?)null);
                var objective = objectives.SetObjective(robot, RobotObjective.GoTo("PLAYER"));
                var objectiveOk = target.TryGetValue(out var registration)
                    && registration.IsActive
                    && objectives.TryResolveTarget("PLAYER", out _)
                    && objective.TryGetValue(out var handle)
                    && handle.IsActive
                    && objectives.TryGetObjective(robot, out var currentObjective)
                    && ReferenceEquals(currentObjective, handle)
                    && objectives.ClearObjective(robot).Succeeded;
                if (!objectiveOk)
                {
                    Fail("robots.objectives-dialogue-voice", "target or objective operation failed");
                    return;
                }

                robotObjectivePassed = true;
                Context.Ui.ShowToast("Acceptance robot ready: interact with it, then hold F9 and speak.");
                TryPassRobotAcceptance();
            }
            catch (Exception exception)
            {
                Fail("robots.objectives-dialogue-voice", exception.Message);
            }
            finally
            {
                robotProbeRunning = false;
            }
        }

        private void BeginRobotVoiceCapture()
        {
            if (voiceCapture != null)
            {
                return;
            }

            if (!CheckMainThread("robots.objectives-dialogue-voice", "voice-start")
                || dialogueInput == null
                || !dialogueInput.IsVoiceAvailable)
            {
                Fail("robots.objectives-dialogue-voice", "voice input is unavailable on this acceptance host");
                return;
            }

            var started = dialogueInput.BeginVoiceCapture();
            if (!started.TryGetValue(out var capture) || !capture.IsRecording)
            {
                Fail("robots.objectives-dialogue-voice", started.ErrorMessage);
                return;
            }

            voiceCapture = capture;
            Context.Ui.ShowToast("Voice capture active; release F9 to transcribe and query the robot brain.");
        }

        private async Task CompleteRobotDialogueAsync(IVoiceCapture capture)
        {
            try
            {
                var transcript = await capture.StopAsync(Context.Lifetime.StoppingToken);
                capture.Dispose();
                if (!CheckMainThread("robots.objectives-dialogue-voice", "voice-completion")
                    || !transcript.TryGetValue(out var spoken)
                    || string.IsNullOrWhiteSpace(spoken.Text))
                {
                    Fail("robots.objectives-dialogue-voice", transcript.ErrorMessage);
                    return;
                }

                robotVoicePassed = true;
                var brain = robotBrain;
                if (brain == null || !brain.IsAvailable)
                {
                    Fail("robots.objectives-dialogue-voice", "robot brain query is unavailable on this acceptance host");
                    return;
                }

                var answer = await brain.QueryAsync(
                    new BrainQueryRequest(
                        "A tester said: " + spoken.Text + ". Return READY.",
                        new[]
                        {
                            new BrainOutputField(
                                "status",
                                "Acceptance status.",
                                allowedStrings: new[] { "READY" })
                        },
                        usage: "topiaforge-sdk-acceptance",
                        temperature: 0f),
                    Context.Lifetime.StoppingToken);
                if (!CheckMainThread("robots.objectives-dialogue-voice", "brain-completion")
                    || !answer.TryGetValue(out var brainResult)
                    || !brainResult.TryGet("status", out _))
                {
                    Fail("robots.objectives-dialogue-voice", answer.ErrorMessage);
                    return;
                }

                robotBrainPassed = true;
                var conversations = robotConversations;
                if (conversations == null || !conversations.IsAvailable)
                {
                    Fail("robots.objectives-dialogue-voice", "robot conversation is unavailable on this acceptance host");
                    return;
                }

                var begun = conversations.BeginConversation(new RobotConversationRequest(
                    "You are a concise SDK acceptance robot.",
                    new[] { "ACKNOWLEDGE" },
                    maxTurns: 1,
                    temperature: 0f,
                    usage: "topiaforge-sdk-acceptance-conversation"));
                if (!begun.TryGetValue(out var conversation))
                {
                    Fail("robots.objectives-dialogue-voice", begun.ErrorMessage);
                    return;
                }

                var turn = await conversation.SubmitAsync(spoken.Text, Context.Lifetime.StoppingToken);
                conversation.Dispose();
                if (!CheckMainThread("robots.objectives-dialogue-voice", "conversation-completion")
                    || !turn.TryGetValue(out var response)
                    || string.IsNullOrWhiteSpace(response.Reply)
                    || string.IsNullOrWhiteSpace(response.Decision))
                {
                    Fail("robots.objectives-dialogue-voice", turn.ErrorMessage);
                    return;
                }

                robotConversationPassed = true;
                TryPassRobotAcceptance();
            }
            catch (Exception exception)
            {
                capture.Dispose();
                Fail("robots.objectives-dialogue-voice", exception.Message);
            }
        }

        private void TryPassRobotAcceptance()
        {
            if (robotCorePassed && robotObjectivePassed && robotInteractionPassed
                && robotBrainPassed && robotConversationPassed && robotVoicePassed)
            {
                Pass("robots.objectives-dialogue-voice", "robot/objective/interaction/brain/dialogue/voice complete");
            }
        }

        private void ObservePlayerAndWorld()
        {
            if (Context.LocalPlayer.TryGetSnapshot(out var player) && player != null)
            {
                if (!lifecycleProbeRunning
                    && completed.Contains("ui.accessibility-and-focus")
                    && timeControl != null
                    && promptOverrides != null
                    && robotObjectives != null
                    && ugcLiveSync != null
                    && worlds != null)
                {
                    lifecycleProbeRunning = true;
                    _ = RunLifetimeCyclesAsync(player);
                }

                if (!completed.Contains("player.health-and-control"))
                {
                    var lease = Context.LocalPlayer.AcquireControl("SDK live acceptance");
                    lease.Value?.Dispose();
                    var healthOk = true;
                    if (Context.LocalPlayer.TryGetHealth(out _))
                    {
                        var damage = Context.LocalPlayer.Damage(new PlayerDamageRequest(0.01f, "topiaforge-acceptance"));
                        var heal = Context.LocalPlayer.Heal(0.01f, "topiaforge-acceptance");
                        healthOk = damage.Succeeded && heal.Succeeded;
                    }

                    if (lease.Succeeded && healthOk)
                    {
                        Pass("player.health-and-control");
                    }
                }

                if (!robotProbeRunning && acceptanceRobot == null && robotAgents?.IsAvailable == true)
                {
                    robotProbeRunning = true;
                    _ = RunRobotCoreChecksAsync(player);
                }

                if (!completed.Contains("content.audio"))
                {
                    var first = Context.Audio.Play(new AudioPlayRequest("acceptance.confirm", 0.2f));
                    var second = Context.Audio.Play(new AudioPlayRequest("acceptance.world", 0.2f, false, player.Position));
                    if (first.Succeeded && second.Succeeded)
                    {
                        first.Value?.Dispose();
                        second.Value?.Dispose();
                        Pass("content.audio");
                    }
                }

                if (Context.Physics.TryRaycast(player.AimRay, 20f, out var hit) && hit != null)
                {
                    Context.Physics.TrySphereCast(player.AimRay, 0.25f, 20f, out _);
                    Context.Physics.Overlap(new Bounds(hit.Point, new Vec3(2f, 2f, 2f)), 16);
                    if (interaction == null)
                    {
                        var registration = Context.Interactions.Register(
                            hit.Entity,
                            new InteractableDefinition("SDK ACCEPT", 5f),
                            _ => Pass("world.physics-entities-items-interactions", "interaction callback received"));
                        if (registration.TryGetValue(out var registered))
                        {
                            interaction = registered;
                        }
                    }
                }
            }

            if (!itemProbeRunning && Context.Items.TryGetHeld(out var item) && item != null)
            {
                itemProbeRunning = true;
                _ = DropAndRestoreItem(item);
            }
        }

        private async Task DropAndRestoreItem(HeldItemSnapshot item)
        {
            try
            {
                var drop = await Context.Items.DropHeldAsync(
                    Vec3.Zero,
                    Context.Lifetime.StoppingToken);
                if (!CheckMainThread("world.physics-entities-items-interactions", "drop-completion"))
                {
                    return;
                }
                var delay = await Context.Scheduler.DelayAsync(
                    TimeSpan.FromMilliseconds(250),
                    Context.Lifetime.StoppingToken);
                if (!delay.Succeeded)
                {
                    return;
                }
                var restore = await Context.Items.GiveAsync(
                    new ItemGrantRequest(item.ItemId),
                    Context.Lifetime.StoppingToken);
                if (!CheckMainThread("world.physics-entities-items-interactions", "give-completion"))
                {
                    return;
                }
                if (drop.Succeeded && restore.Succeeded)
                {
                    Context.Logger.Info(Prefix + "|OBSERVE|item-drop-give|" + item.ItemId);
                }
                else
                {
                    Fail("world.physics-entities-items-interactions", drop.ErrorMessage + " " + restore.ErrorMessage);
                }
            }
            catch (Exception exception)
            {
                Fail("world.physics-entities-items-interactions", exception.Message);
            }
            finally
            {
                itemProbeRunning = false;
            }
        }

        private void Pass(string id, string detail = "ok")
        {
            if (completed.Add(id))
            {
                Context.Logger.Info(Prefix + "|PASS|" + id + "|" + Sanitize(detail));
            }
        }

        private void Fail(string id, string detail)
        {
            if (failed.Add(id + "|" + detail))
            {
                Context.Logger.Error(Prefix + "|FAIL|" + id + "|" + Sanitize(detail));
            }
        }

        private bool CheckMainThread(string caseId, string stage)
        {
            var current = Thread.CurrentThread.ManagedThreadId;
            if (current == mainThreadId)
            {
                return true;
            }

            Fail(caseId, stage + " ran on thread " + current + "; expected " + mainThreadId);
            return false;
        }

        private static string Sanitize(string value)
        {
            return (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Replace('|', '/');
        }
    }
}
