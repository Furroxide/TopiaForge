using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class WorldViewTests
    {
        private sealed class ViewFixture : IModContext, IInternalWorldViewContext, IInternalReplicationContext, IDisposable
        {
            internal readonly FakeModContext Fake = new FakeModContext();
            public IRuntimeReplication Replication { get; set; } = null!;
            internal WorldReadiness? ViewReadiness;
            internal IModLifetime? ViewLifetime;
            public bool TryGetViewWorld(out WorldReadiness? world, out IModLifetime? lifetime)
            { world = ViewReadiness; lifetime = ViewLifetime; return world != null && lifetime != null && !lifetime.IsStopping; }
            public ModIdentity Identity => Fake.Identity;
            public IRuntimeInfo Runtime => Fake.Runtime;
            public IModLifetime Lifetime => Fake.Lifetime;
            public IModEvents Events => Fake.Events;
            public IModFiles Files => Fake.Files;
            public IModConfigService Config => Fake.Config;
            public ILocalModStorageService LocalStorage => Fake.LocalStorage;
            public IInputService Input => Fake.Input;
            public ILocalPlayerService LocalPlayer => Fake.LocalPlayer;
            public ISceneService Scenes => Fake.Scenes;
            public IEntityService Entities => Fake.Entities;
            public IPhysicsService Physics => Fake.Physics;
            public IInteractionService Interactions => Fake.Interactions;
            public IItemService Items => Fake.Items;
            public IAssetService Assets => Fake.Assets;
            public IAudioService Audio => Fake.Audio;
            public IUiService Ui => Fake.Ui;
            public ILocalizationService Localization => Fake.Localization;
            public ICommandService Commands => Fake.Commands;
            public IDiagnosticsService Diagnostics => Fake.Diagnostics;
            public IExtensionService Extensions => Fake.Extensions;
            public IGameTime Time => Fake.Time;
            public IModScheduler Scheduler => Fake.Scheduler;
            public IModLogger Logger => Fake.Logger;
            public void Dispose() => Fake.Dispose();
        }
    }
}
