using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace TopiaForge.ModManager.Core
{
    [DataContract]
    public sealed class WorldLaunchSettings
    {
        public const string AdditiveArena = "additiveArena";
        public const string SceneReplacement = "sceneReplacement";

        public WorldLaunchSettings()
        {
            SeedDefaults();
        }

        [DataMember(Name = "selectedWorldId")]
        public string SelectedWorldId { get; set; } = "";

        [DataMember(Name = "selectedGamemodeId")]
        public string SelectedGamemodeId { get; set; } = "";

        [DataMember(Name = "loadMode")]
        public string LoadMode { get; set; } = AdditiveArena;

        [DataMember(Name = "autoLoadOnStart")]
        public bool AutoLoadOnStart { get; set; }

        [DataMember(Name = "allowAdditiveFallback")]
        public bool AllowAdditiveFallback { get; set; } = true;

        public bool PreferSceneReplacement => LoadMode == SceneReplacement;

        public static string NormalizeLoadMode(string? value)
        {
            return value == SceneReplacement || value == AdditiveArena ? value : AdditiveArena;
        }

        public static string ReconcileLoadMode(
            bool supportsSceneReplacement,
            bool supportsAdditiveArena,
            string? requestedMode)
        {
            var normalized = NormalizeLoadMode(requestedMode);
            if ((normalized == SceneReplacement && supportsSceneReplacement)
                || (normalized == AdditiveArena && supportsAdditiveArena))
            {
                return normalized;
            }

            if (supportsAdditiveArena)
            {
                return AdditiveArena;
            }

            if (supportsSceneReplacement)
            {
                return SceneReplacement;
            }

            return normalized;
        }

        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            SeedDefaults();
        }

        private void SeedDefaults()
        {
            SelectedWorldId = "";
            SelectedGamemodeId = "";
            LoadMode = AdditiveArena;
            AutoLoadOnStart = false;
            AllowAdditiveFallback = true;
        }
    }
}
