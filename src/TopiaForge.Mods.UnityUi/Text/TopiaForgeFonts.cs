using System;
using TMPro;
using UnityEngine;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// TMP font resolution with a tiered fallback chain, resolved lazily on first use:
    ///   1. Brand bundle embedded in the DLL: Quicksand Medium, Quicksand Light and Audiowide SDF assets.
    ///   2. Dynamic TMP asset from an OS font (Segoe UI).
    ///   3. The game's own TMP default font asset.
    ///   4. Null — TopiaForgeSafeMode surfaces a diagnostic banner; labels render empty.
    /// Each tier logs, and the resolved tier is part of the one-shot init report. A style's face comes
    /// from <see cref="TopiaForgeTokens.FaceOf"/> alone, so small text gets the Medium face everywhere.
    /// </summary>
    public static class TopiaForgeFonts
    {
        private static bool resolved;
        private static TMP_FontAsset? body;
        private static TMP_FontAsset? heading;
        private static TMP_FontAsset? display;

        /// <summary>Gets or sets resolved tier.</summary>
        public static string ResolvedTier { get; private set; } = "unresolved";

        /// <summary>Gets the small-text face: body, label and caption text (Quicksand Medium in the brand tier).</summary>
        public static TMP_FontAsset? Body
        {
            get
            {
                EnsureResolved();
                return body;
            }
        }

        /// <summary>Gets the face for Quicksand text larger than body text (Quicksand Light in the brand tier).</summary>
        public static TMP_FontAsset? Heading
        {
            get
            {
                EnsureResolved();
                return heading;
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

        /// <summary>
        /// True in every tier: no bold face ships, so bold styles are drawn with TMP faux bold over the face
        /// their style resolves to.
        /// </summary>
        public static bool UseFauxBold => true;

        /// <summary>True when display styles share a text face (fallback tiers).</summary>
        public static bool UseFauxDisplay => EnsureResolved() && (ReferenceEquals(display, body) || ReferenceEquals(display, heading));

        /// <summary>Gets the font asset for a text style: the face <see cref="TopiaForgeTokens.FaceOf"/> gives it.</summary>
        public static TMP_FontAsset? For(TopiaForgeTextStyle style)
        {
            EnsureResolved();
            switch (TopiaForgeTokens.FaceOf(style))
            {
                case TopiaForgeFace.Display:
                    return display;
                case TopiaForgeFace.Heading:
                    return heading;
                default:
                    return body;
            }
        }

        private static bool EnsureResolved()
        {
            if (resolved)
            {
                return true;
            }

            resolved = true;

            // Tier 1: embedded brand bundle. A bundle that lacks one Quicksand weight draws its styles with the other.
            if (TopiaForgeBrandBundle.TryLoad() && (TopiaForgeBrandBundle.BodyFont != null || TopiaForgeBrandBundle.HeadingFont != null))
            {
                var bodyFont = TopiaForgeBrandBundle.BodyFont != null ? TopiaForgeBrandBundle.BodyFont : TopiaForgeBrandBundle.HeadingFont!;
                var headingFont = TopiaForgeBrandBundle.HeadingFont != null ? TopiaForgeBrandBundle.HeadingFont : bodyFont;
                var displayFont = TopiaForgeBrandBundle.DisplayFont != null ? TopiaForgeBrandBundle.DisplayFont : headingFont;
                body = bodyFont;
                heading = headingFont;
                display = displayFont;
                ResolvedTier = "brand-bundle";
                TopiaForgeLog.Info("Fonts resolved from brand bundle (body: " + bodyFont.name + ", heading: " + headingFont.name + ", display: " + displayFont.name + ").");
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
                        heading = asset;
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
                    heading = gameDefault;
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
