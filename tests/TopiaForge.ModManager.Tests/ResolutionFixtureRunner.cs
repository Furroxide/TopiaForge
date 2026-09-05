using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Executes one launch-resolution fixture: builds a profile from the case, resolves the request,
    /// and compares the plan or the reasons.
    /// </summary>
    /// <remarks>
    /// The resolver takes pure data, which is what makes this possible at all. The preflight it
    /// replaces read a catalog file written by a previous run, so it could not be reproduced from its
    /// inputs and could disagree with the profile actually enabled without anything noticing.
    /// </remarks>
    internal static class ResolutionFixtureRunner
    {
        public static JsonElement Snapshot(JsonElement body, bool verifyOrder = true)
        {
            if (verifyOrder)
            {
                var first = Snapshot(body, false);
                var reversed = JsonNode.Parse(body.GetRawText())!;
                foreach (var key in new[] { "packages", "disabledPackages" })
                    if (reversed["profile"]![key] is JsonArray values)
                        reversed["profile"]![key] = new JsonArray(values.Reverse().Select(item => item!.DeepClone()).ToArray());
                if (reversed["observation"]?["envelopes"] is JsonArray envelopes)
                    reversed["observation"]!["envelopes"] = new JsonArray(envelopes.Reverse().Select(item => item!.DeepClone()).ToArray());
                using var input = JsonDocument.Parse(reversed.ToJsonString());
                if (!DeclarationDigest.Equal(first, Snapshot(input.RootElement, false)))
                    throw new InvalidOperationException("Resolution changes with input package/observation order.");
                return first;
            }
            var profile = ReadProfile(body.GetProperty("profile"));
            var result = LaunchResolver.Resolve(profile, ReadRequest(body.GetProperty("request")),
                body.TryGetProperty("observation", out var raw) ? ReadObservation(profile, raw) : RuntimeObservation.None,
                body.TryGetProperty("bindings", out var bindings) ? ReadBindings(bindings) : null);
            object output;
            if (result.Plan == null)
            {
                output = new
                {
                    outcome = "reject",
                    blocks = result.Blocks.Select(block => new
                    {
                        code = Camel(block.Code.ToString()),
                        subject = block.Subject,
                        subjectVersion = block.SubjectVersion
                    }).ToArray()
                };
            }
            else
            {
                var plan = result.Plan;
                using var planDocument = JsonDocument.Parse(LaunchTransportJson.WritePlan(plan.Descriptor));
                var normalized = planDocument.RootElement.Clone();
                output = new { outcome = "accept", normalized };
                if (body.TryGetProperty("verifyImmutability", out var check) && check.GetBoolean()
                    && plan.Packages.Any(identity => profile.Packages.Any(input => ReferenceEquals(identity, input))))
                    throw new InvalidOperationException("Plan retains caller-owned package/manifest objects.");
            }
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(output));
            return document.RootElement.Clone();
        }

        private static EffectiveProfile ReadProfile(JsonElement raw) =>
            new EffectiveProfile(
                raw.TryGetProperty("profileId", out var profileId) ? profileId.GetString()! : "fixture",
                raw.TryGetProperty("revision", out var revision) ? revision.GetInt32() : 1,
                ReadPackages(raw, "packages"),
                ReadInstall(raw),
                ReadPackages(raw, "disabledPackages"));

        private static IReadOnlyList<ResolvedPackage> ReadPackages(JsonElement raw, string name)
        {
            var result = new List<ResolvedPackage>();
            if (!raw.TryGetProperty(name, out var packages)) return result;
            foreach (var item in packages.EnumerateArray())
            {
                var manifest = ModManifestJson.Deserialize(item.GetProperty("manifest").GetRawText());
                var expected = item.GetProperty("validation");
                var codes = new SortedSet<string>(ManifestValidator.Validate(manifest).Select(message => message.Split(' ')[0]), StringComparer.Ordinal);
                var declared = expected.TryGetProperty("errorCodes", out var errors)
                    ? errors.EnumerateArray().Select(error => error.GetString()!).ToArray() : Array.Empty<string>();
                if (!codes.SetEquals(declared) || (codes.Count == 0) != (expected.GetProperty("outcome").GetString() == "accept"))
                    throw new InvalidOperationException("Resolution input manifest validation differs for " + item.GetProperty("id").GetString()
                        + ": " + string.Join(", ", codes));
                result.Add(new ResolvedPackage(item.GetProperty("id").GetString()!, item.GetProperty("version").GetString()!, manifest));
            }
            return result;
        }

        private static InstallFacts ReadInstall(JsonElement raw) =>
            raw.TryGetProperty("install", out var install)
                ? new InstallFacts(
                    Text(install, "platform"),
                    Text(install, "architecture"),
                    Text(install, "contentTarget"),
                    Text(install, "gameVersion"))
                : new InstallFacts();

        private static LaunchRequest ReadRequest(JsonElement raw) =>
            new LaunchRequest(
                Text(raw, "targetId"),
                raw.TryGetProperty("worldOverride", out var world) ? world.GetString() : null,
                raw.TryGetProperty("transitionOverride", out var transition) ? transition.GetString() : null);

        private static RuntimeObservation ReadObservation(EffectiveProfile profile, JsonElement raw) =>
            RuntimeObservation.FromEnvelopes(profile, raw.GetProperty("envelopes").EnumerateArray()
                .Select(item => LaunchTransportJson.ReadObservation(item.GetRawText())));

        private static RuntimeBindingSnapshot ReadBindings(JsonElement raw) => new RuntimeBindingSnapshot(
            Text(raw, "profileId"), raw.GetProperty("profileRevision").GetInt32(), Text(raw, "packageSetDigest"),
            raw.GetProperty("boundWorldIds").EnumerateArray().Select(item => item.GetString()!),
            raw.GetProperty("boundGamemodeIds").EnumerateArray().Select(item => item.GetString()!),
            raw.TryGetProperty("availability", out var availability) ? availability.EnumerateArray().Select(item =>
                new DeclarationAvailability(Text(item, "kind"), Text(item, "id"), item.GetProperty("blocks").EnumerateArray().Select(block =>
                    new LaunchBlock(Enum.Parse<LaunchBlockCode>(Text(block, "code"), true), Text(block, "subject"), Text(block, "subjectVersion")))))
                : Array.Empty<DeclarationAvailability>());

        private static string Text(JsonElement raw, string name) =>
            raw.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;

        /// <summary>
        /// Renders a reason in the one spelling both languages share. C# names its enum members in
        /// PascalCase and Dart in camelCase, so the fixtures would otherwise be forced to pick one
        /// language's convention and make the other translate.
        /// </summary>
        private static string Camel(string name) =>
            name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);
    }
}
