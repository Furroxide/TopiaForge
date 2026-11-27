using System;

namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>
    /// The host's "offers voice chat" setting. A host sets it as it starts hosting, and it holds while anyone is connected,
    /// as the descriptor the host signed with it once does. A session that offers no voice carries none: the host drops
    /// every voice datagram as it arrives, before the relay policy spends a token or a strike on it, relays nothing, and its
    /// own player sends nothing. A joined player learns the setting from <c>JoinAccepted</c>, the host's first answer to
    /// every join and rejoin, so it knows before it could send; without voice it sends nothing, and drops and counts
    /// whatever its host relays anyway. What the player was shown of the session before joining holds as well, and the
    /// stricter of the two wins. See docs/internal/MultiplayerVoice.md, section 2.12.
    /// </summary>
    internal sealed partial class SessionEngine
    {
        /// <summary>
        /// Whether the session offers voice chat: on a host, its own setting for this hosting run; on a joined player, what
        /// its host said in the last <c>JoinAccepted</c>. True until one says otherwise, and again once a host closes every
        /// connection or the process changes role.
        /// </summary>
        private bool voiceOffered = true;

        /// <summary>
        /// Whether a joined player was shown, before this join, that the session offers no voice chat: by the host's signed
        /// descriptor, or by the listing it was picked from. False when nothing was shown, as on a link handed to the
        /// engine directly. It holds for every link the join opens, whatever the host says in <c>JoinAccepted</c>.
        /// </summary>
        private bool shownWithoutVoice;

        /// <summary>
        /// Gets whether the session offers voice chat, as far as this engine knows: false while it hosts a session that
        /// offers none, and on a joined player once its host said so in <c>JoinAccepted</c>, or the join was shown so before
        /// it began. True otherwise, solo play included. Voice travels only where this holds and the engine has voice
        /// options.
        /// </summary>
        internal bool IsVoiceOffered
        {
            get
            {
                RequireOwnerThread();
                return voiceOffered && !shownWithoutVoice;
            }
        }

        /// <summary>
        /// Says whether the session this host opens to players offers voice chat. It is fixed while anyone is connected:
        /// say it as hosting starts, before the first player joins. Closing every connection ends the hosting run and
        /// returns the engine to offering voice, so hosting again applies whatever is said then. A change while a
        /// connection is open is refused with <see cref="InvalidOperationException"/>.
        /// </summary>
        internal void OfferVoice(bool offered)
        {
            RequireOwnerThread();
            if (Role != EngineRole.Host) throw new InvalidOperationException("Only a host offers voice chat.");
            if (offered == voiceOffered) return;
            if (connections.Count > 0) throw new InvalidOperationException("Whether a session offers voice chat is fixed while players are connected.");
            voiceOffered = offered;

            // A frame of the host's own player may be waiting for its pair; a session without voice sends none of it.
            if (!offered) DiscardHeldVoice();
        }

        /// <summary>What a host tells each joining player in <c>JoinAccepted</c>: that it offers voice, and can carry it at all.</summary>
        private bool VoiceOfferedToPlayers => voiceOffered && options.Voice != null;

        /// <summary>
        /// Drops a player's voice datagram, and counts it, while the session this host runs offers no voice: as it arrives,
        /// before the relay policy spends a token or a strike on it, so a player that has not heard the setting yet, or never
        /// reads it, loses the datagram and nothing more. True when it was dropped.
        /// </summary>
        private bool DropsUplinkWithoutVoice()
        {
            if (voiceOffered) return false;
            voiceDropped[(int)VoiceDropReason.NotOffered]++;
            return true;
        }

        /// <summary>
        /// Drops a datagram a host relays, and counts it, while this player's session offers no voice, as the host said or
        /// the join was shown: whatever a host relays anyway, as a hostile one may, is never played. True when it was dropped.
        /// </summary>
        private bool DropsRelayedWithoutVoice()
        {
            if (voiceOffered && !shownWithoutVoice) return false;
            voiceDropped[(int)VoiceDropReason.NotOffered]++;
            return true;
        }

        /// <summary>Notes what a joining player was shown before it connected; see <see cref="Connect"/>.</summary>
        private void NoteVoiceShown(bool withoutVoice) => shownWithoutVoice = withoutVoice;

        /// <summary>Takes what the host said in <c>JoinAccepted</c>; what the join was shown still holds beside it.</summary>
        private void HearVoiceOffer(bool offered) => voiceOffered = offered;

        /// <summary>Returns the setting to offering voice, as a host that closes every connection and a role change do.</summary>
        private void ForgetVoiceOffer()
        {
            voiceOffered = true;
            shownWithoutVoice = false;
        }
    }
}
