using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Robotopia.Mods;
using UnityEngine;

namespace Robotopia.RobotKit
{
    // Publishes IPlayerDialogueInputService: push-to-talk voice capture transcribed through the game's own /agent/stt
    // backend, mirroring the base game exactly (16 kHz mono PCM16-LE, gzipped, audio/pcm + Content-Encoding gzip +
    // Session-Id + Bearer — see robotopia-basegame-conversation-input). The microphone read happens on the main
    // thread at Stop(); the HTTP transcription runs off-thread and its result is marshalled back on the service Tick,
    // same idiom as the brain-query service. Typed text is handled by the consumer's UI with the shared
    // Robotopia.Mods.TextInputBuffer; this service is the voice half.
    internal sealed class PlayerDialogueInputService : IPlayerDialogueInputService, IDisposable
    {
        // The backend STT accepts ONLY 16 kHz mono (AudioUtil.GetPCMData throws otherwise), so do not change these.
        private const int SampleRate = 16000;
        private const int ClipSeconds = 35;       // base game: 30s max + 5s buffer, looping
        private const float MaxRecordSeconds = 30f;
        private const float MinRecordSeconds = 0.5f; // base game rejects clips shorter than this
        private const float MinRmsDbfs = -45f;       // base game's "Audio too quiet" floor
        private const float SttTimeoutSeconds = 5f;

        private readonly RoboApiClient client;
        private readonly CancellationTokenSource serviceCts = new CancellationTokenSource();
        private readonly List<VoiceCapture> active = new List<VoiceCapture>();
        private readonly IModLogger logger;

        private bool disposed;
        private bool loggedAvailability;

        public PlayerDialogueInputService(IModLogger logger)
        {
            this.logger = logger;
            var tokenPath = Path.Combine(Application.persistentDataPath, "robo_token.json");
            client = new RoboApiClient(tokenPath, Guid.NewGuid().ToString("N"), logger);
        }

        public bool IsVoiceAvailable => !disposed && HasMicrophone() && client.HasToken;

        public IVoiceCapture BeginVoiceCapture()
        {
            if (disposed || !IsVoiceAvailable)
            {
                return VoiceCapture.Unavailable();
            }

            var device = PickDevice();
            var clip = Microphone.Start(device, true, ClipSeconds, SampleRate);
            if (clip == null)
            {
                return VoiceCapture.Unavailable();
            }

            LogAvailabilityOnce(device);
            var capture = new VoiceCapture(client, logger, serviceCts.Token, device, clip);
            active.Add(capture);
            return capture;
        }

        // Drain finished transcriptions back onto the main thread and drop completed captures.
        public void Tick(float deltaTime)
        {
            if (disposed)
            {
                return;
            }

            for (var index = active.Count - 1; index >= 0; index--)
            {
                var capture = active[index];
                capture.Pump();
                if (capture.IsComplete)
                {
                    active.RemoveAt(index);
                }
            }
        }

        public void OnSceneChanged()
        {
            client.InvalidateToken();
            for (var index = 0; index < active.Count; index++)
            {
                active[index].Cancel();
            }

            active.Clear();
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            try
            {
                serviceCts.Cancel();
            }
            catch
            {
            }

            for (var index = 0; index < active.Count; index++)
            {
                active[index].Cancel();
            }

            active.Clear();
            serviceCts.Dispose();
        }

        private static bool HasMicrophone()
        {
            var devices = Microphone.devices;
            return devices != null && devices.Length > 0;
        }

        // Prefer a real microphone over known virtual devices (the base game filters these too).
        private static string? PickDevice()
        {
            var devices = Microphone.devices;
            if (devices == null || devices.Length == 0)
            {
                return null;
            }

            for (var index = 0; index < devices.Length; index++)
            {
                var name = devices[index];
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                if (name.IndexOf("Microsoft Teams", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("OBS Virtual", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Steam Streaming", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                return name;
            }

            return devices[0];
        }

        private void LogAvailabilityOnce(string? device)
        {
            if (loggedAvailability)
            {
                return;
            }

            loggedAvailability = true;
            logger.Info("RobotKit: voice input enabled — push-to-talk on '" + (device ?? "<default>")
                + "' transcribes through /agent/stt (16 kHz mono).");
        }

        // One push-to-talk capture: records while held, transcribes on Stop. Microphone reads happen on the main
        // thread (ctor + Stop + Pump); only the HTTP call is off-thread.
        private sealed class VoiceCapture : IVoiceCapture
        {
            private readonly RoboApiClient? client;
            private readonly IModLogger? logger;
            private readonly string? device;
            private readonly AudioClip? clip;
            private readonly CancellationToken serviceToken;

            private CancellationTokenSource? cts;
            private Task<string?>? sttTask;
            private bool recording;
            private bool complete;
            private bool found;
            private string text = string.Empty;
            private string? error;
            private float recordStartTime;

            private VoiceCapture()
            {
                complete = true;
                error = "voice unavailable";
            }

            public VoiceCapture(RoboApiClient client, IModLogger logger, CancellationToken serviceToken, string? device, AudioClip clip)
            {
                this.client = client;
                this.logger = logger;
                this.serviceToken = serviceToken;
                this.device = device;
                this.clip = clip;
                recording = true;
                recordStartTime = Time.realtimeSinceStartup;
            }

            public static VoiceCapture Unavailable() => new VoiceCapture();

            public bool IsRecording => recording;

            public bool IsComplete => complete;

            public bool Found => found;

            public string Text => text;

            public string? Error => error;

            public void Stop()
            {
                if (!recording)
                {
                    return;
                }

                recording = false;
                try
                {
                    FinalizeAndTranscribe();
                }
                catch (Exception ex)
                {
                    logger?.Debug("Voice capture finalize failed: " + ex.Message);
                    CompleteEmpty("capture failed");
                }
            }

            public void Cancel()
            {
                recording = false;
                StopMic();
                try
                {
                    cts?.Cancel();
                    cts?.Dispose();
                }
                catch
                {
                }

                cts = null;
                complete = true;
            }

            // Poll the in-flight transcription; called by the service tick.
            public void Pump()
            {
                if (complete || sttTask == null || !sttTask.IsCompleted)
                {
                    return;
                }

                string? transcript = null;
                try
                {
                    transcript = sttTask.Status == TaskStatus.RanToCompletion ? sttTask.Result : null;
                }
                catch
                {
                    transcript = null;
                }

                sttTask = null;
                cts?.Dispose();
                cts = null;

                if (!string.IsNullOrWhiteSpace(transcript))
                {
                    text = transcript!.Trim();
                    found = true;
                    error = null;
                }
                else
                {
                    error = "no transcript";
                }

                complete = true;
            }

            private void FinalizeAndTranscribe()
            {
                if (clip == null || client == null)
                {
                    CompleteEmpty("no clip");
                    return;
                }

                var position = Microphone.GetPosition(device);
                StopMic();

                if (position <= 0)
                {
                    CompleteEmpty("nothing recorded");
                    return;
                }

                var elapsed = Time.realtimeSinceStartup - recordStartTime;
                if (elapsed < MinRecordSeconds)
                {
                    CompleteEmpty("too short");
                    return;
                }

                var sampleCount = Mathf.Min(position, clip.samples);
                var samples = new float[sampleCount];
                clip.GetData(samples, 0);

                if (RmsDbfs(samples) < MinRmsDbfs)
                {
                    CompleteEmpty("too quiet");
                    return;
                }

                var pcm = FloatToPcm16Le(samples, Mathf.Min(sampleCount, (int)(MaxRecordSeconds * SampleRate)));
                var gzipped = Gzip(pcm);

                cts = CancellationTokenSource.CreateLinkedTokenSource(serviceToken);
                sttTask = client.SttAsync(gzipped, SttTimeoutSeconds, cts.Token);
            }

            private void StopMic()
            {
                try
                {
                    if (Microphone.IsRecording(device))
                    {
                        Microphone.End(device);
                    }
                }
                catch
                {
                }
            }

            private void CompleteEmpty(string reason)
            {
                logger?.Debug("Voice capture rejected: " + reason);
                error = reason;
                complete = true;
            }

            private static float RmsDbfs(float[] samples)
            {
                if (samples.Length == 0)
                {
                    return float.NegativeInfinity;
                }

                double sum = 0;
                for (var index = 0; index < samples.Length; index++)
                {
                    sum += samples[index] * (double)samples[index];
                }

                var rms = Math.Sqrt(sum / samples.Length);
                if (rms <= 1e-7)
                {
                    return float.NegativeInfinity;
                }

                return (float)(20.0 * Math.Log10(rms));
            }

            private static byte[] FloatToPcm16Le(float[] samples, int count)
            {
                count = Mathf.Clamp(count, 0, samples.Length);
                var bytes = new byte[count * 2];
                for (var index = 0; index < count; index++)
                {
                    var clamped = Mathf.Clamp(samples[index], -1f, 1f);
                    var value = (short)Mathf.Clamp(Mathf.RoundToInt(clamped * 32767f), short.MinValue, short.MaxValue);
                    bytes[index * 2] = (byte)(value & 0xFF);
                    bytes[(index * 2) + 1] = (byte)((value >> 8) & 0xFF);
                }

                return bytes;
            }

            private static byte[] Gzip(byte[] data)
            {
                using var output = new MemoryStream();
                using (var gzip = new GZipStream(output, CompressionMode.Compress, true))
                {
                    gzip.Write(data, 0, data.Length);
                }

                return output.ToArray();
            }
        }
    }
}
