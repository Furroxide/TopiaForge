using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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
        public static (bool Accepted, SortedSet<string> Codes, string Detail) Execute(JsonElement body)
        {
            var profile = ReadProfile(body.GetProperty("profile"));
            var request = ReadRequest(body.GetProperty("request"));
            var observation = body.TryGetProperty("observation", out var raw)
                ? ReadObservation(raw)
                : RuntimeObservation.None;

            var resolution = LaunchResolver.Resolve(profile, request, observation);
            if (!resolution.Resolved)
            {
                var codes = new SortedSet<string>(
                    resolution.Blocks.Select(block => Camel(block.Code.ToString())),
                    StringComparer.Ordinal);
                return (false, codes, string.Join(", ", resolution.Blocks.Select(item => item.ToString())));
            }

            return (true, new SortedSet<string>(StringComparer.Ordinal), Describe(resolution.Plan!));
        }

        /// <summary>Renders a plan in the shared shape both runners compare.</summary>
        public static Dictionary<string, object> Normalize(JsonElement body)
        {
            var resolution = LaunchResolver.Resolve(
                ReadProfile(body.GetProperty("profile")),
                ReadRequest(body.GetProperty("request")),
                body.TryGetProperty("observation", out var raw)
                    ? ReadObservation(raw)
                    : RuntimeObservation.None);
            var plan = resolution.Plan!;
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["launchTargetId"] = plan.LaunchTargetId,
                ["gamemodeId"] = plan.GamemodeId,
                ["worldId"] = plan.WorldId,
                ["worldInstanceId"] = plan.WorldInstanceId,
                ["transition"] = plan.Transition,
                ["resolvedPackages"] = plan.ResolvedPackages
                    .Select(item => item.Id + "@" + item.Version).ToList()
            };
        }

        /// <summary>
        /// The digest is a plan's own claim about the set it was built from, so the fixture checks the
        /// claim holds rather than pinning a hash value the two languages would have to agree on by
        /// coincidence.
        /// </summary>
        public static bool DigestAgreesWithItsPackages(JsonElement body)
        {
            var resolution = LaunchResolver.Resolve(
                ReadProfile(body.GetProperty("profile")),
                ReadRequest(body.GetProperty("request")),
                body.TryGetProperty("observation", out var raw)
                    ? ReadObservation(raw)
                    : RuntimeObservation.None);
            var plan = resolution.Plan!;
            return LaunchResolver.Revalidate(plan, plan.ResolvedPackages).Count == 0
                && LaunchResolver.Revalidate(plan, Array.Empty<ResolvedPackage>()).Count == 1;
        }

        private static EffectiveProfile ReadProfile(JsonElement raw) =>
            new EffectiveProfile(
                "fixture",
                1,
                ReadPackages(raw, "packages"),
                ReadInstall(raw),
                ReadPackages(raw, "disabledPackages"));

        private static IReadOnlyList<ResolvedPackage> ReadPackages(JsonElement raw, string name) =>
            raw.TryGetProperty(name, out var packages)
                ? packages.EnumerateArray()
                    .Select(item => new ResolvedPackage(
                        item.GetProperty("id").GetString() ?? string.Empty,
                        item.GetProperty("version").GetString() ?? string.Empty,
                        ModManifestJson.Deserialize(item.GetProperty("manifest").GetRawText())))
                    .ToList()
                : (IReadOnlyList<ResolvedPackage>)Array.Empty<ResolvedPackage>();

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
                Text(raw, "worldOverride"),
                Text(raw, "transitionOverride"));

        private static RuntimeObservation ReadObservation(JsonElement raw) =>
            new RuntimeObservation(
                Strings(raw, "unavailableWorldIds"),
                Strings(raw, "discoveredWorldIds"),
                Strings(raw, "unboundGamemodeIds"));

        private static IReadOnlyList<string> Strings(JsonElement raw, string name) =>
            raw.TryGetProperty(name, out var values)
                ? values.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToList()
                : (IReadOnlyList<string>)Array.Empty<string>();

        private static string Text(JsonElement raw, string name) =>
            raw.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;

        private static string Describe(LaunchPlan plan) =>
            plan.LaunchTargetId + " -> " + plan.GamemodeId + " in " + plan.WorldId
            + " via " + plan.Transition;

        /// <summary>
        /// Renders a reason in the one spelling both languages share. C# names its enum members in
        /// PascalCase and Dart in camelCase, so the fixtures would otherwise be forced to pick one
        /// language's convention and make the other translate.
        /// </summary>
        private static string Camel(string name) =>
            name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);
    }
}
