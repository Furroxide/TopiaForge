using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class SdkPublicApiBaselineTests
    {
        private const string RequirementMarker = " [requires-topiaforge=";

        private static string FormatRequirement(MemberInfo member)
        {
            string? required = null;
            for (MemberInfo? current = member; current != null; current = current.DeclaringType)
            {
                var attribute = current.GetCustomAttribute<RequiresTopiaForgeAttribute>(inherit: false);
                if (attribute == null) continue;
                if (!VersionUtil.TryParse(attribute.Version, out _))
                    throw new InvalidOperationException(current.Name + " needs a valid SemVer in RequiresTopiaForge.");
                if (required == null || !VersionUtil.IsAtLeast(required, attribute.Version)) required = attribute.Version;
            }

            return required == null ? string.Empty : RequirementMarker + JsonSerializer.Serialize(required) + "]";
        }

        private static void RequireVersionedAdditions(string expected, string actual)
        {
            var old = ApiEntries(expected);
            var added = ApiEntries(actual).Where(entry => !old.ContainsKey(entry.Key));
            foreach (var entry in added)
            {
                if (!Regex.IsMatch(entry.Value, " \\[requires-topiaforge=\"[^\"]+\"\\]$"))
                    throw new InvalidOperationException("New SDK API " + entry.Key.Trim() +
                        " must add [RequiresTopiaForge(\"<first-release-version>\")] to the member or its declaring type before refreshing the baseline.");
            }
        }

        private static Dictionary<string, string> ApiEntries(string baseline)
        {
            var entries = new Dictionary<string, string>(StringComparer.Ordinal);
            var type = string.Empty;
            foreach (var line in baseline.Split('\n'))
            {
                if (line.Length == 0 || line[0] == '#' || line.StartsWith("assembly ", StringComparison.Ordinal)
                    || line.StartsWith("target-framework ", StringComparison.Ordinal)) continue;
                if (line[0] != ' ') type = line.Split(new[] { RequirementMarker }, StringSplitOptions.None)[0];
                entries[type + "\n" + line] = line;
            }

            return entries;
        }

        private static void VersionGateBaselineTests()
        {
            const string old = "interface Example.IOld\n  method void Existing()\n";
            const string added = "  method void New()\n";
            try
            {
                RequireVersionedAdditions(old, old + added);
                throw new InvalidOperationException("An unmarked API addition passed its baseline gate.");
            }
            catch (InvalidOperationException exception) when (exception.Message.Contains("must add [RequiresTopiaForge", StringComparison.Ordinal)) { }
            RequireVersionedAdditions(old, old + added.TrimEnd() + RequirementMarker + "\"0.2.0\"]\n");
            try
            {
                RequireVersionedAdditions(old, old + "  property System.Int32 New { get [requires-topiaforge=\"0.2.0\"]; set; }\n");
                throw new InvalidOperationException("A new property with an unmarked setter passed its baseline gate.");
            }
            catch (InvalidOperationException exception) when (exception.Message.Contains("must add [RequiresTopiaForge", StringComparison.Ordinal)) { }
            var type = typeof(VersionedFixture);
            if (!FormatRequirement(type.GetMethod(nameof(VersionedFixture.Inherited))!).Contains("0.2.0", StringComparison.Ordinal)
                || !FormatRequirement(type.GetProperty(nameof(VersionedFixture.Later))!.GetMethod!).Contains("0.3.0", StringComparison.Ordinal))
                throw new InvalidOperationException("API baselines must record inherited and accessor requirements.");
            Console.WriteLine("SDK version baseline checks passed (4 cases).");
        }

        [RequiresTopiaForge("0.2.0")]
        private sealed class VersionedFixture
        {
            public void Inherited() { }
            public int Later { [RequiresTopiaForge("0.3.0")] get => 0; }
        }
    }
}
