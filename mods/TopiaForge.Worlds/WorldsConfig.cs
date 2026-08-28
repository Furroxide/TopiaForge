using System.Runtime.Serialization;

namespace TopiaForge.Worlds
{
    [DataContract]
    public sealed class WorldsConfig
    {
        public WorldsConfig()
        {
            SeedDefaults();
        }

        [DataMember(Name = "selectedWorldId")]
        public string SelectedWorldId { get; set; } = WorldsService.OpenSandboxWorldId;

        [DataMember(Name = "selectedGamemodeId")]
        public string SelectedGamemodeId { get; set; } = WorldsService.SandboxGamemodeId;

        [DataMember(Name = "loadMode")]
        public string LoadMode { get; set; } = "additiveArena";

        [DataMember(Name = "autoLoadOnStart")]
        public bool AutoLoadOnStart { get; set; }

        [DataMember(Name = "allowAdditiveFallback")]
        public bool AllowAdditiveFallback { get; set; } = true;

        // Automatically end the active world session when a non-gameplay scene (menu/boot/loader) becomes the
        // active scene — e.g. the player used the game's own pause-menu exit. Leave on unless a gamemode must
        // survive menu round-trips and manages its own teardown.
        [DataMember(Name = "endSessionOnMenuScene")]
        public bool EndSessionOnMenuScene { get; set; } = true;

        // While a session is active, rewire the vanilla pause menu's exit button to end the session cleanly
        // (and host gamemode-registered pause actions). The scene-load auto-end above still applies when off.
        [DataMember(Name = "interceptPauseMenu")]
        public bool InterceptPauseMenu { get; set; } = true;

        // Allow loading a .roboworld (or .json / .json.gz) export the player already has on disk through the
        // game's own local import host. Strictly local: no sign-in, no publish, no backend call. Turning this
        // off makes the local-world folder inert without changing anything else.
        [DataMember(Name = "enableLocalWorlds")]
        public bool EnableLocalWorlds { get; set; } = true;

        // The folder scanned for local exports. Empty means "whatever the game itself scans by default",
        // which is the setting that needs no explanation; set it to keep TopiaForge's worlds somewhere else.
        [DataMember(Name = "localWorldFolder")]
        public string LocalWorldFolder { get; set; } = string.Empty;

        public bool PreferSceneReplacement => LoadMode == "sceneReplacement";

        // DataContractJsonSerializer builds the instance with FormatterServices.GetUninitializedObject, which
        // bypasses the constructor and property initializers, so absent fields would deserialize to null/false.
        // Seed real defaults before members are read; present members still override them.
        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            SeedDefaults();
        }

        private void SeedDefaults()
        {
            SelectedWorldId = WorldsService.OpenSandboxWorldId;
            SelectedGamemodeId = WorldsService.SandboxGamemodeId;
            LoadMode = "additiveArena";
            AutoLoadOnStart = false;
            AllowAdditiveFallback = true;
            EndSessionOnMenuScene = true;
            InterceptPauseMenu = true;
            EnableLocalWorlds = true;
            LocalWorldFolder = string.Empty;
        }
    }
}
