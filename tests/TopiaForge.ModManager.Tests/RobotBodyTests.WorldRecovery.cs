using System;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class RobotBodyTests
    {
        internal static void RunWorldRecovery()
        { CachedFacadeWorldRecovery(); Console.WriteLine("Cached production body facade world recovery passed."); }

        private static void CachedFacadeWorldRecovery()
        {
            var rig = NewRig();
            using var world = new BodyReadContext();
            using var provider = new Bodies(rig.Host, world);
            var owner = new AuthorityMod(rig.Host, ModExecutionContext.GamemodeServer);
            using var reads = world.For(owner.Lifetime);
            var cached = provider.Bind(owner, reads);
            var old = cached.Spawn(Request()).Value!;
            var oldRead = ((IInternalOwnedNativeEntity)old).NativeReadScope!;
            Check(oldRead.IsCurrentWorld(reads), "first body captures current world authority");
            provider.Provider.EndWorld();
            world.WorldLifetime.Dispose();
            world.WorldLifetime = new TopiaForge.Mods.Testing.FakeModLifetime();
            world.World = new WorldReadiness(world.World.Scene, TransformState.Identity);
            Check(!old.IsAlive && !oldRead.IsCurrentWorld(reads), "replacement retires body and old read authority");
            var result = cached.Spawn(Request(4));
            Check(result.TryGetValue(out var fresh) && fresh.IsAlive, "cached production facade creates in new ready world");
            var freshRead = ((IInternalOwnedNativeEntity)fresh!).NativeReadScope!;
            Check(!ReferenceEquals(oldRead, freshRead) && freshRead.IsCurrentWorld(reads), "new body captures fresh read scope");
            owner.Lifetime.Dispose();
            Check(!fresh.IsAlive && cached.Spawn(Request()).ErrorCode == ModErrorCode.Cancelled, "stopped cached scope still refuses");
            var nextOwner = new AuthorityMod(rig.Host, ModExecutionContext.GamemodeServer);
            var next = provider.Bind(nextOwner);
            provider.Provider.Dispose();
            Check(next.Spawn(Request()).ErrorCode == ModErrorCode.Unavailable, "disposed provider still refuses cached facade");
        }
    }
}
