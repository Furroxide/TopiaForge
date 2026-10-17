using System;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Multiplayer.ControlPlane
{
    /// <summary>Why no answer could be read from the service. None of them names an address, a host or anything the service sent.</summary>
    internal enum ControlPlaneTransportFailure
    {
        None = 0,

        /// <summary>The name did not resolve, nothing answered, or the connection broke.</summary>
        Unreachable = 1,

        /// <summary>The service did not answer within the request's time limit.</summary>
        TimedOut = 2,

        /// <summary>TLS failed: the certificate did not verify for the service's name, or the handshake broke.</summary>
        Tls = 3,

        /// <summary>The service answered with a redirect, which this client never follows.</summary>
        Redirected = 4,

        /// <summary>The answer was larger than the request allows.</summary>
        TooLarge = 5,

        /// <summary>The answer was not well-formed HTTP/1.1.</summary>
        Malformed = 6,

        /// <summary>The caller gave up on the request.</summary>
        Cancelled = 7
    }

    internal enum ControlPlaneMethod
    {
        Get = 0,
        Post = 1,
        Put = 2,
        Delete = 3
    }

    /// <summary>
    /// One request to the service: a method, a path under <c>/v1/</c>, an optional bearer credential and an optional
    /// canonical JSON body, and the most the answer may hold. The credential is a host token or a join ticket; it never
    /// appears in a path and nothing prints it.
    /// </summary>
    internal sealed class ControlPlaneRequest
    {
        internal const int MaximumPathLength = 256;
        internal const int MaximumCredentialLength = 256;

        internal ControlPlaneRequest(ControlPlaneMethod method, string path, string? bearer, byte[]? body, int maximumResponseBytes)
        {
            if (!IsPath(path)) throw new ArgumentException("A service path starts with /v1/ and holds only plain path characters.", nameof(path));
            if (bearer != null && !IsCredential(bearer)) throw new ArgumentException("A service credential is 1 to 256 base64url characters.", nameof(bearer));
            if ((method == ControlPlaneMethod.Get || method == ControlPlaneMethod.Delete) && body != null) throw new ArgumentException("GET and DELETE send no body.", nameof(body));
            if ((method == ControlPlaneMethod.Post || method == ControlPlaneMethod.Put) && body == null) throw new ArgumentException("POST and PUT send a body.", nameof(body));
            if (maximumResponseBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumResponseBytes));
            Method = method;
            Path = path;
            Bearer = bearer;
            Body = body;
            MaximumResponseBytes = maximumResponseBytes;
        }

        internal ControlPlaneMethod Method { get; }

        internal string Path { get; }

        internal string? Bearer { get; }

        internal byte[]? Body { get; }

        internal int MaximumResponseBytes { get; }

        internal string MethodText => Method == ControlPlaneMethod.Get ? "GET" : Method == ControlPlaneMethod.Post ? "POST" : Method == ControlPlaneMethod.Put ? "PUT" : "DELETE";

        /// <summary>Prints the method only: paths carry server ids, and the credential is a secret.</summary>
        public override string ToString() => "service request (" + MethodText + ")";

        /// <summary>Whether a credential is safe in a header: the base64url alphabet only, so it can never end a header line.</summary>
        internal static bool IsCredential(string? value)
        {
            if (string.IsNullOrEmpty(value) || value!.Length > MaximumCredentialLength) return false;
            foreach (var character in value)
            {
                var letter = (character >= 'a' && character <= 'z') || (character >= 'A' && character <= 'Z');
                if (!letter && (character < '0' || character > '9') && character != '-' && character != '_') return false;
            }

            return true;
        }

        private static bool IsPath(string? path)
        {
            if (path == null || path.Length > MaximumPathLength || !path.StartsWith("/v1/", StringComparison.Ordinal)) return false;
            foreach (var character in path)
            {
                var letter = (character >= 'a' && character <= 'z') || (character >= 'A' && character <= 'Z');
                if (!letter && (character < '0' || character > '9') && character != '/' && character != '-' && character != '_') return false;
            }

            return path.IndexOf("//", StringComparison.Ordinal) < 0;
        }
    }

    /// <summary>
    /// What the service answered, or why nothing could be read. The body is at most the request's bound, and it is the
    /// service's to describe: callers parse it strictly and never log, show or keep its text.
    /// </summary>
    internal sealed class ControlPlaneResponse
    {
        private ControlPlaneResponse(ControlPlaneTransportFailure failure, int status, byte[] body, bool json, int? retryAfterSeconds)
        {
            Failure = failure;
            Status = status;
            Body = body;
            IsJson = json;
            RetryAfterSeconds = retryAfterSeconds;
        }

        internal ControlPlaneTransportFailure Failure { get; }

        /// <summary>Gets the HTTP status, from 200 to 599; zero when the request failed.</summary>
        internal int Status { get; }

        internal byte[] Body { get; }

        /// <summary>Gets whether the service labelled the body <c>application/json</c>.</summary>
        internal bool IsJson { get; }

        /// <summary>Gets the delay a single, plain <c>Retry-After</c> header asked for, in seconds, when it named at most a day.</summary>
        internal int? RetryAfterSeconds { get; }

        internal bool Succeeded => Failure == ControlPlaneTransportFailure.None;

        internal static ControlPlaneResponse Answered(int status, byte[] body, bool json, int? retryAfterSeconds)
        {
            if (status < 200 || status > 599) throw new ArgumentOutOfRangeException(nameof(status));
            return new ControlPlaneResponse(ControlPlaneTransportFailure.None, status, body ?? throw new ArgumentNullException(nameof(body)), json, retryAfterSeconds);
        }

        internal static ControlPlaneResponse Failed(ControlPlaneTransportFailure failure)
        {
            if (failure == ControlPlaneTransportFailure.None) throw new ArgumentException("A failed response names its failure.", nameof(failure));
            return new ControlPlaneResponse(failure, 0, Array.Empty<byte>(), false, null);
        }

        /// <summary>Prints the status or the failure only; the body is the service's own text.</summary>
        public override string ToString() => Succeeded ? "service answer " + Status.ToString(System.Globalization.CultureInfo.InvariantCulture) : "service failure " + Failure;
    }

    /// <summary>
    /// Carries requests to the service's origin and nowhere else. The game's transport is a small HTTPS client of its own
    /// (<see cref="HttpsControlPlaneTransport"/>); tests put a fake service behind the same interface. An implementation
    /// never throws for anything the network or the service does: every failure is a <see cref="ControlPlaneResponse"/>.
    /// </summary>
    internal interface IControlPlaneTransport
    {
        Task<ControlPlaneResponse> SendAsync(ControlPlaneRequest request, CancellationToken cancellation);
    }
}
