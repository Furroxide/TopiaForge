using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.RobotKit;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class RobotBodyTests
    {
        private static void CompositionDiagnostics()
        {
            var expected = new[]
            {
                "prefab-lookup", "missing-prefab", "roots", "hierarchy", "inactive-clone", "dormant-brain", "quieten", "appearance",
                "anchor-setup", "renderers", "mesh", "bake", "vertex-budget", "materials", "no-visible-meshes", "anchor-activation",
            }.Select(label => "RobotKit body composition refused stage=" + label);
            var diagnostics = new RobotBodyCompositionDiagnostics();
            var logger = new CompositionLogger();
            foreach (RobotBodyCompositionStage stage in Enum.GetValues(typeof(RobotBodyCompositionStage)))
                diagnostics.Report(null, stage);
            for (var attempt = 0; attempt < 10; attempt++)
                for (var stage = -1; stage <= 64; stage++)
                    diagnostics.Report(logger, (RobotBodyCompositionStage)stage);
            Check(logger.Warnings.SequenceEqual(expected), "only the closed warning set is emitted, once per stage and provider");

            var nextProvider = new RobotBodyCompositionDiagnostics();
            var failing = new CompositionLogger { Throw = true };
            nextProvider.Report(failing, RobotBodyCompositionStage.Mesh);
            failing.Throw = false;
            nextProvider.Report(failing, RobotBodyCompositionStage.Mesh);
            nextProvider.Report(failing, RobotBodyCompositionStage.Bake);
            Check(failing.Warnings.SequenceEqual(new[] { "RobotKit body composition refused stage=mesh",
                "RobotKit body composition refused stage=bake" }), "logger faults are swallowed, bounded and do not suppress other stages or providers");

            var rig = NewRig();
            using var provider = new Bodies(rig.Host) { Refuse = true };
            var request = Request();
            var participant = new AuthorityMod(rig.Host, ModExecutionContext.Participant);
            Check(provider.Bind(participant).Spawn(request).ErrorCode == ModErrorCode.NotAuthoritative && provider.Attempts == 0,
                "non-server scope cannot reach the composition diagnostic");
            var host = new AuthorityMod(rig.Host, ModExecutionContext.GamemodeServer);
            var canonical = provider.Bind(host).Spawn(request);
            var replica = provider.Provider.CreateReplica(RobotBodyWire.Describe(request));
            Check(provider.AuthoritativeAttempts.SequenceEqual(new[] { true, false }),
                "only canonical host composition may diagnose, even when the replica factory runs on the same provider");
            Check(canonical.ErrorCode == ModErrorCode.Unavailable && replica.ErrorCode == canonical.ErrorCode &&
                canonical.ErrorMessage == "RobotKit could not compose the requested body. Check the loaded robot and accessory catalog." &&
                replica.ErrorMessage == canonical.ErrorMessage && provider.Native.Count == 0,
                "composition refusal preserves the player result and retains no entity");
        }

        private sealed class CompositionLogger : IModLogger
        {
            internal readonly List<string> Warnings = new List<string>();
            internal bool Throw;
            public void Warn(string message)
            {
                Warnings.Add(message);
                if (Throw) throw new InvalidOperationException("logger-only data");
            }
            public void Debug(string message) => throw new InvalidOperationException();
            public void Info(string message) => throw new InvalidOperationException();
            public void Error(string message) => throw new InvalidOperationException();
            public void Error(Exception exception, string message) => throw new InvalidOperationException();
        }
    }
}
