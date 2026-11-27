using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Voice
{
    /// <summary>
    /// Other players' voices in the game's audio: one emitter for each participant the session lists who speaks, playing
    /// that speaker's stream. A speaker gets an emitter once the voice service has heard them in the last 250 ms
    /// (<see cref="IInternalVoiceService.IsSpeaking"/>), and keeps it while their stream lasts. The stream comes from
    /// <see cref="IInternalVoiceService.TryGetPlaybackStream"/>, which makes nothing for a speaker the session does not list
    /// and allocates nothing when it has nothing to give, so asking again after a stream ended costs nothing; this never
    /// asks <see cref="IInternalVoiceService.GetPlaybackStream"/>, which would make a stream for every call. A stream that
    /// ends, because its speaker left or a join let go of the list that named them, gives its emitter back at once: it
    /// plays only silence from then on, and never anybody else. Voices play non-positionally until
    /// <see cref="Positions"/> is set, which WS3.10's avatars do. Main thread only; a settled frame allocates nothing.
    /// </summary>
    internal sealed class VoicePlayback
    {
        private readonly IVoiceEmitters emitters;
        private readonly Dictionary<ParticipantId, Playing> playing = new Dictionary<ParticipantId, Playing>(16);
        private readonly List<ParticipantId> ending = new List<ParticipantId>(16);
        private float volume = 1f;

        internal VoicePlayback(IVoiceEmitters emitters)
        {
            this.emitters = emitters ?? throw new ArgumentNullException(nameof(emitters));
        }

        /// <summary>
        /// Gets or sets where each speaker's voice comes from. Null, the default until avatars exist (WS3.10), plays every
        /// voice as if beside the listener; with a source, a speaker it places plays from there, 3 m at full volume and
        /// silent past 40 m.
        /// </summary>
        internal IVoicePositions? Positions { get; set; }

        /// <summary>Gets how many speakers play now.</summary>
        internal int Count => playing.Count;

        /// <summary>Gets or sets the voices' volume, from 0 to 1.</summary>
        internal float Volume
        {
            get => volume;
            set
            {
                var next = float.IsNaN(value) ? 1f : Math.Max(0f, Math.Min(1f, value));
                if (next == volume) return;
                volume = next;
                foreach (var entry in playing) entry.Value.Emitter.SetVolume(next);
            }
        }

        /// <summary>Gets whether <paramref name="speaker"/> has an emitter now.</summary>
        internal bool IsPlaying(ParticipantId speaker) => playing.ContainsKey(speaker);

        /// <summary>
        /// Main thread, every frame: gives each speaking participant without an emitter one, and takes back the emitters of
        /// streams that ended and of speakers the session no longer lists. Without a voice service or a session, every
        /// emitter goes back.
        /// </summary>
        internal void Tick(IInternalVoiceService? voice, MultiplayerSessionSnapshot? snapshot)
        {
            if (voice == null || snapshot == null)
            {
                StopAll();
                return;
            }

            var participants = snapshot.Participants;
            for (var index = 0; index < participants.Count; index++)
            {
                var participant = participants[index];
                if (participant.IsLocal || !participant.IsConnected || playing.ContainsKey(participant.Id)) continue;
                if (!voice.IsSpeaking(participant.Id)) continue;
                if (!voice.TryGetPlaybackStream(participant.Id, out var stream) || stream == null || stream.IsEnded) continue;
                var emitter = emitters.Rent();
                emitter.Play(stream, volume);
                playing.Add(participant.Id, new Playing(emitter, stream));
            }

            var positions = Positions;
            foreach (var entry in playing)
            {
                if (entry.Value.Stream.IsEnded || !Lists(participants, entry.Key))
                {
                    ending.Add(entry.Key);
                    continue;
                }

                if (!(entry.Value.Stream is IInternalRoutedVoiceStream routed && routed.NonSpatial)
                    && positions != null && positions.TryGetPosition(entry.Key, out var position)) entry.Value.Emitter.Place(position);
                else entry.Value.Emitter.Unplace();
            }

            for (var index = 0; index < ending.Count; index++) Stop(ending[index]);
            ending.Clear();
        }

        /// <summary>Gives every emitter back, as when voice goes away or this game leaves its session.</summary>
        internal void StopAll()
        {
            if (playing.Count == 0) return;
            foreach (var entry in playing) ending.Add(entry.Key);
            for (var index = 0; index < ending.Count; index++) Stop(ending[index]);
            ending.Clear();
        }

        private void Stop(ParticipantId speaker)
        {
            if (!playing.TryGetValue(speaker, out var entry)) return;
            playing.Remove(speaker);
            entry.Emitter.Stop();
            emitters.Return(entry.Emitter);
        }

        private static bool Lists(IReadOnlyList<MultiplayerParticipant> participants, ParticipantId speaker)
        {
            for (var index = 0; index < participants.Count; index++)
            {
                var participant = participants[index];
                if (participant.Id.Equals(speaker)) return participant.IsConnected && !participant.IsLocal;
            }

            return false;
        }

        private readonly struct Playing
        {
            internal Playing(IVoiceEmitter emitter, IVoicePlaybackStream stream)
            {
                Emitter = emitter;
                Stream = stream;
            }

            internal IVoiceEmitter Emitter { get; }

            internal IVoicePlaybackStream Stream { get; }
        }
    }
}
