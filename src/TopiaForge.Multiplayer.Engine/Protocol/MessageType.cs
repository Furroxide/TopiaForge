namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>
    /// Envelope types of the session protocol. Types 0x01-0x3F are critical: a peer that does not know one ends the
    /// connection. Types 0x40-0x7F are ignorable: a peer that does not know one skips it by its length.
    /// </summary>
    internal static class MessageType
    {
        internal const byte JoinRequest = 0x01;
        internal const byte JoinAccepted = 0x02;
        internal const byte JoinRejected = 0x03;
        internal const byte SnapshotBegin = 0x04;
        internal const byte SnapshotEnd = 0x05;
        internal const byte ReadyAck = 0x06;
        internal const byte ParticipantsChanged = 0x07;
        internal const byte SessionEnded = 0x08;
        internal const byte SessionReplaced = 0x09;
        internal const byte Commit = 0x0A;
        internal const byte Leave = 0x0B;
        internal const byte Command = 0x10;
        internal const byte CommandResult = 0x11;
        internal const byte ObjectInput = 0x12;
        internal const byte ObjectInputResult = 0x13;
        internal const byte StateDelta = 0x14;
        internal const byte ObjectSpawned = 0x15;
        internal const byte ObjectChanged = 0x16;
        internal const byte ObjectDespawned = 0x17;
        internal const byte PresentationEvent = 0x18;

        /// <summary>Reserved for runtime system channels (world replication, avatars, chat, scene follow).</summary>
        internal const byte SystemMessage = 0x20;

        /// <summary>Reserved for voice frames.</summary>
        internal const byte Voice = 0x30;

        internal const byte ClockSync = 0x40;
        internal const byte ClockSyncReply = 0x41;

        internal const byte LastCritical = 0x3F;
        internal const byte LastIgnorable = 0x7F;

        internal static bool IsIgnorable(ulong type) => type > LastCritical && type <= LastIgnorable;
    }
}
