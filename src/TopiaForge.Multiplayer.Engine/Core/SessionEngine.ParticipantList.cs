using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>
    /// A client's participant list: the last list a host sent it, labelled with the ids that host gave. Every host numbers
    /// its players itself, so an id names a player only on the host that gave it, and the session shows a list only beside
    /// the ids of the host that sent it. See docs/internal/MultiplayerSessionEngine.md, "Participants and connections".
    /// </summary>
    internal sealed partial class SessionEngine
    {
        private List<ParticipantEntry> clientParticipants = new List<ParticipantEntry>();

        /// <summary>The id the host that sent the list had given this player, as it was when the list came.</summary>
        private ParticipantId? listedLocalParticipant;

        /// <summary>The id the host that sent the list gave its own player, or null for a headless host.</summary>
        private ParticipantId? listedHostParticipant;

        /// <summary>
        /// Gets how many times a client has let its list go: at a join that withheld it, and on going back to solo play. From
        /// then on the ids the list held may name other people. That holds even when the next host's whole answer arrives in
        /// one pump, so no snapshot ever lists nobody, and whatever a consumer keeps per participant id, such as a voice
        /// playback stream, goes when this changes. A join that keeps the list, a lost link and a stall leave it as it is.
        /// </summary>
        internal ulong ParticipantListGeneration { get; private set; }

        /// <summary>
        /// Shows the list <paramref name="current"/>'s host sent. Nothing else fills a client's list, and this notes the
        /// host as the one whose players the session lists, which the listener mutes and the next join go by.
        /// </summary>
        private void ListParticipants(ClientChannel current, List<ParticipantEntry> participants)
        {
            clientParticipants = participants;
            listedLocalParticipant = localParticipantId;
            listedHostParticipant = hostParticipantId;
            NoteListedHost(current);
            MarkSessionChanged(participantsChanged: true);
        }

        /// <summary>
        /// Decides at a join whether the session goes on listing the players it lists. It does when the join finds the host
        /// that sent the list with this player's record kept, and that host gives the ids it gave before: they still name
        /// the same people, and label the list as its host did. Any other join withholds the list, so the session lists
        /// nobody until the host the join found sends its own, after its snapshot: one to another host, to this host after
        /// it restarted or let the record expire, or after a join to it went unanswered, and one to another process under
        /// the same identity that gives other ids. A lost link never withholds it. It runs before
        /// <see cref="AdoptListenerMutes"/>, which forgets a listed host that lost the record.
        /// </summary>
        private void AdoptParticipantList(string host, JoinAcceptedMessage message)
        {
            if (message.RecordKept &&
                string.Equals(listedHost, host, StringComparison.Ordinal) &&
                string.Equals(listedLocalParticipant?.Value, message.ParticipantId, StringComparison.Ordinal) &&
                string.Equals(listedHostParticipant?.Value, message.HostParticipantId, StringComparison.Ordinal))
            {
                return;
            }

            ForgetParticipantList();
        }

        /// <summary>Empties a client's list and the ids that labelled it, and moves <see cref="ParticipantListGeneration"/> on.</summary>
        private void ForgetParticipantList()
        {
            clientParticipants = new List<ParticipantEntry>();
            listedLocalParticipant = null;
            listedHostParticipant = null;
            ParticipantListGeneration++;
        }
    }
}
