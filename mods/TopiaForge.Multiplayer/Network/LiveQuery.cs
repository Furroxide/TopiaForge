using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Multiplayer.Engine;
using TopiaForge.Multiplayer.Transport;

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>
    /// The checks a resolved target must pass before anything connects to it, in order: the game build, the protocol's
    /// major version, the path against the player's privacy choice, and the content. A join and a query share them.
    /// </summary>
    internal static class JoinChecks
    {
        internal const string DirectNotAllowedDetail = "Joining directly would show the host your address; allow it for this session to continue.";

        /// <summary>Runs the checks on a resolved target; <paramref name="check"/> is the content check when it ran.</summary>
        internal static LiveFailure Evaluate(ResolvedTarget resolved, bool allowDirect, ILiveSessionContent content, out string detail, out ContentCheck? check)
        {
            if (resolved == null) throw new ArgumentNullException(nameof(resolved));
            if (content == null) throw new ArgumentNullException(nameof(content));
            check = null;
            // The details are this game's own words: the host's build and protocol are in its descriptor, for the interface
            // to show as the host's.
            var descriptor = resolved.Descriptor!.Descriptor;
            if (descriptor.GameBuild != content.GameBuild)
            {
                detail = "The host runs another Robotopia build; this game runs " + content.GameBuild + ".";
                return LiveFailure.WrongGameBuild;
            }

            if (Major(descriptor.ProtocolVersion) != Major(SessionEngine.ProtocolVersion))
            {
                detail = "The host speaks a multiplayer protocol this game cannot.";
                return LiveFailure.ProtocolMismatch;
            }

            if (resolved.Path!.Kind == TransportPathKind.Direct && !allowDirect)
            {
                detail = DirectNotAllowedDetail;
                return LiveFailure.DirectNotAllowed;
            }

            check = content.Check(descriptor);
            if (!check.IsReady)
            {
                detail = "This game's mods do not match the host's.";
                return LiveFailure.ContentMismatch;
            }

            detail = string.Empty;
            return LiveFailure.None;
        }

        private static string Major(string version)
        {
            var dot = version.IndexOf('.');
            return dot < 0 ? version : version.Substring(0, dot);
        }
    }

    /// <summary>
    /// A look at one server without joining it: the target is resolved and its descriptor verified off the main thread,
    /// then checked as a join would check it. Nothing connects, and relay-only is honoured exactly as when joining. Read
    /// it on the main thread; it completes at the first read after the lookup lands.
    /// </summary>
    internal sealed class LiveQuery : IDisposable
    {
        private readonly ILiveSessionContent content;
        private readonly bool allowDirect;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private Task<ResolvedTarget>? resolving;
        private bool disposed;

        internal LiveQuery(IJoinTargetResolver resolver, JoinTarget target, bool allowDirect, ILiveSessionContent content)
        {
            if (resolver == null) throw new ArgumentNullException(nameof(resolver));
            if (target == null) throw new ArgumentNullException(nameof(target));
            this.content = content ?? throw new ArgumentNullException(nameof(content));
            this.allowDirect = allowDirect;
            resolving = resolver.ResolveAsync(target, allowDirect, null, cancellation.Token);
        }

        /// <summary>Gets whether the lookup finished; reading it completes the checks once the resolve has landed.</summary>
        internal bool IsCompleted
        {
            get
            {
                if (resolving != null && resolving.IsCompleted) Finish();
                return resolving == null;
            }
        }

        internal LiveFailure Failure { get; private set; }

        /// <summary>Gets what went wrong, for the player. It never names an address.</summary>
        internal string Detail { get; private set; } = string.Empty;

        /// <summary>Gets the verified descriptor once the target resolved, even when a later check failed.</summary>
        internal ServerDescriptor? Descriptor { get; private set; }

        internal ContentCheck? Content { get; private set; }

        /// <summary>Gets, when a typed host now serves a different server, that server's id.</summary>
        internal string? ChangedServerId { get; private set; }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (resolving != null)
            {
                Failure = LiveFailure.Cancelled;
                resolving = null;
            }

            // The resolve may still be running on its token, so the source is cancelled but not disposed.
            cancellation.Cancel();
        }

        private void Finish()
        {
            var task = resolving!;
            resolving = null;
            if (task.IsCanceled || task.IsFaulted)
            {
                Failure = task.IsCanceled ? LiveFailure.Cancelled : LiveFailure.Unreachable;
                Detail = "The host could not be reached.";
                return;
            }

            var resolved = task.Result;
            if (resolved.Failure != LiveFailure.None)
            {
                Failure = resolved.Failure;
                Detail = resolved.Detail;
                ChangedServerId = resolved.ServedServerId;
                return;
            }

            Descriptor = resolved.Descriptor!.Descriptor;
            Failure = JoinChecks.Evaluate(resolved, allowDirect, content, out var detail, out var check);
            Detail = detail;
            Content = check;
        }
    }
}
