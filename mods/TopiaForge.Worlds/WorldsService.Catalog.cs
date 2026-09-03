using System;
using System.IO;
using System.Text;
using TopiaForge.Mods;
using UnityEngine.SceneManagement;

namespace TopiaForge.Worlds
{
    public sealed partial class WorldsService
    {
        public void DiscoverBuiltIns()
        {
            ThrowIfDisposed();
            RegisterWorld(new WorldDefinition(
                OpenSandboxWorldId,
                "Open Sandbox",
                "Generated open-world sandbox arena.",
                supportsAdditiveArena: true));
            RegisterGamemode(new GamemodeDefinition(
                SandboxGamemodeId,
                "Sandbox",
                "Freeform creator sandbox."));

            // Prefer the game's curated level entry points: these carry a checkpoint asset, so we can launch
            // them through the game's own loader and they come up in correct play state (player + HDRP).
            var levels = levelBridge.GetLevels();
            if (levels.Count > 0)
            {
                foreach (var level in levels)
                {
                    var worldId = "io.github.furroxide.topiaforge.worlds.level." + Slug(level.SceneName);
                    RegisterWorld(new WorldDefinition(
                        worldId,
                        level.DisplayName,
                        string.IsNullOrWhiteSpace(level.Description) ? "First-party Robotopia level." : level.Description,
                        level.SceneName,
                        firstParty: true,
                        supportsSceneReplacement: true,
                        supportsAdditiveArena: false));
                    worldCheckpoints[worldId] = level.CheckpointAsset;
                }
            }
            else
            {
                DiscoverBuildSettingsScenes();
            }
        }

        private void DiscoverBuildSettingsScenes()
        {
            var activeScene = SceneManager.GetActiveScene().name;
            for (var index = 0; index < SceneManager.sceneCountInBuildSettings; index++)
            {
                var scenePath = SceneUtility.GetScenePathByBuildIndex(index);
                var sceneName = Path.GetFileNameWithoutExtension(scenePath);
                if (string.IsNullOrWhiteSpace(sceneName))
                {
                    continue;
                }

                // Skip menu/boot/loader scenes so users cannot "launch" a non-gameplay scene as a world.
                if (string.Equals(sceneName, activeScene, StringComparison.OrdinalIgnoreCase) || GameScenes.IsNonGameplayScene(sceneName))
                {
                    continue;
                }

                RegisterWorld(new WorldDefinition(
                    "io.github.furroxide.topiaforge.worlds.first-party." + Slug(sceneName),
                    sceneName,
                    "First-party Robotopia scene.",
                    sceneName,
                    firstParty: true,
                    supportsSceneReplacement: true,
                    supportsAdditiveArena: true));
            }
        }

        /// <summary>
        /// Requests a refresh of the published world catalog. Registration happens over many frames -- every
        /// gamemode mod declares <c>loadAfter: worlds</c>, so Zombies and Sandbox register after this provider
        /// has already loaded -- so the catalog is marked dirty here and written from the frame loop instead of
        /// being snapshotted at one moment. Writing it once at load time was why a gamemode contributed by a
        /// later-loading mod never appeared in the file, and therefore never appeared in the launcher.
        /// </summary>
        public void MarkCatalogDirty()
        {
            ThrowIfDisposed();
            catalogDirty = true;
        }

        /// <summary>
        /// Starts a best-effort write of the diagnostic world catalog. The catalog is an inspection aid, not a
        /// runtime input, so a read-only data directory, a full disk, or a locked file must never fail the
        /// provider that Zombies, Sandbox, UiGallery, and Creator Tools all hard-depend on. The write is also
        /// never waited on: <see cref="UpdateTransition"/> drains its result on Unity's main thread.
        /// </summary>
        private void StartCatalogWrite()
        {
            if (catalogWrite != null)
            {
                return;
            }

            try
            {
                var json = CatalogJson();
                var bytes = new UTF8Encoding(false, true).GetByteCount(json);
                if (bytes > MaxCatalogBytes)
                {
                    logger.Warn("World catalog exceeds " + MaxCatalogBytes
                        + " bytes and was not written; world routing is unaffected.");
                    return;
                }

                catalogWrite = files.WriteDataTextAsync("catalog.json", json, lifetimeToken);
            }
            catch (Exception ex)
            {
                logger.Warn("Could not start the world catalog write: " + ex.Message);
            }
        }

        /// <summary>
        /// Main-thread pump for the catalog. Drains a finished write, then starts a new one when the registry
        /// has changed since the last write began. Requests that arrive while a write is in flight coalesce
        /// into the next one rather than being dropped, so the file always converges on the live registry.
        /// </summary>
        private void UpdateCatalogWrite()
        {
            var pending = catalogWrite;
            if (pending == null)
            {
                if (catalogDirty)
                {
                    catalogDirty = false;
                    StartCatalogWrite();
                }

                return;
            }

            if (!pending.IsCompleted)
            {
                return;
            }

            catalogWrite = null;
            OperationResult<bool> result;
            try
            {
                result = pending.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                logger.Warn("Could not write the world catalog: " + ex.Message);
                return;
            }

            if (!result.Succeeded && result.ErrorCode != ModErrorCode.Cancelled)
            {
                logger.Warn("Could not write the world catalog: " + result.ErrorMessage);
            }
        }

        private string CatalogJson()
        {
            var builder = new StringBuilder();
            builder.Append("{\"worlds\":[");
            AppendWorlds(builder);
            builder.Append("],\"gamemodes\":[");
            AppendGamemodes(builder);
            // Menu entries carry the world a gamemode actually wants to start in. Publishing them lets the
            // launcher offer "play this gamemode" without having to guess a world from the manifest.
            builder.Append("],\"menuEntries\":[");
            AppendMenuEntries(builder);
            builder.Append("]}");
            return builder.ToString();
        }

        private void AppendWorlds(StringBuilder builder)
        {
            for (var index = 0; index < worlds.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                var world = worlds[index];
                builder
                    .Append("{\"id\":\"").Append(Escape(world.Id))
                    .Append("\",\"name\":\"").Append(Escape(world.Name))
                    .Append("\",\"description\":\"").Append(Escape(world.Description))
                    .Append("\",\"sceneName\":\"").Append(Escape(world.SceneName))
                    .Append("\",\"firstParty\":").Append(world.FirstParty ? "true" : "false")
                    .Append(",\"supportsSceneReplacement\":").Append(world.SupportsSceneReplacement ? "true" : "false")
                    .Append(",\"supportsAdditiveArena\":").Append(world.SupportsAdditiveArena ? "true" : "false")
                    .Append('}');
            }
        }

        private void AppendGamemodes(StringBuilder builder)
        {
            for (var index = 0; index < gamemodes.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                var gamemode = gamemodes[index];
                builder
                    .Append("{\"id\":\"").Append(Escape(gamemode.Id))
                    .Append("\",\"name\":\"").Append(Escape(gamemode.Name))
                    .Append("\",\"description\":\"").Append(Escape(gamemode.Description))
                    .Append("\"}");
            }
        }

        private void AppendMenuEntries(StringBuilder builder)
        {
            for (var index = 0; index < menuEntries.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                var entry = menuEntries[index];
                builder
                    .Append("{\"id\":\"").Append(Escape(entry.Id))
                    .Append("\",\"title\":\"").Append(Escape(entry.Title))
                    .Append("\",\"description\":\"").Append(Escape(entry.Description))
                    .Append("\",\"gamemodeId\":\"").Append(Escape(entry.GamemodeId))
                    .Append("\",\"worldId\":\"").Append(Escape(entry.WorldId))
                    .Append("\"}");
            }
        }

        private static string Escape(string value)
        {
            var builder = new StringBuilder(value.Length + 8);
            foreach (var c in value)
            {
                switch (c)
                {
                    case '\\': builder.Append("\\\\"); break;
                    case '"': builder.Append("\\\""); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    default:
                        if (c < 0x20)
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            builder.Append(c);
                        }

                        break;
                }
            }

            return builder.ToString();
        }

        private static string Slug(string value)
        {
            var builder = new StringBuilder();
            foreach (var character in value.ToLowerInvariant())
            {
                builder.Append(char.IsLetterOrDigit(character) ? character : '_');
            }

            return builder.ToString().Trim('_');
        }
    }
}
