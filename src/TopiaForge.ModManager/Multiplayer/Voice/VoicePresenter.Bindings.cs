using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Voice
{
    /// <summary>
    /// VOICE's push-to-talk binding and the microphones to choose from. The binding resolves against the census of the
    /// game's own bindings whenever either changes, and the runtime reads what it resolved to; a capture suspends
    /// push-to-talk until the new control is let go, so the press that binds it never talks.
    /// </summary>
    internal sealed partial class VoicePresenter
    {
        /// <summary>How long a census that could not read the game's bindings waits before it reads them again, in seconds.</summary>
        internal const float CensusRetrySeconds = 10f;

        /// <summary>The longest microphone name the list shows; a longer one is cut.</summary>
        internal const int DeviceChars = 64;

        private const string EscapeKey = "Escape";

        private readonly IVoiceMicrophone microphone;
        private readonly IVoiceInput input;
        private readonly IVoiceNativeBindings nativeBindings;
        private readonly List<string> nativePaths = new List<string>(64);
        private readonly InputBinding?[] resolved = new InputBinding?[VoicePushToTalk.SlotCount];
        private NativeInputCensus census = NativeInputCensus.Fallback;
        private VoiceSettings? resolvedFrom;
        private NativeInputCensus? resolvedWith;
        private InternalSessionRole censusRole;
        private float sinceCensus;
        private int bindingsVersion;
        private InternalVoiceTransmitMode readyMode;
        private string fallbackNote = string.Empty;
        private int capturingSlot = -1;
        private bool captureArmed;
        private InputBinding? releaseFirst;
        private string captureNote = string.Empty;
        private ScreenTone captureTone = ScreenTone.Neutral;
        private IReadOnlyList<string> listedDevices = Array.Empty<string>();
        private bool devicesListed;
        private int devicesVersion;
        private int optionsVersion = -1;
        private string? optionsDevice;
        private string[] deviceOptions = { VoiceText.DefaultDevice };
        private string?[] deviceNames = { null };
        private int deviceIndex;
        private string deviceNote = string.Empty;

        public int CapturingSlot => capturingSlot;

        public string BindingNote => capturingSlot >= 0 ? captureNote : fallbackNote;

        public ScreenTone BindingNoteTone => capturingSlot >= 0 ? captureTone : fallbackNote.Length > 0 ? ScreenTone.Warning : ScreenTone.Muted;

        public IReadOnlyList<string> DeviceOptions => deviceOptions;

        public int Device => deviceIndex;

        public string DeviceNote => deviceNote;

        /// <summary>Gets the census push-to-talk resolves against now, for tests.</summary>
        internal NativeInputCensus Census => census;

        public string BindingText(int slot)
        {
            if (slot < 0 || slot >= VoicePushToTalk.SlotCount) return string.Empty;
            return slot == capturingSlot ? VoiceText.CapturePrompt : VoiceControls.Describe(resolved[slot]);
        }

        public void CaptureBinding(int slot)
        {
            if (slot < 0 || slot >= VoicePushToTalk.SlotCount || !preferences.IsLoaded) return;
            if (slot == capturingSlot)
            {
                CancelCapture();
                return;
            }

            capturingSlot = slot;
            captureArmed = false;
            captureNote = VoiceText.CaptureNote;
            captureTone = ScreenTone.Neutral;

            // Push-to-talk waits while the player picks its control, so the press that binds it never talks.
            runtime.PushToTalkSuspended = true;
            RefreshCensus();
        }

        public void ResetBindings()
        {
            if (!preferences.IsLoaded) return;
            CancelCapture();
            var settings = preferences.Settings;
            if (IsDefault(settings.PushToTalk)) return;
            preferences.Update(settings.WithDefaultPushToTalk());
            Notify(VoiceText.BindingsReset, ScreenTone.Neutral);
        }

        public void SetDevice(int index)
        {
            if (!preferences.IsLoaded || index < 0 || index >= deviceNames.Length) return;
            var name = deviceNames[index];
            if (string.Equals(name, preferences.Settings.Device, StringComparison.Ordinal)) return;
            preferences.Update(preferences.Settings.WithDevice(name));
            runtime.RetryMicrophone();
        }

        public void RefreshDevices()
        {
            IReadOnlyList<string>? listed;
            try
            {
                listed = microphone.ListDevices();
            }
            catch (Exception)
            {
                listed = null;
            }

            var kept = new List<string>(listed?.Count ?? 0);
            if (listed != null)
            {
                foreach (var name in listed)
                {
                    if (VoiceSettings.IsDeviceName(name) && !kept.Contains(name)) kept.Add(name);
                }
            }

            listedDevices = kept;
            devicesListed = true;
            devicesVersion++;
            runtime.RetryMicrophone();
        }

        /// <summary>Reads the game's bindings again; while they cannot be read, the census's fallback stands in.</summary>
        private void RefreshCensus()
        {
            sinceCensus = 0f;
            nativePaths.Clear();
            bool read;
            try
            {
                read = nativeBindings.TryRead(nativePaths);
            }
            catch (Exception)
            {
                read = false;
            }

            var next = read && nativePaths.Count > 0 ? NativeInputCensus.FromPaths(nativePaths) : NativeInputCensus.Fallback;
            nativePaths.Clear();

            // Bindings that name no key or mouse button, as a gamepad's alone, tell nothing: the fallback stands in for them too.
            if (next.Count == 0) next = NativeInputCensus.Fallback;
            if (!next.SameAs(census)) census = next;
        }

        private void TickBindings(VoiceSettings settings, float deltaTime)
        {
            var role = ReadRole();
            if (role != censusRole)
            {
                // A session that starts or ends may bring the game's player, and its bindings, with it.
                censusRole = role;
                RefreshCensus();
            }
            else if (!census.IsFromGame)
            {
                sinceCensus += float.IsNaN(deltaTime) || deltaTime < 0f ? 0f : deltaTime;
                if (sinceCensus >= CensusRetrySeconds) RefreshCensus();
            }

            if (!ReferenceEquals(settings, resolvedFrom) || !ReferenceEquals(census, resolvedWith)) Resolve(settings);
            if (releaseFirst.HasValue && !input.IsHeld(releaseFirst.Value))
            {
                releaseFirst = null;
                if (capturingSlot < 0) runtime.PushToTalkSuspended = false;
            }

            if (capturingSlot >= 0) TickCapture(settings);
            TickDevices(settings);
        }

        private void Resolve(VoiceSettings settings)
        {
            resolvedFrom = settings;
            resolvedWith = census;
            var fellBack = VoicePushToTalk.Resolve(settings.PushToTalk, census, resolved);
            runtime.SetBindings(resolved);
            bindingsVersion++;
            fallbackNote = fellBack == 0 ? string.Empty : FallbackNote(settings.PushToTalk);
        }

        private string FallbackNote(IReadOnlyList<InputBinding?> chosen)
        {
            for (var slot = 0; slot < resolved.Length; slot++)
            {
                var wanted = slot < chosen.Count ? chosen[slot] : null;
                if (!wanted.HasValue) continue;
                var actual = resolved[slot];
                if (actual.HasValue && VoicePushToTalk.Same(actual.Value, wanted.Value)) continue;
                return VoiceText.FellBack(
                    VoiceControls.Describe(wanted),
                    census.Uses(wanted.Value),
                    actual.HasValue ? VoiceControls.Describe(actual) : null,
                    wanted.Value.Kind == InputBindingKind.MouseButton);
            }

            return string.Empty;
        }

        private void TickCapture(VoiceSettings settings)
        {
            // The frame the capture starts may still hold the press that started it, such as Enter on its button.
            if (!captureArmed)
            {
                input.TryTakePressed(out _);
                captureArmed = true;
                return;
            }

            if (input.IsTyping || !input.TryTakePressed(out var pressed)) return;
            if (pressed.Kind == InputBindingKind.Key && string.Equals(pressed.Control, EscapeKey, StringComparison.Ordinal))
            {
                CancelCapture();
                return;
            }

            var slot = capturingSlot;
            var problem = VoicePushToTalk.Check(pressed, slot, census, resolved);
            if (problem != VoiceBindingProblem.None)
            {
                captureNote = VoicePushToTalk.Explain(problem, pressed);
                captureTone = ScreenTone.Warning;
                return;
            }

            // The control is down as it binds: push-to-talk waits until it is let go.
            releaseFirst = pressed;
            CancelCapture();
            var chosen = settings.PushToTalk[slot];
            if (chosen.HasValue && VoicePushToTalk.Same(chosen.Value, pressed)) return;
            preferences.Update(settings.WithPushToTalk(slot, pressed));

            // Push-to-talk takes the new control in this frame, not the next.
            Resolve(preferences.Settings);
            Notify(VoiceText.BindingNow + VoiceControls.Describe(pressed) + ".", ScreenTone.Success);
        }

        private void CancelCapture()
        {
            if (capturingSlot < 0) return;
            capturingSlot = -1;
            captureArmed = false;
            captureNote = string.Empty;
            if (!releaseFirst.HasValue) runtime.PushToTalkSuspended = false;
        }

        /// <summary>Says how to talk with the bindings push-to-talk resolved to.</summary>
        private string ReadyText()
        {
            string? first = null;
            string? second = null;
            foreach (var binding in resolved)
            {
                if (!binding.HasValue) continue;
                if (first == null) first = VoiceControls.Describe(binding);
                else second = VoiceControls.Describe(binding);
            }

            return first == null ? VoiceText.NoPushToTalk : VoiceText.HoldToTalk(first, second);
        }

        private void TickDevices(VoiceSettings settings)
        {
            var chosen = settings.Device;
            if (optionsVersion == devicesVersion && string.Equals(optionsDevice, chosen, StringComparison.Ordinal)) return;
            optionsVersion = devicesVersion;
            optionsDevice = chosen;
            var listed = listedDevices;
            var known = chosen == null || Contains(listed, chosen);
            var count = listed.Count + (known ? 1 : 2);
            var options = new string[count];
            var names = new string?[count];
            options[0] = VoiceText.DefaultDevice;
            var index = 0;
            for (var position = 0; position < listed.Count; position++)
            {
                options[position + 1] = TopiaForgePlainText.Sanitize(listed[position], DeviceChars);
                names[position + 1] = listed[position];
                if (string.Equals(listed[position], chosen, StringComparison.Ordinal)) index = position + 1;
            }

            if (!known)
            {
                // The chosen microphone stays listed while it is away, so choosing nothing never forgets it.
                index = count - 1;
                options[index] = TopiaForgePlainText.Sanitize(chosen, DeviceChars) + (devicesListed ? VoiceText.NotConnectedSuffix : string.Empty);
                names[index] = chosen;
            }

            deviceOptions = options;
            deviceNames = names;
            deviceIndex = index;
            deviceNote = !devicesListed ? string.Empty : listed.Count == 0 ? VoiceText.NoDevices : known ? string.Empty : VoiceText.DeviceMissing;
        }

        private static bool Contains(IReadOnlyList<string> listed, string name)
        {
            for (var index = 0; index < listed.Count; index++)
            {
                if (string.Equals(listed[index], name, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        private static bool IsDefault(IReadOnlyList<InputBinding?> slots)
        {
            for (var slot = 0; slot < VoicePushToTalk.SlotCount; slot++)
            {
                var binding = slot < slots.Count ? slots[slot] : null;
                if (!binding.HasValue || !VoicePushToTalk.Same(binding.Value, VoicePushToTalk.DefaultOf(slot))) return false;
            }

            return true;
        }
    }
}
