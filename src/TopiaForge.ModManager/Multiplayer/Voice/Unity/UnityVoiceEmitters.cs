using System;
using System.Collections.Generic;
using TopiaForge.Mods.Internal;
using UnityEngine;

namespace TopiaForge.ModManager.Voice
{
    /// <summary>
    /// The emitters other players' voices play through: each an <c>AudioSource</c> on its own object under one root that
    /// outlives scene loads, playing a looping streaming <c>AudioClip</c> at 48 kHz whose reader callback hands Unity's
    /// buffer to <see cref="VoiceEmitterReader"/> on the audio thread. An emitter given back is kept for the next speaker,
    /// so a session's voices allocate once. Voices keep playing while the game's audio pauses, as a shared world never
    /// pauses. Main thread only.
    /// </summary>
    internal sealed class UnityVoiceEmitters : IVoiceEmitters, IDisposable
    {
        private readonly Stack<UnityVoiceEmitter> free = new Stack<UnityVoiceEmitter>();
        private readonly List<UnityVoiceEmitter> made = new List<UnityVoiceEmitter>();
        private GameObject? root;

        public IVoiceEmitter Rent()
        {
            if (free.Count > 0) return free.Pop();
            if (root == null)
            {
                root = new GameObject("TopiaForgeVoiceChat");
                UnityEngine.Object.DontDestroyOnLoad(root);
            }

            var emitter = new UnityVoiceEmitter(root.transform, made.Count);
            made.Add(emitter);
            return emitter;
        }

        public void Return(IVoiceEmitter emitter)
        {
            if (!(emitter is UnityVoiceEmitter mine) || !made.Contains(mine) || free.Contains(mine)) return;
            mine.Stop();
            free.Push(mine);
        }

        /// <summary>Stops every voice and destroys the emitters, as the game closes.</summary>
        public void Dispose()
        {
            foreach (var emitter in made) emitter.Destroy();
            made.Clear();
            free.Clear();
            if (root != null) UnityEngine.Object.Destroy(root);
            root = null;
        }
    }

    /// <summary>One speaker's voice: an audio source and its streaming clip.</summary>
    internal sealed class UnityVoiceEmitter : IVoiceEmitter
    {
        /// <summary>The rate of every voice stream, in hertz.</summary>
        internal const int PlaybackRate = 48000;

        /// <summary>Within this distance, in metres, a placed voice plays at full volume.</summary>
        internal const float FullVolumeMetres = 3f;

        /// <summary>Past this distance, in metres, a placed voice is silent.</summary>
        internal const float SilentMetres = 40f;

        private readonly GameObject owner;
        private readonly AudioSource source;
        private readonly AudioClip clip;
        private readonly VoiceEmitterReader reader = new VoiceEmitterReader();
        private bool placed;

        internal UnityVoiceEmitter(Transform parent, int index)
        {
            owner = new GameObject("Voice " + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            owner.transform.SetParent(parent, false);
            source = owner.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = true;
            source.spatialBlend = 0f;
            source.dopplerLevel = 0f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = FullVolumeMetres;
            source.maxDistance = SilentMetres;
            source.ignoreListenerPause = true;
            source.bypassReverbZones = true;
            clip = AudioClip.Create("TopiaForge voice", PlaybackRate, 1, PlaybackRate, true, reader.Read);
            source.clip = clip;
        }

        public void Play(IVoicePlaybackStream stream, float volume)
        {
            reader.Attach(stream ?? throw new ArgumentNullException(nameof(stream)));
            source.volume = Clamp(volume);
            if (!source.isPlaying) source.Play();
        }

        public void SetVolume(float volume) => source.volume = Clamp(volume);

        public void Place(VoicePosition position)
        {
            if (!placed)
            {
                placed = true;
                source.spatialBlend = 1f;
            }

            owner.transform.position = new Vector3(position.X, position.Y, position.Z);
        }

        public void Unplace()
        {
            if (!placed) return;
            placed = false;
            source.spatialBlend = 0f;
        }

        public void Stop()
        {
            source.Stop();
            reader.Attach(null);
            Unplace();
        }

        internal void Destroy()
        {
            Stop();
            UnityEngine.Object.Destroy(clip);
            UnityEngine.Object.Destroy(owner);
        }

        private static float Clamp(float volume) => float.IsNaN(volume) ? 0f : Math.Max(0f, Math.Min(1f, volume));
    }
}
