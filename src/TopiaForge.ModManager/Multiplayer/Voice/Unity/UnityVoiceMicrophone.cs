using System;
using System.Collections.Generic;
using UnityEngine;

namespace TopiaForge.ModManager.Voice
{
    /// <summary>
    /// The microphone over Unity's <c>Microphone</c>: a one-second looping recording, read each frame from where the last
    /// read stopped into the caller's buffer, so nothing it hears is kept beyond that second or written anywhere. It asks
    /// for 48 kHz, or the nearest rate the device offers; the voice service resamples whatever it gets. Unity keys a
    /// recording by its device, so a recording another use of the microphone took is let go without being ended
    /// (<see cref="Abandon"/>), and ended only once that use is done (<see cref="EndAbandoned"/>). Main thread only.
    /// </summary>
    internal sealed class UnityVoiceMicrophone : IVoiceMicrophone
    {
        /// <summary>The rate voice chat asks a microphone for, in hertz.</summary>
        internal const int PreferredRate = 48000;

        private const int ClipSeconds = 1;

        private AudioClip? clip;
        private string? device;
        private int readPosition;
        private bool abandoned;
        private string? abandonedDevice;
        private AudioClip? abandonedClip;

        public bool IsOpen => clip != null;

        public int SampleRate { get; private set; } = PreferredRate;

        public IReadOnlyList<string> ListDevices() => Microphone.devices ?? Array.Empty<string>();

        public VoiceMicrophoneOpen Open(string? wanted)
        {
            if (clip != null) return VoiceMicrophoneOpen.Opened;
            var devices = Microphone.devices;
            if (devices == null || devices.Length == 0) return VoiceMicrophoneOpen.NoDevice;

            // A chosen microphone that is gone records on the system's default instead, until it is back.
            var name = wanted != null && Array.IndexOf(devices, wanted) >= 0 ? wanted : null;
            AudioClip? started;
            try
            {
                Microphone.GetDeviceCaps(name, out var minimum, out var maximum);
                started = Microphone.Start(name, true, ClipSeconds, RateFor(minimum, maximum));
            }
            catch (Exception)
            {
                started = null;
            }

            if (started == null) return VoiceMicrophoneOpen.Failed;
            clip = started;
            device = name;
            readPosition = 0;
            SampleRate = started.frequency;
            return VoiceMicrophoneOpen.Opened;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            var source = clip;
            if (source == null || count <= 0) return 0;
            if (!Microphone.IsRecording(device))
            {
                // The recording stopped under voice chat, as when the device was unplugged.
                Forget();
                return -1;
            }

            var position = Microphone.GetPosition(device);
            var length = source.samples;
            if (position < 0 || position >= length) return 0;
            var available = position - readPosition;
            if (available < 0) available += length;
            if (available == 0) return 0;
            if (available > count)
            {
                // More waited than one read takes, as after a long frame: the oldest goes, so what is sent stays current.
                readPosition = (position - count + length) % length;
                available = count;
            }

            var first = Math.Min(available, length - readPosition);
            source.GetData(new Span<float>(buffer, offset, first), readPosition);
            if (available > first) source.GetData(new Span<float>(buffer, offset + first, available - first), 0);
            readPosition = position;
            return available;
        }

        public void Close()
        {
            var source = clip;
            if (source == null) return;
            var name = device;
            clip = null;
            device = null;
            End(name);
            Release(source);
        }

        public void Abandon()
        {
            var source = clip;
            if (source == null) return;

            // Another use took the device: nothing more is read, and its recording, perhaps that use's now, goes on.
            abandoned = true;
            abandonedDevice = device;
            abandonedClip = source;
            clip = null;
            device = null;
        }

        public void EndAbandoned()
        {
            if (!abandoned) return;
            abandoned = false;
            var name = abandonedDevice;
            var source = abandonedClip;
            abandonedDevice = null;
            abandonedClip = null;

            // The use that took the device has let go, so a recording still running there is the one voice chat left.
            End(name);
            Release(source);
        }

        /// <summary>The rate to ask for: 48 kHz, or the nearest the device offers. Zero for both means any rate.</summary>
        internal static int RateFor(int minimum, int maximum)
        {
            if (minimum <= 0 && maximum <= 0) return PreferredRate;
            var rate = maximum > 0 ? Math.Min(maximum, PreferredRate) : PreferredRate;
            return minimum > 0 ? Math.Max(minimum, rate) : rate;
        }

        private void Forget()
        {
            var source = clip;
            clip = null;
            device = null;
            Release(source);
        }

        private static void End(string? name)
        {
            try
            {
                if (Microphone.IsRecording(name)) Microphone.End(name);
            }
            catch (Exception)
            {
                // A device that went away has nothing left to end.
            }
        }

        private static void Release(AudioClip? source)
        {
            if (source != null) UnityEngine.Object.Destroy(source);
        }
    }
}
