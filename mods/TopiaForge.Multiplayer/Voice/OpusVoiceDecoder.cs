using System;
using TopiaForge.Multiplayer.ThirdParty.Concentus.Structs;

namespace TopiaForge.Multiplayer.Voice
{
    /// <summary>
    /// Decodes one speaker's 20 ms frames. Every call fills the caller's 960-sample frame: with the decoded packet, with
    /// a frame recovered from the next packet's in-band FEC, or with packet-loss concealment. Packets come from the
    /// network, so a packet that fails <see cref="OpusToc"/> or that the codec rejects is concealed instead, and never
    /// throws. It allocates nothing itself. One thread owns it.
    /// </summary>
    internal sealed class OpusVoiceDecoder
    {
        private readonly OpusDecoder decoder;

        internal OpusVoiceDecoder()
        {
            // Upstream points callers at OpusCodecFactory, which only adds the native library left out here.
#pragma warning disable CS0618
            decoder = new OpusDecoder(OpusVoiceCodec.SampleRateHz, 1);
#pragma warning restore CS0618
        }

        /// <summary>
        /// Decodes <paramref name="packet"/> into <paramref name="frame"/>. Returns false, with concealment in the frame,
        /// when the packet is not a valid voice packet.
        /// </summary>
        internal bool Decode(ReadOnlySpan<byte> packet, Span<short> frame)
        {
            OpusVoiceCodec.RequireFrame(frame.Length, nameof(frame));
            if (OpusToc.IsVoicePacket(packet) && TryDecode(packet, frame, recover: false)) return true;
            ConcealFrame(frame);
            return false;
        }

        /// <summary>Fills <paramref name="frame"/> with packet-loss concealment for a frame that never arrived.</summary>
        internal void Conceal(Span<short> frame)
        {
            OpusVoiceCodec.RequireFrame(frame.Length, nameof(frame));
            ConcealFrame(frame);
        }

        /// <summary>
        /// Rebuilds the frame before <paramref name="nextPacket"/> from the in-band FEC that packet carries, falling back
        /// to concealment where it carries none. The caller then decodes <paramref name="nextPacket"/> itself, as usual.
        /// Returns false, with concealment in the frame, when the next packet is not a valid voice packet.
        /// </summary>
        internal bool Recover(ReadOnlySpan<byte> nextPacket, Span<short> frame)
        {
            OpusVoiceCodec.RequireFrame(frame.Length, nameof(frame));
            if (OpusToc.IsVoicePacket(nextPacket) && TryDecode(nextPacket, frame, recover: true)) return true;
            ConcealFrame(frame);
            return false;
        }

        /// <summary>Forgets the stream so far, so the next packet decodes as the first of a new one.</summary>
        internal void Reset() => decoder.ResetState();

        private void ConcealFrame(Span<short> frame)
        {
            if (!TryDecode(ReadOnlySpan<byte>.Empty, frame, recover: false)) frame.Clear();
        }

        private bool TryDecode(ReadOnlySpan<byte> packet, Span<short> frame, bool recover)
        {
            try
            {
                var samples = decoder.Decode(packet, frame, OpusVoiceCodec.FrameSampleCount, recover);
                if (samples == OpusVoiceCodec.FrameSampleCount) return true;
            }
            catch (Exception)
            {
                // Concentus reports a malformed packet by throwing. It came from the network, so it is concealed.
            }

            decoder.ResetState();
            return false;
        }
    }
}
