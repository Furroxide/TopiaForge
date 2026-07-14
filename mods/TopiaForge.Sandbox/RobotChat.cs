using System;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;

namespace TopiaForge.Sandbox
{
    /// <summary>
    /// The PROGRAM verb's flow: opens a conversation with one sandbox robot, drains each completed turn through
    /// <see cref="RobotProgramDirector"/>, and either keeps the chat open (CHAT / degraded turns) or exits the chat
    /// and programs the robot (any accepted action). Owns every key (Tab/V; ESC arrives via the window's dismiss),
    /// the voice push-to-talk capture, and the player-control suspension; the window only renders this state
    /// (Zombies conversation discipline). The robot's previous program is remembered and restored when the operator
    /// leaves without programming anything new.
    /// </summary>
    internal sealed partial class RobotChat : IDisposable
    {
        private enum InputMode
        {
            Text,
            Voice
        }

        // How long the robot's acceptance line stays on screen before the chat closes itself and the program runs.
        private const float ExitLingerSeconds = 1.5f;

        private readonly IModContext context;
        private readonly SandboxConfig config;
        private readonly UiHost ui;
        private readonly IRobotAgentService robots;
        private readonly IRobotConversationService conversations;
        private readonly IRobotObjectiveService objectives;
        private readonly IPlayerDialogueInputService? dialogueInput;
        private readonly Func<string, IRobotAgent?>? findRobotByTargetName; // for "what is ROBOT 2 doing" facts

        private Ui.RobotChatWindow? window;
        private IRobotConversation? conversation;
        private IRobotAgent? agent;
        private string robotName = "Robot";
        private RobotObjective? previousProgram;
        private RobotBrainMode previousBrainMode;
        private RobotObjective? acceptedProgram;
        private bool acceptedAutonomous;
        private readonly System.Collections.Generic.List<string> offeredTargets =
            new System.Collections.Generic.List<string>();
        private readonly System.Collections.Generic.List<string> offeredRobotTargets =
            new System.Collections.Generic.List<string>();
        private string selfTargetName = string.Empty;
        private int processedTurns;
        private string lastOperatorText = string.Empty;
        private RobotObjective? describedProgram;
        private string describedProgramText = "NONE";
        private string reply = string.Empty;
        private string status = string.Empty;
        private InputMode inputMode;
        private IVoiceCapture? voiceCapture;
        private float closeAt;
        private bool open;

        public RobotChat(
            IModContext context,
            SandboxConfig config,
            UiHost ui,
            IRobotAgentService robots,
            IRobotConversationService conversations,
            IRobotObjectiveService objectives,
            IPlayerDialogueInputService? dialogueInput,
            Func<string, IRobotAgent?>? findRobotByTargetName = null)
        {
            this.context = context;
            this.config = config;
            this.ui = ui;
            this.robots = robots;
            this.conversations = conversations;
            this.objectives = objectives;
            this.dialogueInput = dialogueInput;
            this.findRobotByTargetName = findRobotByTargetName;
        }

        public bool IsOpen => open;

        // Window-facing state.
        public string RobotName => robotName;
        public string Reply => reply;
        public string Status => status;
        public bool Thinking => conversation != null && conversation.IsThinking;
        public int Turn => conversation?.TurnCount ?? 0;
        public int MaxTurns => conversation?.MaxTurns ?? config.ChatMaxTurns;
        public bool VoiceMode => inputMode == InputMode.Voice;
        public bool VoiceRecording => voiceCapture != null && voiceCapture.IsRecording;
        public bool VoiceAvailable => config.VoiceInputEnabled && dialogueInput != null && dialogueInput.IsVoiceAvailable;
        public string VoiceKeyName => config.VoiceKey;
        public bool Closing => acceptedProgram != null || acceptedAutonomous;
        public bool QuickControlsEnabled => open && conversation != null && !conversation.IsThinking && voiceCapture == null && !Closing;
        public string InteractionVerb => previousBrainMode == RobotBrainMode.Autonomous ? "REPROGRAM" : "PROGRAM";
        public bool HasProgram => acceptedAutonomous || ResolveDisplayedProgram() != null;
        public string ProgramDescription
        {
            get
            {
                if (acceptedAutonomous)
                {
                    return "AUTONOMOUS";
                }

                var objective = ResolveDisplayedProgram();
                if (!ReferenceEquals(objective, describedProgram))
                {
                    describedProgram = objective;
                    describedProgramText = objective?.Describe() ?? "NONE";
                }

                return describedProgramText;
            }
        }

        /// <summary>Opens the chat with a robot. False (with a toast) when the brain backend is unavailable.</summary>
        public bool Begin(IRobotAgent target, string displayName, string ownTargetName)
        {
            if (open || target == null || !target.IsAlive)
            {
                return false;
            }

            if (!conversations.IsAvailable)
            {
                ui.Toast("Robot brain offline — check your connection and try again.", TopiaForgeTone.Warning);
                return false;
            }

            agent = target;
            robotName = string.IsNullOrWhiteSpace(displayName) ? "Robot" : displayName;

            // The chat suspends whatever the robot was doing — its program AND its own brain (reprogramming an
            // autonomous robot overrides the native brain); LEAVE without a new program restores both.
            var current = objectives.GetObjective(target);
            previousProgram = current?.Objective;
            previousBrainMode = target.BrainMode;
            objectives.ClearObjective(target);
            target.SetBrainMode(RobotBrainMode.Dormant);

            // The robot must not be offered itself as a target ("follow yourself" is nonsense); its own name is
            // handed over separately, resolvable only as a delivered task's target ("tell it to follow you").
            offeredTargets.Clear();
            offeredRobotTargets.Clear();
            foreach (var name in objectives.TargetNames)
            {
                if (string.Equals(name, ownTargetName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                offeredTargets.Add(name);
                if (objectives.TryGetTargetInfo(name, out var info) && info.Kind == RobotTargetKind.Robot)
                {
                    offeredRobotTargets.Add(name); // the closed set REPROGRAM recipients come from
                }
            }

            selfTargetName = ownTargetName ?? string.Empty;

            conversation = conversations.BeginConversation(RobotProgramDirector.BuildRequest(
                robotName,
                previousProgram?.Describe() ?? string.Empty,
                offeredTargets,
                string.IsNullOrWhiteSpace(selfTargetName) ? null : selfTargetName,
                DescribeOfferedTargets,
                config.ChatMaxTurns,
                config.ChatTemperature));

            processedTurns = 0;
            lastOperatorText = string.Empty;
            describedProgram = null;
            describedProgramText = "NONE";
            reply = string.Empty;
            status = "Say what you want it to do.";
            acceptedProgram = null;
            acceptedAutonomous = false;
            closeAt = 0f;
            inputMode = InputMode.Text;
            open = true;

            robots.SetPlayerControlsEnabled(false);
            window ??= new Ui.RobotChatWindow(ui, this);
            window.Show(robotName);
            return true;
        }

        public void Update()
        {
            if (!open)
            {
                return;
            }

            // The robot vanished mid-chat (undo, cleanup, killed) — tear down with nothing to program.
            if (agent == null || !agent.IsAlive)
            {
                Close(applyProgram: false, restorePrevious: false);
                return;
            }

            // The acceptance line lingered long enough — run the program (or set the robot free).
            if (Closing)
            {
                if (Time.unscaledTime >= closeAt)
                {
                    Close(applyProgram: true, restorePrevious: false);
                }

                window?.Tick();
                return;
            }

            ReadInput();
            if (!open)
            {
                return;
            }

            DrainTurn();
            window?.Tick();
        }

        public void Dispose()
        {
            if (open)
            {
                Close(applyProgram: false, restorePrevious: true);
            }

            window?.Dispose();
            window = null;
        }

        private void DrainTurn()
        {
            if (conversation == null)
            {
                return;
            }

            // Drain a completed turn exactly once.
            if (conversation.TurnReady && conversation.TurnCount > processedTurns)
            {
                processedTurns = conversation.TurnCount;
                HandleTurn();
                if (!open || Closing)
                {
                    return;
                }
            }

            // Out of turns with no program accepted — the robot got bored; restore what it was doing.
            if (conversation.Ended && !conversation.IsThinking && processedTurns >= conversation.TurnCount)
            {
                ui.Toast(robotName + " went back to what it was doing.", TopiaForgeTone.Neutral);
                Close(applyProgram: false, restorePrevious: true);
            }
        }

        private void HandleTurn()
        {
            if (conversation == null)
            {
                return;
            }

            if (!string.IsNullOrEmpty(conversation.LastReply))
            {
                reply = conversation.LastReply;
            }

            if (!string.IsNullOrEmpty(conversation.LastError))
            {
                status = "Brain unreachable — try again in a moment.";
                return;
            }

            // The robot's face plays along with its line (best-effort native garnish, CHAT turns included). The
            // expression is derived from the decision rather than an LLM output field — a fourth structured field
            // would exceed the backend's 5-output cap and fail the whole turn.
            var emote = RobotProgramDirector.EmoteForDecision(conversation.LastDecision);
            if (!string.IsNullOrEmpty(emote))
            {
                agent?.SetEmote(emote);
            }

            conversation.LastValues.TryGetValue(RobotProgramDirector.TargetField, out var target);
            conversation.LastValues.TryGetValue(RobotProgramDirector.ProgramField, out var program);
            conversation.LastValues.TryGetValue(RobotProgramDirector.ProgramTargetField, out var programTarget);
            // Parse against the OFFERED (own-name-filtered) list — the full registry would let the robot be
            // programmed to follow itself; its own name resolves only inside a delivered task.
            var result = RobotProgramDirector.Parse(
                conversation.LastDecision,
                target,
                program,
                programTarget,
                offeredTargets,
                offeredRobotTargets,
                string.IsNullOrWhiteSpace(selfTargetName) ? null : selfTargetName,
                lastOperatorText);
            if (result.IsChat)
            {
                status = result.Problem ?? string.Empty;
                return;
            }

            // Exit-chat: the robot accepted a task (or was set free). Let its acceptance line linger, then close.
            if (result.GoAutonomous)
            {
                acceptedAutonomous = true;
                status = "Set free — thinking for itself.";
            }
            else
            {
                acceptedProgram = result.Objective;
                status = "Programmed: " + result.Objective!.Describe();
            }

            closeAt = Time.unscaledTime + ExitLingerSeconds;
        }

        private void Close(bool applyProgram, bool restorePrevious)
        {
            var target = agent;
            var program = acceptedProgram;
            var goAutonomous = acceptedAutonomous;
            var restore = previousProgram;
            var restoreBrainMode = previousBrainMode;

            conversation?.End();
            conversation = null;
            voiceCapture?.Cancel();
            voiceCapture = null;
            agent = null;
            previousProgram = null;
            previousBrainMode = RobotBrainMode.Dormant;
            acceptedProgram = null;
            acceptedAutonomous = false;
            offeredTargets.Clear();
            offeredRobotTargets.Clear();
            selfTargetName = string.Empty;
            processedTurns = 0;
            lastOperatorText = string.Empty;
            describedProgram = null;
            describedProgramText = "NONE";
            open = false;

            window?.Hide();
            robots.SetPlayerControlsEnabled(true);

            if (target != null && target.IsAlive)
            {
                target.SetEmote(string.Empty); // best-effort: drop any chat-time expression

                if (applyProgram && goAutonomous)
                {
                    // Set free: no mod objective; hand the robot back to its own native brain.
                    objectives.ClearObjective(target);
                    target.SetBrainMode(RobotBrainMode.Autonomous);
                    ui.Toast(robotName + " set free — thinking for itself.", TopiaForgeTone.Success);
                    context.Logger.Info("Sandbox set '" + robotName + "' autonomous.");
                }
                else if (applyProgram && program != null)
                {
                    // Programmed: the robot stays mod-driven (Begin already forced Dormant).
                    objectives.SetObjective(target, program);
                    ui.Toast(robotName + " programmed: " + program.Describe(), TopiaForgeTone.Success);
                    context.Logger.Info("Sandbox programmed '" + robotName + "': " + program.Describe());
                }
                else if (restorePrevious)
                {
                    // LEAVE: put back what the chat suspended — the program and the robot's own brain. Unless a
                    // courier delivered a program to THIS robot mid-chat: Begin cleared its objective, so any
                    // objective present now arrived by delivery, and the delivery beats the stale restore.
                    if (objectives.GetObjective(target) == null)
                    {
                        if (restore != null)
                        {
                            objectives.SetObjective(target, restore);
                        }

                        target.SetBrainMode(restoreBrainMode);
                    }
                }
            }
        }

    }
}
