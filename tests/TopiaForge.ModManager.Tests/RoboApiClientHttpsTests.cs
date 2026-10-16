using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.RobotKit;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class RoboApiClientTests
    {
        private const string HttpsToken = "synthetic-robo-token-before-rotation";
        private const string HttpsReplacement = "synthetic-robo-token-after-rotation";
        private const string HttpsSession = "synthetic-robo-session";
        private const string HttpsTranscript = "synthetic-private-transcript";
        private const string GoodBrain = "{\"values\":{\"reply\":\"synthetic-private-transcript\"}}";
        private const string GoodStt = "{\"text\":\"synthetic-private-transcript\"}";

        private static void RunHttpsResponsePaths(string directory)
        {
            var previousRoot = Environment.GetEnvironmentVariable("ROBOAPI_BACKEND_ROOT");
            var tokenFile = Path.Combine(directory, RoboApiClient.TokenFileName);
            var previousToken = File.ReadAllText(tokenFile);
            var cases = 0;
            try
            {
                foreach (var speech in new[] { false, true })
                {
                    RunHttpSuccess(directory, speech);
                    cases++;
                    RunUnauthorizedRotation(directory, speech);
                    cases++;
                    foreach (var status in new[] { 429, 500, 503 })
                    {
                        RunRejectedStatus(directory, speech, status);
                        cases++;
                    }
                    foreach (var status in new[] { 301, 302, 303, 307, 308 })
                    {
                        RunRedirectRefusal(directory, speech, status);
                        cases++;
                    }
                    RunDefaultTlsRejection(directory, speech);
                    cases++;
                }
                var nullRejected = false;
                try { _ = new RoboApiClient(directory, HttpsSession, new RecordingLogger(), null!); }
                catch (ArgumentNullException) { nullRejected = true; }
                Assert(nullRejected, "an explicitly missing caller-owned transport must be rejected");
                Console.WriteLine("RoboApiClient HTTPS loopback tests passed (" + (cases + 1) + " cases).");
            }
            finally
            {
                File.WriteAllText(tokenFile, previousToken);
                Environment.SetEnvironmentVariable("ROBOAPI_BACKEND_ROOT", previousRoot);
            }
        }

        private static void RunHttpSuccess(string directory, bool speech)
        {
            WriteHttpsToken(directory, HttpsToken);
            using var server = new HttpsLoopback(new HttpsReply(200, speech ? GoodStt : GoodBrain));
            using var transport = PinnedLoopbackTransport(server);
            Environment.SetEnvironmentVariable("ROBOAPI_BACKEND_ROOT", server.Root);
            var logger = new RecordingLogger();
            var client = new RoboApiClient(directory, HttpsSession, logger, transport);
            try { AssertHttpsSuccess(client, speech); }
            catch { Console.WriteLine("Loopback diagnostic: connections=" + server.Connections + ", requests=" + server.Requests.Length + ", failure=" + server.FailureType); throw; }
            AssertRequest(server, speech, HttpsToken, 1);
            AssertHttpsLogs(logger);
        }

        private static void RunUnauthorizedRotation(string directory, bool speech)
        {
            WriteHttpsToken(directory, HttpsToken);
            using var server = new HttpsLoopback(
                new HttpsReply(401, "{\"error\":\"" + HttpsToken + "\"}"),
                new HttpsReply(200, speech ? GoodStt : GoodBrain));
            using var transport = PinnedLoopbackTransport(server);
            Environment.SetEnvironmentVariable("ROBOAPI_BACKEND_ROOT", server.Root);
            var logger = new RecordingLogger();
            var client = new RoboApiClient(directory, HttpsSession, logger, transport);
            Assert(client.HasToken, "the first synthetic token must be cached");
            WriteHttpsToken(directory, HttpsReplacement);
            AssertHttpsFailure(client, speech, 401);
            AssertRequest(server, speech, HttpsToken, 1);
            AssertHttpsSuccess(client, speech);
            AssertRequest(server, speech, HttpsReplacement, 2);
            AssertHttpsLogs(logger);
        }

        private static void RunRejectedStatus(string directory, bool speech, int status)
        {
            WriteHttpsToken(directory, HttpsToken);
            using var server = new HttpsLoopback(new HttpsReply(status, speech ? GoodStt : GoodBrain));
            using var transport = PinnedLoopbackTransport(server);
            Environment.SetEnvironmentVariable("ROBOAPI_BACKEND_ROOT", server.Root);
            var logger = new RecordingLogger();
            var client = new RoboApiClient(directory, HttpsSession, logger, transport);
            AssertHttpsFailure(client, speech, status);
            AssertRequest(server, speech, HttpsToken, 1);
            AssertHttpsLogs(logger);
        }

        private static void RunRedirectRefusal(string directory, bool speech, int status)
        {
            WriteHttpsToken(directory, HttpsToken);
            using var destination = new HttpsLoopback(new HttpsReply(200, speech ? GoodStt : GoodBrain));
            using var origin = new HttpsLoopback(new HttpsReply(status, "", destination.Root + "/redirected"));
            using var transport = PinnedLoopbackTransport(origin, destination);
            Environment.SetEnvironmentVariable("ROBOAPI_BACKEND_ROOT", origin.Root);
            var logger = new RecordingLogger();
            var client = new RoboApiClient(directory, HttpsSession, logger, transport);
            AssertHttpsFailure(client, speech, status);
            AssertRequest(origin, speech, HttpsToken, 1);
            Assert(destination.Connections == 0 && destination.Requests.Length == 0,
                "redirect refusal must occur before connecting or sending any request to the other loopback origin");
            AssertHttpsLogs(logger);
        }

        private static void RunDefaultTlsRejection(string directory, bool speech)
        {
            WriteHttpsToken(directory, HttpsToken);
            using var server = new HttpsLoopback(new HttpsReply(200, speech ? GoodStt : GoodBrain));
            Environment.SetEnvironmentVariable("ROBOAPI_BACKEND_ROOT", server.Root);
            var logger = new RecordingLogger();
            // The actual three-argument default uses normal OS certificate validation, without our pin callback.
            var client = new RoboApiClient(directory, HttpsSession, logger);
            AssertHttpsFailure(client, speech, null);
            server.WaitForCompletedAsync(1).GetAwaiter().GetResult();
            Assert(server.Connections >= 1 && server.Requests.Length == 0,
                "the default must reject the untrusted TLS certificate before transmitting HTTP or credentials");
            // A separately scoped exact pin proves this same server/certificate can complete TLS and HTTP.
            using var control = PinnedLoopbackTransport(server);
            AssertHttpsSuccess(new RoboApiClient(directory, HttpsSession, logger, control), speech);
            AssertRequest(server, speech, HttpsToken, 1);
            AssertHttpsLogs(logger);
        }

        private static void AssertHttpsFailure(RoboApiClient client, bool speech, int? status)
        {
            if (speech)
            {
                Assert(client.SttAsync(new byte[] { 1, 2, 3 }, 5f, CancellationToken.None).GetAwaiter().GetResult() == null,
                    "a refused HTTPS transcription must produce no transcript");
                return;
            }
            var result = client.Check3Async(SampleRequest(), 5f, CancellationToken.None).GetAwaiter().GetResult();
            Assert(!result.Succeeded, "a refused HTTPS brain query must not report success");
            Assert(result.ErrorCode == (status == 401 ? ModErrorCode.Unavailable : ModErrorCode.External),
                "HTTP rejection or invalid TLS must not be misreported as a timeout/cancellation");
            if (status.HasValue && status != 401)
                Assert(result.ErrorMessage == "Robot brain request failed with HTTP " + status + ".",
                    "the concrete HTTP status must reach the production response branch");
        }

        private static void AssertHttpsSuccess(RoboApiClient client, bool speech)
        {
            if (speech)
            {
                Assert(client.SttAsync(new byte[] { 1, 2, 3 }, 5f, CancellationToken.None).GetAwaiter().GetResult() == HttpsTranscript,
                    "a trusted loopback transcription must reach the real response parser");
                return;
            }
            var result = client.Check3Async(SampleRequest(), 5f, CancellationToken.None).GetAwaiter().GetResult();
            Assert(result.Succeeded, "a trusted loopback brain response must reach the real response parser; result=" + result.ErrorCode);
        }

        private static void AssertRequest(HttpsLoopback server, bool speech, string token, int count)
        {
            var requests = server.Requests;
            Assert(requests.Length == count, "HTTP requests must occur exactly once per explicit call, without hidden retry");
            var request = requests[count - 1];
            Assert(request.Target == (speech ? "/v1/agent/stt" : "/v1/agent/check3"), "the expected endpoint must receive POST");
            Assert(request.Authorization == "Bearer " + token, "the request must carry only the expected synthetic token generation");
            Assert(request.Session == HttpsSession && request.Body.Length > 0, "the actual request must carry the session and body");
        }

        private static void WriteHttpsToken(string directory, string token) =>
            File.WriteAllText(Path.Combine(directory, RoboApiClient.TokenFileName), "{\"agent_token\":\"" + token + "\"}");

        private static void AssertHttpsLogs(RecordingLogger logger)
        {
            foreach (var message in logger.Messages)
                foreach (var marker in new[] { HttpsToken, HttpsReplacement, HttpsSession, HttpsTranscript, "Bearer" })
                    Assert(!message.Contains(marker, StringComparison.Ordinal), "HTTPS paths must never log sensitive fixture material");
        }
    }
}
