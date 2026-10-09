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

        // Rewire the vanilla pause exit through the manager's bound menu operation.
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
            InterceptPauseMenu = true;
            EnableLocalWorlds = true;
            LocalWorldFolder = string.Empty;
        }
    }
}
