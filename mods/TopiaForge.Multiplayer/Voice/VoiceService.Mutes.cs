using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Multiplayer.Voice
{
    /// <summary>
    /// Listener mutes. The engine owns the set and keeps or forgets it at every join (docs/internal/MultiplayerVoice.md,
    /// section 2.5); the service keeps no copy. It keeps only the player's wish to hear nobody, which names nobody and may
    /// follow the player anywhere.
    /// </summary>
    internal sealed partial class VoiceService
    {
        private readonly HashSet<ParticipantId> engineMutes = new HashSet<ParticipantId>();
        private bool muteAllWish;

        /// <summary>
        /// Sets whom the local player does not want to hear; the host stops relaying them, and anything of theirs still
        /// buffered here goes at once. The engine keeps the set for as long as the host keeps this player's record. Names
        /// given while a join is under way, or while the link is down, belong to the host this player joined last, so the
        /// join keeps them with that host and never gives them to another.
        /// </summary>
        /// <remarks>
        /// The wish to hear nobody, <paramref name="muteAll"/>, lasts until the player changes it: the service gives it back
        /// to the engine wherever the engine forgets it, and makes it again while a join is under way.
        /// </remarks>
        internal void SetListenerMutes(IReadOnlyCollection<ParticipantId> muted, bool muteAll = false)
        {
            if (muted == null) throw new ArgumentNullException(nameof(muted));
            engine.RequireOwnerThread();
            if (muted.Count > ProtocolLimits.MaximumParticipants) throw new ArgumentException("Too many participants to mute.", nameof(muted));
            foreach (var participant in muted)
            {
                if (!participant.IsValid) throw new ArgumentException("A muted participant id is invalid.", nameof(muted));
            }

            var wasAll = engine.GetListenerMutes(engineMutes);
            engine.SetListenerMutes(muted, muteAll);
            muteAllWish = muteAll;
            lock (inbound.Gate)
            {
                var batch = inbound.Writable;
                if (muteAll && !wasAll) batch.AddControl(VoiceControlKind.ResetAll);
                if (!wasAll && !muteAll)
                {
                    foreach (var participant in muted)
                    {
                        if (!engineMutes.Contains(participant)) batch.AddControl(VoiceControlKind.Reset, participant);
                    }
                }
            }

            worker.Signal();
        }

        /// <summary>
        /// Copies whom the local player does not want to hear into <paramref name="into"/>, after clearing it, and returns
        /// whether they want to hear nobody: the engine's set, as it kept or forgot it at the last join.
        /// </summary>
        internal bool GetListenerMutes(ICollection<ParticipantId> into) => engine.GetListenerMutes(into);

        /// <summary>
        /// Gives the wish to hear nobody back where the engine forgot the set, at a role change or a join that found a host
        /// without this player's record: it names nobody, so it is never a list of people one host learns from another.
        /// While a join is under way the wish is made again, so the engine applies it to whichever session the join finds;
        /// a set that also names people is left for the join to keep or forget.
        /// </summary>
        private void AdoptListenerMutes(MultiplayerSessionState state)
        {
            if (!muteAllWish) return;
            var muteAll = engine.GetListenerMutes(engineMutes);
            var joining = state == MultiplayerSessionState.Connecting;
            if (muteAll ? !joining || engineMutes.Count > 0 : joining && engineMutes.Count > 0) return;
            engine.SetListenerMutes(engineMutes, muteAll: true);
            if (muteAll) return;
            lock (inbound.Gate) inbound.Writable.AddControl(VoiceControlKind.ResetAll);
            worker.Signal();
        }
    }
}
