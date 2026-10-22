using System;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Voice
{
    /// <summary>
    /// Reads the microphone into one preallocated buffer and hands each read to the voice service
    /// (<see cref="IInternalVoiceService.SubmitCapture"/>). It only moves samples: the provider resamples, detects voice,
    /// codes and sends, and the gate that decides whether the microphone is open at all is <see cref="VoiceChatRuntime"/>'s.
    /// Nothing is recorded or written: the buffer is the only place samples rest, and the next read overwrites it. The
    /// buffer holds a second of the open recording's rate and grows only for a faster device, so steady capture allocates
    /// nothing. Main thread only.
    /// </summary>
    internal sealed class VoiceCapture
    {
        /// <summary>How much one read can carry: a second, past anything a frame or a stall the service keeps.</summary>
        internal const int BufferSeconds = 1;

        private readonly IVoiceMicrophone microphone;
        private float[] buffer = Array.Empty<float>();
        private bool abandoned;

        internal VoiceCapture(IVoiceMicrophone microphone)
        {
            this.microphone = microphone ?? throw new ArgumentNullException(nameof(microphone));
        }

        /// <summary>Gets whether a recording is open and read.</summary>
        internal bool IsCapturing => microphone.IsOpen;

        /// <summary>Gets whether a recording was left running for another use of the microphone and not ended yet.</summary>
        internal bool HasAbandoned => abandoned;

        /// <summary>Gets how the last attempt to open the microphone went.</summary>
        internal VoiceMicrophoneOpen LastOpen { get; private set; } = VoiceMicrophoneOpen.Opened;

        /// <summary>Gets the samples handed to the voice service so far, for tests and diagnostics.</summary>
        internal long SubmittedSamples { get; private set; }

        /// <summary>Gets the buffer reads land in, for tests that check it is never replaced.</summary>
        internal float[] Buffer => buffer;

        /// <summary>Opens the microphone on <paramref name="device"/> unless it is open; false when it could not be opened.</summary>
        internal bool Open(string? device)
        {
            if (microphone.IsOpen) return true;
            LastOpen = microphone.Open(device);
            if (LastOpen != VoiceMicrophoneOpen.Opened || !microphone.IsOpen) return false;
            var needed = Math.Max(1, microphone.SampleRate) * BufferSeconds;
            if (buffer.Length < needed) buffer = new float[needed];
            return true;
        }

        /// <summary>Ends the recording.</summary>
        internal void Close()
        {
            if (microphone.IsOpen) microphone.Close();
        }

        /// <summary>Stops reading without ending the recording: another use of the microphone took its device.</summary>
        internal void Abandon()
        {
            if (!microphone.IsOpen) return;
            microphone.Abandon();
            abandoned = true;
        }

        /// <summary>Ends a recording left running for another use, once that use has let go; nothing when none was left.</summary>
        internal void EndAbandoned()
        {
            if (!abandoned) return;
            abandoned = false;
            microphone.EndAbandoned();
        }

        /// <summary>
        /// Reads what was recorded since the last pump, scales it by <paramref name="gain"/> and hands it to
        /// <paramref name="voice"/>. Returns how many samples went; negative when the recording stopped under voice chat.
        /// </summary>
        internal int Pump(IInternalVoiceService voice, float gain)
        {
            if (voice == null) throw new ArgumentNullException(nameof(voice));
            if (!microphone.IsOpen) return 0;
            var count = microphone.Read(buffer, 0, buffer.Length);
            if (count < 0)
            {
                // The device went away under the recording; it reads as no microphone until one opens again.
                LastOpen = VoiceMicrophoneOpen.NoDevice;
                return count;
            }

            if (count == 0) return 0;
            if (gain != 1f) Scale(buffer, count, gain);
            voice.SubmitCapture(buffer, count, microphone.SampleRate);
            SubmittedSamples += count;
            return count;
        }

        private static void Scale(float[] samples, int count, float gain)
        {
            for (var index = 0; index < count; index++)
            {
                var value = samples[index] * gain;
                samples[index] = value > 1f ? 1f : value < -1f ? -1f : value;
            }
        }
    }
}
