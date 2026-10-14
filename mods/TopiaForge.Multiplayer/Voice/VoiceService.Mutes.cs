using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Multiplayer.Voice
{
    /// <summary>
    /// Listener mutes. The engine owns the set and keeps or forgets it at every join (docs/internal/MultiplayerVoice.md,
    /// section 2.5); the service keeps no copy. It keeps the player's wish to hear nobody, which names nobody and may
    /// follow the player anywhere, and names given while this player is not Ready, which wait for the session they were
    /// given in.
    /// </summary>
    internal sealed partial class VoiceService
    {
        private readonly HashSet<ParticipantId> engineMutes = new HashSet<ParticipantId>();
        private readonly List<ParticipantId> heldNames = new List<ParticipantId>();
        private MultiplayerSessionId heldNamesSession;
        private bool hasHeldNames;
        private bool muteAllWish;

        /// <summary>
        /// Sets whom the local player does not want to hear; the host stops relaying them, and anything of theirs still
        /// buffered here goes at once. The engine keeps the set for as long as the host keeps this player's record.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The wish to hear nobody, <paramref name="muteAll"/>, lasts until the player changes it: the service gives it back
        /// to the engine wherever the engine forgets it, and makes it again while a join is under way.
        /// </para>
        /// <para>
        /// Names given while this player is not Ready are held back. The engine applies a set made while it awaits a join
        /// to whichever session the join finds, so a name meant for one host's player would silence whoever has that id
        /// on the next. They are given to the engine when this player is Ready again in the session they were given in,
        /// and forgotten if the join found another.
        /// </para>
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

            var snapshot = engine.Snapshot;
            var wasAll = engine.GetListenerMutes(engineMutes);
            var hold = muted.Count > 0 && snapshot.State != MultiplayerSessionState.Ready;
            engine.SetListenerMutes(hold ? Array.Empty<ParticipantId>() : muted, muteAll);
            heldNames.Clear();
            hasHeldNames = hold;
            if (hold)
            {
                heldNames.AddRange(muted);
                heldNamesSession = snapshot.Id;
            }

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
        /// whether they want to hear nobody: the engine's set, as it kept or forgot it at the last join, and any names held
        /// back until this player is Ready again.
        /// </summary>
        internal bool GetListenerMutes(ICollection<ParticipantId> into)
        {
            var muteAll = engine.GetListenerMutes(into);
            if (!hasHeldNames) return muteAll;
            foreach (var participant in heldNames)
            {
                if (!into.Contains(participant)) into.Add(participant);
            }

            return muteAll;
        }

        /// <summary>
        /// Gives the engine what the player asked for and the engine allows. Names held back while this player was not
        /// Ready go to the engine once it is Ready in the session they were given in, and are forgotten in any other. The
        /// wish to hear nobody returns where the engine forgot the set, at a role change or a join that found a host
        /// without this player's record: it names nobody, so it is never a list of people one host learns from another.
        /// While a join is under way the wish is made again, so the engine applies it to whichever session the join finds;
        /// a set that also names people is left for the join to keep or forget.
        /// </summary>
        private void AdoptListenerMutes(MultiplayerSessionState state, MultiplayerSessionId session)
        {
            if (hasHeldNames && state == MultiplayerSessionState.Ready)
            {
                hasHeldNames = false;
                if (session.Equals(heldNamesSession)) engine.SetListenerMutes(heldNames, muteAllWish);
                heldNames.Clear();
            }

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
