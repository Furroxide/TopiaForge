using System;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.Sandbox
{
    /// <summary>HUD commands plus keyboard/voice input for <see cref="RobotChat"/>.</summary>
    internal sealed partial class RobotChat
    {
        /// <summary>SEND click / Enter in the input field.</summary>
        public void SubmitFromHud(string text)
        {
            if (!open || conversation == null || conversation.IsThinking || Closing
                || string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            lastOperatorText = text;
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

        /// <summary>FOLLOW ME click: deterministic clean-slate program, no LLM turn required.</summary>
        public void FollowMeFromHud()
        {
            AcceptProgramFromHud(RobotObjective.Follow("PLAYER"));
        }

        /// <summary>IDLE click: deterministic clean-slate stand-down, no LLM turn required.</summary>
        public void IdleFromHud()
        {
            AcceptProgramFromHud(RobotObjective.Idle());
        }

        /// <summary>SET FREE click: clear mod control and hand the native brain back.</summary>
        public void SetFreeFromHud()
        {
            if (!QuickControlsEnabled)
            {
                return;
            }

            acceptedProgram = null;
            acceptedAutonomous = true;
            status = "Set free — thinking for itself.";
            closeAt = Time.unscaledTime + ExitLingerSeconds;
        }

        private void AcceptProgramFromHud(RobotObjective program)
        {
            if (!QuickControlsEnabled || program == null)
            {
                return;
            }

            acceptedAutonomous = false;
            acceptedProgram = program;
            status = "Programmed: " + program.Describe();
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
                    lastOperatorText = heard;
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

        private static KeyCode ParseKey(string value, KeyCode fallback)
        {
            return Enum.TryParse<KeyCode>(value, ignoreCase: true, out var parsed) && parsed != KeyCode.None
                ? parsed
                : fallback;
        }
    }
}
