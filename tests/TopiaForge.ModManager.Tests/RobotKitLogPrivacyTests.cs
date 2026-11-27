using System;
using System.Collections.Generic;
using System.IO;
using TopiaForge.Mods;
using TopiaForge.RobotKit;

namespace TopiaForge.ModManager.Tests
{
    internal static class RobotKitLogPrivacyTests
    {
        public static int RunFocused(string mode)
        {
            try
            {
                if (mode != "--robotkit-log-privacy-convention") Run();
                if (mode != "--robotkit-log-privacy-behavior") RobotKitLogPrivacyConventionTests.Run();
                if (mode == "--robotkit-log-privacy") MicrophoneLogPrivacyConventionTests.Run();
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.Message);
                return 1;
            }
        }

        public static void Run()
        {
            TokenReadFailure();
            DamagedTokenFile();
            ExceptionPayloadIsNeverRead();
            CompatibilityFallback();
            ObjectiveTargetNamesArePrivate();
            ObjectiveTargetResolverFailure();
            Console.WriteLine("RobotKitLogPrivacyTests passed: locked and damaged tokens, exception payloads, diagnostic fallback, target resolver, private target names.");
        }

        private static void DamagedTokenFile()
        {
            var directory = Directory.CreateTempSubdirectory("TopiaForgeTokenPrivacy-");
            var previous = Environment.GetEnvironmentVariable("ROBOAPI_BACKEND_ROOT");
            try
            {
                Environment.SetEnvironmentVariable("ROBOAPI_BACKEND_ROOT", null);
                var path = Path.Combine(directory.FullName, RoboApiClient.TokenFileName);
                // Too large, then not UTF-8 text: the file opens, so the lock and permission advice would mislead.
                var cases = new[]
                {
                    (Contents: new byte[RoboApiClient.MaxTokenFileBytes + 1], Category: "InvalidDataException"),
                    (Contents: new byte[] { 0xFF, 0xFE, 0xFD }, Category: "DecoderFallbackException"),
                };
                foreach (var damaged in cases)
                {
                    File.WriteAllBytes(path, damaged.Contents);
                    var logger = new RecordingLogger();
                    Assert(!new RoboApiClient(directory.FullName, "private-session-sentinel", logger).HasToken, "a damaged token is unavailable");
                    Assert(logger.Messages.Count == 1 && logger.Messages[0]
                        == "RoboAPI token file is too large or is not valid text; restart the game so it can refresh its sign-in: " + damaged.Category,
                        "a damaged token file is reported as damaged, with its category and no path (" + damaged.Category + ")");
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("ROBOAPI_BACKEND_ROOT", previous);
                directory.Delete(true);
            }
        }

        private static void ObjectiveTargetResolverFailure()
        {
            var logger = new RecordingLogger();
            using var objectives = new RobotObjectiveService(logger, () => 0f);
            objectives.RegisterTarget("Kitchen", RobotTargetKind.Custom, () => throw new HostileException());
            Assert(!objectives.TryResolveTarget("Kitchen", out _), "a throwing target resolver resolves nothing");
            Assert(logger.Messages.Count == 1 && logger.Messages[0] == "Robot objective Custom target resolver failed: HostileException",
                "a resolver failure identifies the target kind and exception category without reading arbitrary text");
        }

        private static void ObjectiveTargetNamesArePrivate()
        {
            // Registration accepts mod-supplied text, which can be derived from a player or a local file.
            // Matching a registered target therefore does not make its name safe for a diagnostic.
            var names = new[]
            {
                Path.Combine(Path.GetTempPath(), "private-target-sentinel"),
                "private-player-sentinel",
                "Kitchen\r\nprivate-player-sentinel",
            };
            foreach (var name in names)
            {
                var logger = new RecordingLogger();
                using var objectives = new RobotObjectiveService(logger, () => 0f);
                objectives.RegisterTarget(name, RobotTargetKind.Custom, () => throw new HostileException());
                Assert(!objectives.TryResolveTarget(name, out _), "a throwing named resolver resolves nothing");
                Assert(logger.Messages.Count == 1 && !logger.Messages[0].Contains(name, StringComparison.OrdinalIgnoreCase),
                    "a registered target name can contain a path or player text and must not reach the log");
            }
        }

        private static void TokenReadFailure()
        {
            var directory = Directory.CreateTempSubdirectory("TopiaForgeTokenPrivacy-");
            var previous = Environment.GetEnvironmentVariable("ROBOAPI_BACKEND_ROOT");
            try
            {
                Environment.SetEnvironmentVariable("ROBOAPI_BACKEND_ROOT", null);
                var path = Path.Combine(directory.FullName, RoboApiClient.TokenFileName);
                File.WriteAllText(path, "{\"agent_token\":\"private-token-sentinel\"}");
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var failedWithPath = false;
                    try { File.ReadAllText(path); }
                    catch (IOException exception) { failedWithPath = exception.Message.Contains(path, StringComparison.Ordinal); }
                    Assert(failedWithPath, "the unreadable token produces an exception containing its path");

                    var logger = new RecordingLogger();
                    var client = new RoboApiClient(directory.FullName, "private-session-sentinel", logger);
                    Assert(!client.HasToken, "an unreadable token is unavailable");
                    Assert(logger.Messages.Count == 1, "the token failure emits exactly one diagnostic");
                    var message = logger.Messages[0];
                    Assert(!message.Contains(path, StringComparison.Ordinal)
                        && !message.Contains(directory.FullName, StringComparison.Ordinal)
                        && !message.Contains(directory.Name, StringComparison.Ordinal)
                        && !message.Contains("private-token-sentinel", StringComparison.Ordinal)
                        && !message.Contains("private-session-sentinel", StringComparison.Ordinal),
                        "the original log contains no token path, directory, credential or session text");
                    Assert(message == "RoboAPI token could not be read; check the token file permissions and close any program locking it, then restart the game: IOException",
                        "the token failure identifies the operation, recovery and exception category without private text");
                    Assert(!client.HasToken && logger.Messages.Count == 1, "cached failure does not repeatedly log or read");
                    client.InvalidateToken();
                }

                var recovered = new RoboApiClient(directory.FullName, "private-session-sentinel", new RecordingLogger());
                Assert(recovered.HasToken, "releasing the lock restores normal token reads");
            }
            finally
            {
                Environment.SetEnvironmentVariable("ROBOAPI_BACKEND_ROOT", previous);
                directory.Delete(true);
            }
        }

        private static void ExceptionPayloadIsNeverRead()
        {
            var privatePath = Path.Combine(Path.GetTempPath(), "private-file-sentinel");
            Exception[] failures =
            {
                new IOException(privatePath), new UnauthorizedAccessException(privatePath),
                new FileNotFoundException("private-token-sentinel", privatePath),
                new InvalidOperationException("private-player-text", new IOException(privatePath)),
                new HostileException(),
            };
            foreach (var exception in failures)
                Assert(RobotKitLog.ExceptionType(exception) == exception.GetType().Name, "only the exception category is retained");
        }

        private static void CompatibilityFallback()
        {
            var logger = new RecordingLogger();
            RobotKitDiagnostics.Configure(logger);
            try
            {
                RobotKitDiagnostics.ReportOnce("test operation", new HostileException());
                RobotKitDiagnostics.ReportOnce("test operation", new HostileException());
                Assert(logger.Messages.Count == 1 && logger.Messages[0] == "RobotKit compatibility fallback in test operation: HostileException",
                    "compatibility failures retain their operation and category and are reported once");
                var previous = Console.Error;
                using var captured = new StringWriter();
                try
                {
                    Console.SetError(captured);
                    logger.Fail = true;
                    RobotKitDiagnostics.ReportOnce("fallback operation", new HostileException());
                    Assert(captured.ToString().Trim() == "RobotKit compatibility fallback in fallback operation: HostileException; diagnostic logger also failed: HostileException",
                        "the emergency console sink also excludes both exception payloads");
                }
                finally { Console.SetError(previous); }
            }
            finally { RobotKitDiagnostics.Clear(logger); }
        }

        private sealed class HostileException : Exception
        {
            public override string Message => throw new InvalidOperationException("Exception payload was read.");
            public override string ToString() => throw new InvalidOperationException("Exception payload was formatted.");
        }

        private sealed class RecordingLogger : IModLogger
        {
            public List<string> Messages { get; } = new();
            public bool Fail { get; set; }
            public void Debug(string message)
            {
                if (Fail) throw new HostileException();
                Messages.Add(message);
            }
            public void Info(string message) => Debug(message);
            public void Warn(string message) => Debug(message);
            public void Error(string message) => Debug(message);
            public void Error(Exception exception, string message) => throw new InvalidOperationException("Raw exception reached logger.");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
        }
    }
}
