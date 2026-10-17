using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Runs the realm and factory-seam suites (WS3.1 and WS3.3): the realm at the runtime and the phase it reads from a real
    /// orchestrator, the spawn ledger, the network decorators, the profile every context carries, the replication SPI and
    /// its privacy.
    /// </summary>
    internal static class MultiplayerSeamTests
    {
        internal static void Run(string root)
        {
            MultiplayerRealmRuntimeTests.Run();
            MultiplayerRealmPhaseTests.Run(System.IO.Path.Combine(root, "multiplayer-realm-phase"));
            MultiplayerSpawnLedgerTests.Run();
            NetGameplayServicesTests.Run();
            ModContextNetProfileTests.Run(System.IO.Path.Combine(root, "multiplayer-profiles"));
            RuntimeReplicationTests.Run(System.IO.Path.Combine(root, "multiplayer-replication"));
            ReplicationSpiPrivacyTests.Run();
            Console.WriteLine("Multiplayer realm and factory seam tests passed.");
        }
    }

    /// <summary>An entity a test spawns: alive until disposed or killed, with an id that can be made to fail.</summary>
    internal sealed class SeamEntity : ISpawnedEntity
    {
        private static int next;
        private readonly string id;

        internal SeamEntity(string? id = null)
        {
            this.id = id ?? "seam-entity-" + System.Threading.Interlocked.Increment(ref next);
        }

        internal bool ThrowOnId { get; set; }

        public string Id => ThrowOnId ? throw new InvalidOperationException("the entity's id at 203.0.113.9 could not be read") : id;

        public string Name => "seam entity";

        public bool IsAlive { get; set; } = true;

        public Vec3 Position => Vec3.Zero;

        public TransformState InitialTransform => TransformState.Identity;

        public void Dispose() => IsAlive = false;
    }

    /// <summary>The census a promotion takes, recorded; it can be made to fail.</summary>
    internal sealed class RecordingCensus : IMultiplayerPromotionCensus
    {
        internal List<MultiplayerPromotion> Promotions { get; } = new List<MultiplayerPromotion>();

        internal Exception? Failure { get; set; }

        public void TakeCensus(MultiplayerPromotion promotion)
        {
            Promotions.Add(promotion);
            if (Failure != null) throw Failure;
        }
    }

    /// <summary>Builders and assertions the seam suites share.</summary>
    internal static class Seam
    {
        internal static readonly NetRealm[] Realms = { NetRealm.Offline, NetRealm.ListenHost, NetRealm.Client };

        internal static readonly ModNetworkClass[] Classes =
        {
            ModNetworkClass.Shared, ModNetworkClass.ClientLocal, ModNetworkClass.ServerOnly, ModNetworkClass.Standalone
        };

        internal static readonly ModExecutionContext[] Contexts =
        {
            ModExecutionContext.Process, ModExecutionContext.WorldOwner, ModExecutionContext.GamemodeServer, ModExecutionContext.Participant
        };

        internal static readonly SessionPhase[] Phases =
        {
            SessionPhase.Idle, SessionPhase.Preparing, SessionPhase.LoadingWorld, SessionPhase.StartingMode, SessionPhase.Running, SessionPhase.Stopping
        };

        internal static ModManifest Manifest(string id, string? mode = null)
        {
            var last = id.Substring(id.LastIndexOf('.') + 1);
            var manifest = new ModManifest
            {
                SchemaVersion = 6,
                Id = id,
                Name = char.ToUpperInvariant(last[0]) + last.Substring(1),
                Version = "1.0.0",
                EntryAssembly = "Seam.dll",
                EntryType = "Seam.Entry"
            };
            if (mode != null) manifest.Multiplayer = new ModMultiplayerMetadata { Mode = mode };
            return manifest;
        }

        /// <summary>A composition root over a fake control, its log, and a session phase the test sets.</summary>
        internal static (MultiplayerRuntime Runtime, FakeMultiplayerControl Control, RecordingRuntimeLog Log) Runtime(Func<SessionPhase> phase)
        {
            var log = new RecordingRuntimeLog();
            var control = new FakeMultiplayerControl();
            var runtime = new MultiplayerRuntime(() => control, new MultiplayerRuntimeHost("2545", "0.2.0", log), log) { SessionPhaseSource = phase };
            return (runtime, control, log);
        }

        /// <summary>One frame of the composition root, as ModRuntime runs it around the mods.</summary>
        internal static void Frame(MultiplayerRuntime runtime)
        {
            runtime.BeforeModsUpdate(0.016f);
            runtime.AfterModsUpdate(0.016f);
            runtime.BeforeModsLateUpdate(new GameTimeSample(GameLoopPhase.Late, 0.016f, 0.016f, 0d, 0));
        }

        internal static void That(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Multiplayer seam test failed: " + message);
        }

        internal static void Throws<TException>(Action action, string message) where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }

            throw new InvalidOperationException("Multiplayer seam test failed: " + message + " (no " + typeof(TException).Name + ")");
        }

        internal static long Allocated(Action frame, int frames = 10_000)
        {
            for (var index = 0; index < 64; index++) frame();
            _ = GC.GetAllocatedBytesForCurrentThread();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < frames; index++) frame();
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }
}
