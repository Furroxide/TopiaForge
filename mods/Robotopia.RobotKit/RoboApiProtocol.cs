using System.Collections.Generic;
using System.Globalization;
using Robotopia.Mods;

namespace Robotopia.RobotKit
{
    // Pure (Unity-free, network-free) translation between the SDK's BrainQueryRequest/Result and the RoboAPI
    // /agent/check3 wire format, plus reading the cached per-user token. Kept separate from RoboApiClient (which owns
    // the HttpClient + file IO + Unity paths) so the wire schema lives in exactly one unit-testable place — RoboAPI is
    // unversioned for non-game clients, so isolating the shape means schema drift degrades here, not across the mod.
    //
    // Wire schema (verified from observed RoboAPI behavior):
    //   request  { prompt, model_name, usage, success_description, use_reasoning, outputs:[{name,description,type,
    //              allowed_strings?}], temperature } → POST /agent/check3 (Bearer agent_token + Session-Id)
    //   response { values: { <field>: <scalar>, ..., success: bool } }  (or { error|message: "..." } on failure)
    internal static class RoboApiProtocol
    {
        // The only model the gateway serves for a player token, based on observed RoboAPI behavior.
        public const string DefaultModel = "llama-3.3-70b";

        // Hard ceiling on any returned string, so a runaway generation can never overflow a HUD label or balloon
        // memory (the backend enforces no output length cap).
        public const int DefaultMaxValueChars = 600;

        // Build the /agent/check3 request body JSON from a structured brain query.
        public static string BuildCheck3Body(BrainQueryRequest request)
        {
            var outputs = new List<object?>();
            foreach (var field in request.Outputs)
            {
                var entry = new Dictionary<string, object?>
                {
                    ["name"] = field.Name,
                    ["description"] = field.Description,
                    ["type"] = TypeToken(field.Type),
                };

                if (field.AllowedStrings != null && field.AllowedStrings.Count > 0)
                {
                    var allowed = new List<object?>(field.AllowedStrings.Count);
                    foreach (var s in field.AllowedStrings)
                    {
                        allowed.Add(s);
                    }

                    entry["allowed_strings"] = allowed;
                }

                outputs.Add(entry);
            }

            var body = new Dictionary<string, object?>
            {
                ["prompt"] = request.Prompt,
                ["model_name"] = DefaultModel,
                ["usage"] = string.IsNullOrEmpty(request.Usage) ? "robot-brain-query" : request.Usage,
                ["success_description"] = request.SuccessDescription ?? string.Empty,
                ["use_reasoning"] = request.UseReasoning,
                ["outputs"] = outputs,
                ["temperature"] = ClampTemperature(request.Temperature),
            };

            return MiniJson.Serialize(body);
        }

        // Parse a /agent/check3 response body into a BrainQueryResult. `available` is the transport verdict (did the
        // call complete with a body at all); content success is decided here from the parsed `values`.
        public static BrainQueryResult ParseCheck3Response(string? body, int maxValueChars = DefaultMaxValueChars)
        {
            if (MiniJson.Deserialize(body) is not Dictionary<string, object?> root)
            {
                return new BrainQueryResult(true, false, EmptyValues, "malformed response");
            }

            // App- or gateway-level error envelopes.
            if (root.TryGetValue("error", out var error) && error != null)
            {
                return new BrainQueryResult(true, false, EmptyValues, Stringify(error, maxValueChars));
            }

            if (!root.TryGetValue("values", out var valuesObj) || valuesObj is not Dictionary<string, object?> values)
            {
                if (root.TryGetValue("message", out var message) && message != null)
                {
                    return new BrainQueryResult(true, false, EmptyValues, Stringify(message, maxValueChars));
                }

                return new BrainQueryResult(true, false, EmptyValues, "no values in response");
            }

            var map = new Dictionary<string, string>(values.Count);
            foreach (var pair in values)
            {
                map[pair.Key] = Stringify(pair.Value, maxValueChars);
            }

            // A well-formed, non-empty `values` object IS the brain's answer, so the query Succeeded. Do NOT gate this
            // on the model-authored `success` field that also rides in `values`: that is the model self-grading whether
            // it met success_description, and it is noisy/false-positive-prone. Concretely (verified live): a hostile
            // robot that correctly picks REFUSE still returns a real reply + decision but self-grades success:false —
            // treating that as a failure made the consumer discard a perfectly good answer, so the JACK-IN channel
            // showed only "…static on the channel…" every turn and the verb was unwinnable. Genuine failures arrive as
            // the error/message envelopes handled above; the self-grade is preserved in the map for any consumer that
            // deliberately wants it (read Values["success"]).
            var hasAnswer = map.Count > 0;
            return new BrainQueryResult(true, hasAnswer, map, hasAnswer ? null : "empty response");
        }

        // Parse a /agent/stt response into the transcript string. The verified base-game DTO is
        // SpeechToTextResponse.text, so `text` is the primary field; a couple of lenient fallbacks (transcript /
        // values.text) cover backend drift. Returns null when no usable transcript is present (including error
        // envelopes), so the caller treats it as "nothing said".
        public static string? ParseSttResponse(string? body, int maxValueChars = DefaultMaxValueChars)
        {
            if (MiniJson.Deserialize(body) is not Dictionary<string, object?> root)
            {
                return null;
            }

            if (root.ContainsKey("error") || root.ContainsKey("message"))
            {
                return null; // gateway/app error envelope — no transcript
            }

            if (TryReadString(root, "text", maxValueChars, out var text) ||
                TryReadString(root, "transcript", maxValueChars, out text))
            {
                return text;
            }

            if (root.TryGetValue("values", out var valuesObj) &&
                valuesObj is Dictionary<string, object?> values &&
                TryReadString(values, "text", maxValueChars, out text))
            {
                return text;
            }

            return null;
        }

        private static bool TryReadString(Dictionary<string, object?> map, string key, int maxChars, out string value)
        {
            if (map.TryGetValue(key, out var raw) && raw is string s && !string.IsNullOrWhiteSpace(s))
            {
                value = Stringify(s, maxChars);
                return true;
            }

            value = string.Empty;
            return false;
        }

        // Extract the agent_token JWT from the contents of robo_token.json. Returns null when absent/malformed.
        public static string? ParseAgentToken(string? roboTokenJson)
        {
            if (MiniJson.Deserialize(roboTokenJson) is Dictionary<string, object?> root &&
                root.TryGetValue("agent_token", out var token) &&
                token is string s &&
                !string.IsNullOrWhiteSpace(s))
            {
                return s;
            }

            return null;
        }

        private static readonly IReadOnlyDictionary<string, string> EmptyValues = new Dictionary<string, string>();

        private static string TypeToken(BrainFieldType type)
        {
            switch (type)
            {
                case BrainFieldType.Number:
                    return "number";
                case BrainFieldType.Boolean:
                    return "boolean";
                default:
                    return "string";
            }
        }

        private static float ClampTemperature(float temperature)
        {
            if (temperature < 0f)
            {
                return 0f;
            }

            return temperature > 2f ? 2f : temperature;
        }

        private static string Stringify(object? value, int maxChars)
        {
            string text;
            switch (value)
            {
                case null:
                    text = string.Empty;
                    break;
                case string s:
                    text = s;
                    break;
                case bool b:
                    text = b ? "true" : "false";
                    break;
                case double d:
                    // Render integers without a trailing ".0" so an enum-as-number reads cleanly.
                    text = d == System.Math.Floor(d) && !double.IsInfinity(d)
                        ? ((long)d).ToString(CultureInfo.InvariantCulture)
                        : d.ToString("R", CultureInfo.InvariantCulture);
                    break;
                default:
                    text = value.ToString() ?? string.Empty;
                    break;
            }

            if (maxChars > 0 && text.Length > maxChars)
            {
                text = text.Substring(0, maxChars);
            }

            return text;
        }
    }
}
