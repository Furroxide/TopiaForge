using System;
using Robotopia.Mods;
using Robotopia.Mods.UnityUi;
using UnityEngine;

namespace Robotopia.Sandbox
{
    /// <summary>
    /// The PROGRAM verb's flow: opens a conversation with one sandbox robot, drains each completed turn through
    /// <see cref="RobotProgramDirector"/>, and either keeps the chat open (CHAT / degraded turns) or exits the chat
    /// and programs the robot (any accepted action). Owns every key (Tab/V; ESC arrives via the window's dismiss),
    /// the voice push-to-talk capture, and the player-control suspension; the window only renders this state
    /// (Zombies conversation discipline). The robot's previous program is remembered and restored when the operator
    /// leaves without programming anything new.
    /// </summary>
    internal sealed class RobotChat : IDisposable
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
        private int processedTurns;
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
            IPlayerDialogueInputService? dialogueInput)
        {
            this.context = context;
            this.config = config;
            this.ui = ui;
            this.robots = robots;
            this.conversations = conversations;
            this.objectives = objectives;
            this.dialogueInput = dialogueInput;
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
        public bool VoiceAvailable => dialogueInput != null && dialogueInput.IsVoiceAvailable;
        public string VoiceKeyName => config.VoiceKey;
        public bool Closing => acceptedProgram != null || acceptedAutonomous;
        public bool HasProgram => acceptedProgram != null || (agent != null && objectives.GetObjective(agent) != null);
        public string ProgramDescription =>
            acceptedProgram?.Describe()
            ?? (agent != null ? objectives.GetObjective(agent)?.Objective.Describe() : null)
            ?? previousProgram?.Describe()
            ?? "NONE";

        /// <summary>Opens the chat with a robot. False (with a toast) when the brain backend is unavailable.</summary>
        public bool Begin(IRobotAgent target, string displayName, string ownTargetName)
        {
            if (open || target == null || !target.IsAlive)
            {
                return false;
            }

            if (!conversations.IsAvailable)
            {
                ui.Toast("Robot brain offline — check your connection and try again.", QwTone.Warning);
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

            // The robot must not be offered itself as a target ("follow yourself" is nonsense).
            offeredTargets.Clear();
            foreach (var name in objectives.TargetNames)
            {
                if (!string.Equals(name, ownTargetName, StringComparison.OrdinalIgnoreCase))
                {
                    offeredTargets.Add(name);
                }
            }

            conversation = conversations.BeginConversation(RobotProgramDirector.BuildRequest(
                robotName,
                previousProgram?.Describe() ?? string.Empty,
                offeredTargets,
                DescribeOfferedTargets,
                config.ChatMaxTurns,
                config.ChatTemperature));

            processedTurns = 0;
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

        /// <summary>SEND click / Enter in the input field.</summary>
        public void SubmitFromHud(string text)
        {
            if (!open || conversation == null || conversation.IsThinking || Closing
                || string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            conversation.Submit(text);
            window?.ClearInput();
            status = string.Empty;
        }

        /// <summary>LEAVE click or the window being dismissed (ESC / close button).</summary>
        public void LeaveFromHud()
        {
            if (!open)
            {
                return;
            }

            // A dismissal while the acceptance line is lingering still applies the accepted program.
            if (Closing)
            {
                Close(applyProgram: true, restorePrevious: false);
                return;
            }

            Close(applyProgram: false, restorePrevious: true);
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
                ui.Toast(robotName + " went back to what it was doing.", QwTone.Neutral);
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

            conversation.LastValues.TryGetValue(RobotProgramDirector.TargetField, out var target);
            // Parse against the OFFERED (own-name-filtered) list — the full registry would let the robot be
            // programmed to follow itself.
            var result = RobotProgramDirector.Parse(conversation.LastDecision, target, offeredTargets);
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

        private void ReadInput()
        {
            if (Input.GetKeyDown(KeyCode.Tab) && VoiceAvailable)
            {
                ToggleInputMode();
            }

            // While the mic is actively recording, keep draining it even if voice availability drops mid-record.
            if (voiceCapture != null && voiceCapture.IsRecording)
            {
                ReadVoiceInput();
                return;
            }

            if (inputMode == InputMode.Voice && VoiceAvailable)
            {
                ReadVoiceInput();
            }
        }

        private void ReadVoiceInput()
        {
            var voiceKey = ParseKey(config.VoiceKey, KeyCode.V);

            if (voiceCapture == null && Input.GetKeyDown(voiceKey) && dialogueInput != null
                && conversation != null && !conversation.IsThinking && !Closing)
            {
                voiceCapture = dialogueInput.BeginVoiceCapture();
                status = "Listening…";
                return;
            }

            if (voiceCapture == null)
            {
                return;
            }

            if (voiceCapture.IsRecording && Input.GetKeyUp(voiceKey))
            {
                voiceCapture.Stop();
                status = "Transcribing…";
                return;
            }

            if (voiceCapture.IsComplete)
            {
                var heard = voiceCapture.Found ? voiceCapture.Text : string.Empty;
                var why = voiceCapture.Error;
                voiceCapture = null;
                if (!string.IsNullOrWhiteSpace(heard) && conversation != null)
                {
                    conversation.Submit(heard);
                    status = string.Empty;
                }
                else
                {
                    status = string.IsNullOrEmpty(why)
                        ? "Didn't catch that — try again."
                        : "Didn't catch that (" + why + ") — try again.";
                }
            }
        }

        private void ToggleInputMode()
        {
            inputMode = inputMode == InputMode.Text ? InputMode.Voice : InputMode.Text;
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
            processedTurns = 0;
            open = false;

            window?.Hide();
            robots.SetPlayerControlsEnabled(true);

            if (target != null && target.IsAlive)
            {
                if (applyProgram && goAutonomous)
                {
                    // Set free: no mod objective; hand the robot back to its own native brain.
                    objectives.ClearObjective(target);
                    target.SetBrainMode(RobotBrainMode.Autonomous);
                    ui.Toast(robotName + " set free — thinking for itself.", QwTone.Success);
                    context.Logger.Info("Sandbox set '" + robotName + "' autonomous.");
                }
                else if (applyProgram && program != null)
                {
                    // Programmed: the robot stays mod-driven (Begin already forced Dormant).
                    objectives.SetObjective(target, program);
                    ui.Toast(robotName + " programmed: " + program.Describe(), QwTone.Success);
                    context.Logger.Info("Sandbox programmed '" + robotName + "': " + program.Describe());
                }
                else if (restorePrevious)
                {
                    // LEAVE: put back what the chat suspended — the program and the robot's own brain.
                    if (restore != null)
                    {
                        objectives.SetObjective(target, restore);
                    }

                    target.SetBrainMode(restoreBrainMode);
                }
            }
        }

        // Per-turn "who/what/where" lines for every offered target, from the robot's current position — the
        // ground truth that lets it follow another robot or answer "where is X?" instead of guessing at names.
        private System.Collections.Generic.IReadOnlyList<string> DescribeOfferedTargets()
        {
            var described = new System.Collections.Generic.List<string>(offeredTargets.Count);
            var observer = agent;
            if (observer == null || !observer.IsAlive)
            {
                return described;
            }

            var from = observer.Position;
            foreach (var name in offeredTargets)
            {
                if (!objectives.TryGetTargetInfo(name, out var info))
                {
                    continue;
                }

                var snapshot = objectives.TryResolveTarget(name, out var resolved)
                    ? resolved
                    : (RobotTargetSnapshot?)null;
                described.Add(name + ": " + RobotTargetFacts.Describe(info, snapshot, from));
            }

            return described;
        }

        private static KeyCode ParseKey(string value, KeyCode fallback)
        {
            return Enum.TryParse<KeyCode>(value, ignoreCase: true, out var parsed) && parsed != KeyCode.None
                ? parsed
                : fallback;
        }
    }
}
