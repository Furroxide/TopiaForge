using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.Worlds;

namespace TopiaForge.ModManager.Tests
{
    internal static class WorldsSafetyTests
    {
        public static void Run()
        {
            OpenSandboxFallbackRejectsShellScenes();
            OpenSandboxFallbackGuardPrecedesArenaCreation();
            ManualUnsubscribeReleasesTheTrackedLifetimeNode();
            EarlyUnsubscribeBeforeLeaseAttachReleasesTheLateLease();
            StopDuringLeaseAttachLeavesNoSubscriberOrTrackedNode();
            OwnerFacadeUsesSelfReleasingSubscriptions();
            Console.WriteLine("All Worlds safety tests passed.");
        }

        private static void OpenSandboxFallbackRejectsShellScenes()
        {
            var knownGameplayScenes = new[] { "UgcPlay", "TestCity", "02 City Streets" };
            foreach (var scene in new string?[]
                     {
                         null,
                         string.Empty,
                         GameScenes.MainMenuSceneName,
                         "MainMenu_Remastered",
                         "BootScene",
                         "LevelLoader",
                         "SplashIntro"
                     })
            {
                Assert(!OpenSandboxFallbackPolicy.CanBuildInScene(scene, knownGameplayScenes),
                    "Open Sandbox fallback must reject shell scene '" + (scene ?? "<null>") + "'");
            }

            foreach (var scene in knownGameplayScenes)
            {
                Assert(OpenSandboxFallbackPolicy.CanBuildInScene(scene, knownGameplayScenes),
                    "Open Sandbox fallback should remain available in gameplay scene '" + scene + "'");
            }

            Assert(!OpenSandboxFallbackPolicy.CanBuildInScene("ArbitraryUnknownScene", knownGameplayScenes),
                "a non-shell name is not gameplay proof unless it appears in the registered/build-settings catalog");
        }

        private static void OpenSandboxFallbackGuardPrecedesArenaCreation()
        {
            var source = ReadWorldsServiceSource();
            const string methodMarker = "private WorldLoadResult LoadOpenSandbox(";
            const string nextMethodMarker = "private void ArmSandboxArena()";
            var methodStart = source.IndexOf(methodMarker, StringComparison.Ordinal);
            Assert(methodStart >= 0,
                "the Open Sandbox fallback source invariant must locate LoadOpenSandbox");
            var methodEnd = source.IndexOf(nextMethodMarker, methodStart, StringComparison.Ordinal);
            Assert(methodEnd > methodStart,
                "the Open Sandbox fallback source invariant must locate the end of LoadOpenSandbox");

            var method = source.Substring(methodStart, methodEnd - methodStart);
            var guard = method.IndexOf(
                "OpenSandboxFallbackPolicy.CanBuildInScene(activeScene, KnownGameplaySceneNames())",
                StringComparison.Ordinal);
            Assert(guard >= 0,
                "LoadOpenSandbox must consult the current-scene fallback safety policy");
            var failure = method.IndexOf("return WorldLoadResult.Fail(", guard, StringComparison.Ordinal);
            var arena = method.IndexOf("BuildArena();", StringComparison.Ordinal);
            var session = method.IndexOf("return StartSession(", arena, StringComparison.Ordinal);
            Assert(failure > guard && arena > failure && session > arena,
                "LoadOpenSandbox must reject a non-gameplay active scene before building an arena or session");
        }

        private static void ManualUnsubscribeReleasesTheTrackedLifetimeNode()
        {
            using var lifetime = new FakeModLifetime();
            var publisher = new TestPublisher<string>();
            var calls = 0;
            var disposedCallbacks = 0;
            var subscription = new OwnerEventSubscription<string>(
                _ => calls++,
                publisher.Subscribe,
                publisher.Unsubscribe,
                () => disposedCallbacks++);

            subscription.AttachPublisher();
            subscription.AttachLifetimeLease(lifetime.Track(subscription));
            Assert(publisher.SubscriberCount == 1 && lifetime.TrackedResourceCount == 1,
                "an active owner event subscription should be published and lifetime tracked");

            subscription.Dispose();
            publisher.Raise("after-unsubscribe");
            Assert(publisher.SubscriberCount == 0 && lifetime.TrackedResourceCount == 0,
                "manual unsubscribe should detach the handler and immediately release its tracked lifetime node");
            Assert(calls == 0 && disposedCallbacks == 1,
                "manual unsubscribe should suppress future callbacks and notify disposal exactly once");
        }

        private static void EarlyUnsubscribeBeforeLeaseAttachReleasesTheLateLease()
        {
            using var lifetime = new FakeModLifetime();
            var publisher = new TestPublisher<int>();
            var subscription = new OwnerEventSubscription<int>(
                _ => { },
                publisher.Subscribe,
                publisher.Unsubscribe,
                () => { });

            subscription.AttachPublisher();
            subscription.Dispose();
            subscription.AttachLifetimeLease(lifetime.Track(subscription));

            Assert(subscription.IsDisposed && publisher.SubscriberCount == 0,
                "an unsubscribe that wins before lease attachment should remain detached");
            Assert(lifetime.TrackedResourceCount == 0,
                "a lifetime lease returned after early unsubscribe should release itself immediately");
        }

        private static void StopDuringLeaseAttachLeavesNoSubscriberOrTrackedNode()
        {
            using var lifetime = new StopDuringTrackLifetime();
            var publisher = new TestPublisher<int>();
            var calls = 0;
            var subscription = new OwnerEventSubscription<int>(
                _ => calls++,
                publisher.Subscribe,
                publisher.Unsubscribe,
                () => { });

            subscription.AttachPublisher();
            subscription.AttachLifetimeLease(lifetime.Track(subscription));
            publisher.Raise(1);

            Assert(lifetime.IsStopping && subscription.IsDisposed,
                "the deterministic stop race should dispose the subscription before lease attachment finishes");
            Assert(lifetime.OutstandingLeaseCount == 0 && publisher.SubscriberCount == 0 && calls == 0,
                "stop during tracking must leave neither a subscriber nor a retained lifetime node");
        }

        private static void OwnerFacadeUsesSelfReleasingSubscriptions()
        {
            var source = ReadWorldsServiceSource();
            Assert(source.Contains("List<OwnerEventSubscription<WorldSession>>", StringComparison.Ordinal)
                   && source.Contains("List<OwnerEventSubscription<WorldSessionEnd>>", StringComparison.Ordinal),
                "both owner-facade session events must use the self-releasing subscription primitive");
            Assert(source.Contains(
                    "subscription.AttachLifetimeLease(lifetime.Track(subscription));",
                    StringComparison.Ordinal),
                "owner-facade subscriptions must attach the returned lifetime lease");
        }

        private static string ReadWorldsServiceSource()
        {
            var directory = Path.Combine(
                Program.FindRepoRoot(),
                "mods",
                "TopiaForge.Worlds");
            var files = Directory.GetFiles(directory, "WorldsService*.cs");
            Array.Sort(files, StringComparer.Ordinal);
            return string.Join(Environment.NewLine, Array.ConvertAll(files, File.ReadAllText));
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private sealed class TestPublisher<T>
        {
            private readonly List<Action<T>> handlers = new List<Action<T>>();

            public int SubscriberCount => handlers.Count;

            public void Subscribe(Action<T> handler)
            {
                handlers.Add(handler);
            }

            public void Unsubscribe(Action<T> handler)
            {
                handlers.Remove(handler);
            }

            public void Raise(T value)
            {
                foreach (var handler in handlers.ToArray())
                {
                    handler(value);
                }
            }
        }

        private sealed class StopDuringTrackLifetime : IModLifetime
        {
            private readonly CancellationTokenSource stopping = new CancellationTokenSource();
            private int outstandingLeaseCount;

            public CancellationToken StoppingToken => stopping.Token;
            public bool IsStopping => stopping.IsCancellationRequested;
            public int OutstandingLeaseCount => Volatile.Read(ref outstandingLeaseCount);

            public IDisposable Track(IDisposable resource)
            {
                if (resource == null)
                {
                    throw new ArgumentNullException(nameof(resource));
                }

                Interlocked.Increment(ref outstandingLeaseCount);
                var lease = new StopRaceLease(this, resource);
                stopping.Cancel();
                resource.Dispose();
                return lease;
            }

            public IDisposable Defer(Action cleanup)
            {
                return Track(new DeferredAction(cleanup));
            }

            public void Dispose()
            {
                if (!stopping.IsCancellationRequested)
                {
                    stopping.Cancel();
                }

                stopping.Dispose();
            }

            private void Release(IDisposable resource)
            {
                resource.Dispose();
                Interlocked.Decrement(ref outstandingLeaseCount);
            }

            private sealed class StopRaceLease : IDisposable
            {
                private StopDuringTrackLifetime? owner;
                private IDisposable? resource;

                public StopRaceLease(StopDuringTrackLifetime owner, IDisposable resource)
                {
                    this.owner = owner;
                    this.resource = resource;
                }

                public void Dispose()
                {
                    var currentOwner = Interlocked.Exchange(ref owner, null);
                    var currentResource = Interlocked.Exchange(ref resource, null);
                    if (currentOwner != null && currentResource != null)
                    {
                        currentOwner.Release(currentResource);
                    }
                }
            }

            private sealed class DeferredAction : IDisposable
            {
                private Action? cleanup;

                public DeferredAction(Action cleanup)
                {
                    this.cleanup = cleanup ?? throw new ArgumentNullException(nameof(cleanup));
                }

                public void Dispose()
                {
                    Interlocked.Exchange(ref cleanup, null)?.Invoke();
                }
            }
        }
    }
}
