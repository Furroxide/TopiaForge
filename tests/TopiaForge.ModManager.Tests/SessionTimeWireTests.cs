using System;
using System.Linq;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.Seam;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The session's time and its one message (WS3.13): valid by construction, within Chronos' own bounds, and read strictly,
    /// so a host's time outside them never reaches a player's clock.
    /// </summary>
    internal static class SessionTimeWireTests
    {
        internal static void Run()
        {
            TheSessionTimeIsValidByConstruction();
            TheModesMirrorChronos();
            MessagesRoundTrip();
            MessagesOutsideTheBoundsAreRefused();
        }

        private static void TheSessionTimeIsValidByConstruction()
        {
            That(default(InternalSessionTime).Scale == 1f && default(InternalSessionTime).Mode == InternalTimeMode.Realtime &&
                 InternalSessionTime.Realtime.Equals(default(InternalSessionTime)) && !InternalSessionTime.Realtime.IsFrozen,
                "the default session time is real time");
            That(new InternalSessionTime(0f, InternalTimeMode.Paused).IsFrozen && new InternalSessionTime(0.25f, InternalTimeMode.Slowed).Scale == 0.25f &&
                 new InternalSessionTime(1f, InternalTimeMode.Realtime).Equals(InternalSessionTime.Realtime), "a freeze, a slow and real time");
            var negativeZero = new InternalSessionTime(-0f, InternalTimeMode.Paused);
            That(BitConverter.SingleToInt32Bits(negativeZero.Scale) == 0 && negativeZero.Equals(new InternalSessionTime(0f, InternalTimeMode.Paused)),
                "a negative zero is zero, in one form");
            foreach (var mode in new[] { InternalTimeMode.Realtime, InternalTimeMode.Slowed, InternalTimeMode.Paused, InternalTimeMode.TurnBased })
            {
                foreach (var scale in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -0.25f, -float.Epsilon, 1.0001f, 2f, float.MaxValue })
                {
                    That(!InternalSessionTime.IsValid(scale, mode) && !InternalSessionTime.TryCreate(scale, mode, out var refused) && refused.Equals(default(InternalSessionTime)),
                        scale + " is outside the bounds for " + mode);
                    Throws<ArgumentOutOfRangeException>(() => _ = new InternalSessionTime(scale, mode), scale + " cannot be made for " + mode);
                }
            }

            That(!InternalSessionTime.IsValid(0.5f, InternalTimeMode.Realtime) && !InternalSessionTime.IsValid(0.5f, InternalTimeMode.Paused) &&
                 !InternalSessionTime.IsValid(1f, InternalTimeMode.Slowed) && !InternalSessionTime.IsValid(0f, InternalTimeMode.Realtime),
                "a mode that disagrees with its scale is refused");
            That(InternalSessionTime.IsValid(0f, InternalTimeMode.Slowed) && InternalSessionTime.IsValid(0f, InternalTimeMode.TurnBased) &&
                 InternalSessionTime.IsValid(1f, InternalTimeMode.TurnBased) && InternalSessionTime.IsValid(0.5f, InternalTimeMode.TurnBased),
                "a driver may slow to zero, and turn-based play runs at any scale");
            That(!InternalSessionTime.IsValid(1f, (InternalTimeMode)4) && !InternalSessionTime.IsValid(1f, (InternalTimeMode)255), "an undefined mode is refused");
            That(new InternalSessionTime(0.5f, InternalTimeMode.Slowed).ToString() == "0.5 (Slowed)", "it reads as a scale and a mode");
        }

        private static void TheModesMirrorChronos()
        {
            var spi = Enum.GetValues(typeof(InternalTimeMode)).Cast<object>().Select(value => (Name: value.ToString(), Value: Convert.ToInt32(value))).ToArray();
            var chronos = Enum.GetValues(typeof(TimeMode)).Cast<object>().Select(value => (Name: value.ToString(), Value: Convert.ToInt32(value))).ToArray();
            That(spi.SequenceEqual(chronos), "InternalTimeMode mirrors Chronos' TimeMode name for name and value for value");
        }

        private static void MessagesRoundTrip()
        {
            var buffer = new byte[SessionTimeWire.Length];
            foreach (var time in new[]
                     {
                         InternalSessionTime.Realtime, new InternalSessionTime(0f, InternalTimeMode.Paused), new InternalSessionTime(0.03f, InternalTimeMode.Slowed),
                         new InternalSessionTime(0.5f, InternalTimeMode.Slowed), new InternalSessionTime(0f, InternalTimeMode.TurnBased),
                         new InternalSessionTime(1f, InternalTimeMode.TurnBased), new InternalSessionTime(float.Epsilon, InternalTimeMode.Slowed)
                     })
            {
                SessionTimeWire.Write(buffer, time);
                That(SessionTimeWire.TryRead((byte[])buffer.Clone(), out var read) && read.Equals(time) &&
                     BitConverter.SingleToInt32Bits(read.Scale) == BitConverter.SingleToInt32Bits(time.Scale), time + " round-trips exactly");
            }

            SessionTimeWire.Write(buffer, new InternalSessionTime(0.5f, InternalTimeMode.Slowed));
            That(buffer.SequenceEqual(new byte[] { 1, 1, 0x00, 0x00, 0x00, 0x3F, 1 }), "seven bytes: version, type, the scale in little-endian order, the mode");
            Throws<ArgumentException>(() => SessionTimeWire.Write(new byte[6], InternalSessionTime.Realtime), "a short buffer is refused");
            Throws<ArgumentNullException>(() => SessionTimeWire.Write(null!, InternalSessionTime.Realtime), "and no buffer");
        }

        private static void MessagesOutsideTheBoundsAreRefused()
        {
            var good = new byte[SessionTimeWire.Length];
            SessionTimeWire.Write(good, new InternalSessionTime(0.5f, InternalTimeMode.Slowed));
            That(!SessionTimeWire.TryRead(null, out _), "no message");
            for (var length = 0; length <= 12; length++)
            {
                if (length == SessionTimeWire.Length) continue;
                var cut = new byte[length];
                Array.Copy(good, cut, Math.Min(length, good.Length));
                That(!SessionTimeWire.TryRead(cut, out var time) && time.Equals(InternalSessionTime.Realtime), "a message of " + length + " bytes is refused");
            }

            That(!SessionTimeWire.TryRead(With(good, 0, 2), out _) && !SessionTimeWire.TryRead(With(good, 0, 0), out _), "another version is refused");
            That(!SessionTimeWire.TryRead(With(good, 1, 2), out _) && !SessionTimeWire.TryRead(With(good, 1, 0), out _), "another type is refused");
            foreach (var scale in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -0.5f, 1.5f, 1e30f, -float.Epsilon })
            {
                That(!SessionTimeWire.TryRead(Scale(good, scale, InternalTimeMode.TurnBased), out _), "a host's scale of " + scale + " is refused");
            }

            That(!SessionTimeWire.TryRead(With(good, 6, 4), out _) && !SessionTimeWire.TryRead(With(good, 6, 255), out _), "an undefined mode is refused");
            That(!SessionTimeWire.TryRead(Scale(good, 0.5f, InternalTimeMode.Realtime), out _) && !SessionTimeWire.TryRead(Scale(good, 0.5f, InternalTimeMode.Paused), out _) &&
                 !SessionTimeWire.TryRead(Scale(good, 1f, InternalTimeMode.Slowed), out _), "and so is a mode that disagrees with its scale");
            var quietNaN = BitConverter.Int32BitsToSingle(0x7FC00001);
            That(!SessionTimeWire.TryRead(Scale(good, quietNaN, InternalTimeMode.Slowed), out _), "whatever payload a NaN carries");
        }

        private static byte[] With(byte[] source, int index, byte value)
        {
            var copy = (byte[])source.Clone();
            copy[index] = value;
            return copy;
        }

        private static byte[] Scale(byte[] source, float scale, InternalTimeMode mode)
        {
            var copy = (byte[])source.Clone();
            var bits = BitConverter.SingleToInt32Bits(scale);
            copy[2] = (byte)bits;
            copy[3] = (byte)(bits >> 8);
            copy[4] = (byte)(bits >> 16);
            copy[5] = (byte)(bits >> 24);
            copy[6] = (byte)mode;
            return copy;
        }
    }
}
