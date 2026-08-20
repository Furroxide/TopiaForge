using System;
using System.IO;
using TopiaForge.Mods;
using TopiaForge.RobotKit;

namespace TopiaForge.ModManager.Tests
{
    internal static class RoboApiClientTests
    {
        public static void Run(string root)
        {
            var logger = new RecordingLogger();
            Assert(
                RoboApiClient.ResolveBackendRoot("https://api.example.test/v2/", logger) == "https://api.example.test/v2",
                "HTTPS backend roots should be normalized");
            Assert(
                RoboApiClient.ResolveBackendRoot(null, logger) == "https://api.tomatocake.dev/v1",
                "an absent override should use the built-in HTTPS endpoint");
            Assert(
                RoboApiClient.ResolveBackendRoot("http://api.example.test/v2", logger) == string.Empty,
                "an explicit plaintext backend root must fail closed instead of reaching production");
            Assert(logger.WarningCount == 1, "an insecure configured endpoint should emit one warning");

            foreach (var invalid in new[]
                     {
                         string.Empty,
                         "   ",
                         "not-a-url",
                         "https://user:secret@api.example.test/v2",
                         "https://api.example.test/v2?token=secret",
                         "https://api.example.test/v2#fragment"
                     })
            {
                Assert(RoboApiClient.ResolveBackendRoot(invalid, logger) == string.Empty,
                    "every explicit invalid backend override must disable remote features: " + invalid);
            }

            // The client owns the file name and only ever reads it inside the directory it is handed, so each
            // scenario below gets its own directory rather than its own file name.
            var tokenDirectory = Path.Combine(root, "robo-token-oversized");
            Directory.CreateDirectory(tokenDirectory);
            var tokenPath = Path.Combine(tokenDirectory, RoboApiClient.TokenFileName);
            File.WriteAllBytes(tokenPath, new byte[RoboApiClient.MaxTokenFileBytes + 1]);
            var previousRoot = Environment.GetEnvironmentVariable("ROBOAPI_BACKEND_ROOT");
            try
            {
                Environment.SetEnvironmentVariable("ROBOAPI_BACKEND_ROOT", null);
                var client = new RoboApiClient(tokenDirectory, "test-session", logger);
                Assert(!client.HasToken, "oversized token files should be rejected without parsing");
                Assert(logger.DebugCount == 1, "token read rejection should be observable without exposing token data");

                File.WriteAllText(tokenPath, "{\"agent_token\":\"secret-token\"}");
                Environment.SetEnvironmentVariable("ROBOAPI_BACKEND_ROOT", "http://attacker.invalid");
                var disabled = new RoboApiClient(tokenDirectory, "test-session", logger);
                Assert(!disabled.HasToken,
                    "an explicit invalid backend override must disable the client even when a valid token exists");
                Assert(logger.DebugCount == 1,
                    "a disabled backend must not read token material or attempt a production fallback");

                // RuntimeCapabilityProbe evaluates IsAvailable on every mod load and scene change. Probing must
                // answer from file existence alone, never by parsing and caching the player's bearer token,
                // otherwise installing RobotKit materialises the credential with every consumer feature off.
                Environment.SetEnvironmentVariable("ROBOAPI_BACKEND_ROOT", null);
                var probeDirectory = Path.Combine(root, "robo-token-probe");
                Directory.CreateDirectory(probeDirectory);
                File.WriteAllBytes(
                    Path.Combine(probeDirectory, RoboApiClient.TokenFileName),
                    new byte[RoboApiClient.MaxTokenFileBytes + 1]);
                var probeLogger = new RecordingLogger();
                var probe = new RoboApiClient(probeDirectory, "test-session", probeLogger);
                Assert(probe.HasTokenFile,
                    "an availability probe should report a present credential without validating it");
                Assert(probeLogger.DebugCount == 0,
                    "an availability probe must not read or parse token material");
                Assert(!probe.HasToken,
                    "the request guard must still reject an oversized token file");
                Assert(probeLogger.DebugCount == 1,
                    "only the request guard should read token material");
                Assert(probe.HasTokenFile,
                    "the probe must keep answering from file existence after a failed parse, never from the cache");
                Assert(probeLogger.DebugCount == 1,
                    "re-probing after a failed parse must not read token material again");

                var absentDirectory = Path.Combine(root, "robo-token-absent");
                Directory.CreateDirectory(absentDirectory);
                var absent = new RoboApiClient(absentDirectory, "s", probeLogger);
                Assert(!absent.HasTokenFile, "a missing credential file must probe as unavailable");

                // The client composes the path itself, so a caller cannot redirect it at another file, and a
                // directory it cannot resolve is refused outright rather than silently probing somewhere else.
                Assert(
                    RoboApiClient.ResolveTokenPath(probeDirectory) ==
                        Path.Combine(Path.GetFullPath(probeDirectory), RoboApiClient.TokenFileName),
                    "the client must read its own file name inside the supplied directory");
                foreach (var invalid in new[] { string.Empty, "   " })
                {
                    var rejected = false;
                    try
                    {
                        RoboApiClient.ResolveTokenPath(invalid);
                    }
                    catch (ArgumentException)
                    {
                        rejected = true;
                    }

                    Assert(rejected, "an unusable token directory must be refused, not probed");
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("ROBOAPI_BACKEND_ROOT", previousRoot);
            }

            Console.WriteLine("RoboApiClientTests passed.");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Assertion failed: " + message);
            }
        }

        private sealed class RecordingLogger : IModLogger
        {
            public int WarningCount { get; private set; }
            public int DebugCount { get; private set; }

            public void Debug(string message) => DebugCount++;
            public void Info(string message) { }
            public void Warn(string message) => WarningCount++;
            public void Error(string message) { }
            public void Error(Exception exception, string message) { }
        }
    }
}
