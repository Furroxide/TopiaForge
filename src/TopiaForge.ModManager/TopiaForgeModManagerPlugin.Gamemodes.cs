using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BepInEx;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TopiaForge.ModManager
{
    public sealed partial class TopiaForgeModManagerPlugin
    {
        public IWorldGamemodeService? GetWorldService()
        {
            return runtime?.GetService<IWorldGamemodeService>();
        }

        public WorldLaunchSettings ReadWorldLaunchSettings()
        {
            try
            {
                return JsonUtil.LoadPersistentFile(
                    paths.GetConfigPath("topiaforge.worlds"),
                    new WorldLaunchSettings());
            }
            catch (Exception ex)
            {
                managerLogger.Warn("World launch settings could not be read; using defaults: " + ex.Message);
                return new WorldLaunchSettings();
            }
        }

        public void SaveWorldLaunchSettings(WorldLaunchSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            var path = paths.GetConfigPath("topiaforge.worlds");
            string existingJson;
            try
            {
                existingJson = JsonUtil.LoadPersistentJsonObject(path, "{}");
            }
            catch (Exception ex)
            {
                managerLogger.Warn("World config could not be read within the bounded JSON policy; replacing it: "
                    + ex.Message);
                existingJson = "{}";
            }

            string merged;
            try
            {
                merged = settings.MergeIntoJson(existingJson);
            }
            catch (Exception ex)
            {
                // A malformed provider config was already unreadable. Recover the launch fields rather than
                // making PLAY unusable; the warning makes the loss of unrecoverable raw content explicit.
                managerLogger.Warn("World config could not be merged; replacing malformed JSON: " + ex.Message);
                merged = settings.MergeIntoJson("{}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? paths.Config);
            var tempPath = path + ".manager.tmp";
            File.WriteAllText(tempPath, merged);
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(tempPath, path, null);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Delete(path);
                    File.Move(tempPath, path);
                }
            }
            else
            {
                File.Move(tempPath, path);
            }
        }

        public async Task<(bool Ok, string Message)> LaunchGamemode(string entryId)
        {
            var service = GetWorldService();
            if (service == null)
            {
                return (false, "World/gamemode service unavailable. Enable the TopiaForge Worlds mod.");
            }

            try
            {
                var result = await service.LaunchMenuEntryAsync(entryId);
                var message = result.Succeeded
                    ? "Launched gamemode entry '" + entryId + "'."
                    : result.ErrorMessage;
                managerLogger.Info("Gamemode launch '" + entryId + "': " + message);
                return (result.Succeeded, message);
            }
            catch (Exception ex)
            {
                managerLogger.Error(ex, "Failed to launch gamemode '" + entryId + "'.");
                return (false, "Failed to launch: " + ex.Message);
            }
        }

        public async Task<(bool Ok, string Message)> LaunchGamemodeSelection(
            string entryId,
            string worldId,
            string gamemodeId,
            string loadMode)
        {
            var service = GetWorldService();
            if (service == null)
            {
                return (false, "World/gamemode service unavailable. Enable the TopiaForge Worlds mod.");
            }

            try
            {
                var world = service.Worlds.FirstOrDefault(item =>
                    string.Equals(item.Id, worldId, StringComparison.OrdinalIgnoreCase));
                if (world == null)
                {
                    return (false, "Unknown world: " + worldId);
                }

                var gamemode = service.Gamemodes.FirstOrDefault(item =>
                    string.Equals(item.Id, gamemodeId, StringComparison.OrdinalIgnoreCase));
                if (gamemode == null)
                {
                    return (false, "Unknown gamemode: " + gamemodeId);
                }

                var resolvedLoadMode = WorldLaunchSettings.ReconcileLoadMode(
                    world.SupportsSceneReplacement,
                    world.SupportsAdditiveArena,
                    loadMode);
                var existing = ReadWorldLaunchSettings();
                var settings = new WorldLaunchSettings
                {
                    SelectedWorldId = worldId,
                    SelectedGamemodeId = gamemodeId,
                    LoadMode = resolvedLoadMode,
                    AutoLoadOnStart = existing.AutoLoadOnStart,
                    AllowAdditiveFallback = existing.AllowAdditiveFallback,
                    EndSessionOnMenuScene = existing.EndSessionOnMenuScene,
                    InterceptPauseMenu = existing.InterceptPauseMenu
                };
                SaveWorldLaunchSettings(settings);

                var result = await service.LoadAsync(new WorldLoadRequest(
                    worldId,
                    gamemodeId,
                    preferSceneReplacement: settings.PreferSceneReplacement,
                    allowAdditiveFallback: settings.AllowAdditiveFallback));
                var message = result.Succeeded
                    ? "Launched '" + gamemode.Name + "' in '" + world.Name + "'."
                    : result.ErrorMessage;
                managerLogger.Info("Gamemode launch '" + entryId + "' world '" + world.Name + "' [" + world.Id
                    + "] gamemode '" + gamemode.Name + "' [" + gamemode.Id + "] loadMode '" + settings.LoadMode
                    + "': " + message);
                return (result.Succeeded, message);
            }
            catch (Exception ex)
            {
                managerLogger.Error(ex, "Failed to launch gamemode '" + entryId + "' for world '" + worldId + "'.");
                return (false, "Failed to launch: " + ex.Message);
            }
        }
    }
}
