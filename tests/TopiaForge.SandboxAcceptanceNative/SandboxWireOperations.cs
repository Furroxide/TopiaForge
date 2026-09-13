using System;
using System.IO;
using System.Linq;

namespace TopiaForge.SandboxAcceptance.Native
{
    // Protocol v2 wire vocabulary. Bounded operations (everything outside prepare/begin/capture/advance/cleanup)
    // are accepted only for the prepared cycle and only while the observer's expected next action matches; an
    // out-of-sequence request is refused by ending the single-use transport, exactly like unregister-source in v1.
    internal static class SandboxWireOperations
    {
        internal static readonly string[] Lifecycle = { "prepare", "begin", "capture", "advance", "cleanup" };
        internal static readonly string[] Bounded =
        {
            "unregister-source", "request-session-stop", "external-write", "destroy-borrowed", "register-competing-host",
            "unregister-competing-host", "control-cue", "stop-control-cue", "spawn-control-robot", "despawn-control-robot",
            "accessibility-high-contrast", "accessibility-scale-150", "accessibility-reduced-motion", "accessibility-reset"
        };
        internal static readonly string[] All =
        {
            "prepare", "begin", "capture", "advance", "unregister-source", "request-session-stop", "external-write", "destroy-borrowed",
            "register-competing-host", "unregister-competing-host", "control-cue", "stop-control-cue", "spawn-control-robot",
            "despawn-control-robot", "accessibility-high-contrast", "accessibility-scale-150", "accessibility-reduced-motion",
            "accessibility-reset", "cleanup"
        };

        internal static bool IsBounded(string operation) => Bounded.Contains(operation, StringComparer.Ordinal);

        /// <summary>The observer action that must be the expected next step for a bounded operation to be accepted.</summary>
        internal static string RequiredAction(string operation)
        {
            if (operation == "request-session-stop") return "stop-world-session";
            if (IsBounded(operation)) return operation;
            throw new InvalidDataException("Unknown bounded operation: " + operation);
        }

        internal static void Authorize(SandboxNativeScenarios scenarios, string scenarioId, int cycle, string operation)
        {
            var required = RequiredAction(operation);
            if (!scenarios.Matches(scenarioId, cycle) || scenarios.ExpectedAction != required)
                throw new InvalidDataException("Bounded operation '" + operation + "' is out of sequence.");
        }
    }
}
