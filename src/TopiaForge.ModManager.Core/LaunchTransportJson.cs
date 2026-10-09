using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace TopiaForge.ModManager.Core
{
    /// <summary>Strict inactive transport readers; serializer coercion never defines the wire contract.</summary>
    public static partial class LaunchTransportJson
    {
        public const int MaxDocumentBytes = 4 * 1024 * 1024;
        public const int MaxObservationBytes = 16 * 1024 * 1024;

        public static string WritePlan(LaunchPlanDescriptor plan) => Bounded(PlanJson(plan), MaxDocumentBytes);
        public static LaunchPlanDescriptor ReadPlan(string json) => Read(json, MaxDocumentBytes, ParsePlan);
        public static string WriteProfile(ProfileLaunchConfigurationV4 profile) => Bounded(ProfileJson(profile), MaxDocumentBytes);
        public static ProfileLaunchConfigurationV4 ReadProfile(string json) => Read(json, MaxDocumentBytes, ParseProfile);

        private static string PlanJson(LaunchPlanDescriptor plan) => Object(
            Pair("targetId", Quote(plan.TargetId)), Pair("gamemodeId", Quote(plan.GamemodeId)),
            Pair("worldId", Quote(plan.WorldId)), Optional("worldFamilyId", plan.WorldFamilyId),
            Pair("transition", Quote(plan.Transition)), Pair("request", Object(
                Pair("targetId", Quote(plan.Request.TargetId)), Optional("worldOverride", plan.Request.WorldOverride),
                Optional("transitionOverride", plan.Request.TransitionOverride))),
            Pair("packages", PackagesJson(plan.Packages)), Pair("digest", Quote(plan.Digest)));

        private static LaunchPlanDescriptor ParsePlan(string json)
        {
            var value = new Fields(json, "targetId", "gamemodeId", "worldId", "worldFamilyId", "transition", "request", "packages", "digest");
            var request = new Fields(value.Required("request"), "targetId", "worldOverride", "transitionOverride");
            var worldOverride = request.OptionalString("worldOverride");
            var transitionOverride = request.OptionalString("transitionOverride");
            if (worldOverride == "" || transitionOverride == "") throw Invalid("Optional overrides must be omitted rather than empty.");
            return new LaunchPlanDescriptor(value.String("targetId"), value.String("gamemodeId"), value.String("worldId"),
                value.String("transition"), new LaunchRequest(request.String("targetId"), worldOverride, transitionOverride),
                ParsePackages(value.Required("packages")), value.OptionalString("worldFamilyId"), value.String("digest"));
        }

        private static string ProfileJson(ProfileLaunchConfigurationV4 profile) => Object(
            Pair("schemaVersion", "4"), Pair("profileId", Quote(profile.ProfileId)), Pair("profileRevision", Number(profile.ProfileRevision)),
            Pair("requestId", Quote(profile.RequestId)), Pair("command", Quote(profile.Command)),
            Pair("packages", PackagesJson(profile.Packages)), Pair("digest", Quote(profile.Digest)),
            Pair("safeMode", Boolean(profile.SafeMode)), Pair("inheritManagerModState", Boolean(profile.InheritManagerModState)),
            Pair("enabledMods", Array(profile.EnabledMods.Select(Quote))),
            Pair("selectedVersions", Object(profile.SelectedVersions.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => Pair(pair.Key, Quote(pair.Value))).ToArray())),
            profile.Plan == null ? null : Pair("plan", PlanJson(profile.Plan)));

        private static ProfileLaunchConfigurationV4 ParseProfile(string json)
        {
            var value = new Fields(json, "schemaVersion", "profileId", "profileRevision", "requestId", "command", "packages", "digest",
                "safeMode", "inheritManagerModState", "enabledMods", "selectedVersions", "plan");
            value.Version(4);
            var versions = new Fields(value.Required("selectedVersions"), allowed: null);
            return new ProfileLaunchConfigurationV4(value.String("profileId"), value.Integer("profileRevision"), value.String("requestId"),
                value.String("command"), ParsePackages(value.Required("packages")), value.String("digest"), value.Bool("safeMode"),
                value.Bool("inheritManagerModState"), Strings(value.Required("enabledMods")), versions.Values.ToDictionary(
                    pair => pair.Key, pair => String(pair.Value), StringComparer.Ordinal),
                value.Has("plan") ? ParsePlan(value.Required("plan")) : null);
        }

        private static string PackagesJson(IEnumerable<PackageIdentity> packages) => Array(packages.Select(PackageJson));
        private static string PackageJson(PackageIdentity package) => Object(Pair("id", Quote(package.Id)), Pair("version", Quote(package.Version)));
        private static IReadOnlyList<PackageIdentity> ParsePackages(string json) => Values(json).Select(ParsePackage).ToArray();
        private static PackageIdentity ParsePackage(string json)
        {
            var value = new Fields(json, "id", "version");
            return new PackageIdentity(value.String("id"), value.String("version"));
        }

        private static T Read<T>(string json, int maximumBytes, Func<string, T> parse)
        {
            try { return parse(Bounded(json, maximumBytes)); }
            catch (Exception exception) when (exception is ArgumentException || exception is FormatException || exception is InvalidDataException
                || exception is System.Runtime.Serialization.SerializationException || exception is System.Xml.XmlException || exception is OverflowException)
            {
                throw new InvalidDataException("invalidLaunchTransport: " + exception.Message, exception);
            }
        }

        private static string Bounded(string json, int maximumBytes)
        {
            if (json == null || new UTF8Encoding(false, true).GetByteCount(json) > maximumBytes)
                throw Invalid("Transport document exceeds its byte limit.");
            return json;
        }

        private static string Quote(string value) => JsonUtil.Serialize(value);
        private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
        private static string Boolean(bool value) => value ? "true" : "false";
        private static string Pair(string key, string value) => Quote(key) + ":" + value;
        private static string? Optional(string key, string? value) => value == null ? null : Pair(key, Quote(value));
        private static string Object(params string?[] fields) => "{" + string.Join(",", fields.Where(value => value != null)) + "}";
        private static string Array(IEnumerable<string> values) => "[" + string.Join(",", LaunchContractValues.Copy(values)) + "]";
        private static IReadOnlyList<string> Values(string json) => LaunchContractValues.Copy(JsonObjectMerge.ReadArrayValues(json));
        private static IReadOnlyList<string> Strings(string json) => Values(json).Select(String).ToArray();
        private static string String(string raw)
        {
            raw = raw.Trim();
            if (raw.Length == 0 || raw[0] != '"') throw Invalid("Expected a JSON string.");
            return JsonUtil.Deserialize<string>(raw);
        }

        private static InvalidDataException Invalid(string message) => new InvalidDataException(message);

        private sealed class Fields
        {
            internal Fields(string json, params string[]? allowed)
            {
                var properties = LaunchContractValues.Copy(JsonObjectMerge.ReadProperties(json));
                if (properties.GroupBy(item => item.Name, StringComparer.Ordinal).Any(group => group.Count() > 1))
                    throw Invalid("Duplicate JSON field.");
                Values = properties.ToDictionary(item => item.Name, item => item.RawValue.Trim(), StringComparer.Ordinal);
                if (allowed != null && Values.Keys.Any(key => !allowed.Contains(key, StringComparer.Ordinal)))
                    throw Invalid("Unknown JSON field.");
            }

            internal IReadOnlyDictionary<string, string> Values { get; }
            internal bool Has(string name) => Values.ContainsKey(name);
            internal string Required(string name) => Values.TryGetValue(name, out var value) ? value : throw Invalid("Missing field " + name + ".");
            internal string String(string name) => LaunchTransportJson.String(Required(name));
            internal string? OptionalString(string name) => Has(name) ? String(name) : null;
            internal bool Bool(string name) => Required(name) == "true" ? true : Required(name) == "false" ? false : throw Invalid("Expected boolean " + name + ".");
            internal bool? OptionalBool(string name) => Has(name) ? Bool(name) : (bool?)null;
            internal int Integer(string name)
            {
                if (!double.TryParse(Required(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    || double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value > int.MaxValue || value != Math.Truncate(value))
                    throw Invalid("Expected nonnegative integer " + name + ".");
                return (int)value;
            }
            internal void Version(int expected)
            {
                if (Integer("schemaVersion") != expected) throw Invalid("Unsupported schemaVersion.");
            }
        }
    }
}
