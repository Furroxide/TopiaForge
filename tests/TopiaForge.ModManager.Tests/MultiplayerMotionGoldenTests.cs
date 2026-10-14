using System;
using System.Text.Json.Nodes;
using TopiaForge.ModManager.Core.Multiplayer;
using static TopiaForge.ModManager.Tests.MultiplayerModelAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Replays ownership.json and interpolation.json from tests/fixtures/multiplayer/replication, which generate.py
    /// computes from the written rules, call by call: every outcome, credit, cooldown, render time and delay must match.
    /// </summary>
    internal static class MultiplayerMotionGoldenTests
    {
        public static void Run()
        {
            var ownership = Ownership();
            var interpolation = Interpolation();
            Console.WriteLine(
                "MultiplayerMotionGoldenTests passed (" + ownership + " ownership and " + interpolation + " interpolation steps).");
        }

        private static int Ownership()
        {
            var document = LoadFixture("ownership.json");
            var policy = document["policy"]!;
            var defaults = OwnershipPolicy.Default;
            var reach = (long)policy["holdRangeUnits"]!.GetValue<int>();
            Equal(reach * reach, defaults.MaximumHoldRangeSquaredUnits, "the vectors' hold range");
            Equal(policy["burstCreditUnits"]!.GetValue<long>(), defaults.BurstCreditUnits, "the vectors' burst credit");
            Equal(policy["firstSampleTimeoutMicros"]!.GetValue<long>(), defaults.FirstSampleTimeoutMicros, "the vectors' first-sample deadline");
            Equal(policy["yieldCooldownMicros"]!.GetValue<long>(), defaults.YieldCooldownMicros, "the vectors' cooldown");
            Equal(policy["silenceTimeoutMicros"]!.GetValue<long>(), defaults.SilenceTimeoutMicros, "the vectors' silence timeout");
            Equal(policy["releaseWindowMicros"]!.GetValue<long>(), defaults.ReleaseWindowMicros, "the vectors' release window");

            var count = 0;
            foreach (var scenario in document["scenarios"]!.AsArray())
            {
                var name = scenario!["name"]!.GetValue<string>();
                var host = new OwnershipStateMachine(initialEpoch: scenario["initialEpoch"]!.GetValue<uint>());
                var index = 0;
                foreach (var step in scenario["steps"]!.AsArray())
                {
                    var call = step!["call"]!.AsArray();
                    var where = name + ", step " + ++index + " (" + call.ToJsonString() + ")";
                    var accepted = default(MotionHandback);
                    OwnershipOutcome outcome;
                    switch (call[0]!.GetValue<string>())
                    {
                        case "request":
                            outcome = host.Request(Int(call[1]), UInt(call[2]), Position(call[3]), Position(call[4]), Long(call[5]));
                            break;
                        case "stream":
                            outcome = host.Stream(Int(call[1]), UInt(call[2]), Position(call[3]), Position(call[4]), Long(call[5]));
                            break;
                        case "release":
                            outcome = host.BeginRelease(Int(call[1]), UInt(call[2]), Position(call[3]), Long(call[4]));
                            break;
                        case "handback":
                            var handback = new MotionHandback(Position(call[3]), Vector(call[4]), Vector(call[5]));
                            outcome = host.Handback(Int(call[1]), UInt(call[2]), handback, Position(call[6]), Long(call[7]), out accepted);
                            break;
                        case "disconnect":
                            outcome = host.Disconnect(Int(call[1]));
                            break;
                        case "reclaim":
                            outcome = host.Reclaim();
                            break;
                        case "poll":
                            outcome = host.Poll(Long(call[1]));
                            break;
                        default:
                            throw new InvalidOperationException(where + ": unknown call");
                    }

                    var expected = step["outcome"]!.AsArray();
                    Equal(expected[0]!.GetValue<string>(), outcome.Result.ToString(), where + ": result");
                    Equal(expected[1]!.GetValue<string>(), outcome.ReclaimReason.ToString(), where + ": reclaim reason");
                    Equal(expected[2]!.GetValue<string>(), outcome.State.ToString(), where + ": state");
                    Equal(expected[3]!.GetValue<int>(), outcome.Owner.Participant, where + ": owner");
                    Equal(expected[4]!.GetValue<uint>(), outcome.Epoch, where + ": epoch");
                    Equal(expected[5]!.GetValue<bool>(), outcome.EpochChanged, where + ": epoch changed");
                    Equal(expected[6]!.GetValue<long>(), host.DistanceCreditUnits, where + ": credit");
                    Equal(expected[7]!.GetValue<int>(), host.YieldingParticipant, where + ": yielding participant");
                    Equal(expected[8]!.GetValue<long>(), host.YieldUntilMicros, where + ": yield deadline");
                    Equal(expected[9]!.GetValue<bool>(), host.AwaitingFirstSample, where + ": first sample owed");
                    Equal(expected[10]!.GetValue<bool>(), host.Contested, where + ": hold contested");
                    if (step["accepted"] is JsonArray velocities)
                    {
                        Equal(VectorBits(velocities[0]!), VectorBits(accepted.LinearVelocity), where + ": accepted linear velocity");
                        Equal(VectorBits(velocities[1]!), VectorBits(accepted.AngularVelocity), where + ": accepted angular velocity");
                    }

                    count++;
                }
            }

            return count;
        }

        private static int Interpolation()
        {
            var document = LoadFixture("interpolation.json");
            Equal(SnapshotInterpolationBuffer.Capacity, document["capacity"]!.GetValue<int>(), "the vectors' capacity");
            var count = 0;
            foreach (var scenario in document["scenarios"]!.AsArray())
            {
                var name = scenario!["name"]!.GetValue<string>();
                var buffer = new SnapshotInterpolationBuffer(scenario["nominalIntervalMicros"]!.GetValue<long>());
                var index = 0;
                foreach (var node in scenario["steps"]!.AsArray())
                {
                    var step = node!.AsArray();
                    var where = name + ", step " + ++index;
                    if (step[0]!.GetValue<string>() == "add")
                    {
                        var server = Long(step[1]);
                        var position = new NetVector3(FloatFromHex(step[3]!.GetValue<string>()), 0f, 0f);
                        var sample = step[4] == null
                            ? new NetTransformSample(server, position, NetQuaternion.Identity)
                            : new NetTransformSample(server, position, NetQuaternion.Identity, new NetVector3(FloatFromHex(step[4]!.GetValue<string>()), 0f, 0f));
                        Equal(step[5]!.GetValue<string>(), buffer.Add(sample, Long(step[2])).ToString(), where + ": add at " + Long(step[2]));
                    }
                    else
                    {
                        var now = Long(step[1]);
                        var sampled = buffer.TrySample(now, out var result);
                        var mode = step[3]!.GetValue<string>();
                        Equal(mode != "Empty", sampled, where + ": a frame at " + now);
                        Equal(mode, result.Mode.ToString(), where + ": mode at " + now);
                        Equal(Long(step[2]), result.RenderTimeMicros, where + ": render time at " + now);
                        Equal(step[4]!.GetValue<string>(), HexOf(result.Position.X), where + ": x at " + now);
                        Equal(Long(step[5]), buffer.TargetDelayMicros, where + ": target delay at " + now);
                        Equal(Long(step[6]), buffer.PlaybackOffsetMicros, where + ": playback offset at " + now);
                    }

                    count++;
                }

                var final = scenario["final"]!;
                Equal(Long(final["offset"]), buffer.OffsetMicros, name + ": final offset");
                Equal(Long(final["jitter"]), buffer.JitterMicros, name + ": final jitter");
                Equal(Long(final["interval"]), buffer.IntervalMicros, name + ": final interval");
                Equal(Long(final["largestGap"]), buffer.LargestGapMicros, name + ": final largest gap");
            }

            return count;
        }

        private static int Int(JsonNode? node) => node!.GetValue<int>();

        private static uint UInt(JsonNode? node) => node!.GetValue<uint>();

        private static long Long(JsonNode? node) => node!.GetValue<long>();

        private static NetQuantizedPosition Position(JsonNode? node)
        {
            var axes = node!.AsArray();
            return new NetQuantizedPosition(Int(axes[0]), Int(axes[1]), Int(axes[2]));
        }

        private static NetVector3 Vector(JsonNode? node)
        {
            var axes = node!.AsArray();
            return new NetVector3(
                FloatFromHex(axes[0]!.GetValue<string>()),
                FloatFromHex(axes[1]!.GetValue<string>()),
                FloatFromHex(axes[2]!.GetValue<string>()));
        }

        private static string VectorBits(JsonNode node)
        {
            var axes = node.AsArray();
            return string.Join(",", axes[0]!.GetValue<string>(), axes[1]!.GetValue<string>(), axes[2]!.GetValue<string>());
        }

        private static string VectorBits(NetVector3 value) => string.Join(",", HexOf(value.X), HexOf(value.Y), HexOf(value.Z));
    }
}
