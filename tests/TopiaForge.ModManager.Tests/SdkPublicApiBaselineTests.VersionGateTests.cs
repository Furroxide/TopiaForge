using System;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class SdkPublicApiBaselineTests
    {
        private const string MustAdd = "must add [RequiresTopiaForge";
        private const string MustNotGain = "must not gain [RequiresTopiaForge]";
        private const string MustKeep = "must keep that marker";

        private static void VersionGateBaselineTests()
        {
            const string old = "interface Example.IOld\n  method void Existing()\n";
            const string added = "  method void New()\n";
            ExpectRejected(old, old + added, MustAdd, "an unmarked API addition");
            RequireVersionedAdditions(old, old + added.TrimEnd() + RequirementMarker + "\"0.2.0\"]\n");
            ExpectRejected(old, old + "  property System.Int32 New { get [requires-topiaforge=\"0.2.0\"]; set; }\n", MustAdd,
                "a new property with an unmarked setter");
            var type = typeof(VersionedFixture);
            if (!FormatRequirement(type.GetMethod(nameof(VersionedFixture.Inherited))!).Contains("0.2.0", StringComparison.Ordinal)
                || !FormatRequirement(type.GetProperty(nameof(VersionedFixture.Later))!.GetMethod!).Contains("0.3.0", StringComparison.Ordinal))
                throw new InvalidOperationException("API baselines must record inherited and accessor requirements.");
            ChangedApiBaselineTests();
            SignatureIdentityBaselineTests();
            Console.WriteLine("SDK version baseline checks passed (20 cases).");
        }

        // An API that is already in the baseline keeps working on older loaders when its line changes, so it
        // needs no marker; a new overload, accessor or type does; and an existing API never gains one.
        private static void ChangedApiBaselineTests()
        {
            const string header = "type public class Example.Service : System.Object\n";
            const string method = "  method System.Void Run(System.String name)\n";
            const string property = "  property System.Int32 Count { get; }\n";
            const string old = header + property + method;
            RequireVersionedAdditions(old, old.Replace(method, "  method System.Void Run(System.String name) [obsolete message=\"Use Start\" error=false]\n",
                StringComparison.Ordinal));
            RequireVersionedAdditions(old, old.Replace(method, "  method System.Void Run(System.String? label)\n", StringComparison.Ordinal));
            RequireVersionedAdditions(old, old.Replace(header, "type public class Example.Service : System.Object, Example.IService\n",
                StringComparison.Ordinal));
            ExpectRejected(old, old + "  method System.Void Run(System.String name, System.Int32 count)\n", MustAdd, "an unmarked new overload");
            RequireVersionedAdditions(old, old + "  method System.Void Run(System.String name, System.Int32 count) [requires-topiaforge=\"0.2.0\"]\n");
            ExpectRejected(old, old.Replace(method, method.TrimEnd() + " [requires-topiaforge=\"0.2.0\"]\n", StringComparison.Ordinal),
                MustNotGain, "an existing method marked newer");
            ExpectRejected(old, old.Replace(property, "  property System.Int32 Count { get; set; }\n", StringComparison.Ordinal),
                "adds the set accessor", "an unmarked setter on an existing property");
            RequireVersionedAdditions(old, old.Replace(property, "  property System.Int32 Count { get; set [requires-topiaforge=\"0.2.0\"]; }\n",
                StringComparison.Ordinal));
            ExpectRejected(old, old.Replace(property, "  property System.Int32 Count { get [requires-topiaforge=\"0.2.0\"]; }\n",
                StringComparison.Ordinal), MustNotGain, "an existing getter marked newer");
            ExpectRejected(old, old.Replace(header, header.TrimEnd() + " [requires-topiaforge=\"0.2.0\"]\n", StringComparison.Ordinal),
                MustNotGain, "an existing type marked newer");
            const string newType = "type public class Example.Added : System.Object\n  method System.Void Go()\n";
            ExpectRejected(old, old + newType, MustAdd, "an unmarked new type");
            RequireVersionedAdditions(old, old + "type public class Example.Added : System.Object [requires-topiaforge=\"0.2.0\"]\n"
                + "  method System.Void Go() [requires-topiaforge=\"0.2.0\"]\n");
            // An API the baseline records as newer keeps its marker, because older loaders still lack it.
            var markedOld = old.Replace(method, method.TrimEnd() + " [requires-topiaforge=\"0.2.0\"]\n", StringComparison.Ordinal);
            ExpectRejected(markedOld, old, MustKeep, "a marked method losing its marker");
            RequireVersionedAdditions(markedOld, markedOld.Replace("String name)", "String label)", StringComparison.Ordinal));
            ExpectRejected(old.Replace(header, header.TrimEnd() + " [requires-topiaforge=\"0.2.0\"]\n", StringComparison.Ordinal), old,
                MustKeep, "a marked type losing its marker");
            ExpectRejected(old.Replace(property, "  property System.Int32 Count { get; set [requires-topiaforge=\"0.2.0\"]; }\n", StringComparison.Ordinal),
                old.Replace(property, "  property System.Int32 Count { get; set; }\n", StringComparison.Ordinal), MustKeep,
                "a marked setter losing its marker");
        }

        private static void ExpectRejected(string old, string current, string message, string description)
        {
            try
            {
                RequireVersionedAdditions(old, current);
            }
            catch (InvalidOperationException exception) when (exception.Message.Contains(message, StringComparison.Ordinal))
            {
                return;
            }

            throw new InvalidOperationException("The baseline gate accepted " + description + ".");
        }
    }
}
