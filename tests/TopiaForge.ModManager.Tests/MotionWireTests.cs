using System;
using System.Linq;
using TopiaForge.ModManager.Core.Multiplayer;
using static TopiaForge.ModManager.Tests.WorldAssert;

namespace TopiaForge.ModManager.Tests
{
    internal static class MotionWireTests
    {
        internal static void Run()
        {
            var message = new MotionMessage
            {
                Target = new MotionTarget(new NetworkEntityId(17)),
                Request = 1,
                Epoch = 7,
                Slot = 2,
                State = OwnershipState.Owned,
                Result = OwnershipResult.Granted,
                TimeMicros = 1_000_000,
                Pose = new NetPose(new NetVector3(2, 3, 4), NetQuaternion.Identity, new NetVector3(5, 0, 0)),
                AngularVelocity = new NetVector3(0, 2, 0),
                PlayerPosition = new NetVector3(1, 0, 0)
            };
            foreach (var type in new[] { MotionMessages.Grab, MotionMessages.Sample, MotionMessages.Release,
                MotionMessages.Handback, MotionMessages.Verdict, MotionMessages.State })
            {
                var writer = new WorldWireWriter();
                MotionWire.Write(writer, type, message);
                var bytes = writer.Segment.ToArray();
                var result = new MotionMessage();
                That(Read(bytes, type, result) && result.Request == 1 && result.Epoch == 7, "motion message round trips: " + type);
                That(bytes.Length < 128, "each motion message has a fixed small bound");
                for (var length = 0; length < bytes.Length; length++)
                    That(!Read(bytes.Take(length).ToArray(), type, result), "every truncated motion message is refused");
                That(!Read(bytes.Concat(new byte[] { 0 }).ToArray(), type, result), "motion messages reject trailing data");
            }

            That(!MotionRules.IsValid(new NetVector3(float.NaN, 0, 0)) && !MotionRules.IsValid(new NetVector3(4096, 0, 0)), "positions are finite and within the wire grid");
            That(!MotionRules.IsValidVelocity(new NetVector3(float.PositiveInfinity, 0, 0)), "velocity is finite");
            That(MotionRules.ClampVelocity(new NetVector3(1000, 0, 0), 64).X == 64, "extrapolation cannot amplify a forged velocity");
            var next = 0L;
            var count = 0;
            for (var now = 1L; now < 10_000_001; now += 16_000)
                if (now >= next) { count++; next = MotionRules.NextSample(next, now); }
            That(count >= 299 && count <= 301, "30 Hz keeps its phase across 16 ms frames, without catch-up bursts");
            That(MotionRules.NextSample(1, 10_000_000) > 10_000_000, "a stalled sender drops missed samples");
        }

        private static bool Read(byte[] bytes, byte type, MotionMessage message)
        {
            var reader = new WorldWireReader();
            reader.Reset(bytes);
            return reader.TryReadHeader(out var readType) && readType == type && MotionWire.Read(reader, type, message);
        }
    }
}
