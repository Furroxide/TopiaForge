using System;

namespace TopiaForge.Multiplayer.Voice
{
    /// <summary>
    /// Checks the table-of-contents byte that starts every Opus packet (RFC 6716, section 3.1) before anything decodes
    /// the packet. A voice packet is mono, holds one frame (code 0), and uses one of the nine configurations whose
    /// frames last 20 ms. A host runs this on every frame it relays, so no player can make the others decode stereo,
    /// longer or multi-frame audio: a code-0 packet of at most 160 bytes always decodes to exactly one 20 ms frame.
    /// </summary>
    internal static class OpusToc
    {
        private const int StereoFlag = 0x04;
        private const int FrameCountCode = 0x03;

        /// <summary>Gets whether <paramref name="packet"/> is 1 to 160 bytes that start with a voice TOC byte.</summary>
        internal static bool IsVoicePacket(ReadOnlySpan<byte> packet) =>
            packet.Length >= 1 && packet.Length <= OpusVoiceCodec.MaximumPacketLength && IsVoiceToc(packet[0]);

        /// <summary>Gets whether a TOC byte names a mono, single-frame, 20 ms configuration.</summary>
        internal static bool IsVoiceToc(byte toc)
        {
            if ((toc & (StereoFlag | FrameCountCode)) != 0) return false;
            switch (toc >> 3)
            {
                case 1: // SILK-only narrowband
                case 5: // SILK-only mediumband
                case 9: // SILK-only wideband, which the voice encoder uses
                case 13: // hybrid super-wideband
                case 15: // hybrid fullband
                case 19: // CELT-only narrowband
                case 23: // CELT-only wideband
                case 27: // CELT-only super-wideband
                case 31: // CELT-only fullband
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Gets whether a voice packet is a discontinuous-transmission packet: the TOC byte alone, which the encoder
        /// writes for silence and a decoder turns into comfort noise.
        /// </summary>
        internal static bool IsDiscontinuous(ReadOnlySpan<byte> packet) => packet.Length == 1 && IsVoiceToc(packet[0]);
    }
}
