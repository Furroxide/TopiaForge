using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace TopiaForge.ModManager
{
    /// <summary>Keeps a verified file open while any descriptor uses it and releases unused assets within shared files.</summary>
    internal sealed class UnityUiThemeBundle
    {
        private readonly Dictionary<int, AssetReference> assets = new Dictionary<int, AssetReference>();
        private readonly Action onUnloaded;
        private readonly List<AssetReference> pendingRelease = new List<AssetReference>();
        private int references;
        private int requests;
        private bool unloadFailed;

        internal UnityUiThemeBundle(AssetBundle native, Action onUnloaded)
        { Native = native; this.onUnloaded = onUnloaded; }

        internal AssetBundle Native { get; }

        internal IDisposable Lease()
        {
            if (unloadFailed) throw new InvalidOperationException("The UI bundle could not be retired.");
            references++;
            return new ReleaseLease(ReleaseBundle);
        }

        internal IDisposable PinRequest()
        {
            requests++;
            return new ReleaseLease(() =>
            {
                if (--requests == 0) ReleaseUnused();
            });
        }

        internal IDisposable LeaseAssets(UnityObject[] loaded, UnityObject? resolved)
        {
            var unique = new HashSet<int>();
            var held = new List<AssetReference>();
            try
            {
                foreach (var asset in loaded) Hold(asset);
                Hold(resolved);
            }
            catch (Exception error)
            {
                try { ReleaseAssets(held); }
                catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
                throw;
            }
            return new ReleaseLease(() => ReleaseAssets(held));

            void Hold(UnityObject? asset)
            {
                if (asset is Sprite sprite && sprite != null)
                {
                    Add(sprite);
                    Add(sprite.texture);
                }
                else if (asset is TMP_FontAsset font && font != null)
                {
                    Add(font);
                    Add(font.material);
                    if (font.atlasTextures != null)
                        foreach (var page in font.atlasTextures) Add(page);
                }
            }

            void Add(UnityObject? asset)
            {
                if (asset == null) return;
                var id = asset.GetInstanceID();
                if (!unique.Add(id)) return;
                if (!assets.TryGetValue(id, out var entry))
                    assets.Add(id, entry = new AssetReference(id, asset));
                entry.References++;
                held.Add(entry);
            }
        }

        private void ReleaseAssets(List<AssetReference> releasing)
        {
            foreach (var entry in releasing)
            {
                if (--entry.References == 0) pendingRelease.Add(entry);
            }
            if (references > 1 && requests == 0) ReleaseUnused();
        }

        private void ReleaseUnused()
        {
            List<Exception>? failures = null;
            foreach (var entry in pendingRelease)
            {
                if (entry.References != 0 || !assets.Remove(entry.Id) || entry.Asset == null) continue;
                try { Resources.UnloadAsset(entry.Asset); }
                catch (Exception error) { (failures ??= new List<Exception>()).Add(error); }
            }
            pendingRelease.Clear();
            if (failures != null)
                throw new AggregateException("One or more UI assets could not retire.", failures);
        }

        private void ReleaseBundle()
        {
            if (--references != 0) return;
            try { Native.Unload(true); }
            catch { unloadFailed = true; throw; }
            onUnloaded();
        }

        private sealed class AssetReference
        {
            internal AssetReference(int id, UnityObject asset) { Id = id; Asset = asset; }
            internal readonly int Id;
            internal readonly UnityObject Asset;
            internal int References;
        }

        private sealed class ReleaseLease : IDisposable
        {
            private Action? release;
            internal ReleaseLease(Action release) { this.release = release; }
            public void Dispose()
            {
                var action = release;
                release = null;
                action?.Invoke();
            }
        }
    }
}
