using System;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Worlds;

namespace TopiaForge.ModManager.Tests
{
    internal static class WorldsOwnerFacadeTests
    {
        internal static void Run()
        {
            var backend = new WorldsService();
            using var lifetime = new OwnerModLifetime();
            var facade = (IWorldGamemodeService)((IOwnerBoundExtensionFactory)backend)
                .CreateOwnerFacade(typeof(IWorldGamemodeService), "scope.mod", lifetime);
            var changed = 0; var ended = 0;
            facade.SessionChanged += _ => changed++;
            facade.SessionEnded += _ => ended++;
            backend.Raise();
            Assert(changed == 1 && ended == 1, "active owner events should forward once");
            lifetime.BeginStop();
            backend.Raise();
            Assert(changed == 1 && ended == 1, "cancel-only must suppress owner events before registration disposal");
            Assert(facade.EndSession(WorldSessionEndReason.EndedByGamemode).ErrorCode == ModErrorCode.Cancelled,
                "a stale world facade cannot stop a later session");
            Assert(facade.LoadLocalWorld("world.rgd").ErrorCode == ModErrorCode.Cancelled,
                "a stale world facade cannot start an untracked local import");
            Assert(facade.RegisterWorld(null!).ErrorCode == ModErrorCode.Cancelled
                && facade.RegisterGamemode(null!).ErrorCode == ModErrorCode.Cancelled
                && facade.RegisterMenuEntry(null!).ErrorCode == ModErrorCode.Cancelled
                && facade.RegisterAssetOverride(null!).ErrorCode == ModErrorCode.Cancelled,
                "stopped registration calls must never enter the backing service");
            Assert(facade.LoadAsync(null!).GetAwaiter().GetResult().ErrorCode == ModErrorCode.Cancelled
                && facade.LaunchMenuEntryAsync("target").GetAwaiter().GetResult().ErrorCode == ModErrorCode.Cancelled,
                "stopped async launch calls must fail before backing-service work");
            Assert(backend.Calls == 0, "no stale mutation may reach the backing service");
            Console.WriteLine("WorldsOwnerFacadeTests passed.");
        }
        private static void Assert(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
    }
}
