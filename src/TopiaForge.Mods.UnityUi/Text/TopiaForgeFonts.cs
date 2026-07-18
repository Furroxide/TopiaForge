using System;
using TMPro;
using UnityEngine;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// TMP font resolution with a tiered fallback chain, resolved lazily on first use:
    ///   1. Brand bundle embedded in the DLL (Quicksand + Audiowide SDF assets).
    ///   2. Dynamic TMP asset from an OS font (Segoe UI).
    ///   3. The game's own TMP default font asset.
    ///   4. Null — TopiaForgeSafeMode surfaces a diagnostic banner; labels render empty.
    /// Each tier logs, and the resolved tier is part of the one-shot init report.
    /// </summary>
    public static class TopiaForgeFonts
    {
        private static bool resolved;
        private static TMP_FontAsset? body;
        private static TMP_FontAsset? bold;
        private static TMP_FontAsset? display;

        /// <summary>Gets or sets resolved tier.</summary>
        public static string ResolvedTier { get; private set; } = "unresolved";

        /// <summary>Gets body.</summary>
        public static TMP_FontAsset? Body
        {
            get
            {
                EnsureResolved();
                return body;
            }
        }

        /// <summary>Gets bold.</summary>
        public static TMP_FontAsset? Bold
        {
            get
            {
                EnsureResolved();
                return bold;
            }
        }

        /// <summary>Gets display.</summary>
        public static TMP_FontAsset? Display
        {
            get
            {
                EnsureResolved();
                return display;
            }
        }

        /// <summary>True when a style must be synthesized with TMP faux-bold (no dedicated asset).</summary>
        public static bool UseFauxBold => EnsureResolved() && ReferenceEquals(bold, body);

        /// <summary>True when display styles share the body asset (fallback tiers).</summary>
        public static bool UseFauxDisplay => EnsureResolved() && ReferenceEquals(display, body);

        /// <summary>Gets a motion preset adjusted for the active accessibility settings.</summary>
        public static TMP_FontAsset? For(TopiaForgeTextStyle style)
        {
            EnsureResolved();
            if (TopiaForgeTokens.IsDisplay(style))
            {
                return display;
            }

            return TopiaForgeTokens.IsBold(style) ? bold : body;
        }

        private static bool EnsureResolved()
        {
            if (resolved)
            {
                return true;
            }

            resolved = true;

            // Tier 1: embedded brand bundle.
            if (TopiaForgeBrandBundle.TryLoad() && TopiaForgeBrandBundle.BodyFont != null)
            {
                body = TopiaForgeBrandBundle.BodyFont;
                bold = TopiaForgeBrandBundle.BoldFont != null ? TopiaForgeBrandBundle.BoldFont : TopiaForgeBrandBundle.BodyFont;
                display = TopiaForgeBrandBundle.DisplayFont != null ? TopiaForgeBrandBundle.DisplayFont : bold;
                ResolvedTier = "brand-bundle";
                TopiaForgeLog.Info("Fonts resolved from brand bundle (body: " + body.name + ", display: " + display.name + ").");
                return true;
            }

            // Tier 2: dynamic TMP asset from an OS font.
            try
            {
                var osFont = Font.CreateDynamicFontFromOSFont("Segoe UI", 32);
                if (osFont != null)
                {
                    var asset = TMP_FontAsset.CreateFontAsset(osFont);
                    if (asset != null)
                    {
                        body = asset;
                        bold = asset;
                        display = asset;
                        ResolvedTier = "os-font";
                        TopiaForgeLog.Warn("Fonts resolved from OS font 'Segoe UI' (brand bundle unavailable). Brand typography is degraded.");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                TopiaForgeLog.Warn("OS font tier failed (" + ex.GetType().Name + ": " + ex.Message + "); trying the game's TMP default.");
            }

            // Tier 3: the game's TMP default font asset.
            try
            {
                var gameDefault = TMP_Settings.defaultFontAsset;
                if (gameDefault != null)
                {
                    body = gameDefault;
                    bold = gameDefault;
                    display = gameDefault;
                    ResolvedTier = "game-default";
                    TopiaForgeLog.Warn("Fonts resolved from the game's TMP default asset '" + gameDefault.name + "'.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                TopiaForgeLog.Warn("Game TMP default tier failed (" + ex.GetType().Name + ": " + ex.Message + ").");
            }

            // Tier 4: nothing usable.
            ResolvedTier = "none";
            TopiaForgeLog.Error("No TMP font could be resolved; UI text will not render. TopiaForgeSafeMode banner engaged.");
            TopiaForgeSafeMode.Engage("TopiaForge UI failed to initialize text - see the BepInEx log.");
            return true;
        }
    }
}
