using System;
using System.IO;
using System.Reflection;
using TMPro;
using UnityEngine;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Loads the TopiaForge brand AssetBundle embedded in this DLL (see the csproj
    /// EmbeddedResource and `topiaforge unity build-ui-bundle`). Embedding version-locks assets
    /// to code, so a missing bundle only ever means "not built yet" — the kit then runs
    /// on the OS-font/procedural-sprite tiers and logs why.
    /// </summary>
    public static class TopiaForgeBrandBundle
    {
        private const string ResourceName = "TopiaForge.Mods.UnityUi.topiaforge-ui.bundle";

        // Quicksand Medium (500): body, label and caption text.
        private const string BodyName = "TopiaForge Body Medium SDF";

        // Quicksand Light (300), the variable font's default instance: headings and numerals.
        private const string HeadingName = "TopiaForge Body SDF";
        private const string DisplayName = "TopiaForge Display SDF";

        private static bool attempted;
        private static AssetBundle? bundle;
        private static Stream? bundleStream; // must stay open for the bundle's lifetime

        /// <summary>Gets whether the embedded brand asset bundle is loaded.</summary>
        public static bool IsLoaded => bundle != null;

        /// <summary>Gets the small-text face, Quicksand Medium: body, label and caption text.</summary>
        public static TMP_FontAsset? BodyFont { get; private set; }
        /// <summary>Gets the face for Quicksand text larger than body text, Quicksand Light: headings and numerals.</summary>
        public static TMP_FontAsset? HeadingFont { get; private set; }
        /// <summary>Gets or sets display font.</summary>
        public static TMP_FontAsset? DisplayFont { get; private set; }

        /// <summary>Attempts the embedded-bundle load once; subsequent calls are free.</summary>
        public static bool TryLoad()
        {
            if (attempted)
            {
                return bundle != null;
            }

            attempted = true;
            try
            {
                var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
                if (stream == null)
                {
                    TopiaForgeLog.Info("Brand bundle not embedded in this build (run 'topiaforge unity build-ui-bundle'); using fallback fonts and procedural sprites.");
                    return false;
                }

                bundleStream = stream;
                bundle = AssetBundle.LoadFromStream(stream);
                if (bundle == null)
                {
                    TopiaForgeLog.Warn("Embedded brand bundle failed to load (AssetBundle.LoadFromStream returned null) - likely a Unity version mismatch. Falling back.");
                    Cleanup();
                    return false;
                }

                BodyFont = bundle.LoadAsset<TMP_FontAsset>(BodyName);
                HeadingFont = bundle.LoadAsset<TMP_FontAsset>(HeadingName);
                DisplayFont = bundle.LoadAsset<TMP_FontAsset>(DisplayName);

                var provenance = bundle.LoadAsset<TextAsset>("UiBundleManifest");
                TopiaForgeLog.Info("Brand bundle loaded" + (provenance != null ? ": " + Condense(provenance.text) : "."));

                if (BodyFont == null || HeadingFont == null || DisplayFont == null)
                {
                    TopiaForgeLog.Warn("Brand bundle is missing expected font assets (" + BodyName + ", " + HeadingName + ", " + DisplayName + "); the faces it has fill the gaps.");
                }

                return true;
            }
            catch (Exception ex)
            {
                TopiaForgeLog.Error(ex, "Embedded brand bundle load failed; falling back to OS fonts and procedural sprites.");
                Cleanup();
                return false;
            }
        }

        /// <summary>Optional bundle sprite override hook (returns null when absent).</summary>
        public static Sprite? LoadSprite(string name)
        {
            return bundle == null ? null : bundle.LoadAsset<Sprite>(name);
        }

        private static void Cleanup()
        {
            if (bundle != null)
            {
                bundle.Unload(unloadAllLoadedObjects: true);
                bundle = null;
            }

            bundleStream?.Dispose();
            bundleStream = null;
            BodyFont = null;
            HeadingFont = null;
            DisplayFont = null;
        }

        private static string Condense(string json)
        {
            return json.Replace("\r", string.Empty).Replace("\n", " ").Replace("  ", string.Empty);
        }
    }
}
