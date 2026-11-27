using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Voice
{
    /// <summary>
    /// Whom this player does not want to hear, by each player's stable key (<see cref="MultiplayerParticipant.PlayerKey"/>),
    /// and their wish to hear nobody. The voice service keeps listener mutes by participant id, and an id names a player only
    /// on the host that gave it, so the engine forgets named mutes at a join that finds another host or a host that lost this
    /// player's record (docs/internal/MultiplayerVoice.md, section 2.5). The book keeps the keys instead, which mean the same
    /// player on a host across sessions and nobody on any other host, and gives the engine the ids they name once the host
    /// lists its players again. During a join, or with no session, it passes only the wish to hear nobody, since names given
    /// then would belong to whichever host listed its players last. A player the host gave no key, as the host itself has
    /// none on its own session, is muted by their id for the session the mute was made in, and heard again in any other.
    /// The book lives as long as the game: nothing of it is written to disk. Main thread only; a settled frame allocates
    /// nothing.
    /// </summary>
    internal sealed class VoiceMuteBook
    {
        /// <summary>The most players the book keeps, the protocol's bound on one mute set.</summary>
        internal const int Capacity = 256;

        /// <summary>How often the book compares with the engine when nothing visible moved, in seconds.</summary>
        internal const float RecheckSeconds = 1f;

        private static readonly ParticipantId[] Nobody = Array.Empty<ParticipantId>();

        private readonly HashSet<MultiplayerPlayerKey> muted = new HashSet<MultiplayerPlayerKey>();
        private readonly HashSet<ParticipantId> mutedInSession = new HashSet<ParticipantId>();
        private readonly List<ParticipantId> named = new List<ParticipantId>(16);
        private readonly List<ParticipantId> held = new List<ParticipantId>(16);
        private MultiplayerSessionId mutedSession;
        private bool hearNobody;
        private int version;
        private int appliedVersion = -1;
        private MultiplayerSessionSnapshot? appliedSnapshot;
        private IInternalVoiceService? appliedTo;
        private float sinceCheck;

        /// <summary>Gets how many players are muted, by key and in a session.</summary>
        internal int Count => muted.Count + mutedInSession.Count;

        /// <summary>Gets a counter that moves with every change, so a view can tell when to rebind.</summary>
        internal int Version => version;

        /// <summary>Gets or sets the wish to hear nobody, which holds across joins and hosts.</summary>
        internal bool HearNobody
        {
            get => hearNobody;
            set
            {
                if (hearNobody == value) return;
                hearNobody = value;
                version++;
            }
        }

        /// <summary>Gets whether the player with <paramref name="key"/> is muted.</summary>
        internal bool IsMuted(MultiplayerPlayerKey key) => key.IsValid && muted.Contains(key);

        /// <summary>
        /// Mutes or unmutes the player with <paramref name="key"/>. False, changing nothing, for a key the host gave no value,
        /// which names nobody for sure, or past <see cref="Capacity"/>.
        /// </summary>
        internal bool SetMuted(MultiplayerPlayerKey key, bool mute)
        {
            if (!key.IsValid) return false;
            if (!mute)
            {
                if (muted.Remove(key)) version++;
                return true;
            }

            if (muted.Contains(key)) return true;
            if (Count >= Capacity) return false;
            muted.Add(key);
            version++;
            return true;
        }

        /// <summary>
        /// Gets whether <paramref name="participant"/> of <paramref name="session"/> is muted: by their key, or, for a player
        /// the host gave no key, by their id in the session the mute was made in.
        /// </summary>
        internal bool IsMuted(MultiplayerParticipant participant, MultiplayerSessionId session)
        {
            if (participant == null) throw new ArgumentNullException(nameof(participant));
            if (participant.PlayerKey.IsValid) return IsMuted(participant.PlayerKey);
            return session.IsValid && session.Equals(mutedSession) && mutedInSession.Contains(participant.Id);
        }

        /// <summary>
        /// Mutes or unmutes <paramref name="participant"/> of <paramref name="session"/>: by their key, or, without one, by
        /// their id for that session alone. A mute made in another session first forgets those of the session before, whose
        /// ids name nobody for sure now. False, changing nothing, without a session, or past <see cref="Capacity"/>.
        /// </summary>
        internal bool SetMuted(MultiplayerParticipant participant, MultiplayerSessionId session, bool mute)
        {
            if (participant == null) throw new ArgumentNullException(nameof(participant));
            if (participant.PlayerKey.IsValid) return SetMuted(participant.PlayerKey, mute);
            if (!session.IsValid) return false;
            if (!session.Equals(mutedSession))
            {
                if (mutedInSession.Count > 0) version++;
                mutedInSession.Clear();
                mutedSession = session;
            }

            if (!mute)
            {
                if (mutedInSession.Remove(participant.Id)) version++;
                return true;
            }

            if (mutedInSession.Contains(participant.Id)) return true;
            if (Count >= Capacity) return false;
            mutedInSession.Add(participant.Id);
            version++;
            return true;
        }

        /// <summary>
        /// Gets whether <paramref name="snapshot"/> lists the players of the host this player is joined to now: a session that
        /// is Ready and names anyone. A join withholds the list until its host sends one (docs/internal/MultiplayerSessionEngine.md,
        /// "Participants and connections"), and reconnecting is a join too.
        /// </summary>
        internal static bool ListsHostsPlayers(MultiplayerSessionSnapshot? snapshot) =>
            snapshot != null && snapshot.State == MultiplayerSessionState.Ready && snapshot.Participants.Count > 0;

        /// <summary>
        /// Main thread, every frame: makes the engine's mute set what the book says for <paramref name="snapshot"/>. It reads
        /// the engine's set back when the book, the snapshot or the service moved, and once a second besides, since the engine
        /// forgets named mutes at some joins; it calls <see cref="IInternalVoiceService.SetListenerMutes"/> only when they differ.
        /// </summary>
        internal void Apply(IInternalVoiceService voice, MultiplayerSessionSnapshot? snapshot, float deltaTime)
        {
            if (voice == null) throw new ArgumentNullException(nameof(voice));
            sinceCheck += float.IsNaN(deltaTime) || deltaTime < 0f ? 0f : deltaTime;
            if (version == appliedVersion && ReferenceEquals(snapshot, appliedSnapshot) && ReferenceEquals(voice, appliedTo) && sinceCheck < RecheckSeconds) return;
            sinceCheck = 0f;
            appliedVersion = version;
            appliedSnapshot = snapshot;
            appliedTo = voice;
            var engineHearsNobody = voice.GetListenerMutes(held);
            if (!ListsHostsPlayers(snapshot))
            {
                // Names given now would belong to the host that listed its players last, which a join may be leaving.
                if (engineHearsNobody != hearNobody) voice.SetListenerMutes(Nobody, hearNobody);
                return;
            }

            named.Clear();
            var participants = snapshot!.Participants;
            for (var index = 0; index < participants.Count; index++)
            {
                var participant = participants[index];
                if (!participant.IsLocal && IsMuted(participant, snapshot.Id)) named.Add(participant.Id);
            }

            if (engineHearsNobody == hearNobody && SameSet(named, held)) return;
            voice.SetListenerMutes(named, hearNobody);
        }

        /// <summary>Asks for a comparison at the next frame, as after the service was replaced.</summary>
        internal void Invalidate() => appliedVersion = -1;

        private static bool SameSet(List<ParticipantId> left, List<ParticipantId> right)
        {
            if (left.Count != right.Count) return false;
            for (var index = 0; index < left.Count; index++)
            {
                if (!right.Contains(left[index])) return false;
            }

            return true;
        }
    }
}
