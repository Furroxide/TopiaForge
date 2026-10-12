using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Multiplayer.Transport.Shaping
{
    /// <summary>
    /// The traffic classes inside one connection's budget. Voice and Realtime each have a capped bucket; Session has no
    /// cap of its own and takes whatever the connection's pacer allows. With the default caps Session therefore keeps
    /// at least 96 kbit/s of the 256 kbit/s, and all of it while the datagram classes are idle.
    /// </summary>
    internal sealed class ClassShaper
    {
        /// <summary>Gets each class bucket's depth: two full datagrams.</summary>
        internal const int DepthBytes = 2 * TransportOptions.MaximumDatagramBytes;

        private readonly TokenBucket voice;
        private readonly TokenBucket realtime;
        private readonly int voiceBytesPerSecond;
        private int sheddingLevel;

        internal ClassShaper(int voiceBytesPerSecond, int realtimeBytesPerSecond, long now)
        {
            this.voiceBytesPerSecond = voiceBytesPerSecond;
            voice = new TokenBucket(voiceBytesPerSecond, DepthBytes, now);
            realtime = new TokenBucket(realtimeBytesPerSecond, DepthBytes, now);
        }

        /// <summary>Gets the voice rate after shedding.</summary>
        internal long VoiceBytesPerSecond => voice.BytesPerSecond;

        internal bool CanSend(byte channel, int bytes, long now) => BucketFor(channel).CanSpend(bytes, now);

        internal void OnSent(byte channel, int bytes) => BucketFor(channel).Spend(bytes);

        internal long NextAvailable(byte channel, int bytes, long now) => BucketFor(channel).NextAvailable(bytes, now);

        /// <summary>Follows the endpoint's shedding: level 1 halves voice, level 2 and above stops it.</summary>
        internal void ApplyShedding(int level, long now)
        {
            if (level == sheddingLevel) return;
            sheddingLevel = level;
            voice.SetRate(level == 0 ? voiceBytesPerSecond : level == 1 ? voiceBytesPerSecond / 2 : 0, now);
        }

        private TokenBucket BucketFor(byte channel) => channel == (byte)EngineChannel.Voice ? voice : realtime;
    }
}
