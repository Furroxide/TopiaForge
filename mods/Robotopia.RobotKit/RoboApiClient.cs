using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Robotopia.Mods;

namespace Robotopia.RobotKit
{
    // The first-ever mod-layer RoboAPI client: posts a structured brain query to /agent/check3 reusing the game's own
    // per-user token (read from robo_token.json), the same way the native robot brains authenticate. Self-contained
    // (System.Net.Http + System.IO only, no Unity types) so the transport lives in one place; the caller (the brain
    // query service) supplies the Unity-resolved token path and ticks the async result back onto the main thread.
    //
    // Hardening: a hard per-call timeout, single shared HttpClient, the token cached until a 401 invalidates it, and
    // every returned string clamped by RoboApiProtocol. Never throws — failures resolve to an unavailable result so a
    // consumer's deterministic fallback always stands.
    internal sealed class RoboApiClient
    {
        private const string DefaultBackendRoot = "https://api.tomatocake.dev/v1";
        private const string Check3Route = "/agent/check3";
        private const string SttRoute = "/agent/stt";

        // One process-wide client (creating one per call exhausts sockets). Timeout is managed per-call via a linked
        // CancellationTokenSource, so the handler-level timeout is left generous.
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        private readonly string tokenFilePath;
        private readonly string endpoint;
        private readonly string sttEndpoint;
        private readonly string sessionId;
        private readonly IModLogger logger;

        private string? cachedToken;
        private bool tokenLoaded;

        public RoboApiClient(string tokenFilePath, string sessionId, IModLogger logger)
        {
            this.tokenFilePath = tokenFilePath;
            this.sessionId = sessionId;
            this.logger = logger;

            var root = Environment.GetEnvironmentVariable("ROBOAPI_BACKEND_ROOT");
            if (string.IsNullOrWhiteSpace(root))
            {
                root = DefaultBackendRoot;
            }

            var trimmedRoot = root!.TrimEnd('/');
            endpoint = trimmedRoot + Check3Route;
            sttEndpoint = trimmedRoot + SttRoute;
        }

        // True when a usable token is resolvable. Reads the token file at most once (until invalidated), so polling
        // this per frame does not hit disk repeatedly.
        public bool HasToken => TryGetToken(out _);

        // Run one /agent/check3 call. Returns an unavailable result (never throws) when there is no token, the call
        // times out, the network fails, or the gateway rejects the token (401, which also invalidates the cache).
        public async Task<BrainQueryResult> Check3Async(BrainQueryRequest request, float timeoutSeconds, CancellationToken ct)
        {
            if (!TryGetToken(out var token))
            {
                return BrainQueryResult.Unavailable;
            }

            try
            {
                var body = RoboApiProtocol.BuildCheck3Body(request);
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var message = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
                message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                message.Headers.TryAddWithoutValidation("Session-Id", sessionId);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(0.25f, timeoutSeconds)));

                using var response = await Http
                    .SendAsync(message, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
                    .ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    // The 24h token rotated/expired; drop it so the next attempt re-reads the file.
                    InvalidateToken();
                    return new BrainQueryResult(false, false, EmptyResult.Values, "unauthorized");
                }

                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return RoboApiProtocol.ParseCheck3Response(text);
            }
            catch (Exception ex)
            {
                // Timeout (OperationCanceledException), DNS/connect failure, etc. — all degrade to unavailable.
                logger.Debug("RoboAPI brain query failed: " + ex.GetType().Name + ": " + ex.Message);
                return new BrainQueryResult(false, false, EmptyResult.Values, ex.GetType().Name);
            }
        }

        // Run one /agent/stt speech-to-text call. `gzippedPcm` must already be gzip-compressed 16 kHz mono PCM16-LE
        // (the only format the backend accepts — see robotopia-basegame-conversation-input). Returns the transcript,
        // or null (never throws) when there is no token, the call times out/fails, the gateway rejects the token, or
        // nothing usable came back.
        public async Task<string?> SttAsync(byte[] gzippedPcm, float timeoutSeconds, CancellationToken ct)
        {
            if (gzippedPcm == null || gzippedPcm.Length == 0 || !TryGetToken(out var token))
            {
                return null;
            }

            try
            {
                using var content = new ByteArrayContent(gzippedPcm);
                content.Headers.TryAddWithoutValidation("Content-Type", "audio/pcm");
                content.Headers.TryAddWithoutValidation("Content-Encoding", "gzip");
                using var message = new HttpRequestMessage(HttpMethod.Post, sttEndpoint) { Content = content };
                message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                message.Headers.TryAddWithoutValidation("Session-Id", sessionId);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(0.5f, timeoutSeconds)));

                using var response = await Http
                    .SendAsync(message, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
                    .ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    InvalidateToken();
                    return null;
                }

                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return RoboApiProtocol.ParseSttResponse(text);
            }
            catch (Exception ex)
            {
                logger.Debug("RoboAPI speech-to-text failed: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        // Force the token to be re-read on the next access (after a 401 or a scene change where the user may have
        // signed in).
        public void InvalidateToken()
        {
            tokenLoaded = false;
            cachedToken = null;
        }

        private bool TryGetToken(out string token)
        {
            if (!tokenLoaded)
            {
                tokenLoaded = true;
                cachedToken = LoadToken();
            }

            token = cachedToken ?? string.Empty;
            return cachedToken != null;
        }

        private string? LoadToken()
        {
            try
            {
                if (!File.Exists(tokenFilePath))
                {
                    return null;
                }

                return RoboApiProtocol.ParseAgentToken(File.ReadAllText(tokenFilePath));
            }
            catch (Exception ex)
            {
                logger.Debug("RoboAPI token read failed: " + ex.Message);
                return null;
            }
        }

        private static readonly BrainQueryResult EmptyResult = BrainQueryResult.Unavailable;
    }
}
