using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static class RuntimeSessionSelectionTests
    {
        internal static void Run()
        {
            PreservesRepresentableSelections();
            foreach (var invalid in new[] { ("invalid id", "1.0.0"), ("example.bad", "broken version"), ("example.é", "1.0.0") })
                RejectsMalformedIdentity(invalid.Item1, invalid.Item2, true);
            RejectsMalformedIdentity("example.disabled", "broken version", false);
            Console.WriteLine("Runtime session selection tests passed (5 cases).");
        }
        private static void PreservesRepresentableSelections()
        {
            var valid = Package("example.base");
            var excluded = Package("example.base.longer");
            var invalid = Package("example.invalid", errors: new[] { "receipt mismatch" });
            var disabled = Package("example.disabled", enabled: false);
            var order = new LoadOrderResult(new[] { valid }, new Dictionary<string, IReadOnlyList<string>>
            { [excluded.Manifest!.Id] = new[] { "dependency unavailable" } });
            var selected = RuntimeSessionSelection.Create("profile", 4, new[] { disabled, excluded, invalid, valid }, order,
                new ManifestValidationContext(gameVersion: "0.0.2409", platform: "windows", architecture: "x64", contentTargets: new[] { "windows-x64" }));
            Assert(selected.Profile.Packages.Select(value => value.Id).OrderBy(value => value).SequenceEqual(
                new[] { "example.base", "example.base.longer", "example.invalid" }), "all enabled selected manifests must survive load-order and receipt rejection");
            Assert(selected.Packages[0].Manifest!.Id == valid.Manifest!.Id, "successful dependency order must be preserved");
            Assert(selected.Packages.Single(value => value.Manifest!.Id == excluded.Manifest!.Id).Errors.Contains("dependency unavailable"),
                "excluded package wrappers must carry the actual load-order failure into runtime binding");
            Assert(selected.Profile.DisabledPackages.Single().Id == disabled.Manifest!.Id, "disabled ownership must remain available for diagnostics");
            Assert(selected.Profile.Install.GameVersion == "0.0.2409", "installation facts must be captured with the selection");
            excluded.Manifest.Version = "9.0.0";
            excluded.State!.Enabled = false;
            Assert(selected.Profile.Packages.Single(value => value.Id == "example.base.longer").Version == "1.0.0"
                && selected.Packages.Single(value => value.Manifest!.Id == "example.base.longer").State!.Enabled,
                "later scan-state changes must not mutate the activation input");
        }
        private static void RejectsMalformedIdentity(string id, string version, bool enabled)
        {
            var healthy = Package("example.healthy");
            var malformed = Package(id, enabled, new[] { "original manifest validation error" });
            malformed.Manifest!.Version = version;
            var order = new LoadOrderResult(new[] { healthy }, new Dictionary<string, IReadOnlyList<string>>());
            var selection = RuntimeSessionSelection.Create("invalid-selection", 1, new[] { malformed, healthy }, order,
                new ManifestValidationContext());
            Assert(selection.Profile.Packages.Single().Id == healthy.Manifest!.Id
                && selection.Packages.Single().Manifest!.Id == healthy.Manifest.Id && selection.Profile.DisabledPackages.Count == 0,
                "Malformed identities cannot abort healthy loading or enter the typed launch profile.");
            var rejected = selection.RejectedSelections.Single();
            Assert(rejected.PackagePath == malformed.PackagePath && rejected.Id == id && rejected.Version == version
                && rejected.IsEnabled == enabled && rejected.Errors.Contains("original manifest validation error"),
                "Rejected selections must retain original identity, path, enablement, and actionable scan errors.");
            Assert(malformed.Manifest.Id == id && malformed.Manifest.Version == version && malformed.Errors.Count == 1,
                "Constructing the runtime view must leave the original scanned UI state unchanged.");
            malformed.Manifest.Id = "changed.id"; malformed.Manifest.Version = "9.0.0"; malformed.State!.Enabled = !enabled;
            Assert(rejected.Id == id && rejected.Version == version && rejected.IsEnabled == enabled,
                "Later scanned state mutation must not rewrite a frozen rejection.");
        }
        private static ModPackage Package(string id, bool enabled = true, IReadOnlyList<string>? errors = null) =>
            new ModPackage(id, new ModManifest { Id = id, Version = "1.0.0", Name = id },
                new InstalledModState { Id = id, Enabled = enabled, Version = "1.0.0" }, errors ?? Array.Empty<string>());
        private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
