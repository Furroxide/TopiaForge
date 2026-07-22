using System;
using System.Globalization;
using TopiaForge.Mods;

namespace TopiaForge.Zombies
{
    internal sealed partial class ZombiesConversationController
    {
        private OperationResult<bool> Rebuild()
        {
            if (surface == null)
            {
                return OperationResult<bool>.Failure(
                    ModErrorCode.InvalidState,
                    "The JACK IN channel is closed.");
            }

            UpdateStatus();
            var busy = turnTask != null || voiceTask != null || voiceCapture != null;
            var voiceAvailable = voiceAction != null && dialogueInput?.IsVoiceAvailable == true;
            UiNode actions = voiceAvailable
                ? new UiRow(
                    new UiButton(
                        "jack-in-submit",
                        busy ? "TRANSMITTING..." : "TRANSMIT",
                        Submit,
                        UiButtonStyle.Primary,
                        !busy && !voiceMode),
                    new UiButton(
                        "jack-in-input-mode",
                        voiceMode ? "USE TEXT" : "USE VOICE",
                        ToggleVoiceMode,
                        UiButtonStyle.Ghost,
                        !busy),
                    new UiButton(
                        "jack-in-close",
                        "SEVER LINK",
                        () => Finish(ConversationDecision.Refuse),
                        UiButtonStyle.Ghost))
                : new UiRow(
                    new UiButton(
                        "jack-in-submit",
                        busy ? "TRANSMITTING..." : "TRANSMIT",
                        Submit,
                        UiButtonStyle.Primary,
                        !busy),
                    new UiButton(
                        "jack-in-close",
                        "SEVER LINK",
                        () => Finish(ConversationDecision.Refuse),
                        UiButtonStyle.Ghost));
            return surface.SetContent(new UiColumn(
                new UiScroll(new UiText(transcript, UiTextStyle.Body), height: 220f),
                new UiTextInput(
                    "jack-in-text",
                    "Message",
                    draft,
                    value => draft = value,
                    placeholder: busy
                        ? "Waiting for the channel..."
                        : voiceMode
                            ? "Voice mode is active"
                            : "Persuade the robot in your own words",
                    maximumLength: 240,
                    enabled: !busy && !voiceMode),
                actions,
                new UiText(
                    voiceAvailable
                        ? (voiceMode
                            ? "VOICE MODE // hold " + VoiceControl() + " to speak; use the button for text"
                            : "TEXT MODE // use the button to enable voice")
                        : "TEXT CHANNEL // live decisions are still gated by robot resistance",
                    UiTextStyle.Caption,
                    UiTone.Warning)));
        }

        private void UpdateStatus()
        {
            if (surface == null)
            {
                return;
            }

            lastDisplayedSecond = (int)Math.Ceiling(remainingSeconds);
            surface.SetBody(
                "SIGNAL  " + (disposition * 100f).ToString("0", CultureInfo.InvariantCulture)
                + "%    TIME  " + lastDisplayedSecond.ToString(CultureInfo.InvariantCulture)
                + "s    TURN  " + ((conversation?.TurnCount ?? 0) + 1).ToString(CultureInfo.InvariantCulture)
                + " / " + (conversation?.MaxTurns ?? 0).ToString(CultureInfo.InvariantCulture));
        }

        private void AcquirePause()
        {
            var chronosError = string.Empty;
            if (time?.IsAvailable == true)
            {
                var result = time.Freeze("zombies-jack-in", suspendPlayer: true);
                if (result.TryGetValue(out var lease))
                {
                    freeze = lease;
                    PauseAcquired();
                    return;
                }

                chronosError = result.ErrorMessage;
            }

            var fallback = context.LocalPlayer.AcquireControl("Zombies JACK IN");
            if (fallback.TryGetValue(out var playerControl))
            {
                control = playerControl;
                PauseAcquired();
            }
            else
            {
                pauseRetryTimer = PauseRetrySeconds;
                if (!pauseFailureReported)
                {
                    pauseFailureReported = true;
                    context.Diagnostics.Report(new DiagnosticEntry(
                        "ZOMBIES_JACK_IN_PAUSE_FAILED",
                        "JACK IN could not pause gameplay; pause acquisition will retry in the background.",
                        DiagnosticSeverity.Warning,
                        string.IsNullOrWhiteSpace(chronosError)
                            ? fallback.ErrorMessage
                            : "Chronos: " + chronosError + " Player fallback: " + fallback.ErrorMessage));
                }
            }
        }

        private void EnsurePause(float controlDelta)
        {
            if (freeze?.IsActive == true || control?.IsActive == true)
            {
                PauseAcquired();
                return;
            }

            pauseRetryTimer = Math.Max(0f, pauseRetryTimer - Math.Max(0f, controlDelta));
            if (pauseRetryTimer > 0f)
            {
                return;
            }

            freeze?.Dispose();
            freeze = null;
            control?.Dispose();
            control = null;
            AcquirePause();
        }

        private void PauseAcquired()
        {
            pauseRetryTimer = 0f;
            pauseFailureReported = false;
        }

        private void ToggleVoiceMode()
        {
            if (dialogueInput?.IsVoiceAvailable != true
                || turnTask != null || voiceTask != null || voiceCapture != null)
            {
                return;
            }

            voiceMode = !voiceMode;
            Rebuild();
        }

        private IInputAction? RegisterAction(
            string name,
            string displayName,
            string key,
            bool suppressWhileUiFocused = true)
        {
            var result = context.Input.RegisterAction(new InputActionDefinition(
                name,
                displayName,
                new[] { InputBinding.Key(key) },
                suppressWhileUiFocused));
            if (result.TryGetValue(out var action))
            {
                return action;
            }

            context.Diagnostics.Report(new DiagnosticEntry(
                "ZOMBIES_INPUT_UNAVAILABLE",
                "Zombies input '" + name + "' is unavailable.",
                DiagnosticSeverity.Warning,
                result.ErrorMessage));
            return null;
        }

        private string VoiceControl()
        {
            var bindings = voiceAction?.Bindings;
            return bindings != null && bindings.Count > 0
                ? bindings[0].Control
                : config.VoiceKey;
        }
    }
}
