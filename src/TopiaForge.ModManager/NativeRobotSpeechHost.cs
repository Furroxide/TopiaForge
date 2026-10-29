using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    // The runtime owns native interception. RobotKit owns head associations and consuming-context routing.
    internal sealed class NativeRobotSpeechHost : IInternalNativeSpeechHost
    {
        private const string ProviderId = "io.github.furroxide.topiaforge.robotkit";
        private readonly IModLifetime lifetime;
        private static Installation? active;
        [ThreadStatic] private static Playback? entering;

        private NativeRobotSpeechHost(IModLifetime lifetime) { this.lifetime = lifetime; }

        internal static IInternalNativeSpeechHost? ForProvider(string ownerModId, IModLifetime lifetime) =>
            string.Equals(ownerModId, ProviderId, StringComparison.OrdinalIgnoreCase)
                ? new NativeRobotSpeechHost(lifetime) : null;

        public IInternalNativeSpeechLease Install(IInternalNativeSpeechRouter router)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (router == null) throw new ArgumentNullException(nameof(router));
            lifetime.StoppingToken.ThrowIfCancellationRequested();
            if (active != null) throw new InvalidOperationException("Native robot speech already has a provider.");
            var lease = OwnerHarmonyLease.Create(ProviderId, "robot-speech", lifetime);
            var installation = new Installation(lease, router, lifetime.StoppingToken);
            active = installation;
            try
            {
                lifetime.Track(installation);
                var simple = typeof(AgentHead).GetMethod("Say", new[] { typeof(string), typeof(HintGroup), typeof(TaskGraph),
                    typeof(CancellationToken), typeof(FMODAudioClipAsset), typeof(bool) });
                var named = typeof(AgentHead).GetMethod("Say", new[] { typeof(string), typeof(string), typeof(HintGroup), typeof(TaskGraph),
                    typeof(CancellationToken), typeof(FMODAudioClipAsset), typeof(bool) });
                if (simple == null || named == null) throw new InvalidOperationException("Native robot speech bindings are unavailable.");
                lease.Patch(simple, prefix: typeof(NativeRobotSpeechHost).GetMethod(nameof(Say), BindingFlags.NonPublic | BindingFlags.Static));
                lease.Patch(named, prefix: typeof(NativeRobotSpeechHost).GetMethod(nameof(SayNamed), BindingFlags.NonPublic | BindingFlags.Static));
                installation.Ready();
                return installation;
            }
            catch
            {
                installation.Dispose();
                throw;
            }
        }

        private static bool Say(AgentHead __instance, string text, CancellationToken ct, FMODAudioClipAsset overrideSynth,
            bool subtitlesOnly, ref UniTask<AgentHead.SayResult> __result)
        {
            if (!TryRoute(__instance, text, ct, overrideSynth == null && !subtitlesOnly, out var result)) return true;
            __result = Route(result!);
            return false;
        }

        private static bool SayNamed(AgentHead __instance, string text, CancellationToken ct, FMODAudioClipAsset overrideSynth,
            bool subtitlesOnly, ref UniTask __result)
        {
            // Consume only this lease's exact initial playback entry. Reentrant calls, including calls to other
            // heads, cannot inherit a blanket thread-local bypass while the native method runs synchronously.
            if (entering != null && entering.Consume(__instance, text, ct)) return true;
            if (!TryRoute(__instance, text, ct, overrideSynth == null && !subtitlesOnly, out var result)) return true;
            __result = RouteNamed(result!);
            return false;
        }

        private static bool TryRoute(AgentHead head, string text, CancellationToken token, bool supported,
            out Task<OperationResult<bool>>? result)
        {
            result = null;
            var installation = active;
            var router = installation?.Router;
            return router != null && router.TryRoute(head, text, token, supported && installation!.IsAvailable, out result);
        }

        private static async UniTask<AgentHead.SayResult> Route(Task<OperationResult<bool>> result)
        {
            try
            {
                var outcome = await result;
                return outcome.TryGetValue(out var heard) ? heard ? AgentHead.SayResult.Ok : AgentHead.SayResult.TooFar : AgentHead.SayResult.InvalidTarget;
            }
            catch (Exception) { return AgentHead.SayResult.InvalidTarget; }
        }
        private static async UniTask RouteNamed(Task<OperationResult<bool>> result) => await Route(result);

        private sealed class Playback
        {
            private readonly AgentHead head;
            private readonly string text;
            private readonly CancellationToken token;
            private bool consumed;
            internal Playback(AgentHead head, string text, CancellationToken token)
            { this.head = head; this.text = text; this.token = token; }
            internal bool Consume(AgentHead candidate, string line, CancellationToken ct)
            {
                if (consumed || !ReferenceEquals(head, candidate) || !ReferenceEquals(text, line) || token != ct) return false;
                consumed = true;
                return true;
            }
        }

        private sealed class Installation : IInternalNativeSpeechLease
        {
            private readonly OwnerHarmonyLease patches;
            private readonly CancellationTokenSource stopping = new CancellationTokenSource();
            private readonly CancellationToken ownerStopping;
            private bool ready, closed;
            internal IInternalNativeSpeechRouter? Router { get; private set; }
            internal Installation(OwnerHarmonyLease patches, IInternalNativeSpeechRouter router, CancellationToken ownerStopping)
            { this.patches = patches; Router = router; this.ownerStopping = ownerStopping; }
            public bool IsAvailable => ready && !closed && !ownerStopping.IsCancellationRequested;
            internal void Ready()
            {
                ownerStopping.ThrowIfCancellationRequested();
                if (closed) throw new ObjectDisposedException(nameof(Installation));
                ready = true;
            }
            public IInternalNativeSpeechPlayback Register(object nativeHead, object association)
            {
                UnityMainThreadGuard.AssertCurrent();
                if (!IsAvailable || !ReferenceEquals(active, this)) throw new ObjectDisposedException(nameof(Installation));
                if (!(nativeHead is AgentHead head) || head == null) throw new ArgumentException("A live native head is required.", nameof(nativeHead));
                if (association == null || Router?.IsCurrent(head, association) != true)
                    throw new InvalidOperationException("The native head has no current speech association.");
                return new Registration(this, head, association, Router.AssociationStopping(head, association));
            }
            private bool Owns(Registration registration) => IsAvailable && ReferenceEquals(active, this)
                && !registration.Retired && !registration.AssociationStopping.IsCancellationRequested && registration.Head != null
                && Router?.IsCurrent(registration.Head, registration.Association) == true;
            private async Task PlayAsync(Registration registration, string text, CancellationToken token)
            {
                UnityMainThreadGuard.AssertCurrent();
                if (!Owns(registration)) throw new InvalidOperationException("The native speech association has ended.");
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stopping.Token, ownerStopping,
                    registration.Stopping, registration.AssociationStopping);
                linked.Token.ThrowIfCancellationRequested();
                var head = registration.Head;
                UniTask task;
                var previous = entering;
                entering = new Playback(head, text, linked.Token);
                try { task = head.Say(head.CurrentSpeakerName, text, HintGroup.FromAssets(Array.Empty<HintAsset>()), new TaskGraph("Robot speech"), linked.Token, null, false); }
                finally { entering = previous; }
                await task;
                linked.Token.ThrowIfCancellationRequested();
            }

            private sealed class Registration : IInternalNativeSpeechPlayback
            {
                private readonly Installation owner;
                private readonly CancellationTokenSource stopping = new CancellationTokenSource();
                internal AgentHead Head { get; }
                internal object Association { get; }
                internal CancellationToken AssociationStopping { get; }
                internal bool Retired { get; private set; }
                internal CancellationToken Stopping => stopping.Token;
                internal Registration(Installation owner, AgentHead head, object association, CancellationToken associationStopping)
                { this.owner = owner; Head = head; Association = association; AssociationStopping = associationStopping; }
                public bool IsAvailable => owner.Owns(this);
                public Task PlayAsync(string text, CancellationToken token) => owner.PlayAsync(this, text, token);
                public void Dispose()
                {
                    UnityMainThreadGuard.AssertCurrent();
                    if (Retired) return;
                    Retired = true;
                    try { stopping.Cancel(); }
                    finally { stopping.Dispose(); }
                }
            }
            public void Dispose()
            {
                UnityMainThreadGuard.AssertCurrent();
                try
                {
                    if (!closed)
                    {
                        closed = true;
                        try { stopping.Cancel(); }
                        finally { stopping.Dispose(); }
                    }
                }
                finally
                {
                    // Reserve the host until unpatch succeeds. A failed unpatch keeps owned heads fail-closed
                    // and cannot let stale prefixes invoke a replacement provider. Disposal remains retryable.
                    patches.Dispose();
                    var router = Router;
                    Router = null;
                    if (ReferenceEquals(active, this)) active = null;
                    router?.OnStopped();
                }
            }
        }
    }
}
