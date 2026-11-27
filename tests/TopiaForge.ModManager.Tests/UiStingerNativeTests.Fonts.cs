namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiStingerNativeTests
    {
        private const string FontScenarios = """"
            using System;
            using System.Linq;
            using TMPro;
            using TopiaForge.ModManager.Core;
            using TopiaForge.Mods;
            using TopiaForge.Mods.Testing;
            using TopiaForge.Mods.UnityUi;
            using UnityEngine;

            namespace TopiaForge.ModManager
            {
                internal static partial class StingerProbe
                {
                    private static UiThemeDescriptor FontTheme() => Parse("""
                        {"schema":1,"id":"tested-fonts","fonts":[{"id":"brand","bundlePath":"ui/fonts.bundle",
                         "assetName":"Brand","licensePath":"ui/LICENSE.txt"}],
                         "typography":{"banner":{"font":"brand","case":"upper","trackingEm":0.07},
                                       "title":{"font":"brand","case":"upper","trackingEm":0.02}}}
                        """);

                    private static void FontFitUsesAuthoredInputs()
                    {
                        var theme = FontTheme();
                        var host = new UiHost { EffectiveReducedMotion = true };
                        var provider = new UiThemeScope(theme);
                        var sample = new UiStingerRequest("Check");
                        Check(UnityUiStingers.ValidateForDelivery(host, sample, theme, provider, out var pending).Succeeded &&
                            pending == UiStingerFontFit.KitOnly, "A pending face admits immediately with kit-only wire eligibility.");
                        provider.RecordedFailure = true;
                        Check(UnityUiStingers.ValidateForDelivery(host, sample, theme, provider, out var failed).Succeeded &&
                            failed == UiStingerFontFit.KitOnly && provider.FailedRetries == 0 && provider.Leases == 0,
                            "A recorded failed face measures kit fallback without requesting a retry or leaking a probe lease.");

                        var narrow = new TMP_FontAsset { Advance = 8f };
                        provider.RecordedFailure = false;
                        provider.Ready = new UiThemeRegistration(narrow);
                        Check(UnityUiStingers.ValidateForDelivery(host, sample, theme, provider, out var ready).Succeeded &&
                            ready == UiStingerFontFit.BothMeasured,
                            "An empty subtitle needs no face even when the selected style declares one.");
                        Check(UnityUiStingers.ValidateForDelivery(host, new UiStingerRequest("Check", "Read"), theme, provider,
                            out var partlyReady).Succeeded && partlyReady == UiStingerFontFit.KitOnly,
                            "A missing face on a nonempty role makes the whole event kit-only.");

                        var mixed = new TMP_FontAsset { Coverage = "A", Advance = 8f, Fallback = new TMP_FontAsset { Advance = 84f } };
                        provider.Ready = new UiThemeRegistration(mixed);
                        var mixedTitle = new UiStingerRequest(string.Concat(Enumerable.Repeat("aΩ", 20)));
                        Check(UnityUiStingers.ValidateForDelivery(host, mixedTitle, theme, provider, out _).ErrorCode == ModErrorCode.InvalidArgument,
                            "The complete declared chain measures mixed glyph coverage under the authored case and spacing.");
                        var trackingBoundary = new UiStingerRequest(new string('a', 40));
                        provider.Ready = new UiThemeRegistration(new TMP_FontAsset { Advance = 38f });
                        Check(UnityUiStingers.ValidateForDelivery(host, trackingBoundary, theme, provider, out _).ErrorCode == ModErrorCode.InvalidArgument,
                            "Declared tracking is measured before admitting an event near the two-line boundary.");

                        provider.Ready = new UiThemeRegistration(narrow);
                        var localOnly = new UiStingerRequest(new string('W', 40));
                        Check(UnityUiStingers.ValidateForDelivery(host, localOnly, theme, provider, out _).ErrorCode == ModErrorCode.InvalidArgument,
                            "Delivery also measures the complete kit chain when a receiver may lack the theme or its fonts.");
                        using var scope = new UnityUiStingers(host, new TestLog());
                        Check(scope.Show(localOnly, theme, provider, null, false, out _).Succeeded &&
                            ReferenceEquals(TitleText(Active(host.LastLayer!)).font, narrow),
                            "Local display admits only the typography it actually stages.");
                    }

                    private static void PendingAndKitOnlyEventsKeepTheirFaces()
                    {
                        var theme = FontTheme();
                        var ready = new TMP_FontAsset { Advance = 12f };
                        var assets = new UiThemeAcquisitions { Ready = new UiThemeRegistration(ready, ready) };
                        var host = new UiHost { EffectiveReducedMotion = true };
                        using var life = new FakeModLifetime();
                        var guest = new OwnerUiService(host, life);
                        var catalogue = new AdmittedUiCatalogue(assets);
                        Time.unscaledTimeAsDouble = 20d;
                        Check(guest.ShowRemoteStinger("owner", 1, new UiStingerRequest("Kit only"), theme, catalogue,
                            true, UiStingerFontFit.KitOnly).Succeeded, "A ready guest accepts the sender's kit-only choice.");
                        var forced = TitleText(Active(host.LastLayer!));
                        Check(ReferenceEquals(forced.font, TopiaForgeFonts.Kit) && forced.text == "KIT ONLY" && Near(forced.characterSpacing, 7f),
                            "Kit-only receiving paint keeps the actual kit face, authored transform and authored tracking.");
                        guest.ResetRemoteStingers();

                        assets.Ready = null;
                        using var scope = new UnityUiStingers(host, new TestLog());
                        Check(scope.Show(new UiStingerRequest("Active", durationSeconds: 1), theme, null, assets, false,
                            out _, fontFit: UiStingerFontFit.BothMeasured).Succeeded &&
                            scope.Show(new UiStingerRequest("Waiting", durationSeconds: 1), theme, null, assets, false,
                            out _, fontFit: UiStingerFontFit.BothMeasured).Succeeded,
                            "A pending receiver chooses the measured kit chain for active and waiting entries.");
                        var layer = host.LastLayer!;
                        var active = Active(layer);
                        var waiting = layer.Rect.Children.Single(view => !view.activeSelf);
                        var activeText = TitleText(active);
                        var waitingText = TitleText(waiting);
                        var layoutRequests = TMP_Text.LayoutRequests;
                        assets.Ready = new UiThemeRegistration(ready, ready);
                        host.SetSurfaceTheme(active, UiThemePackParser.Default, _ => ready);
                        host.SetSurfaceTheme(waiting, UiThemePackParser.Default, _ => ready);
                        scope.Tick();
                        Check(ReferenceEquals(activeText.font, TopiaForgeFonts.Kit) && activeText.text == "ACTIVE" &&
                            ReferenceEquals(waitingText.font, TopiaForgeFonts.Kit) && waitingText.text == "WAITING" &&
                            Near(activeText.characterSpacing, 7f) && Near(waitingText.characterSpacing, 7f) &&
                            TMP_Text.LayoutRequests == layoutRequests,
                            "Acquisition completion and a theme reapply preserve the frozen faces, case, spacing and existing layout.");
                        Time.unscaledTimeAsDouble = 21d;
                        scope.Tick();
                        Check(ReferenceEquals(Active(layer), waiting) && ReferenceEquals(waitingText.font, TopiaForgeFonts.Kit),
                            "Promotion renders the font selected at enqueue for the waiting entry.");
                        Check(scope.Show(new UiStingerRequest("Future", durationSeconds: 1), theme, null, assets, false,
                            out _, fontFit: UiStingerFontFit.BothMeasured).Succeeded, "A future entry can use the newly ready face.");
                        Time.unscaledTimeAsDouble = 22d;
                        scope.Tick();
                        Check(ReferenceEquals(TitleText(Active(layer)).font, ready), "The now-ready declared chain applies only to the future entry.");
                        Console.WriteLine("Native stinger fonts verified pending and failed fits, mixed chains and frozen active and queued typography.");
                    }

                    private static TMP_Text TitleText(GameObject view) => view.Find("Title").GetComponent<TMP_Text>();
                }
            }
            """";
    }
}
