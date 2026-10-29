using System;
using System.IO;
using System.Threading;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Interop.Unity;

namespace TopiaForge.ModManager.Tests
{
    internal static class NativeSpeechContextTests
    {
        internal static void Run()
        {
            using var dispatcher = new HostDispatcher();
            var factory = new Factory();
            var manifest = new ModManifest { SchemaVersion = 6, Id = "io.github.furroxide.topiaforge.robotkit",
                Name = "RobotKit", Version = "0.2.0", EntryAssembly = "Test.dll", EntryType = "Test.Entry" };
            var paths = new ManagerPaths(Path.Combine(Path.GetTempPath(), "TopiaForgeSpeech-" + Guid.NewGuid().ToString("N")));
            var parent = new ModContext(manifest, paths, "package", new OwnerFacadeStoppingTests.Logger(),
                new ModServiceRegistry(), null, factory);
            try
            {
                Check(ReferenceEquals(((IInternalNativeSpeechContext)parent).NativeSpeech, factory.Host), "root receives the host without unsafe-native");
                try
                {
                    _ = ((IUnityInteropContext)parent).UnityInterop;
                    throw new Exception("Speech hosting granted general interop.");
                }
                catch (InvalidOperationException) { }
                var creation = parent.CreateChildScopeAsync("speech", CancellationToken.None, () => { },
                    new NativeTransitionAccessSlot("speech", "speech", () => true), dispatcher, ModExecutionContext.GamemodeServer);
                HostDispatcherTests.Pump(dispatcher, creation);
                using var scope = creation.Result;
                Check(((IInternalNativeSpeechContext)scope.Context).NativeSpeech == null, "consuming scopes cannot install a second provider");
            }
            finally { parent.DisposeLifetime(); }
            Check(factory.RootLifetime!.IsStopping, "hosting lifetime follows the provider context");
            Console.WriteLine("Native speech context: root hosting, scope isolation and unchanged interop gate passed.");
        }
        private static void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException("Native speech context: " + message); }
        private sealed class Factory : IGameplayContextFactory
        {
            internal readonly Host Host = new Host();
            internal IModLifetime? RootLifetime;
            public GameplayContextServices Create(string ownerModId, string packagePath, string dataPath, IModLifetime lifetime,
                IModLogger logger, ModNetProfile profile, NativeTransitionAccessSlot? transitionAccess = null)
            {
                RootLifetime ??= lifetime;
                var s = GameplayContextServices.Unavailable(lifetime);
                return new GameplayContextServices(s.Input, s.LocalPlayer, s.Entities, s.Physics, s.Time, s.Scheduler,
                    s.Scenes, s.Interactions, s.Items, s.Assets, s.Audio, s.Ui, s.UnityInterop, s.SceneTransitions, s.WorldRuntime, Host);
            }
        }
        private sealed class Host : IInternalNativeSpeechHost
        {
            public IInternalNativeSpeechLease Install(IInternalNativeSpeechRouter router) => throw new NotSupportedException();
        }
    }
}
