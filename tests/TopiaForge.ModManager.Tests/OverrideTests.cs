using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.RobotKit;
using TopiaForge.Zombies;

namespace TopiaForge.ModManager.Tests
{
    // Unit tests for the Unity-free pieces of the OVERRIDE feature: the RoboAPI wire protocol (+ MiniJson), and the
    // deterministic "robot psychology" decision resolver. No UnityEngine and no network — these compile straight
    // into the net8.0 test assembly via the csproj Compile includes.
    internal static class OverrideTests
    {
        public static void Run()
        {
            TestMiniJsonRoundTrip();
            TestMiniJsonRejectsMalformed();
            TestCheck3BodyShape();
            TestCheck3ResponseParsing();
            TestTokenParsing();
            TestValueClamping();
            TestDecisionDeterminism();
            TestDecisionExtremes();
            TestMindSeeding();
            Console.WriteLine("All OVERRIDE tests passed.");
        }

        private static void TestMiniJsonRoundTrip()
        {
            var graph = new Dictionary<string, object?>
            {
                ["s"] = "he said \"hi\"\nline",
                ["n"] = 3.5,
                ["b"] = true,
                ["nul"] = null,
                ["arr"] = new List<object?> { "a", 1.0, false },
                ["obj"] = new Dictionary<string, object?> { ["k"] = "v" },
            };

            var json = MiniJson.Serialize(graph);
            var back = MiniJson.Deserialize(json) as Dictionary<string, object?>;
            Assert(back != null, "round-trip should deserialize to an object");
            Assert((string?)back!["s"] == "he said \"hi\"\nline", "string escapes should round-trip");
            Assert((double)back["n"]! == 3.5, "number should round-trip");
            Assert((bool)back["b"]! == true, "bool should round-trip");
            Assert(back["nul"] == null, "null should round-trip");
            var arr = back["arr"] as List<object?>;
            Assert(arr != null && arr.Count == 3 && (string?)arr[0] == "a", "array should round-trip");
            var obj = back["obj"] as Dictionary<string, object?>;
            Assert(obj != null && (string?)obj!["k"] == "v", "nested object should round-trip");
        }

        private static void TestMiniJsonRejectsMalformed()
        {
            Assert(MiniJson.Deserialize("{bad") == null, "unterminated object should be null");
            Assert(MiniJson.Deserialize("") == null, "empty string should be null");
            Assert(MiniJson.Deserialize("{\"a\":1} trailing") == null, "trailing junk should be null");
            Assert(MiniJson.Deserialize("[1,2,]") == null, "trailing comma should be null");
            // A valid document still parses.
            Assert(MiniJson.Deserialize("{\"a\":[1,2,3]}") is Dictionary<string, object?>, "valid doc should parse");
        }

        private static void TestCheck3BodyShape()
        {
            var request = new BrainQueryRequest(
                "decide",
                new[]
                {
                    new BrainOutputField("action", "the reaction", BrainFieldType.String, new[] { "comply", "resist" }),
                    new BrainOutputField("bark", "a line", BrainFieldType.String),
                },
                usage: "zombies-override",
                temperature: 0.5f);

            var body = MiniJson.Deserialize(RoboApiProtocol.BuildCheck3Body(request)) as Dictionary<string, object?>;
            Assert(body != null, "body should be a JSON object");
            Assert((string?)body!["model_name"] == "llama-3.3-70b", "model_name must be the served model");
            Assert((string?)body["prompt"] == "decide", "prompt should be carried");
            Assert((string?)body["usage"] == "zombies-override", "usage should be carried");
            Assert((double)body["temperature"]! == 0.5, "temperature should be carried");
            Assert((bool)body["use_reasoning"]! == false, "use_reasoning should default false");

            var outputs = body["outputs"] as List<object?>;
            Assert(outputs != null && outputs.Count == 2, "outputs should have two fields");
            var first = outputs![0] as Dictionary<string, object?>;
            Assert(first != null && (string?)first!["name"] == "action" && (string?)first["type"] == "string", "first output should be the action string field");
            var allowed = first!["allowed_strings"] as List<object?>;
            Assert(allowed != null && allowed.Count == 2 && (string?)allowed[0] == "comply", "allowed_strings should be serialized");
            var second = outputs[1] as Dictionary<string, object?>;
            Assert(second != null && !second!.ContainsKey("allowed_strings"), "a free-text field should omit allowed_strings");
        }

        private static void TestCheck3ResponseParsing()
        {
            var ok = RoboApiProtocol.ParseCheck3Response("{\"values\":{\"action\":\"comply\",\"bark\":\"fine.\",\"success\":true}}");
            Assert(ok.Succeeded, "a values response should succeed");
            Assert(ok.Value!.TryGet("action", out var action) && action == "comply", "action should parse");
            Assert(ok.Value.TryGet("bark", out var bark) && bark == "fine.", "bark should parse");

            // The model's own `success` self-grade is noisy (a hostile robot that correctly picks a refusal
            // self-grades success:false) and must NOT suppress a well-formed answer — otherwise the reply/decision get
            // discarded (the JACK-IN "static on the channel" bug). A non-empty values object succeeds regardless, and
            // the self-grade is preserved as a readable value.
            var selfGradedFail = RoboApiProtocol.ParseCheck3Response("{\"values\":{\"action\":\"resist\",\"reply\":\"never\",\"success\":false}}");
            Assert(selfGradedFail.Succeeded, "a well-formed values response succeeds regardless of the model's success self-grade");
            Assert(selfGradedFail.Value!.TryGet("action", out var refusal) && refusal == "resist", "answer fields stay readable even when success:false");
            Assert(selfGradedFail.Value.TryGet("success", out var grade) && grade == "false", "the success self-grade is preserved as a value");

            // An empty values object carries no answer, so it does not succeed (the graceful degrade path).
            var empty = RoboApiProtocol.ParseCheck3Response("{\"values\":{}}");
            Assert(!empty.Succeeded && empty.ErrorCode == ModErrorCode.External,
                "an empty values object has no answer and returns a stable external failure");

            var numberCoerced = RoboApiProtocol.ParseCheck3Response("{\"values\":{\"n\":3,\"f\":2.5}}");
            Assert(numberCoerced.Value!.TryGet("n", out var n) && n == "3", "integer value should stringify without a decimal");
            Assert(numberCoerced.Value.TryGet("f", out var f) && f == "2.5", "float value should stringify");

            var error = RoboApiProtocol.ParseCheck3Response("{\"error\":\"/agent/check3 error: bad\"}");
            Assert(!error.Succeeded && error.ErrorCode == ModErrorCode.External && error.ErrorMessage.Contains("bad"),
                "error envelope should surface a stable external failure");

            var malformed = RoboApiProtocol.ParseCheck3Response("not json");
            Assert(!malformed.Succeeded, "malformed response should not succeed");
        }

        private static void TestTokenParsing()
        {
            Assert(RoboApiProtocol.ParseAgentToken("{\"agent_token\":\"abc.def\"}") == "abc.def", "agent_token should be extracted");
            Assert(RoboApiProtocol.ParseAgentToken("{\"other\":\"x\"}") == null, "missing agent_token should be null");
            Assert(RoboApiProtocol.ParseAgentToken("{\"agent_token\":\"\"}") == null, "blank agent_token should be null");
            Assert(RoboApiProtocol.ParseAgentToken("garbage") == null, "malformed token file should be null");
        }

        private static void TestValueClamping()
        {
            var big = new string('x', 50);
            var json = "{\"values\":{\"bark\":\"" + big + "\"}}";
            var result = RoboApiProtocol.ParseCheck3Response(json, 10);
            Assert(result.Value!.TryGet("bark", out var bark) && bark.Length == 10, "returned values should be clamped to the cap");
        }

        private static void TestDecisionDeterminism()
        {
            var mind = new RobotMind(0.5f, 0.4f, 0.3f, 0.05f);
            var a = OverrideDecision.Resolve(OverrideCommand.JoinMe, mind, 0.4f, 1f);
            var b = OverrideDecision.Resolve(OverrideCommand.JoinMe, mind, 0.4f, 1f);
            Assert(a.Outcome == b.Outcome && a.Enraged == b.Enraged, "the same mind+command must resolve identically (deterministic)");
        }

        private static void TestDecisionExtremes()
        {
            // A wide-open mind against a defenceless target obeys.
            var compliant = new RobotMind(1f, 0f, 0.5f, 0f);
            Assert(OverrideDecision.Resolve(OverrideCommand.JoinMe, compliant, 0f, 1f).Outcome == HijackOutcome.Convert,
                "a highly suggestible, undefended robot should convert on JoinMe");
            Assert(OverrideDecision.Resolve(OverrideCommand.StandDown, compliant, 0f, 1f).Outcome == HijackOutcome.Freeze,
                "StandDown's target outcome is a pacifying freeze");

            // A loyal, resistant Brute refuses JoinMe and enrages.
            var brute = new RobotMind(0.1f, 1f, 0.2f, 0f);
            var resolution = OverrideDecision.Resolve(OverrideCommand.JoinMe, brute, 0.7f, 1f);
            Assert(resolution.Outcome == HijackOutcome.Resist && resolution.Enraged, "a loyal Brute should resist-and-enrage on JoinMe");

            // The same Brute does not enrage on a non-JoinMe failure.
            var freezeFail = OverrideDecision.Resolve(OverrideCommand.Freeze, brute, 0.95f, 2f);
            Assert(freezeFail.Outcome == HijackOutcome.Resist && !freezeFail.Enraged, "a failed Freeze should not enrage");
        }

        private static void TestMindSeeding()
        {
            var tuning = new OverrideTuning(0.2f, 0.6f, 0.1f, 0.5f, 0.15f, 0.06f, 0.1f, 1f);
            var first = RobotMind.Seed(new Random(123), 3, tuning);
            var second = RobotMind.Seed(new Random(123), 3, tuning);
            Assert(first.Suggestibility == second.Suggestibility && first.Loyalty == second.Loyalty && first.Bias == second.Bias,
                "the same seed must produce the same mind");
            Assert(first.Suggestibility >= 0.2f && first.Suggestibility <= 0.6f, "suggestibility should stay within its configured range");
            Assert(first.Loyalty >= 0.1f && first.Loyalty <= 0.5f, "loyalty should stay within its configured range");
            Assert(Math.Abs(first.Bias) <= 0.1f, "bias should stay within its amplitude");

            // Corruption climbs with the wave and is clamped to 1.
            var early = RobotMind.Seed(new Random(1), 1, tuning);
            var late = RobotMind.Seed(new Random(1), 12, tuning);
            Assert(late.Corruption > early.Corruption, "corruption should rise with the wave");
            Assert(late.Corruption <= 1f, "corruption should clamp at 1");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
