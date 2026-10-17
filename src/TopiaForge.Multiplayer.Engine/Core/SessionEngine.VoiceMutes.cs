using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>
    /// Listener mutes: whom the local player does not want to hear. A participant id names a player only while the host that
    /// assigned it keeps that player's record, so the set follows the people, not the session id. See
    /// docs/internal/MultiplayerVoice.md, section 2.5.
    /// </summary>
    internal sealed partial class SessionEngine
    {
        private readonly HashSet<string> localVoiceMutes = new HashSet<string>(StringComparer.Ordinal);
        private bool localVoiceMuteAll;

        /// <summary>Stands in for no host at all, which every join forgets and no joined host is: no identity is empty.</summary>
        private const string NoMutesHost = "";

        /// <summary>
        /// The authenticated identity of the host whose participants the set names: the host the client had joined when the
        /// set was made, or, for one made while no session is joined or a join is under way, the host whose players the
        /// session listed then. It is null for a set that names nobody made then, which applies to whichever session the
        /// join finds.
        /// </summary>
        private string? listenerMutesHost;

        /// <summary>
        /// The authenticated identity of the host whose players a client's session lists, the only players its player can
        /// name: the host that sent the last participant list. It is null before any list and after a role change, which
        /// list nobody, and <see cref="NoMutesHost"/> once a join finds that host without this player's record, whose ids
        /// then name nobody.
        /// </summary>
        private string? listedHost;

        /// <summary>
        /// Sets whom the local player does not want to hear. A client also sends the set to its host, reliably, so muted
        /// speakers are never relayed to it; <paramref name="muteAll"/> stops all relayed voice. A client's names name the
        /// players its session lists, and those are the players of the host that sent the last list: a join changes them
        /// only when the new host sends its own, after its snapshot. A set made while no session is joined, or while a join
        /// is under way, therefore belongs to that host, and the join keeps or forgets it as it would any set made there.
        /// Names given once a join has found another host, or this one without the player's record, but before that host
        /// lists its players, name nobody in the session and are dropped. A name never silences whoever has that id with the
        /// next host. A set that names nobody, such as muting everyone, applies to whichever session the join finds.
        /// </summary>
        internal void SetListenerMutes(IReadOnlyCollection<ParticipantId> muted, bool muteAll = false)
        {
            if (muted == null) throw new ArgumentNullException(nameof(muted));
            RequireOwnerThread();
            if (muted.Count > ProtocolLimits.MaximumParticipants) throw new ArgumentException("Too many participants to mute.", nameof(muted));
            localVoiceMutes.Clear();
            foreach (var participant in muted)
            {
                if (!participant.IsValid) throw new ArgumentException("A muted participant id is invalid.", nameof(muted));
                localVoiceMutes.Add(participant.Value);
            }

            localVoiceMuteAll = muteAll;
            if (Role == EngineRole.Client) TieListenerMutesToTheirHost();
            if (options.Voice != null && channel != null && channel.Phase != ClientPhase.AwaitingJoin) channel.VoiceMutesPending = true;
            EndOperation();
        }

        /// <summary>
        /// Copies whom the local player does not want to hear into <paramref name="into"/>, after clearing it, and returns
        /// whether they want to hear nobody. The engine keeps or forgets the set at every join, so a voice service reads it
        /// here rather than keeping a copy that could disagree.
        /// </summary>
        internal bool GetListenerMutes(ICollection<ParticipantId> into)
        {
            if (into == null) throw new ArgumentNullException(nameof(into));
            RequireOwnerThread();
            into.Clear();
            foreach (var participant in localVoiceMutes) into.Add(new ParticipantId(participant));
            return localVoiceMuteAll;
        }

        /// <summary>
        /// Ties a client's new set to the host whose players it names. While no session is joined, or a join is under way,
        /// that is the host whose players the session lists. Once joined, it is the joined host, unless the session still
        /// lists the players of before: then the names name nobody here, and go.
        /// </summary>
        private void TieListenerMutesToTheirHost()
        {
            if (channel == null || channel.Phase == ClientPhase.AwaitingJoin)
            {
                listenerMutesHost = localVoiceMutes.Count == 0 ? null : listedHost ?? NoMutesHost;
            }
            else if (!string.Equals(listedHost, channel.Link.PeerIdentity, StringComparison.Ordinal))
            {
                localVoiceMutes.Clear();
            }
        }

        /// <summary>
        /// Decides at a join what becomes of the set. One made with this host stays when the host kept the player's record,
        /// as across an in-place replacement or within the grace period, because its ids still name the same people. One
        /// made with another host, or with this one before it lost the record by restarting or letting it expire, is
        /// forgotten, mute-all included, so it neither silences strangers who got the same ids nor tells this host whom the
        /// player muted. Names given while the join was under way belong to the host whose players the session listed, and
        /// fare the same. A set that names nobody, made while the join was under way, stays, for the session the join found.
        /// Until this host lists its players the session lists the players of before; when they were this host's and it lost
        /// the record, their ids name nobody now.
        /// </summary>
        private void AdoptListenerMutes(string host, bool recordKept)
        {
            if (listenerMutesHost != null && !(recordKept && string.Equals(listenerMutesHost, host, StringComparison.Ordinal)))
            {
                ForgetListenerMutes();
            }

            listenerMutesHost = host;
            if (!recordKept && string.Equals(listedHost, host, StringComparison.Ordinal)) listedHost = NoMutesHost;
        }

        /// <summary>Records that the session now lists the players of <paramref name="current"/>'s host.</summary>
        private void NoteListedHost(ClientChannel current) => listedHost = current.Link.PeerIdentity;

        /// <summary>Forgets the set and the hosts it named, as leaving a session for another role does.</summary>
        private void RetireListenerMutes()
        {
            ForgetListenerMutes();
            listenerMutesHost = null;
            listedHost = null;
        }

        private void ForgetListenerMutes()
        {
            localVoiceMutes.Clear();
            localVoiceMuteAll = false;
        }

        /// <summary>Applies a client's listener mute set: the host stops relaying those speakers to it.</summary>
        private static void ApplyVoiceMutes(HostConnection connection, ProtocolReader body)
        {
            VoiceMessages.ReadMutes(body, out var muteAll, out var muted);
            connection.VoiceMuteAll = muteAll;
            connection.VoiceMutes = muted;
        }
    }
}
