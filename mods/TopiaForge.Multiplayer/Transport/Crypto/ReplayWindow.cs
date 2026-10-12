using System;

namespace TopiaForge.Multiplayer.Transport.Crypto
{
    /// <summary>
    /// The anti-replay window for datagram counters, in the style of RFC 6479. A counter is accepted once, and only if
    /// it is newer than the highest accepted counter or within <see cref="Size"/> of it. Check with
    /// <see cref="CanAccept"/> before decrypting and call <see cref="Accept"/> only once the datagram authenticates, so
    /// a forgery never moves the window.
    /// </summary>
    internal sealed class ReplayWindow
    {
        internal const int Size = 2048;

        private const int Words = Size / 64;
        private readonly ulong[] bitmap = new ulong[Words];
        private ulong highest;
        private bool started;

        /// <summary>Gets the highest counter accepted so far, or zero before the first.</summary>
        internal ulong Highest => highest;

        internal bool CanAccept(ulong counter)
        {
            if (!started || counter > highest) return true;
            if (highest - counter >= Size) return false;
            return (bitmap[WordOf(counter)] & BitOf(counter)) == 0;
        }

        /// <summary>Records <paramref name="counter"/>; returns false for a replay or a counter older than the window.</summary>
        internal bool Accept(ulong counter)
        {
            if (!CanAccept(counter)) return false;
            if (!started || counter > highest) Advance(counter);
            bitmap[WordOf(counter)] |= BitOf(counter);
            return true;
        }

        /// <summary>Moves the window's top to <paramref name="counter"/>, forgetting the counters that slide out.</summary>
        private void Advance(ulong counter)
        {
            if (!started || counter - highest >= Size)
            {
                Array.Clear(bitmap, 0, Words);
            }
            else
            {
                // Bits for the counters about to enter the window still hold counters a full window older.
                for (var next = highest + 1; next <= counter; next++)
                {
                    if (next % 64 == 0 && counter - next >= 63)
                    {
                        bitmap[WordOf(next)] = 0;
                        next += 63;
                        continue;
                    }

                    bitmap[WordOf(next)] &= ~BitOf(next);
                }
            }

            highest = counter;
            started = true;
        }

        private static int WordOf(ulong counter) => (int)(counter / 64 % Words);

        private static ulong BitOf(ulong counter) => 1UL << (int)(counter % 64);
    }
}
