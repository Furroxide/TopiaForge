using System;
using System.IO;
using System.Reflection;
using TMPro;
using UnityEngine;

namespace Robotopia.Mods.UnityUi
{
    /// <summary>
    /// Loads the QuantumWorks brand AssetBundle embedded in this DLL (see the csproj
    /// EmbeddedResource and `robotopia unity build-ui-bundle`). Embedding version-locks assets
    /// to code, so a missing bundle only ever means "not built yet" — the kit then runs
    /// on the OS-font/procedural-sprite tiers and logs why.
    /// </summary>
    public static class QwBrandBundle
    {
        private const string ResourceName = "Robotopia.Mods.UnityUi.quantumworks-ui.bundle";
        private const string QuicksandName = "QuantumWorks-Quicksand SDF";
        private const string QuicksandBoldName = "QuantumWorks-Quicksand-Bold SDF";
        private const string AudiowideName = "QuantumWorks-Audiowide SDF";

        private static bool attempted;
        private static AssetBundle? bundle;
        private static Stream? bundleStream; // must stay open for the bundle's lifetime

        public static bool IsLoaded => bundle != null;

        public static TMP_FontAsset? BodyFont { get; private set; }
        public static TMP_FontAsset? BoldFont { get; private set; }
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
                    QwLog.Info("Brand bundle not embedded in this build (run 'robotopia unity build-ui-bundle'); using fallback fonts and procedural sprites.");
                    return false;
                }

                bundleStream = stream;
                bundle = AssetBundle.LoadFromStream(stream);
                if (bundle == null)
                {
                    QwLog.Warn("Embedded brand bundle failed to load (AssetBundle.LoadFromStream returned null) - likely a Unity version mismatch. Falling back.");
                    Cleanup();
                    return false;
                }

                BodyFont = bundle.LoadAsset<TMP_FontAsset>(QuicksandName);
                BoldFont = bundle.LoadAsset<TMP_FontAsset>(QuicksandBoldName);
                DisplayFont = bundle.LoadAsset<TMP_FontAsset>(AudiowideName);

                var provenance = bundle.LoadAsset<TextAsset>("UiBundleManifest");
                QwLog.Info("Brand bundle loaded" + (provenance != null ? ": " + Condense(provenance.text) : "."));

                if (BodyFont == null || DisplayFont == null)
                {
                    QwLog.Warn("Brand bundle is missing expected font assets (" + QuicksandName + ", " + AudiowideName + "); font fallback tiers will fill the gaps.");
                }

                return true;
            }
            catch (Exception ex)
            {
                QwLog.Error(ex, "Embedded brand bundle load failed; falling back to OS fonts and procedural sprites.");
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
            BoldFont = null;
            DisplayFont = null;
        }

        private static string Condense(string json)
        {
            return json.Replace("\r", string.Empty).Replace("\n", " ").Replace("  ", string.Empty);
        }
    }
}
