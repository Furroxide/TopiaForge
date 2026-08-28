using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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

            RunResponseCaps();
            RunUnreachableBackendPaths(root);

            Console.WriteLine("RoboApiClientTests passed.");
        }

        /// <summary>
        /// The three response caps in docs/PrivacyAndCapabilities.md: 256 KiB for a brain reply, 64 KiB for a
        /// transcript, 2 MiB for a transcription request body. A backend that ignores them must not be able to
        /// make the game allocate without bound, so both the declared-length short circuit and the streaming
        /// path have to refuse.
        /// </summary>
        private static void RunResponseCaps()
        {
            const int cap = 4096;

            // A declared Content-Length over the cap is refused before a single byte is read.
            AssertThrowsInvalidData(
                () => RoboApiClient.ReadBoundedContentAsync(
                    new ByteArrayContent(new byte[cap + 1]), cap, CancellationToken.None),
                "a response declaring more than the cap must be refused before reading it");

            // A response that hides its length is caught while streaming. This is the case that matters:
            // Content-Length is supplied by the backend, so the cap cannot depend on it being honest.
            AssertThrowsInvalidData(
                () => RoboApiClient.ReadBoundedContentAsync(
                    new UnsizedContent(cap + 1), cap, CancellationToken.None),
                "a response that hides its length must still be refused once it exceeds the cap");

            // Exactly at the cap is allowed, so a legitimate maximum-size reply still works.
            var exact = RoboApiClient
                .ReadBoundedContentAsync(new UnsizedContent(cap), cap, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            Assert(exact.Length == cap, "a response of exactly the cap must be accepted whole");

            var empty = RoboApiClient
                .ReadBoundedContentAsync(new UnsizedContent(0), cap, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            Assert(empty.Length == 0, "an empty response must read as empty rather than throwing");
        }

        /// <summary>
        /// Offline, timeout, and cancellation, driven against local sockets so the result does not depend on
        /// name resolution or on anything outside the machine. Also asserts the credential never reaches a log
        /// line on any of those paths, which is the disclosure claim the launcher makes on the player's behalf.
        /// </summary>
        private static void RunUnreachableBackendPaths(string root)
        {
            const string token = "secret-token-value";
            const string session = "session-id-value";
            var directory = CreateScratchDirectory(root, "robo-token-failure-paths");
            File.WriteAllText(
                Path.Combine(directory, RoboApiClient.TokenFileName),
                "{" + "\"agent_token\":\"" + token + "\"}");

            var previousRoot = Environment.GetEnvironmentVariable("ROBOAPI_BACKEND_ROOT");
            var logger = new RecordingLogger();
            try
            {
                // A port nothing listens on: the connection is refused at once, which is what "offline" looks
                // like to the client without waiting on a real network.
                var refusedPort = ReserveClosedPort();
                Environment.SetEnvironmentVariable(
                    "ROBOAPI_BACKEND_ROOT", "https://127.0.0.1:" + refusedPort + "/v1");
                var offline = new RoboApiClient(directory, session, logger);
                Assert(offline.HasToken, "a valid token file must still load when the backend is unreachable");

                var refused = offline
                    .Check3Async(SampleRequest(), 5f, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                Assert(!refused.Succeeded, "an unreachable backend must fail rather than fabricate a reply");
                Assert(
                    refused.ErrorCode == ModErrorCode.External,
                    "a refused connection is an external failure, not a cancellation");

                var noTranscript = offline
                    .SttAsync(new byte[] { 1, 2, 3 }, 5f, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                Assert(noTranscript == null, "transcription against an unreachable backend must return nothing");

                // A listener that accepts and then says nothing stalls the TLS handshake, so the per-call
                // deadline is the only thing that can end the request.
                using (var stalled = new StallingListener())
                {
                    Environment.SetEnvironmentVariable(
                        "ROBOAPI_BACKEND_ROOT", "https://127.0.0.1:" + stalled.Port + "/v1");
                    var timing = new RoboApiClient(directory, session, logger);
                    var timedOut = timing
                        .Check3Async(SampleRequest(), 0.25f, CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                    Assert(!timedOut.Succeeded, "a stalled backend must not resolve successfully");
                    Assert(
                        timedOut.ErrorCode == ModErrorCode.Cancelled,
                        "an expired per-call deadline must surface as a cancellation, not an external error");
                    Assert(
                        timedOut.ErrorMessage != null &&
                        timedOut.ErrorMessage.IndexOf("timed out", StringComparison.Ordinal) >= 0,
                        "a deadline expiry must read as a timeout so a caller can tell it from a user cancel");

                    // A caller who cancels must be told they cancelled, not that the backend was slow.
                    using var cancelled = new CancellationTokenSource();
                    cancelled.Cancel();
                    var byCaller = timing
                        .Check3Async(SampleRequest(), 30f, cancelled.Token)
                        .GetAwaiter()
                        .GetResult();
                    Assert(
                        byCaller.ErrorCode == ModErrorCode.Cancelled,
                        "a caller cancellation must surface as a cancellation");
                    Assert(
                        byCaller.ErrorMessage != null &&
                        byCaller.ErrorMessage.IndexOf("cancelled", StringComparison.Ordinal) >= 0,
                        "a caller cancellation must not be reported as a timeout");
                }

                // An oversized capture is refused locally, so the 2 MiB cap cannot first be spent on upload
                // bandwidth and then rejected by the backend.
                var oversized = offline
                    .SttAsync(new byte[(2 * 1024 * 1024) + 1], 5f, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                Assert(oversized == null, "a capture over the 2 MiB cap must be refused without a request");

                var emptyCapture = offline
                    .SttAsync(Array.Empty<byte>(), 5f, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                Assert(emptyCapture == null, "an empty capture must be refused without a request");

                // Dropping the cached token forces the next access back to the file, which is what the 401
                // path relies on to pick up a rotated credential.
                offline.InvalidateToken();
                Assert(offline.HasToken, "an invalidated token must be re-read from the file, not lost");

                foreach (var message in logger.Messages)
                {
                    Assert(
                        message.IndexOf(token, StringComparison.Ordinal) < 0,
                        "no failure path may write the bearer token to a log: " + message);
                    Assert(
                        message.IndexOf(session, StringComparison.Ordinal) < 0,
                        "no failure path may write the session identifier to a log: " + message);
                    Assert(
                        message.IndexOf("Bearer", StringComparison.Ordinal) < 0,
                        "no failure path may write an authorization header to a log: " + message);
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("ROBOAPI_BACKEND_ROOT", previousRoot);
            }
        }

        /// <summary>The smallest request the protocol accepts; its content is irrelevant here.</summary>
        private static BrainQueryRequest SampleRequest() =>
            new BrainQueryRequest("ping", Array.Empty<BrainOutputField>());

        /// <summary>
        /// Creates a scratch directory under <paramref name="root"/> and proves it stayed there.
        /// The harness takes its root from the command line, so combining a name onto it is a
        /// tainted path expression; resolving both ends and comparing makes the containment a
        /// checked property rather than an assumed one.
        /// </summary>
        private static string CreateScratchDirectory(string root, string name)
        {
            var basePath = Path.GetFullPath(root);
            var prefix = basePath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(Path.Combine(prefix, name));
            if (!candidate.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Scratch directory escaped the test root: " + name);
            }

            Directory.CreateDirectory(candidate);
            return candidate;
        }

        /// <summary>Binds a loopback port, learns its number, then releases it so connecting is refused.</summary>
        private static int ReserveClosedPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static void AssertThrowsInvalidData(Func<Task<string>> call, string message)
        {
            try
            {
                call().GetAwaiter().GetResult();
            }
            catch (InvalidDataException)
            {
                return;
            }

            throw new InvalidOperationException("Assertion failed: " + message);
        }

        /// <summary>Content that reports no length, forcing the streaming branch of the cap check.</summary>
        private sealed class UnsizedContent : HttpContent
        {
            private readonly byte[] payload;

            public UnsizedContent(int size) => payload = new byte[size];

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
                stream.WriteAsync(payload, 0, payload.Length);

            protected override bool TryComputeLength(out long length)
            {
                length = 0;
                return false;
            }
        }

        /// <summary>Accepts connections and never answers, so only the per-call deadline ends the request.</summary>
        private sealed class StallingListener : IDisposable
        {
            private readonly TcpListener listener;
            private readonly List<TcpClient> accepted = new List<TcpClient>();
            private volatile bool stopped;

            public StallingListener()
            {
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                _ = Task.Run(AcceptLoop);
            }

            public int Port { get; }

            private async Task AcceptLoop()
            {
                while (!stopped)
                {
                    try
                    {
                        var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                        lock (accepted)
                        {
                            accepted.Add(client);
                        }
                    }
                    catch
                    {
                        return;
                    }
                }
            }

            public void Dispose()
            {
                stopped = true;
                listener.Stop();
                lock (accepted)
                {
                    foreach (var client in accepted)
                    {
                        client.Close();
                    }

                    accepted.Clear();
                }
            }
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

            // Every line the client emits, so a redaction assertion can search all of them rather than
            // trusting that the one message a test happens to look at is the only one written.
            public List<string> Messages { get; } = new List<string>();

            public void Debug(string message) { DebugCount++; Messages.Add(message); }
            public void Info(string message) => Messages.Add(message);
            public void Warn(string message) { WarningCount++; Messages.Add(message); }
            public void Error(string message) => Messages.Add(message);
            public void Error(Exception exception, string message) => Messages.Add(message + " " + exception);
        }
    }
}
