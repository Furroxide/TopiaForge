using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
namespace TopiaForge.SandboxAutomation.Unity
{
    internal sealed class EditorContext : IModContext
    {
        private readonly FakeModContext fake;
        private readonly IUiService ui;
        internal EditorContext(FakeModContext fake, IUiService ui) { this.fake = fake; this.ui = ui; }
        public ModIdentity Identity => ((IModContext)fake).Identity;
        public IRuntimeInfo Runtime => ((IModContext)fake).Runtime;
        public IModLogger Logger => ((IModContext)fake).Logger;
        public IModLifetime Lifetime => ((IModContext)fake).Lifetime;
        public IModEvents Events => ((IModContext)fake).Events;
        public IModFiles Files => ((IModContext)fake).Files;
        public IModConfigService Config => ((IModContext)fake).Config;
        public ILocalModStorageService LocalStorage => ((IModContext)fake).LocalStorage;
        public IInputService Input => ((IModContext)fake).Input;
        public IGameTime Time => ((IModContext)fake).Time;
        public IModScheduler Scheduler => ((IModContext)fake).Scheduler;
        public ILocalPlayerService LocalPlayer => ((IModContext)fake).LocalPlayer;
        public ISceneService Scenes => ((IModContext)fake).Scenes;
        public IEntityService Entities => ((IModContext)fake).Entities;
        public IPhysicsService Physics => ((IModContext)fake).Physics;
        public IInteractionService Interactions => ((IModContext)fake).Interactions;
        public IItemService Items => ((IModContext)fake).Items;
        public IAssetService Assets => ((IModContext)fake).Assets;
        public IAudioService Audio => ((IModContext)fake).Audio;
        public IUiService Ui => ui;
        public ILocalizationService Localization => ((IModContext)fake).Localization;
        public ICommandService Commands => ((IModContext)fake).Commands;
        public IDiagnosticsService Diagnostics => ((IModContext)fake).Diagnostics;
        public IExtensionService Extensions => ((IModContext)fake).Extensions;
    }
}
