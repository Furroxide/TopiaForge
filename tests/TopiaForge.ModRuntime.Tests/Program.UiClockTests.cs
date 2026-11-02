using System;
using System.IO;
using System.Reflection;
using System.Threading;
using TopiaForge.ModManager;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using RuntimeUnderTest = TopiaForge.ModManager.ModRuntime;

namespace TopiaForge.ModRuntime.Tests
{
    internal static partial class Program
    {
        private static int RunUiClockCases()
        {
            var root = Directory.CreateTempSubdirectory("TopiaForgeUiClockTests-").FullName;
            try { TestUiClockAcrossInitialAttachment(root); return 0; }
            catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
            finally { TryDelete(root); }
        }

        private static void TestUiClockAcrossInitialAttachment(string root)
        {
            const string entry = "TopiaForge.ValidTestMod.RuntimeUiClockMod";
            const string providerId = "io.github.furroxide.topiaforge.multiplayer";
            const string providerEntry = "TopiaForge.SecondTestMod.RuntimeUiClockProviderMod";
            const string providerAssembly = "TopiaForge.SecondTestMod.dll";
            var consumer = NewFixture(root, "ui-clock-consumer", entry);
            var provider = NewFixture(root, "ui-clock-provider", providerEntry, providerAssembly, packageId: providerId);
            var host = new UiClockRuntimeHost();
            var runtime = new RuntimeUnderTest(consumer.Paths, consumer.Logger,
                new ManifestValidationContext("0.0.2309", TopiaForgeVersions.LoaderVersion, TopiaForgeVersions.SdkVersion,
                    platform: "windows", architecture: "x64", contentTargets: new[] { "code", "standalonewindows64" },
                    enforceRuntimeCompatibility: true), consumer.Observer, host);
            var network = new LoopbackNetPair();
            // Its provider was constructed much earlier than the runtime. Attachment must not switch to this origin.
            for (var i = 0; i < 6000; i++) network.Host.Pump();
            var probe = Assembly.LoadFrom(Path.Combine(consumer.Package.PackagePath, FixtureAssembly)).GetType(entry)!;
            var providerProbe = Assembly.LoadFrom(Path.Combine(provider.Package.PackagePath, providerAssembly)).GetType(providerEntry)!;
            IModContext? author = null, laterScope = null;
            IUiSurface? surface = null;
            UiCountdown? timer = null;
            MirroredHudPresenter? shared = null;
            double deadline = 0, beforeAttach = 0;
            var initialEpoch = string.Empty;
            Action<IModContext> onLoad = context =>
            {
                Assert(runtime.Multiplayer.Control == null, "every OnLoad precedes the runtime's provider connection");
                if (context.Identity.Id == providerId)
                {
                    laterScope = context;
                    Thread.Sleep(25);
                    beforeAttach = author!.Ui.ClockTime;
                    Assert(context.Extensions.Register<IInternalMultiplayerControl>(network.Host).Succeeded,
                        "the provider publishes its control during OnLoad");
                    return;
                }

                author = context;
                initialEpoch = runtime.Multiplayer.UiClockEpoch;
                deadline = context.Ui.ClockTime + 30;
                timer = new UiCountdown(deadline, id: "timer");
                var created = context.Ui.CreateSurface(new UiSurfaceRequest("on-load", "Timer", "", content: timer));
                Assert(created.Succeeded, "OnLoad creates an ordinary runtime UI surface before provider attachment");
                surface = created.Value;
                var sharedLifetime = new FakeModLifetime();
                context.Lifetime.Track(sharedLifetime);
                var sharedContext = runtime.Replication.Bind("clock-shared",
                    new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.GamemodeServer), sharedLifetime);
                var sharedUi = new NetUiService(new FakeUiService(sharedLifetime), sharedContext);
                var sharedResult = sharedUi.CreateSurface(new UiSurfaceRequest("on-load-hud", "Timer", "",
                    UiSurfaceKind.Hud, content: timer));
                Assert(sharedResult.Succeeded && sharedResult.Value is MirroredHudPresenter,
                    "the initial server scope caches an eligible shared HUD");
                shared = (MirroredHudPresenter)sharedResult.Value!;
            };
            probe.GetField("OnLoadHook")!.SetValue(null, onLoad);
            providerProbe.GetField("OnLoadHook")!.SetValue(null, onLoad);
            try
            {
                runtime.Load(new[] { consumer.Package, provider.Package });
                Assert(runtime.IsLoaded(consumer.Manifest.Id) && runtime.IsLoaded(providerId),
                    "both clock packages load: consumerLoaded=" + runtime.IsLoaded(consumer.Manifest.Id) +
                    "; consumerFailure=" + (runtime.GetLoadFailure(consumer.Manifest.Id) ?? "<none>") +
                    "; providerLoaded=" + runtime.IsLoaded(providerId) +
                    "; providerFailure=" + (runtime.GetLoadFailure(providerId) ?? "<none>") +
                    "; consumerOnLoad=" + (author != null) + "; providerOnLoad=" + (laterScope != null) +
                    "; runtimeWarnings=[" + string.Join(" | ", consumer.Logger.Warnings) + "]" +
                    "; runtimeErrors=[" + string.Join(" | ", consumer.Logger.Errors) + "]" +
                    "; runtimeObserver=[" + string.Join(" | ", consumer.Observer.Events) + "]" +
                    "; providerFixtureWarnings=[" + string.Join(" | ", provider.Logger.Warnings) + "]" +
                    "; providerFixtureErrors=[" + string.Join(" | ", provider.Logger.Errors) + "]" +
                    "; providerFixtureObserver=[" + string.Join(" | ", provider.Observer.Events) + "]");
                Assert(ReferenceEquals(runtime.Multiplayer.Control, network.Host), "Load connects the published provider after OnLoad");
                Assert(timer != null && surface != null && author != null && laterScope != null, "both OnLoad callbacks completed");
                var loadedAuthor = author!;
                var loadedLaterScope = laterScope!;
                var loadedTimer = timer!;
                var loadedSurface = surface!;
                Assert(runtime.Multiplayer.UiClockEpoch == network.Host.UiClockEpoch && runtime.Multiplayer.UiClockEpoch != initialEpoch &&
                    UiClockBinding.IsCurrent(loadedTimer, runtime.Multiplayer.UiClockEpoch),
                    "initial attachment adopts only the already-bound initial timeline into the provider identity");
                Assert(loadedAuthor.Ui.ClockTime >= beforeAttach && loadedAuthor.Ui.ClockTime < deadline &&
                    loadedAuthor.Ui.ClockTime == loadedLaterScope.Ui.ClockTime && loadedTimer.EndsAt == deadline,
                    "delayed attachment preserves deadline progression and the shared scope origin");
                Assert(loadedSurface.SetContent(loadedTimer).Succeeded, "the previously created surface stays current after attachment");
                AssertSharedClockPayload(shared!, runtime.Multiplayer.UiClockEpoch, deadline);
                var epoch = runtime.Multiplayer.UiClockEpoch;
                var beforePump = loadedAuthor.Ui.ClockTime;
                runtime.DispatchUpdate(0);
                Assert(loadedAuthor.Ui.ClockTime > beforePump && loadedAuthor.Ui.ClockTime < deadline,
                    "the first provider pump advances the adopted timeline without the old provider-origin jump");
                network.StartHosting();
                runtime.DispatchUpdate(0);
                Assert(runtime.Multiplayer.Role == InternalSessionRole.ListenHost && runtime.Multiplayer.UiClockEpoch == epoch &&
                    loadedSurface.SetContent(loadedTimer).Succeeded && loadedTimer.EndsAt == deadline,
                    "opening the same world to peers preserves the OnLoad timer and clock identity");
                AssertSharedClockPayload(shared!, runtime.Multiplayer.UiClockEpoch, deadline);
                var elapsed = loadedAuthor.Ui.ClockTime;
                network.ReplaceSession();
                Assert(runtime.Multiplayer.UiClockEpoch != epoch && loadedAuthor.Ui.ClockTime == elapsed &&
                    loadedSurface.SetContent(loadedTimer).ErrorCode == ModErrorCode.InvalidArgument,
                    "actual runtime session replacement retires the adopted timer without resetting elapsed time");
                var fresh = new UiCountdown(loadedAuthor.Ui.ClockTime + 5, id: "timer");
                Assert(loadedSurface.SetContent(fresh).Succeeded, "a fresh timer works after session replacement");
                network.StopHosting();
                runtime.DispatchUpdate(0);
                epoch = runtime.Multiplayer.UiClockEpoch;
                runtime.Multiplayer.BeginUiWorld();
                Assert(runtime.Multiplayer.UiClockEpoch != epoch && loadedAuthor.Ui.ClockTime == 0 &&
                    loadedSurface.SetContent(fresh).ErrorCode == ModErrorCode.InvalidArgument,
                    "a later solo world rotates identity and resets origin instead of adopting again");
                Console.WriteLine("Runtime UI clock: OnLoad, delayed Connect, same-world hosting and replacement passed.");
            }
            finally
            {
                probe.GetField("OnLoadHook")!.SetValue(null, null);
                providerProbe.GetField("OnLoadHook")!.SetValue(null, null);
                runtime.UnloadAll();
            }
        }

        private static void AssertSharedClockPayload(MirroredHudPresenter shared, string epoch, double deadline)
        {
            var read = new WorldWireReader();
            read.Reset(shared.Payload!);
            Assert(read.TryReadHeader(out var kind) && kind == 0, "the adopted HUD retains its bounded payload");
            _ = UiNodeWireCodec.Id(read);
            _ = UiNodeWireCodec.Text(read); _ = UiNodeWireCodec.Text(read);
            _ = UiNodeWireCodec.Number(read); _ = UiNodeWireCodec.Number(read);
            _ = UiNodeWireCodec.Byte(read, 8); _ = UiNodeWireCodec.Byte(read, 1); _ = UiNodeWireCodec.Byte(read, 1);
            var decoded = UiNodeWireCodec.Read(read, (_, _) => { }, epoch);
            UiNodeWireCodec.End(read);
            Assert(decoded is UiCountdown countdown && countdown.EndsAt == deadline,
                "the cached OnLoad HUD serializes the adopted epoch and original deadline without an author update");
        }

        private sealed class UiClockRuntimeHost : IRuntimeGameplayHost, IMultiplayerPlatform
        {
            public event Action<GameTimeSample>? FixedUpdate { add { } remove { } }
            public event Action<GameTimeSample>? LateUpdate { add { } remove { } }
            public bool RunInBackground { get; set; }
            public GameplayContextServices Create(string ownerModId, string packagePath, string dataPath,
                IModLifetime lifetime, IModLogger logger, ModNetProfile profile, NativeTransitionAccessSlot? transitionAccess = null)
            {
                var other = GameplayContextServices.Unavailable(lifetime);
                var fakeLifetime = new FakeModLifetime();
                lifetime.Track(fakeLifetime);
                return new GameplayContextServices(other.Input, other.LocalPlayer, other.Entities, other.Physics, other.Time,
                    other.Scheduler, other.Scenes, other.Interactions, other.Items, other.Assets, other.Audio,
                    new FakeUiService(fakeLifetime), other.UnityInterop, other.SceneTransitions);
            }
            public GameTimeSample BeginFrame(float deltaTime) => new GameTimeSample(GameLoopPhase.Frame, deltaTime, deltaTime, 0d, 0);
            public void Dispose() { }
        }
    }
}
