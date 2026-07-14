using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Robotopia.ModManager.Core
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

        // These provider-owned switches are carried through even though the manager overlay does not edit
        // them. Saving only the launch subset would otherwise erase existing WorldsConfig preferences.
        [DataMember(Name = "endSessionOnMenuScene")]
        public bool EndSessionOnMenuScene { get; set; } = true;

        [DataMember(Name = "interceptPauseMenu")]
        public bool InterceptPauseMenu { get; set; } = true;

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

        /// <summary>
        /// Writes only manager-owned launch members into an existing Worlds config document. Provider-owned
        /// and future/third-party members remain byte-for-byte JSON values instead of being dropped by a narrow
        /// DTO round-trip.
        /// </summary>
        public string MergeIntoJson(string existingJson)
        {
            return JsonObjectMerge.Merge(existingJson, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["selectedWorldId"] = JsonUtil.Serialize(SelectedWorldId ?? string.Empty),
                ["selectedGamemodeId"] = JsonUtil.Serialize(SelectedGamemodeId ?? string.Empty),
                ["loadMode"] = JsonUtil.Serialize(NormalizeLoadMode(LoadMode)),
                ["autoLoadOnStart"] = JsonUtil.Serialize(AutoLoadOnStart),
                ["allowAdditiveFallback"] = JsonUtil.Serialize(AllowAdditiveFallback),
            });
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
            EndSessionOnMenuScene = true;
            InterceptPauseMenu = true;
        }
    }
}
