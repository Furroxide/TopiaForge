using System;

namespace TopiaForge.ModManager.Core.Multiplayer
{
    internal enum HudMirrorDecision
    {
        /// <summary>Send this value now.</summary>
        SendNow = 0,

        /// <summary>Too soon after the last send; the value waits, and newer values replace it.</summary>
        Deferred = 1,

        /// <summary>Peers already have this value; nothing is sent, and a waiting value is dropped.</summary>
        Unchanged = 2
    }

    /// <summary>
    /// Coalesces one mirrored HUD surface to at most 10 Hz. The first change goes at once; changes inside the interval
    /// wait, the newest replacing older ones, and go when it ends, so peers always end on the latest value.
    /// </summary>
    internal sealed class HudMirrorCoalescer
    {
        private readonly long intervalMicros;
        private bool hasSent;
        private long lastSentMicros;
        private ulong lastSentHash;
        private bool pending;
        private ulong pendingHash;

        public HudMirrorCoalescer(long intervalMicros = PresentationRoutingPolicy.HudMirrorIntervalMicros)
        {
            if (intervalMicros < 1) throw new ArgumentOutOfRangeException(nameof(intervalMicros));
            this.intervalMicros = intervalMicros;
        }

        public bool HasPending => pending;

        /// <summary>When the waiting value is due, or <see cref="long.MaxValue"/> when nothing waits.</summary>
        public long NextDueMicros => pending ? lastSentMicros + intervalMicros : long.MaxValue;

        /// <summary>Offers the surface's current content, identified by a fingerprint such as FNV-1a of its text.</summary>
        public HudMirrorDecision Offer(ulong contentHash, long nowMicros)
        {
            if (nowMicros < 0) throw new ArgumentOutOfRangeException(nameof(nowMicros));
            if (hasSent && contentHash == lastSentHash)
            {
                pending = false;
                return HudMirrorDecision.Unchanged;
            }

            if (!hasSent || nowMicros - lastSentMicros >= intervalMicros)
            {
                MarkSent(contentHash, nowMicros);
                return HudMirrorDecision.SendNow;
            }

            pending = true;
            pendingHash = contentHash;
            return HudMirrorDecision.Deferred;
        }

        /// <summary>Releases the waiting value once its interval has passed.</summary>
        public bool TryFlush(long nowMicros, out ulong contentHash)
        {
            if (nowMicros < 0) throw new ArgumentOutOfRangeException(nameof(nowMicros));
            contentHash = pendingHash;
            if (!pending || nowMicros - lastSentMicros < intervalMicros) return false;
            MarkSent(pendingHash, nowMicros);
            return true;
        }

        private void MarkSent(ulong contentHash, long nowMicros)
        {
            hasSent = true;
            lastSentMicros = nowMicros;
            lastSentHash = contentHash;
            pending = false;
        }
    }

    internal enum BroadcastDecision
    {
        Send = 0,

        /// <summary>The same content went out within the dedupe window.</summary>
        Duplicate = 1,

        /// <summary>Over the kind's rate limit; dropped, and not remembered as sent.</summary>
        RateLimited = 2
    }

    /// <summary>
    /// Throttles broadcast toasts and audio cues from the gamemode server. Identical content within a window is sent
    /// once (2 s for toasts, 250 ms for cues), and each kind has a token bucket (toasts 4 then 2 a second, cues 8 then
    /// 4 a second). A duplicate costs no token. The last 32 sends of each kind are remembered.
    /// </summary>
    internal sealed class PresentationBroadcastLimiter
    {
        public const int ToastBurst = 4;
        public const int ToastsPerSecond = 2;
        public const long ToastDedupeWindowMicros = 2_000_000;
        public const int AudioCueBurst = 8;
        public const int AudioCuesPerSecond = 4;
        public const long AudioCueDedupeWindowMicros = 250_000;
        public const int DedupeMemory = 32;

        private readonly Channel toasts;
        private readonly Channel audioCues;

        public PresentationBroadcastLimiter(long nowMicros)
        {
            toasts = new Channel(new TokenBucket(ToastBurst, ToastsPerSecond, nowMicros), ToastDedupeWindowMicros);
            audioCues = new Channel(
                new TokenBucket(AudioCueBurst, AudioCuesPerSecond, nowMicros),
                AudioCueDedupeWindowMicros);
        }

        /// <summary>Offers one toast or audio cue, identified by a fingerprint of its content.</summary>
        public BroadcastDecision Offer(PresentationKind kind, ulong contentHash, long nowMicros)
        {
            if (nowMicros < 0) throw new ArgumentOutOfRangeException(nameof(nowMicros));
            switch (kind)
            {
                case PresentationKind.Toast:
                    return toasts.Offer(contentHash, nowMicros);
                case PresentationKind.AudioCue:
                    return audioCues.Offer(contentHash, nowMicros);
                default:
                    throw new ArgumentException("Only toasts and audio cues are broadcast.", nameof(kind));
            }
        }

        private sealed class Channel
        {
            private readonly TokenBucket bucket;
            private readonly long windowMicros;
            private readonly ulong[] hashes = new ulong[DedupeMemory];
            private readonly long[] sentAt = new long[DedupeMemory];
            private int count;
            private int next;

            public Channel(TokenBucket bucket, long windowMicros)
            {
                this.bucket = bucket;
                this.windowMicros = windowMicros;
            }

            public BroadcastDecision Offer(ulong contentHash, long nowMicros)
            {
                for (var index = 0; index < count; index++)
                {
                    if (hashes[index] == contentHash && nowMicros - sentAt[index] < windowMicros)
                    {
                        return BroadcastDecision.Duplicate;
                    }
                }

                if (!bucket.TryTake(nowMicros)) return BroadcastDecision.RateLimited;
                hashes[next] = contentHash;
                sentAt[next] = nowMicros;
                next = (next + 1) % DedupeMemory;
                if (count < DedupeMemory) count++;
                return BroadcastDecision.Send;
            }
        }
    }
}
