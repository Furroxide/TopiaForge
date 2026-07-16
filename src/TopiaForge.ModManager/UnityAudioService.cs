using System;
using System.Collections.Generic;
using System.Threading;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.ModManager
{
    internal sealed class OwnerAudioService : IAudioService, IDisposable
    {
        private const int SampleRate = 24000;
        private const int MaxRetainedSources = 24;
        private readonly IModLifetime lifetime;
        private readonly Dictionary<AudioCueKey, AudioClip> clipCache = new Dictionary<AudioCueKey, AudioClip>();
        private readonly Stack<PooledAudioSource> availableSources = new Stack<PooledAudioSource>();
        private readonly HashSet<PooledAudioSource> allSources = new HashSet<PooledAudioSource>();
        private int disposed;

        public OwnerAudioService(IModLifetime lifetime)
        {
            this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
            lifetime.Defer(Dispose);
        }

        public OperationResult<IAudioPlayback> Play(AudioPlayRequest request)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (lifetime.IsStopping || Volatile.Read(ref disposed) != 0)
            {
                return OperationResult<IAudioPlayback>.Failure(
                    ModErrorCode.Cancelled,
                    "The mod is stopping and cannot start audio playback.");
            }

            PooledAudioSource? sourceHost = null;
            UnityAudioPlayback? playback = null;
            try
            {
                var clipResult = GetOrCreateClip(request.CueId, request.Loop);
                if (!clipResult.TryGetValue(out var clip))
                {
                    return OperationResult<IAudioPlayback>.Failure(
                        clipResult.ErrorCode,
                        clipResult.ErrorMessage);
                }

                sourceHost = RentSource();
                ConfigureSource(sourceHost, clip, request);

                playback = new UnityAudioPlayback(this, sourceHost);
                if (!request.Loop)
                {
                    sourceHost.Completion.Initialize(sourceHost.Source, playback);
                }

                sourceHost.Source.Play();
                playback.AttachLifetimeLease(lifetime.Track(playback));

                sourceHost = null;
                return OperationResult<IAudioPlayback>.Success(playback);
            }
            catch (Exception exception)
            {
                if (playback != null)
                {
                    playback.Dispose();
                    sourceHost = null;
                }

                if (sourceHost != null)
                {
                    ReturnSource(sourceHost);
                }

                return OperationResult<IAudioPlayback>.Failure(
                    ModErrorCode.External,
                    "The framework audio cue could not be played: " + exception.Message);
            }
        }

        public void Dispose()
        {
            UnityMainThreadGuard.AssertCurrent();
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            availableSources.Clear();
            foreach (var sourceHost in allSources)
            {
                sourceHost.Destroy();
            }

            allSources.Clear();
            foreach (var clip in clipCache.Values)
            {
                if (clip != null)
                {
                    UnityEngine.Object.Destroy(clip);
                }
            }

            clipCache.Clear();
        }

        private OperationResult<AudioClip> GetOrCreateClip(string cueId, bool loop)
        {
            var key = new AudioCueKey(cueId, loop);
            if (clipCache.TryGetValue(key, out var cached) && cached != null)
            {
                return OperationResult<AudioClip>.Success(cached);
            }

            clipCache.Remove(key);
            AudioClip? clip = null;
            try
            {
                var duration = loop ? 0.32f : 0.16f;
                var sampleCount = (int)(SampleRate * duration);
                clip = AudioClip.Create(
                    "TopiaForge." + cueId,
                    sampleCount,
                    1,
                    SampleRate,
                    false);
                if (clip == null)
                {
                    return OperationResult<AudioClip>.Failure(
                        ModErrorCode.External,
                        "Unity could not allocate the framework audio cue.");
                }

                var samples = BuildCue(cueId, sampleCount);
                if (!clip.SetData(samples, 0))
                {
                    UnityEngine.Object.Destroy(clip);
                    clip = null;
                    return OperationResult<AudioClip>.Failure(
                        ModErrorCode.External,
                        "Unity rejected the framework audio cue samples.");
                }

                clipCache.Add(key, clip);
                return OperationResult<AudioClip>.Success(clip);
            }
            catch (Exception exception)
            {
                if (clip != null)
                {
                    UnityEngine.Object.Destroy(clip);
                }

                return OperationResult<AudioClip>.Failure(
                    ModErrorCode.External,
                    "The framework audio cue could not be prepared: " + exception.Message);
            }
        }

        private PooledAudioSource RentSource()
        {
            while (availableSources.Count > 0)
            {
                var sourceHost = availableSources.Pop();
                if (sourceHost.IsAlive)
                {
                    return sourceHost;
                }

                allSources.Remove(sourceHost);
                sourceHost.Destroy();
            }

            GameObject? host = null;
            try
            {
                host = new GameObject("TopiaForge.Audio.Pooled");
                host.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(host);
                var source = host.AddComponent<AudioSource>();
                var completion = host.AddComponent<AudioPlaybackCompletion>();
                if (source == null || completion == null)
                {
                    throw new InvalidOperationException("Unity could not create a pooled audio source host.");
                }

                var created = new PooledAudioSource(host, source, completion);
                allSources.Add(created);
                host = null;
                return created;
            }
            finally
            {
                if (host != null)
                {
                    UnityEngine.Object.Destroy(host);
                }
            }
        }

        private static void ConfigureSource(
            PooledAudioSource sourceHost,
            AudioClip clip,
            AudioPlayRequest request)
        {
            sourceHost.Completion.Detach();
            sourceHost.Host.name = "TopiaForge.Audio." + request.CueId;
            sourceHost.Host.transform.position = request.Position.HasValue
                ? UnityPhysicsBackend.ToUnity(request.Position.Value)
                : Vector3.zero;
            sourceHost.Host.transform.rotation = Quaternion.identity;

            var source = sourceHost.Source;
            source.Stop();
            source.playOnAwake = false;
            source.clip = clip;
            source.volume = request.Volume;
            source.loop = request.Loop;
            source.pitch = 1f;
            source.mute = false;
            source.spatialBlend = request.Position.HasValue ? 1f : 0f;
            sourceHost.Host.SetActive(true);
        }

        private void ReturnSource(PooledAudioSource sourceHost)
        {
            sourceHost.ResetForPool();
            if (!sourceHost.IsAlive
                || Volatile.Read(ref disposed) != 0
                || availableSources.Count >= MaxRetainedSources)
            {
                allSources.Remove(sourceHost);
                sourceHost.Destroy();
                return;
            }

            availableSources.Push(sourceHost);
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

        private readonly struct AudioCueKey : IEquatable<AudioCueKey>
        {
            public AudioCueKey(string cueId, bool loop)
            {
                CueId = cueId;
                Loop = loop;
            }

            private string CueId { get; }

            private bool Loop { get; }

            public bool Equals(AudioCueKey other) => Loop == other.Loop
                && string.Equals(CueId, other.CueId, StringComparison.Ordinal);

            public override bool Equals(object? obj) => obj is AudioCueKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    return (StringComparer.Ordinal.GetHashCode(CueId) * 397) ^ Loop.GetHashCode();
                }
            }
        }

        private sealed class PooledAudioSource
        {
            private int destroyed;

            public PooledAudioSource(GameObject host, AudioSource source, AudioPlaybackCompletion completion)
            {
                Host = host;
                Source = source;
                Completion = completion;
            }

            public GameObject Host { get; }

            public AudioSource Source { get; }

            public AudioPlaybackCompletion Completion { get; }

            public bool IsAlive => Volatile.Read(ref destroyed) == 0
                && Host != null
                && Source != null
                && Completion != null;

            public void ResetForPool()
            {
                if (!IsAlive)
                {
                    return;
                }

                Completion.Detach();
                Source.Stop();
                Source.clip = null;
                Source.playOnAwake = false;
                Source.loop = false;
                Source.volume = 1f;
                Source.pitch = 1f;
                Source.mute = false;
                Source.spatialBlend = 0f;
                Host.transform.position = Vector3.zero;
                Host.transform.rotation = Quaternion.identity;
                Host.name = "TopiaForge.Audio.Pooled";
                Host.SetActive(false);
            }

            public void Destroy()
            {
                if (Interlocked.Exchange(ref destroyed, 1) != 0)
                {
                    return;
                }

                if (Completion != null)
                {
                    Completion.Detach();
                }

                if (Source != null)
                {
                    Source.Stop();
                    Source.clip = null;
                }

                if (Host != null)
                {
                    UnityEngine.Object.Destroy(Host);
                }
            }
        }

        private sealed class UnityAudioPlayback : IAudioPlayback
        {
            private OwnerAudioService? owner;
            private PooledAudioSource? sourceHost;
            private IDisposable? lifetimeLease;
            private int disposed;

            public UnityAudioPlayback(OwnerAudioService owner, PooledAudioSource sourceHost)
            {
                this.owner = owner;
                this.sourceHost = sourceHost;
            }

            public bool IsPlaying
            {
                get
                {
                    UnityMainThreadGuard.AssertCurrent();
                    var current = sourceHost;
                    return current != null && current.IsAlive && current.Source.isPlaying;
                }
            }

            public void AttachLifetimeLease(IDisposable lease)
            {
                lifetimeLease = lease ?? throw new ArgumentNullException(nameof(lease));
            }

            public void Stop()
            {
                UnityMainThreadGuard.AssertCurrent();
                var current = sourceHost;
                if (current != null && current.IsAlive)
                {
                    current.Source.Stop();
                }

                Dispose();
            }

            public void Dispose()
            {
                UnityMainThreadGuard.AssertCurrent();
                if (Interlocked.Exchange(ref disposed, 1) != 0)
                {
                    return;
                }

                var current = Interlocked.Exchange(ref sourceHost, null);
                var currentOwner = Interlocked.Exchange(ref owner, null);
                try
                {
                    if (current != null && currentOwner != null)
                    {
                        currentOwner.ReturnSource(current);
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
                }
            }
        }

        /// <summary>Returns one-shot playback to its owner pool as soon as Unity reports that it finished.</summary>
        private sealed class AudioPlaybackCompletion : MonoBehaviour
        {
            private AudioSource? source;
            private UnityAudioPlayback? playback;

            public void Initialize(AudioSource value, UnityAudioPlayback owner)
            {
                source = value;
                playback = owner;
                enabled = true;
            }

            public void Detach()
            {
                source = null;
                playback = null;
                enabled = false;
            }

            private void Update()
            {
                if (source != null && source.isPlaying)
                {
                    return;
                }

                var owner = Interlocked.Exchange(ref playback, null);
                source = null;
                owner?.Dispose();
            }
        }
    }
}
