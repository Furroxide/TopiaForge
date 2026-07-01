using System.Runtime.Serialization;

namespace Robotopia.Worlds
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
        }
    }
}
