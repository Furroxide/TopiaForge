using System;

namespace TopiaForge.ModManager.Core.Multiplayer
{
    /// <summary>Bounds shared by motion's sender, receiver and native pickup tracker.</summary>
    internal static class MotionRules
    {
        internal const long IntervalMicros = 1_000_000 / 30;
        internal const int MaximumLocalBodies = 4;
        internal const int MaximumTrackedBodies = 4096;
        internal const int BytesPerSecond = 8192;
        internal const int BurstBytes = 16384;
        internal const int HostSlot = 256;
        internal static readonly OwnershipPolicy Policy = new OwnershipPolicy(maximumParticipants: HostSlot);

        internal static bool IsValid(in NetVector3 position) => position.IsFinite &&
            Math.Abs(position.X) <= 4095 && Math.Abs(position.Y) <= 4095 && Math.Abs(position.Z) <= 4095;

        internal static bool IsValidVelocity(in NetVector3 velocity) => velocity.IsFinite &&
            velocity.LengthSquared <= 1_000_000;

        internal static long NextSample(long previous, long now) =>
            previous == 0 || now - previous >= IntervalMicros ? now + IntervalMicros : previous + IntervalMicros;

        internal static NetVector3 ClampVelocity(NetVector3 velocity, int maximum)
        {
            if (velocity.LengthSquared <= (double)maximum * maximum) return velocity;
            var scale = maximum / Math.Sqrt(velocity.LengthSquared);
            return new NetVector3((float)(velocity.X * scale), (float)(velocity.Y * scale), (float)(velocity.Z * scale));
        }

        internal static string Refusal(OwnershipResult result) => result switch
        {
            OwnershipResult.Denied => "Another player is holding that object. Try again after they let go.",
            OwnershipResult.OutOfRange => "That object is too far away. Move closer, then try again.",
            OwnershipResult.Yielding => "Let another player take a turn with that object, then try again.",
            OwnershipResult.Rejected => "The object moved too far or too fast, so the host took it back. Try grabbing it again.",
            OwnershipResult.Reclaimed => "The object went back to the host because its motion stopped arriving. Try grabbing it again.",
            _ => "The object could not be held in this session. Wait a moment, then try again."
        };
    }
}
