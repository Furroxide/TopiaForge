using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using TopiaForge.ModManager;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using RuntimeUnderTest = TopiaForge.ModManager.ModRuntime;

namespace TopiaForge.ModRuntime.Tests
{
    internal static partial class Program
    {
        private const string ChronosId = "io.github.furroxide.topiaforge.chronos";
        private const string TimeGamemodeId = "tests.time-gamemode";

        /// <summary>
        /// One game for the pause and time tests: a runtime over a gameplay host whose game clock the test reads, with the
        /// real Chronos package and a gamemode's package that depends on it, and the gamemode's time control as each part of it
        /// resolves it: its package code, and its server, world and participant scopes.
        /// </summary>
        private sealed class TimeGame
        {
            private readonly ModPackage chronos;
            private readonly ModPackage gamemode;
            private readonly List<ModContextScope> scopes = new List<ModContextScope>();

            internal TimeGame(string root, LoopbackControl control, WorldTestClock clock)
            {
                Control = control;
                chronos = TimePackage(root, ChronosId, "TopiaForge Chronos", "0.1.0-rc.1", "TopiaForge.Chronos.ChronosMod", "TopiaForge.Chronos.dll", null);
                gamemode = TimePackage(root, TimeGamemodeId, "Time gamemode", "1.0.0", "TopiaForge.ValidTestMod.RuntimeTimeConsumerMod", FixtureAssembly, ChronosId);
                Runtime = new RuntimeUnderTest(
                    new ManagerPaths(root),
                    Logger,
                    new ManifestValidationContext("0.0.2309", TopiaForgeVersions.LoaderVersion, TopiaForgeVersions.SdkVersion, platform: "windows", architecture: "x64",
                        contentTargets: new[] { "code", "standalonewindows64" }, enforceRuntimeCompatibility: true),
                    new CapturedLoadObserver(),
                    Game);
                Runtime.NetworkClock = clock.Read;
            }

            internal LoopbackControl Control { get; }

            internal CapturedRuntimeLogger Logger { get; } = new CapturedRuntimeLogger();

            internal TimeGameHost Game { get; } = new TimeGameHost();

            internal FakeGameClock Clock => Game.Clock;

            internal RuntimeUnderTest Runtime { get; }

            internal MultiplayerSessionClock Time => Runtime.Multiplayer.SessionTime;

            /// <summary>Gets the gamemode package's own time control, as its code outside any session scope resolves it.</summary>
            internal ITimeControlService Package { get; private set; } = null!;

            /// <summary>Gets the time control of the gamemode's server controller, a scope that runs where the gamemode's server does.</summary>
            internal ITimeControlService Server { get; private set; } = null!;

            /// <summary>Gets the time control of the gamemode's world scope, as a joined player's gamemode package runs.</summary>
            internal ITimeControlService World { get; private set; } = null!;

            /// <summary>Gets the time control of the local player's participant controller.</summary>
            internal ITimeControlService Participant { get; private set; } = null!;

            /// <summary>Loads Chronos and the gamemode, then makes the gamemode's scopes and resolves time control through each.</summary>
            internal void Load()
            {
                Runtime.Load(new[] { chronos, gamemode });
                Assert(Runtime.IsLoaded(ChronosId) && Runtime.IsLoaded(TimeGamemodeId),
                    "Chronos and the gamemode load (" + string.Join(" | ", Logger.Errors) + ")");
                var context = (ModContext)TakeGamemodeContexts().Single();
                Package = TimeControl(context);
                Server = TimeControl(Scope(context, "server", ModExecutionContext.GamemodeServer));
                World = TimeControl(Scope(context, "world", ModExecutionContext.WorldOwner));
                Participant = TimeControl(Scope(context, "participant", ModExecutionContext.Participant));
            }

            internal void Attach() => Assert(Runtime.Multiplayer.Attach(Control), "the loopback provider attaches");

            /// <summary>One frame: Update, which runs the composition root around the mods, then LateUpdate.</summary>
            internal void Frame()
            {
                Runtime.DispatchUpdate(0.016f);
                Game.RaiseLateUpdate();
            }

            /// <summary>
            /// Closes the gamemode's scopes, as its session ends them, then unloads every mod. A scope that fails to close is
            /// logged as an error, which the tests check for, so a failure that brought the test here stays the one reported.
            /// </summary>
            internal void Unload()
            {
                foreach (var scope in scopes)
                {
                    try
                    {
                        PumpBinding(Runtime.NativeDispatcher, scope.CloseAsync());
                    }
                    catch (Exception exception)
                    {
                        Logger.Errors.Add("closing a scope failed: " + exception.Message);
                    }
                }

                scopes.Clear();
                Runtime.UnloadAll();
            }

            private ModContext Scope(ModContext parent, string name, ModExecutionContext executionContext)
            {
                var session = "time-session:" + name;
                var creation = parent.CreateChildScopeAsync(session, CancellationToken.None, () => { },
                    new NativeTransitionAccessSlot(session + ":" + parent.Identity.Id, session, () => !parent.Lifetime.IsStopping),
                    Runtime.NativeDispatcher, executionContext);
                PumpBinding(Runtime.NativeDispatcher, creation);
                var scope = creation.GetAwaiter().GetResult();
                scopes.Add(scope);
                return scope.Context;
            }

            private static ITimeControlService TimeControl(IModContext context)
            {
                Assert(context.Extensions.TryGet<ITimeControlService>(out var time), "the gamemode resolves Chronos' time control through its context");
                return time!;
            }

            /// <summary>Takes the contexts the gamemode's package loaded in, from its fixture mod, which the harness does not reference.</summary>
            private static IModContext[] TakeGamemodeContexts() =>
                AppDomain.CurrentDomain.GetAssemblies()
                    .Where(assembly => assembly.GetName().Name == "TopiaForge.ValidTestMod")
                    .SelectMany(assembly => (IModContext[])assembly.GetType("TopiaForge.ValidTestMod.RuntimeTimeConsumerMod", throwOnError: true)!
                        .GetMethod("TakeLoaded")!.Invoke(null, null)!)
                    .ToArray();

            /// <summary>Installs a package as the registry would leave it: the assembly, and a receipt over it and its manifest.</summary>
            private static ModPackage TimePackage(string root, string id, string name, string version, string entryType, string assemblyName, string? dependency)
            {
                var packagePath = Path.Combine(root, "packages", id);
                Directory.CreateDirectory(packagePath);
                File.Copy(Path.Combine(AppContext.BaseDirectory, assemblyName), Path.Combine(packagePath, assemblyName), true);
                var manifest = new ModManifest
                {
                    SchemaVersion = 6,
                    Id = id,
                    Name = name,
                    Version = version,
                    EntryAssembly = assemblyName,
                    EntryType = entryType,
                    SupportedGameVersionRange = "*",
                    SupportedLoaderVersionRange = ">=0.1.0-rc.1 <0.2.0",
                    SupportedSdkVersionRange = ">=0.1.0-rc.1 <0.2.0"
                };
                if (dependency != null) manifest.Dependencies.Add(dependency, ">=0.1.0-rc.1 <0.2.0");
                JsonUtil.SaveFile(
                    Path.Combine(packagePath, PackageInstallReceipt.FileName),
                    PackageInstallReceipt.Create(Path.Combine(packagePath, assemblyName), packagePath, manifest));
                return new ModPackage(
                    packagePath,
                    manifest,
                    new InstalledModState { Id = id, Name = name, Version = version, Enabled = true },
                    Array.Empty<string>());
            }
        }

        /// <summary>
        /// A gameplay host with a game clock, which the runtime gives Chronos and the pause guard as the Unity host does. Each
        /// frame moves the clock on by 16 ms of real time, as Unity's player loop does before Update.
        /// </summary>
        private sealed class TimeGameHost : IRuntimeGameplayHost, IMultiplayerPlatform, IGameClockSource
        {
            public event Action<GameTimeSample>? FixedUpdate { add { } remove { } }

            public event Action<GameTimeSample>? LateUpdate;

            internal FakeGameClock Clock { get; } = new FakeGameClock();

            public IMultiplayerGameClock GameClock => Clock;

            public bool RunInBackground { get; set; }

            public GameplayContextServices Create(
                string ownerModId,
                string packagePath,
                string dataPath,
                IModLifetime lifetime,
                IModLogger logger,
                ModNetProfile profile,
                NativeTransitionAccessSlot? transitionAccess = null) =>
                GameplayContextServices.Unavailable(lifetime);

            public GameTimeSample BeginFrame(float deltaTime)
            {
                Clock.Advance(0.016f);
                return new GameTimeSample(GameLoopPhase.Frame, deltaTime, deltaTime, 0d, 0);
            }

            internal void RaiseLateUpdate() => LateUpdate?.Invoke(new GameTimeSample(GameLoopPhase.Late, 0.016f, 0.016f, 0d, 0));

            public void Dispose()
            {
            }
        }
    }
}
