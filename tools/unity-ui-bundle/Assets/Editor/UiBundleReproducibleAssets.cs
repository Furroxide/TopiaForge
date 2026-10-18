using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using TMPro;
using UnityEditor;

namespace TopiaForge
{
    /// <summary>
    /// Makes a freshly baked TMP font asset reproducible byte for byte. Unity gives a new asset a random
    /// GUID, gives every object AddObjectToAsset adds a random local id, and orders a file's objects by
    /// id. Two bakes of identical glyphs would therefore write different files, and different bundles,
    /// since a bundle derives its object ids from both. After a bake the asset is written again with its
    /// pinned GUID and fixed sub-asset ids, the way an asset arrives from version control.
    /// </summary>
    internal static class UiBundleReproducibleAssets
    {
        private const string DerivativeUserData = "TopiaForge generated SDF derivative";

        // A baked SDF is about 4.4 MB of YAML, two 1024 px atlas pages included.
        private const int MaximumAssetBytes = 64 * 1024 * 1024;

        // The material, then atlas page n: the ids Unity gives a lone material or texture file.
        private const long MaterialId = 2100000;
        private const long FirstAtlasPageId = 2800000;

        // The committed GUID of each baked asset, kept by every re-bake.
        private static readonly Dictionary<string, string> PinnedGuids = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Assets/FontAssets/TopiaForge Body SDF.asset"] = "a7af2c56a723c874e9d65a55ecc05d8d",
            ["Assets/FontAssets/TopiaForge Body Medium SDF.asset"] = "13e3c16d25e95364896e6d265325cd30",
            ["Assets/FontAssets/TopiaForge Display SDF.asset"] = "eda53be3aa9563042882b0e7b7678aaf",
        };

        /// <summary>Rewrites a just-saved font asset with its pinned GUID and fixed sub-asset ids.</summary>
        internal static void Pin(string assetPath, string bundleName)
        {
            if (!PinnedGuids.TryGetValue(assetPath, out var guid))
            {
                throw new InvalidOperationException("No pinned GUID for " + assetPath + "; add one to UiBundleReproducibleAssets.");
            }

            var fontAsset = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(assetPath);
            if (fontAsset == null)
            {
                throw new InvalidOperationException("Baked TMP font asset could not be loaded: " + assetPath);
            }

            var ids = new Dictionary<long, long>();
            AddId(ids, fontAsset.material, MaterialId, assetPath);
            var pages = fontAsset.atlasTextures;
            for (var page = 0; page < pages.Length; page++)
            {
                AddId(ids, pages[page], FirstAtlasPageId + page, assetPath);
            }

            var text = new UTF8Encoding(false).GetString(
                UiSmokeAssemblyFileIo.ReadStableBytes(assetPath, MaximumAssetBytes, "The baked font asset"));
            foreach (var pair in ids)
            {
                // A document anchor (&id) or a reference ({fileID: id}), never a longer number.
                var pattern = "(?<=&|fileID: )" + Regex.Escape(pair.Key.ToString()) + "(?![0-9])";
                text = Regex.Replace(text, pattern, pair.Value.ToString(), RegexOptions.CultureInvariant);
            }

            if (!AssetDatabase.DeleteAsset(assetPath))
            {
                throw new InvalidOperationException("Could not replace the baked asset " + assetPath + ".");
            }

            File.WriteAllText(assetPath, text, new UTF8Encoding(false));
            File.WriteAllText(assetPath + ".meta", Meta(guid, bundleName), new UTF8Encoding(false));
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            AssetDatabase.ForceReserializeAssets(new[] { assetPath }, ForceReserializeAssetsOptions.ReserializeAssetsAndMetadata);

            var imported = AssetDatabase.AssetPathToGUID(assetPath, AssetPathToGUIDOptions.OnlyExistingAssets);
            if (!string.Equals(imported, guid, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(assetPath + " imported with GUID " + imported + " instead of its pinned " + guid + ".");
            }
        }

        private static void AddId(Dictionary<long, long> ids, UnityEngine.Object subAsset, long pinned, string assetPath)
        {
            if (subAsset == null)
            {
                return;
            }

            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(subAsset, out _, out long current))
            {
                throw new InvalidOperationException(subAsset.name + " is not saved inside " + assetPath + ".");
            }

            ids[current] = pinned;
        }

        private static string Meta(string guid, string bundleName)
        {
            return "fileFormatVersion: 2\n" +
                "guid: " + guid + "\n" +
                "NativeFormatImporter:\n" +
                "  externalObjects: {}\n" +
                "  mainObjectFileID: 11400000\n" +
                "  userData: " + DerivativeUserData + "\n" +
                "  assetBundleName: " + bundleName + "\n" +
                "  assetBundleVariant: \n";
        }
    }
}
