using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TopiaForge.ModManager;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModRuntime.Tests
{
    internal static partial class Program
    {
        private static void TestMalformedSelectionKeepsManagerUsable(string root, bool enabled)
        {
            var fixture = NewFixture(root, "malformed-selection-" + enabled, "TopiaForge.ValidTestMod.RuntimeSuccessMod");
            fixture.Manifest.SupportedLoaderVersionRange = "*";
            fixture.Manifest.SupportedSdkVersionRange = "*";
            var bad = new ModManifest { Id = "tests.bad-version", Name = "Invalid installed version", Version = "not a version" };
            var malformed = new ModPackage("invalid-installed-package", bad,
                new InstalledModState { Id = bad.Id, Version = bad.Version, Enabled = enabled }, new[] { "original version validation error" });
            var selection = RuntimeSessionSelection.Create("malformed-production", 1, new[] { malformed, fixture.Package },
                new LoadOrderResult(new[] { fixture.Package }, new Dictionary<string, IReadOnlyList<string>>()), new ManifestValidationContext());
            using var lease = fixture.CreateRuntime();
            var runtime = (TopiaForge.ModManager.ModRuntime)lease;
            var menus = 0;
            var sessions = runtime.ActivateSessionRuntime(selection.Profile,
                (_, _) => { menus++; return Task.FromResult(OperationResult<bool>.Success(true)); }, selection.RejectedSelections);
            runtime.Load(selection.Packages);
            Assert(runtime.IsLoaded(fixture.Manifest.Id), "Malformed installed metadata must not prevent healthy entry-point loading.");
            Assert(runtime.CaptureSessionRuntime().Profile.Packages.Count == 1 && runtime.LaunchTargets.Count == 0,
                "The healthy runtime and target inventory remain inspectable.");
            var failures = new List<Exception>();
            void Check(bool condition, string message) { if (!condition) failures.Add(new InvalidOperationException(message)); }
            var request = new LaunchRequest(fixture.Manifest.Id + ".menu");
            if (enabled)
            {
                var rejected = false;
                try { runtime.ResolveLaunch(request); }
                catch (InvalidRuntimeSelectionException error)
                {
                    rejected = error.RejectedSelections.Single().PackagePath == malformed.PackagePath;
                }
                Check(rejected, "Resolver callers must receive structured malformed-selection failure instead of resolving a truncated profile.");
                var launch = runtime.LaunchTargetAsync(request);
                PumpBinding(runtime.NativeDispatcher, launch);
                Check(launch.Result.ErrorCode == ModErrorCode.InvalidState
                    && launch.Result.ErrorMessage.Contains(malformed.PackagePath, StringComparison.Ordinal),
                    "Target commands must preserve actionable selected-package repair guidance.");
                var descriptor = new LaunchPlanDescriptor(request.TargetId, fixture.Manifest.Id + ".mode",
                    fixture.Manifest.Id + ".world", ModTransitions.SceneReplacement, request,
                    selection.Profile.Packages.Select(package => package.Identity));
                var direct = sessions.StartAsync(descriptor, "invalid-selection-direct");
                PumpBinding(runtime.NativeDispatcher, direct);
                Check(!direct.Result.Succeeded && direct.Result.ErrorMessage.Contains(malformed.PackagePath, StringComparison.Ordinal)
                    && sessions.Current.Phase == SessionPhase.Idle && !runtime.NativeTransitions.IsSceneBusy,
                    "Direct orchestrator admission must also reject malformed selection before native work.");
            }
            else
            {
                var resolution = runtime.ResolveLaunch(request);
                Assert(!resolution.Resolved && resolution.Blocks.Any(block => block.Code == LaunchBlockCode.TargetNotDeclared),
                    "Disabled malformed metadata must not replace normal healthy-profile resolution.");
            }
            var menu = sessions.ReturnToMainMenuAsync();
            PumpBinding(runtime.NativeDispatcher, menu);
            Assert(menu.Result.Succeeded && menus == 1, "Main-menu operation remains available while invalid packages await repair.");
            Assert(bad.Version == "not a version" && malformed.Errors.Single() == "original version validation error",
                "The original scanned package and UI diagnostics remain unchanged.");
            if (failures.Count > 0) throw new AggregateException(failures);
            Console.WriteLine("Malformed selection runtime case passed: " + (enabled ? "enabled" : "disabled"));
        }

        private static int RunMalformedSelectionCase(string name)
        {
            if (name != "enabled" && name != "disabled") return 2;
            var root = Directory.CreateTempSubdirectory("TopiaForgeMalformedSelection-").FullName;
            try { TestMalformedSelectionKeepsManagerUsable(root, name == "enabled"); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally { TryDelete(root); }
        }

        private static void TestMalformedSelectionInFreshProcesses()
        {
            foreach (var name in new[] { "enabled", "disabled" })
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!)
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(typeof(Program).Assembly.Location);
                start.ArgumentList.Add("--malformed-selection-case");
                start.ArgumentList.Add(name);
                using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start malformed-selection harness.");
                var output = child.StandardOutput.ReadToEndAsync();
                var error = child.StandardError.ReadToEndAsync();
                if (!child.WaitForExit(120000)) { child.Kill(true); throw new InvalidOperationException("Malformed-selection child timed out: " + name); }
                Assert(child.ExitCode == 0, "malformed selection " + name + ": " + output.Result + error.Result);
            }
        }
    }
}
