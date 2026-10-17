using System;
using System.IO;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>Reads and writes the multiplayer settings. Its members run off the main thread, on the background queue.</summary>
    internal interface IMultiplayerSettingsStore
    {
        MultiplayerSettings Load();

        void Save(MultiplayerSettings settings);
    }

    /// <summary>The settings in manager data, at <c>data/multiplayer-settings.json</c>, written atomically with a backup.</summary>
    internal sealed class FileMultiplayerSettingsStore : IMultiplayerSettingsStore
    {
        internal const string FileName = "multiplayer-settings.json";

        private readonly string path;

        internal FileMultiplayerSettingsStore(string dataDirectory)
        {
            if (string.IsNullOrEmpty(dataDirectory)) throw new ArgumentException("The manager data folder is required.", nameof(dataDirectory));
            path = Path.Combine(dataDirectory, FileName);
        }

        public MultiplayerSettings Load() =>
            MultiplayerSettings.FromDocument(JsonUtil.LoadPersistentFile<MultiplayerSettingsDocument?>(path, null));

        public void Save(MultiplayerSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            JsonUtil.SaveFile(path, settings.ToDocument());
        }
    }

    /// <summary>
    /// The multiplayer settings as the screens use them: the current value on the main thread, loaded once and saved after
    /// each change through the background queue, so neither ever holds a frame. Saves coalesce: a change while one is in
    /// flight writes the newest value once that one finishes. Settings that could not be read are kept for this session
    /// only, so a file this game cannot read is never overwritten with its defaults.
    /// </summary>
    internal sealed class MultiplayerSettingsService
    {
        internal const string LoadFailedText = "Multiplayer settings could not be read, so changes last for this session only.";
        internal const string SaveFailedText = "Multiplayer settings could not be saved.";

        private readonly MultiplayerBackgroundQueue queue;
        private readonly IMultiplayerSettingsStore store;
        private MultiplayerSettings current = MultiplayerSettings.Default;
        private bool loading;
        private bool saving;
        private bool dirty;
        private bool readOnly;

        internal MultiplayerSettingsService(MultiplayerBackgroundQueue queue, IMultiplayerSettingsStore store)
        {
            this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
            this.store = store ?? throw new ArgumentNullException(nameof(store));
        }

        /// <summary>Gets the settings; the defaults until they load.</summary>
        internal MultiplayerSettings Current => current;

        internal bool IsLoaded { get; private set; }

        /// <summary>Gets whether a change is still being written.</summary>
        internal bool IsSaving => saving || dirty;

        /// <summary>Gets the last failure to read or write, for a toast; the screens take and clear it.</summary>
        internal string Failure { get; private set; } = string.Empty;

        /// <summary>Starts loading the settings, once; <paramref name="loaded"/> runs on the main thread when they arrive.</summary>
        internal void Load(Action<MultiplayerSettings>? loaded = null)
        {
            if (IsLoaded || loading) return;
            loading = true;
            MultiplayerSettings? read = null;
            queue.Enqueue(() => read = store.Load(), failure =>
            {
                loading = false;
                IsLoaded = true;
                if (failure != null || read == null)
                {
                    readOnly = true;
                    Failure = LoadFailedText;
                }
                else
                {
                    current = read;
                }

                loaded?.Invoke(current);
            });
        }

        /// <summary>Replaces the settings and writes them; only once they have loaded.</summary>
        internal void Update(MultiplayerSettings next)
        {
            if (next == null) throw new ArgumentNullException(nameof(next));
            if (!IsLoaded) throw new InvalidOperationException("The multiplayer settings have not loaded yet.");
            if (ReferenceEquals(next, current)) return;
            current = next;
            if (!readOnly) RequestSave();
        }

        /// <summary>Takes the last failure, so it is shown once.</summary>
        internal string TakeFailure()
        {
            var failure = Failure;
            Failure = string.Empty;
            return failure;
        }

        private void RequestSave()
        {
            if (saving)
            {
                dirty = true;
                return;
            }

            saving = true;
            var snapshot = current;
            queue.Enqueue(() => store.Save(snapshot), failure =>
            {
                saving = false;
                if (failure != null) Failure = SaveFailedText;
                if (!dirty) return;
                dirty = false;
                RequestSave();
            });
        }
    }
}
