using System;
using System.Collections.Generic;
using System.Text;
using TopiaForge.Multiplayer.Network;

namespace TopiaForge.Multiplayer.ControlPlane
{
    /// <summary>
    /// Why a request to the TopiaForge service did not give what was asked. The first group is this client's own; the
    /// rest are the service's error catalogue (ControlPlaneApi.md, section 1), read from the code and checked against the
    /// status the catalogue gives it. The service's message is never read: it is fixed per code, and it is text the client
    /// did not write.
    /// </summary>
    internal enum ServiceFailure
    {
        None = 0,

        /// <summary>No service origin is configured, so nothing was contacted.</summary>
        NotConfigured = 1,

        Unreachable = 2,
        TimedOut = 3,

        /// <summary>The service's certificate did not verify, or TLS broke.</summary>
        Tls = 4,

        /// <summary>The service answered with a redirect, which is never followed.</summary>
        Redirected = 5,

        TooLarge = 6,

        /// <summary>The answer broke HTTP, was not the JSON its endpoint answers, or held a value outside its rules.</summary>
        InvalidResponse = 7,

        Cancelled = 8,

        InvalidRequest = 20,
        InvalidDescriptor = 21,
        UnsupportedVisibility = 22,
        ChallengeInvalid = 23,
        ProofInvalid = 24,
        Unauthorized = 25,
        ServerBlocked = 26,
        TicketLimit = 27,
        NotFound = 28,
        MethodNotAllowed = 29,
        StaleDescriptor = 30,
        InviteLimit = 31,
        RoomFull = 32,
        PayloadTooLarge = 33,
        UnsupportedMediaType = 34,
        RateLimited = 35,
        Internal = 36,
        UpstreamFailed = 37,
        FeatureDisabled = 38,
        RelayUnavailable = 39,

        /// <summary>The deployment lacks its configuration (<c>service-unavailable</c>).</summary>
        ServiceMisconfigured = 40,

        /// <summary>A 4xx other than 429 the catalogue does not hold, or one whose code and status disagree.</summary>
        Refused = 41,

        /// <summary>A 5xx the catalogue does not hold, or one whose code and status disagree.</summary>
        Failed = 42
    }

    /// <summary>The empty answer of a request that only succeeds or fails, such as DELETE.</summary>
    internal sealed class ServiceDone
    {
        internal static readonly ServiceDone Instance = new ServiceDone();

        private ServiceDone()
        {
        }

        public override string ToString() => "done";
    }

    /// <summary>The value a service request produced, or why it did not; never both.</summary>
    internal readonly struct ServiceResult<T> where T : class
    {
        private ServiceResult(T? value, ServiceFailure failure, int? retryAfterSeconds)
        {
            Value = value;
            Failure = failure;
            RetryAfterSeconds = retryAfterSeconds;
        }

        internal T? Value { get; }

        internal ServiceFailure Failure { get; }

        /// <summary>Gets the delay the service asked for with a refusal, in seconds, when it gave one.</summary>
        internal int? RetryAfterSeconds { get; }

        internal bool Succeeded => Failure == ServiceFailure.None;

        internal static ServiceResult<T> Ok(T value) => new ServiceResult<T>(value ?? throw new ArgumentNullException(nameof(value)), ServiceFailure.None, null);

        internal static ServiceResult<T> Fail(ServiceFailure failure, int? retryAfterSeconds = null)
        {
            if (failure == ServiceFailure.None) throw new ArgumentException("A failed result names its failure.", nameof(failure));
            return new ServiceResult<T>(null, failure, retryAfterSeconds);
        }

        public override string ToString() => Succeeded ? "service result" : "service failure " + Failure;
    }

    /// <summary>Reads the service's error answers and sorts failures by what a caller does about them.</summary>
    internal static class ServiceErrors
    {
        internal const int MaximumErrorBytes = 4 * 1024;

        private static readonly Dictionary<string, (ServiceFailure Failure, int Status)> Catalogue = new Dictionary<string, (ServiceFailure, int)>(StringComparer.Ordinal)
        {
            ["invalid-request"] = (ServiceFailure.InvalidRequest, 400),
            ["invalid-descriptor"] = (ServiceFailure.InvalidDescriptor, 400),
            ["unsupported-visibility"] = (ServiceFailure.UnsupportedVisibility, 400),
            ["challenge-invalid"] = (ServiceFailure.ChallengeInvalid, 401),
            ["proof-invalid"] = (ServiceFailure.ProofInvalid, 401),
            ["unauthorized"] = (ServiceFailure.Unauthorized, 401),
            ["server-blocked"] = (ServiceFailure.ServerBlocked, 403),
            ["ticket-limit"] = (ServiceFailure.TicketLimit, 403),
            ["not-found"] = (ServiceFailure.NotFound, 404),
            ["method-not-allowed"] = (ServiceFailure.MethodNotAllowed, 405),
            ["stale-descriptor"] = (ServiceFailure.StaleDescriptor, 409),
            ["invite-limit"] = (ServiceFailure.InviteLimit, 409),
            ["room-full"] = (ServiceFailure.RoomFull, 409),
            ["payload-too-large"] = (ServiceFailure.PayloadTooLarge, 413),
            ["unsupported-media-type"] = (ServiceFailure.UnsupportedMediaType, 415),
            ["rate-limited"] = (ServiceFailure.RateLimited, 429),
            ["internal"] = (ServiceFailure.Internal, 500),
            ["upstream-failed"] = (ServiceFailure.UpstreamFailed, 502),
            ["feature-disabled"] = (ServiceFailure.FeatureDisabled, 503),
            ["relay-unavailable"] = (ServiceFailure.RelayUnavailable, 503),
            ["service-unavailable"] = (ServiceFailure.ServiceMisconfigured, 503)
        };

        /// <summary>
        /// Reads an error answer: <c>{"error":{"code","message"}}</c>, canonical JSON. A code the catalogue holds counts only
        /// with the status the catalogue gives it; anything else falls back to the status alone. A 429 is a rate limit
        /// whatever its body says, as when the platform in front of the service writes its own page. The message is never
        /// read.
        /// </summary>
        internal static ServiceFailure Read(ControlPlaneResponse response)
        {
            if (response == null) throw new ArgumentNullException(nameof(response));
            var fallback = response.Status >= 500 ? ServiceFailure.Failed : response.Status == 429 ? ServiceFailure.RateLimited : ServiceFailure.Refused;
            if (!response.IsJson || response.Body.Length == 0 || response.Body.Length > MaximumErrorBytes) return fallback;
            if (!CanonicalJson.TryParse(response.Body, MaximumErrorBytes, out var node, out _)) return fallback;
            if (!(node is JsonObjectNode root) || !root.TryGet("error", out var errorNode) || !(errorNode is JsonObjectNode error)) return fallback;
            if (!error.TryGet("code", out var codeNode) || !(codeNode is JsonStringNode code)) return fallback;
            if (!error.TryGet("message", out var messageNode) || !(messageNode is JsonStringNode)) return fallback;
            return Catalogue.TryGetValue(code.Value, out var entry) && entry.Status == response.Status ? entry.Failure : fallback;
        }

        /// <summary>Gets the failure for a transport that could not read an answer at all.</summary>
        internal static ServiceFailure Of(ControlPlaneTransportFailure failure)
        {
            switch (failure)
            {
                case ControlPlaneTransportFailure.TimedOut:
                    return ServiceFailure.TimedOut;
                case ControlPlaneTransportFailure.Tls:
                    return ServiceFailure.Tls;
                case ControlPlaneTransportFailure.Redirected:
                    return ServiceFailure.Redirected;
                case ControlPlaneTransportFailure.TooLarge:
                    return ServiceFailure.TooLarge;
                case ControlPlaneTransportFailure.Malformed:
                    return ServiceFailure.InvalidResponse;
                case ControlPlaneTransportFailure.Cancelled:
                    return ServiceFailure.Cancelled;
                default:
                    return ServiceFailure.Unreachable;
            }
        }

        /// <summary>
        /// Gets whether the same request may succeed later without anything changing on this side: the service or the path
        /// to it is down or failing, or its answer could not be read. A caller retries these with backoff.
        /// </summary>
        internal static bool IsTransient(ServiceFailure failure) =>
            failure == ServiceFailure.Unreachable || failure == ServiceFailure.TimedOut || failure == ServiceFailure.Tls ||
            failure == ServiceFailure.InvalidResponse || failure == ServiceFailure.TooLarge || failure == ServiceFailure.Redirected ||
            failure == ServiceFailure.Internal || failure == ServiceFailure.UpstreamFailed || failure == ServiceFailure.Failed ||
            failure == ServiceFailure.ServiceMisconfigured || failure == ServiceFailure.RoomFull;

        /// <summary>Writes a JSON body as this client sends every one: canonical, as the service requires.</summary>
        internal static byte[] Body(JsonObjectNode body) => CanonicalJson.Serialize(body);

        internal static string Utf8(byte[] bytes) => new UTF8Encoding(false, true).GetString(bytes);
    }
}
