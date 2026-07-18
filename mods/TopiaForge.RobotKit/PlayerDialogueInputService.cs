using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using UnityEngine;

namespace TopiaForge.RobotKit
{
    // Publishes IPlayerDialogueInputService: push-to-talk voice capture transcribed through the game's own /agent/stt
    // backend, mirroring the base game exactly (16 kHz mono PCM16-LE, gzipped, audio/pcm + Content-Encoding gzip +
    // Session-Id + Bearer — see Robotopia base-game conversation-input protocol). The microphone read happens on the main
    // thread at Stop(); the HTTP transcription runs off-thread and its result is marshalled back on the service Tick,
    // same idiom as the brain-query service. Typed text is handled by the consumer's UI with the shared
    // TopiaForge.Mods.TextInputBuffer; this service is the voice half.
    internal sealed class PlayerDialogueInputService : IPlayerDialogueInputService,
        IOwnerBoundExtensionFactory, IDisposable
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

        public OperationResult<IVoiceCapture> BeginVoiceCapture()
        {
            if (disposed || !IsVoiceAvailable)
            {
                return OperationResult<IVoiceCapture>.Failure(
                    ModErrorCode.Unavailable,
                    "Voice capture is unavailable.");
            }

            for (var index = 0; index < active.Count; index++)
            {
                if (!active[index].IsComplete)
                {
                    return OperationResult<IVoiceCapture>.Failure(
                        ModErrorCode.Conflict,
                        "A voice capture is already in progress.");
                }
            }

            var device = PickDevice();
            var clip = Microphone.Start(device, true, ClipSeconds, SampleRate);
            if (clip == null)
            {
                return OperationResult<IVoiceCapture>.Failure(
                    ModErrorCode.External,
                    "The microphone could not begin recording.");
            }

            LogAvailabilityOnce(device);
            var capture = new VoiceCapture(client, logger, serviceCts.Token, device, clip);
            active.Add(capture);
            return OperationResult<IVoiceCapture>.Success(capture);
        }

        object IOwnerBoundExtensionFactory.CreateOwnerFacade(
            Type contractType,
            string ownerModId,
            IModLifetime lifetime)
        {
            if (contractType != typeof(IPlayerDialogueInputService))
            {
                throw new ArgumentException("Unsupported RobotKit dialogue extension contract.", nameof(contractType));
            }

            return new OwnerFacade(this, lifetime);
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
            catch (Exception ex)
            {
                logger.Debug("RobotKit voice service cancellation failed during unload: " + ex.Message);
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

        private sealed class OwnerFacade : IPlayerDialogueInputService
        {
            private readonly PlayerDialogueInputService service;
            private readonly IModLifetime lifetime;

            public OwnerFacade(PlayerDialogueInputService service, IModLifetime lifetime)
            {
                this.service = service;
                this.lifetime = lifetime;
            }

            public bool IsVoiceAvailable => !lifetime.IsStopping && service.IsVoiceAvailable;

            public OperationResult<IVoiceCapture> BeginVoiceCapture()
            {
                if (lifetime.IsStopping)
                {
                    return OperationResult<IVoiceCapture>.Failure(
                        ModErrorCode.Cancelled,
                        "The mod is stopping and cannot begin voice capture.");
                }

                var result = service.BeginVoiceCapture();
                if (!result.TryGetValue(out var capture))
                {
                    return result;
                }

                try
                {
                    return OperationResult<IVoiceCapture>.Success(
                        new OwnerVoiceCapture(capture, lifetime.Track(capture)));
                }
                catch (ObjectDisposedException)
                {
                    return OperationResult<IVoiceCapture>.Failure(
                        ModErrorCode.Cancelled,
                        "The mod stopped before voice capture could be retained.");
                }
            }

            private sealed class OwnerVoiceCapture : IVoiceCapture
            {
                private readonly IVoiceCapture capture;
                private IDisposable? lifetimeLease;

                public OwnerVoiceCapture(IVoiceCapture capture, IDisposable lifetimeLease)
                {
                    this.capture = capture;
                    this.lifetimeLease = lifetimeLease;
                }

                public bool IsRecording => lifetimeLease != null && capture.IsRecording;
                public Task<OperationResult<VoiceTranscriptResult>> StopAsync(
                    CancellationToken cancellationToken = default) =>
                    capture.StopAsync(cancellationToken);

                public void Dispose()
                {
                    Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
                }
            }
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
            private float recordStartTime;
            private readonly TaskCompletionSource<OperationResult<VoiceTranscriptResult>> completion =
                new TaskCompletionSource<OperationResult<VoiceTranscriptResult>>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

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

            public bool IsRecording => recording;
            internal bool IsComplete => complete;

            public async Task<OperationResult<VoiceTranscriptResult>> StopAsync(
                CancellationToken cancellationToken = default)
            {
                Stop();
                using (cancellationToken.Register(Cancel))
                {
                    return await completion.Task;
                }
            }

            private void Stop()
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

            internal void Cancel()
            {
                recording = false;
                StopMic();
                try
                {
                    cts?.Cancel();
                }
                catch (Exception ex)
                {
                    logger?.Debug("Voice transcription cancellation failed: " + ex.Message);
                }

                try
                {
                    cts?.Dispose();
                }
                catch (Exception ex)
                {
                    logger?.Debug("Voice transcription cleanup failed: " + ex.Message);
                }

                cts = null;
                complete = true;
                completion.TrySetResult(OperationResult<VoiceTranscriptResult>.Failure(
                    ModErrorCode.Cancelled,
                    "Voice capture was cancelled."));
            }

            public void Dispose() => Cancel();

            // Poll the in-flight transcription; called by the service tick.
            public void Pump()
            {
                if (recording && Time.realtimeSinceStartup - recordStartTime >= MaxRecordSeconds)
                {
                    Stop();
                }

                if (complete || sttTask == null || !sttTask.IsCompleted)
                {
                    return;
                }

                string? transcript = null;
                try
                {
                    transcript = sttTask.Status == TaskStatus.RanToCompletion ? sttTask.Result : null;
                }
                catch (Exception ex)
                {
                    logger?.Debug("Voice transcription completion failed: " + ex.Message);
                    transcript = null;
                }

                sttTask = null;
                cts?.Dispose();
                cts = null;

                if (!string.IsNullOrWhiteSpace(transcript))
                {
                    completion.TrySetResult(OperationResult<VoiceTranscriptResult>.Success(
                        new VoiceTranscriptResult(transcript!.Trim())));
                }
                else
                {
                    completion.TrySetResult(OperationResult<VoiceTranscriptResult>.Failure(
                        ModErrorCode.External,
                        "Voice transcription returned no text."));
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
                catch (Exception ex)
                {
                    logger?.Debug("Voice microphone cleanup failed: " + ex.Message);
                }
            }

            private void CompleteEmpty(string reason)
            {
                logger?.Debug("Voice capture rejected: " + reason);
                complete = true;
                completion.TrySetResult(OperationResult<VoiceTranscriptResult>.Failure(
                    ModErrorCode.InvalidArgument,
                    "Voice capture could not be transcribed: " + reason + "."));
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
