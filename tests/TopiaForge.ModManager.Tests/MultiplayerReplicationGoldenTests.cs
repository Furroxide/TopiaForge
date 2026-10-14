using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using TopiaForge.ModManager.Core.Multiplayer;
using static TopiaForge.ModManager.Tests.MultiplayerModelAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Replays tests/fixtures/multiplayer/replication, which generate.py computes from the written rules without the
    /// C# code, so both implementations must agree bit for bit.
    /// </summary>
    internal static class MultiplayerReplicationGoldenTests
    {
        public static void Run()
        {
            var quantization = Quantization();
            var keys = SceneObjectKeys();
            var scheduler = Scheduler();
            var buckets = TokenBuckets();
            Console.WriteLine(
                "MultiplayerReplicationGoldenTests passed (" + quantization + " quantization, " + keys + " scene key, " +
                scheduler + " scheduler and " + buckets + " token bucket vectors).");
        }

        private static int Quantization()
        {
            var document = LoadFixture("quantization.json");
            var count = 0;
            foreach (var node in document["positions"]!.AsArray())
            {
                var value = Input(node!["input"]!);
                var units = NetQuantization.QuantizePositionAxis(value);
                Equal(node["units"]!.GetValue<int>(), units, "position units of " + HexOf(value));
                Equal(node["decoded"]!.GetValue<string>(), HexOf(NetQuantization.DequantizePositionAxis(units)), "decoded position");
                count++;
            }

            foreach (var node in document["velocities"]!.AsArray())
            {
                var value = Input(node!["input"]!);
                var units = NetQuantization.QuantizeVelocityAxis(value);
                Equal(node["units"]!.GetValue<int>(), units, "velocity units of " + HexOf(value));
                Equal(node["decoded"]!.GetValue<string>(), HexOf(NetQuantization.DequantizeVelocityAxis(units)), "decoded velocity");
                count++;
            }

            foreach (var node in document["rotations"]!.AsArray())
            {
                var input = node!["input"]!.AsArray();
                var rotation = new NetQuaternion(Input(input[0]!), Input(input[1]!), Input(input[2]!), Input(input[3]!));
                var packed = NetQuantization.QuantizeRotation(rotation);
                Equal(node["bits"]!.GetValue<string>(), packed.ToString("x8", CultureInfo.InvariantCulture), "rotation bits of " + rotation);
                var decoded = NetQuantization.DequantizeRotation(packed);
                var expected = node["decoded"]!.AsArray();
                Equal(expected[0]!.GetValue<string>(), HexOf(decoded.X), "decoded x of " + rotation);
                Equal(expected[1]!.GetValue<string>(), HexOf(decoded.Y), "decoded y of " + rotation);
                Equal(expected[2]!.GetValue<string>(), HexOf(decoded.Z), "decoded z of " + rotation);
                Equal(expected[3]!.GetValue<string>(), HexOf(decoded.W), "decoded w of " + rotation);
                count++;
            }

            foreach (var node in document["transforms"]!.AsArray())
            {
                var position = node!["position"]!.AsArray();
                var rotation = node["rotation"]!.AsArray();
                var velocityNode = node["velocity"];
                var quantizedPosition = NetQuantization.QuantizePosition(new NetVector3(Input(position[0]!), Input(position[1]!), Input(position[2]!)));
                var packed = NetQuantization.QuantizeRotation(new NetQuaternion(Input(rotation[0]!), Input(rotation[1]!), Input(rotation[2]!), Input(rotation[3]!)));
                NetQuantizedVelocity? velocity = null;
                if (velocityNode != null)
                {
                    var axes = velocityNode.AsArray();
                    velocity = NetQuantization.QuantizeVelocity(new NetVector3(Input(axes[0]!), Input(axes[1]!), Input(axes[2]!)));
                }

                var writer = new NetBitWriter(32);
                NetQuantization.WriteTransform(writer, quantizedPosition, packed, velocity);
                var hex = node["hex"]!.GetValue<string>();
                Equal(hex, HexOf(writer.ToArray()), "transform bytes");
                var reader = new NetBitReader(new ArraySegment<byte>(Convert.FromHexString(hex)));
                True(NetQuantization.TryReadTransform(reader, out var readPosition, out var readRotation, out var readVelocity), "read the golden bytes");
                True(readPosition.Equals(quantizedPosition) && readRotation == packed && Nullable.Equals(readVelocity, velocity), "golden bytes decode");
                True(reader.IsAtCanonicalEnd, "golden bytes end canonically");
                count++;
            }

            return count;
        }

        private static int SceneObjectKeys()
        {
            var document = LoadFixture("scene-object-key.json");
            var count = 0;
            foreach (var node in document["fnv1a64"]!.AsArray())
            {
                Equal(node!["hash"]!.GetValue<string>(), NetHash.Fnv1a64(node["utf8"]!.GetValue<string>()).ToString("x16", CultureInfo.InvariantCulture), "FNV-1a vector");
                count++;
            }

            foreach (var node in document["cases"]!.AsArray())
            {
                var path = new List<SceneObjectPathSegment>();
                foreach (var segment in node!["path"]!.AsArray())
                {
                    path.Add(new SceneObjectPathSegment(segment!["name"]!.GetValue<string>(), segment["siblingIndex"]!.GetValue<int>()));
                }

                var descriptor = new SceneObjectDescriptor(node["scene"]!.GetValue<string>(), path, node["component"]!.GetValue<string>());
                Equal(node["preimage"]!.GetValue<string>(), SceneObjectKey.FormatPreimage(descriptor), "scene key preimage");
                Equal(node["key"]!.GetValue<string>(), SceneObjectKey.Compute(descriptor).ToString(), "scene key");
                count++;
            }

            return count;
        }

        private static int Scheduler()
        {
            var document = LoadFixture("scheduler.json");
            Equal(ReplicationScheduler.RestConfirmationSends, document["restConfirmationSends"]!.GetValue<int>(), "rest confirmations");
            var fields = document["candidateFields"]!.AsArray();
            True(fields.Count == 4 && fields[0]!.GetValue<string>() == "id" && fields[1]!.GetValue<string>() == "priority" &&
                 fields[2]!.GetValue<string>() == "sizeBytes" && fields[3]!.GetValue<string>() == "atRest", "candidate field order");
            var count = 0;
            foreach (var scenario in document["scenarios"]!.AsArray())
            {
                var name = scenario!["name"]!.GetValue<string>();
                var scheduler = new ReplicationScheduler(scenario["budgetBytesPerTick"]!.GetValue<int>());
                var selected = new List<NetworkEntityId>();
                var tickNumber = 0;
                foreach (var tick in scenario["ticks"]!.AsArray())
                {
                    tickNumber++;
                    var candidates = new List<ReplicationCandidate>();
                    foreach (var node in tick!["candidates"]!.AsArray())
                    {
                        var candidate = node!.AsArray();
                        candidates.Add(new ReplicationCandidate(
                            new NetworkEntityId(candidate[0]!.GetValue<uint>()),
                            candidate[1]!.GetValue<int>(),
                            candidate[2]!.GetValue<int>(),
                            candidate[3]!.GetValue<bool>()));
                    }

                    scheduler.Schedule(candidates, selected);
                    var expected = new List<NetworkEntityId>();
                    foreach (var id in tick["selected"]!.AsArray()) expected.Add(new NetworkEntityId(id!.GetValue<uint>()));
                    Sequence(expected, selected, name + ", tick " + tickNumber);
                    Equal(tick["creditAfter"]!.GetValue<long>(), scheduler.Credit, name + ", credit after tick " + tickNumber);
                    count++;
                }
            }

            return count;
        }

        private static int TokenBuckets()
        {
            var document = LoadFixture("token-bucket.json");
            var count = 0;
            foreach (var testCase in document["cases"]!.AsArray())
            {
                var name = testCase!["name"]!.GetValue<string>();
                var bucket = new TokenBucket(
                    testCase["capacity"]!.GetValue<int>(),
                    testCase["tokensPerSecond"]!.GetValue<int>(),
                    testCase["startMicros"]!.GetValue<long>());
                foreach (var step in testCase["steps"]!.AsArray())
                {
                    var at = step!["atMicros"]!.GetValue<long>();
                    Equal(step["allowed"]!.GetValue<bool>(), bucket.TryTake(at, step["cost"]!.GetValue<int>()), name + " at " + at);
                    count++;
                }
            }

            return count;
        }

        private static float Input(JsonNode node) => FloatFromHex(node["bits"]!.GetValue<string>());
    }
}
