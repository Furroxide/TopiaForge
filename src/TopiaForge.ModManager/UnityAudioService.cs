using System;
using System.Threading;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.ModManager
{
    internal sealed class OwnerAudioService : IAudioService
    {
        private const int SampleRate = 24000;
        private readonly IModLifetime lifetime;

        public OwnerAudioService(IModLifetime lifetime)
        {
            this.lifetime = lifetime;
        }

        public OperationResult<IAudioPlayback> Play(AudioPlayRequest request)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (lifetime.IsStopping)
            {
                return OperationResult<IAudioPlayback>.Failure(
                    ModErrorCode.Cancelled,
                    "The mod is stopping and cannot start audio playback.");
            }

            GameObject? host = null;
            AudioClip? clip = null;
            try
            {
                var duration = request.Loop ? 0.32f : 0.16f;
                var sampleCount = (int)(SampleRate * duration);
                clip = AudioClip.Create(
                    "TopiaForge." + request.CueId,
                    sampleCount,
                    1,
                    SampleRate,
                    false);
                if (clip == null)
                {
                    return OperationResult<IAudioPlayback>.Failure(
                        ModErrorCode.External,
                        "Unity could not allocate the framework audio cue.");
                }

                var samples = BuildCue(request.CueId, sampleCount);
                if (!clip.SetData(samples, 0))
                {
                    UnityEngine.Object.Destroy(clip);
                    return OperationResult<IAudioPlayback>.Failure(
                        ModErrorCode.External,
                        "Unity rejected the framework audio cue samples.");
                }

                host = new GameObject("TopiaForge.Audio." + request.CueId);
                UnityEngine.Object.DontDestroyOnLoad(host);
                if (request.Position.HasValue)
                {
                    host.transform.position = UnityPhysicsBackend.ToUnity(request.Position.Value);
                }

                var source = host.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.clip = clip;
                source.volume = request.Volume;
                source.loop = request.Loop;
                source.spatialBlend = request.Position.HasValue ? 1f : 0f;
                source.Play();

                var playback = new UnityAudioPlayback(host, source, clip);
                lifetime.Track(playback);
                if (!request.Loop)
                {
                    UnityEngine.Object.Destroy(host, duration + 0.1f);
                    UnityEngine.Object.Destroy(clip, duration + 0.1f);
                }

                host = null;
                clip = null;
                return OperationResult<IAudioPlayback>.Success(playback);
            }
            catch (Exception exception)
            {
                if (host != null) UnityEngine.Object.Destroy(host);
                if (clip != null) UnityEngine.Object.Destroy(clip);
                return OperationResult<IAudioPlayback>.Failure(
                    ModErrorCode.External,
                    "The framework audio cue could not be played: " + exception.Message);
            }
        }

        private static float[] BuildCue(string cueId, int sampleCount)
        {
            var normalized = cueId.ToLowerInvariant();
            var frequency = normalized.Contains("danger") || normalized.Contains("failure")
                ? 220f
                : normalized.Contains("warning")
                    ? 330f
                    : normalized.Contains("success") || normalized.Contains("confirm")
                        ? 660f
                        : normalized.Contains("zapper")
                            ? 780f
                            : 440f + StableFrequencyOffset(normalized);
            var samples = new float[sampleCount];
            for (var index = 0; index < samples.Length; index++)
            {
                var progress = index / (float)Math.Max(1, samples.Length - 1);
                var envelope = Math.Min(1f, progress * 18f) * (1f - progress);
                var phase = 2d * Math.PI * frequency * index / SampleRate;
                var harmonic = normalized.Contains("zapper") ? Math.Sin(phase * (1.5d + progress)) * 0.2d : 0d;
                samples[index] = (float)((Math.Sin(phase) * 0.35d + harmonic) * envelope);
            }

            return samples;
        }

        private static float StableFrequencyOffset(string cueId)
        {
            unchecked
            {
                var hash = 17;
                foreach (var character in cueId)
                {
                    hash = hash * 31 + character;
                }

                return Math.Abs(hash % 180);
            }
        }

        private sealed class UnityAudioPlayback : IAudioPlayback
        {
            private GameObject? host;
            private AudioSource? source;
            private AudioClip? clip;

            public UnityAudioPlayback(GameObject host, AudioSource source, AudioClip clip)
            {
                this.host = host;
                this.source = source;
                this.clip = clip;
            }

            public bool IsPlaying
            {
                get
                {
                    UnityMainThreadGuard.AssertCurrent();
                    return source != null && source.isPlaying;
                }
            }

            public void Stop()
            {
                UnityMainThreadGuard.AssertCurrent();
                var current = source;
                if (current != null)
                {
                    current.Stop();
                }

                Dispose();
            }

            public void Dispose()
            {
                UnityMainThreadGuard.AssertCurrent();
                var currentSource = Interlocked.Exchange(ref source, null);
                if (currentSource != null)
                {
                    currentSource.Stop();
                }

                var currentHost = Interlocked.Exchange(ref host, null);
                if (currentHost != null)
                {
                    UnityEngine.Object.Destroy(currentHost);
                }

                var currentClip = Interlocked.Exchange(ref clip, null);
                if (currentClip != null)
                {
                    UnityEngine.Object.Destroy(currentClip);
                }
            }
        }
    }
}
