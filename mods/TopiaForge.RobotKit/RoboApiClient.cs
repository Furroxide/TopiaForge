using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.RobotKit
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
        internal const int MaxTokenFileBytes = 32 * 1024;
        private const int MaxCheck3ResponseBytes = 256 * 1024;
        private const int MaxSttResponseBytes = 64 * 1024;
        private const int MaxSttRequestBytes = 2 * 1024 * 1024;

        // One process-wide client (creating one per call exhausts sockets). Timeout is managed per-call via a linked
        // CancellationTokenSource, so the handler-level timeout is left generous.
        private static readonly HttpClient Http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        private readonly string tokenFilePath;
        private readonly string endpoint;
        private readonly string sttEndpoint;
        private readonly bool backendEnabled;
        private readonly string sessionId;
        private readonly IModLogger logger;

        private string? cachedToken;
        private bool tokenLoaded;

        public RoboApiClient(string tokenFilePath, string sessionId, IModLogger logger)
        {
            this.tokenFilePath = tokenFilePath;
            this.sessionId = sessionId;
            this.logger = logger;

            var trimmedRoot = ResolveBackendRoot(Environment.GetEnvironmentVariable("ROBOAPI_BACKEND_ROOT"), logger);
            backendEnabled = trimmedRoot.Length > 0;
            endpoint = backendEnabled ? trimmedRoot + Check3Route : string.Empty;
            sttEndpoint = backendEnabled ? trimmedRoot + SttRoute : string.Empty;
        }

        // True when a usable token is resolvable. Reads the token file at most once (until invalidated), so polling
        // this per frame does not hit disk repeatedly.
        public bool HasToken => backendEnabled && TryGetToken(out _);

        // Run one /agent/check3 call. Returns an unavailable result (never throws) when there is no token, the call
        // times out, the network fails, or the gateway rejects the token (401, which also invalidates the cache).
        public async Task<OperationResult<BrainQueryResult>> Check3Async(
            BrainQueryRequest request,
            float timeoutSeconds,
            CancellationToken ct)
        {
            if (!backendEnabled || !TryGetToken(out var token))
            {
                return OperationResult<BrainQueryResult>.Failure(
                    ModErrorCode.Unavailable,
                    "Robot brain credentials are unavailable.");
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
                    .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                    .ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    // The 24h token rotated/expired; drop it so the next attempt re-reads the file.
                    InvalidateToken();
                    return OperationResult<BrainQueryResult>.Failure(
                        ModErrorCode.Unavailable,
                        "Robot brain credentials were rejected.");
                }

                if (!response.IsSuccessStatusCode)
                {
                    return OperationResult<BrainQueryResult>.Failure(
                        ModErrorCode.External,
                        "Robot brain request failed with HTTP " + (int)response.StatusCode + ".");
                }

                var text = await ReadBoundedContentAsync(response.Content, MaxCheck3ResponseBytes, timeoutCts.Token)
                    .ConfigureAwait(false);
                return RoboApiProtocol.ParseCheck3Response(text);
            }
            catch (OperationCanceledException ex)
            {
                logger.Debug("RoboAPI brain query failed: " + ex.GetType().Name + ": " + ex.Message);
                return OperationResult<BrainQueryResult>.Failure(
                    ModErrorCode.Cancelled,
                    ct.IsCancellationRequested
                        ? "Robot brain query was cancelled."
                        : "Robot brain query timed out.");
            }
            catch (Exception ex)
            {
                logger.Debug("RoboAPI brain query failed: " + ex.GetType().Name + ": " + ex.Message);
                return OperationResult<BrainQueryResult>.Failure(
                    ModErrorCode.External,
                    "Robot brain query could not reach the backend.");
            }
        }

        // Run one /agent/stt speech-to-text call. `gzippedPcm` must already be gzip-compressed 16 kHz mono PCM16-LE
        // (the only format the backend accepts — see Robotopia base-game conversation-input protocol). Returns the transcript,
        // or null (never throws) when there is no token, the call times out/fails, the gateway rejects the token, or
        // nothing usable came back.
        public async Task<string?> SttAsync(byte[] gzippedPcm, float timeoutSeconds, CancellationToken ct)
        {
            if (!backendEnabled || gzippedPcm == null || gzippedPcm.Length == 0 ||
                gzippedPcm.Length > MaxSttRequestBytes || !TryGetToken(out var token))
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
                    .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                    .ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    InvalidateToken();
                    return null;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var text = await ReadBoundedContentAsync(response.Content, MaxSttResponseBytes, timeoutCts.Token)
                    .ConfigureAwait(false);
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

                return RoboApiProtocol.ParseAgentToken(ReadBoundedUtf8File(tokenFilePath, MaxTokenFileBytes));
            }
            catch (Exception ex)
            {
                logger.Debug("RoboAPI token read failed: " + ex.Message);
                return null;
            }
        }

        internal static string ResolveBackendRoot(string? configuredRoot, IModLogger logger)
        {
            var hasExplicitOverride = configuredRoot != null;
            var candidate = hasExplicitOverride ? configuredRoot!.Trim() : DefaultBackendRoot;
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrEmpty(uri.Host) ||
                !string.IsNullOrEmpty(uri.UserInfo) ||
                !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment))
            {
                if (hasExplicitOverride)
                {
                    logger.Warn(
                        "ROBOAPI_BACKEND_ROOT is explicitly set but invalid. Remote AI and speech-to-text are " +
                        "disabled; remove the variable to use the built-in HTTPS endpoint, or set a valid " +
                        "absolute HTTPS root without credentials, query, or fragment.");
                }

                return hasExplicitOverride ? string.Empty : DefaultBackendRoot;
            }

            return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }

        private static async Task<string> ReadBoundedContentAsync(HttpContent content, int maximumBytes, CancellationToken token)
        {
            if (content.Headers.ContentLength.HasValue && content.Headers.ContentLength.Value > maximumBytes)
            {
                throw new InvalidDataException("RoboAPI response exceeds " + maximumBytes + " bytes.");
            }

            using var input = await content.ReadAsStreamAsync().ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            var total = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (total > maximumBytes - read)
                {
                    throw new InvalidDataException("RoboAPI response exceeds " + maximumBytes + " bytes.");
                }

                output.Write(buffer, 0, read);
                total += read;
            }

            return new UTF8Encoding(false, true).GetString(output.GetBuffer(), 0, total);
        }

        private static string ReadBoundedUtf8File(string path, int maximumBytes)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > maximumBytes)
            {
                throw new InvalidDataException("Token file exceeds " + maximumBytes + " bytes.");
            }

            using var output = new MemoryStream((int)stream.Length);
            var buffer = new byte[4096];
            var total = 0;
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                if (total > maximumBytes - read)
                {
                    throw new InvalidDataException("Token file grew beyond " + maximumBytes + " bytes.");
                }

                output.Write(buffer, 0, read);
                total += read;
            }

            return new UTF8Encoding(false, true).GetString(output.GetBuffer(), 0, total);
        }

    }
}
