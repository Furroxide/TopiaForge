using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.UiGallery.Pages;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitPresentationGalleryTests
    {
        private static void Stingers(GalleryScenario scene, CaptureFrames frames, UiThemeRef? theme)
        {
            var queue = PresentationPages.StingerQueue();
            var admissions = new List<ModErrorCode>(queue.Count);
            using var controls = scene.Open(PresentationPages.StingerControls(
                request => Require(scene.ShowStinger(request)),
                () =>
                {
                    foreach (var request in queue)
                        admissions.Add(scene.BroadcastStinger(request).ErrorCode);
                }, theme), local: true);
            var roles = PresentationPages.StingerRoles;
            frames.Verify(roles.SequenceEqual(Enum.GetValues<UiPaintRole>()), "The launcher covers every semantic paint role.");
            frames.Controls(controls, roles.Select(PresentationPages.StingerButtonId).Append(PresentationPages.StingerQueueId).ToArray());

            foreach (var role in roles)
            {
                Require(controls.ActivateButton(PresentationPages.StingerButtonId(role)));
                var request = PresentationPages.Stinger(role);
                frames.Verify(request.Role == role, "The preset preserves the requested paint role.");
                ReadStinger(scene, frames, request, "role/" + role, broadcast: false);
                frames.Verify(scene.Ui.ActiveStinger == null, "The individual announcement expires before playing the next role.");
            }

            var seen = scene.Ui.Stingers.Count;
            Require(controls.ActivateButton(PresentationPages.StingerQueueId));
            frames.Verify(admissions.SequenceEqual(new[]
            {
                ModErrorCode.None, ModErrorCode.None, ModErrorCode.None, ModErrorCode.None, ModErrorCode.None, ModErrorCode.RateLimited
            }), "A queued duplicate coalesces and the fourth waiting request is refused.");
            frames.Verify(scene.Ui.Stingers.Count == seen + 4 &&
                scene.Ui.Stingers.Skip(seen + 1).All(item => double.IsNaN(item.ShownAt)) &&
                ReferenceEquals(scene.Ui.Stingers[seen + 1].Request, queue[1]),
                "Exactly four entries are admitted, with their original durations. Waiting entries have no shown time.");
            frames.Trace(new { queueAdmissions = admissions.Select(code => code.ToString()).ToArray(), count = scene.Ui.Stingers.Count - seen });
            foreach (var index in new[] { 0, 1, 2, 4 })
                ReadStinger(scene, frames, queue[index], "queue/" + index, broadcast: scene.IsGuest);
            frames.Verify(scene.Ui.ActiveStinger == null, "The complete announcement queue drains in order.");
        }

        private static void ReadStinger(GalleryScenario scene, CaptureFrames frames, UiStingerRequest request, string id, bool broadcast)
        {
            var current = scene.Ui.ActiveStinger;
            frames.Verify(current?.Request.Title == request.Title && current.Request.DurationSeconds == request.DurationSeconds &&
                current.Broadcast == broadcast, id + " has the intended request, duration and delivery route.");
            var intro = CaptureStinger(scene.Ui, frames.Viewport);
            var introState = frames.Preferences.ReducedMotion ? "shown" : "entering";
            CheckStinger(frames, request, intro, introState);
            frames.Add(id + "/" + introState, intro);

            if (!frames.Preferences.ReducedMotion)
            {
                scene.Step((frames.Theme.Motion.BaseMs + 10) / 1000d);
                var shown = CaptureStinger(scene.Ui, frames.Viewport);
                CheckStinger(frames, request, shown, "shown");
                frames.Add(id + "/shown", shown);
            }

            scene.Step(current!.ShownAt + request.DurationSeconds + .01d - scene.Ui.ClockTime);
            if (!frames.Preferences.ReducedMotion)
            {
                var exiting = CaptureStinger(scene.Ui, frames.Viewport);
                CheckStinger(frames, request, exiting, "exiting");
                frames.Add(id + "/exiting", exiting);
                scene.Step((frames.Theme.Motion.SlowMs + 10) / 1000d);
            }
        }

        private static void CheckStinger(CaptureFrames frames, UiStingerRequest request, UiTreeCapture tree, string state)
        {
            var panel = Part(tree, "Stinger");
            var title = Part(tree, "StingerTitle");
            var subtitle = Part(tree, "StingerSubtitle");
            var glyph = Part(tree, "StingerGlyph");
            var pair = frames.Theme.SelectScheme(hud: true, frames.Preferences.HighContrast).GetPair(request.Role);
            frames.Verify(panel.State == state && panel.ThemeId == frames.Theme.Id && panel.FillColor == pair.Fill.ToString() &&
                title.FillColor == panel.FillColor && title.InkColor == pair.On.ToString() && subtitle.InkColor == title.InkColor,
                "The announcement uses the selected HUD role and intended motion state.");
            frames.Verify(panel.Announcement == request.Title + ". " + request.Subtitle &&
                title.SemanticLabel == request.Title && subtitle.SemanticLabel == request.Subtitle &&
                glyph.SemanticLabel == request.Glyph?.ToString(), "The announced copy, text and glyph agree.");
        }

        private static void Overlays(GalleryScenario scene, CaptureFrames frames, UiThemeRef? theme)
        {
            var overlays = new Dictionary<PresentationOverlay, FakeUiSurface>();
            FakeUiSurface? hud = null;
            using var controls = scene.Open(PresentationPages.OverlayControls(Show,
                () =>
                {
                    hud = scene.Open(PresentationPages.RetainedHud(theme));
                    foreach (var variant in PresentationPages.OverlayOrder) Show(variant);
                }, theme), local: true);
            frames.Controls(controls, PresentationPages.OverlayOrder.Select(PresentationPages.OverlayButtonId)
                .Append(PresentationPages.OverlayStackId).ToArray());
            Require(controls.ActivateButton(PresentationPages.OverlayStackId));
            using var retained = hud!;
            using var passive = overlays[PresentationOverlay.Passive];
            using var back = overlays[PresentationOverlay.Dismissible];
            using var locked = overlays[PresentationOverlay.ButtonOnly];
            var dismissals = 0;
            locked.Dismissed += () => dismissals++;
            back.Dismissed += () => dismissals++;
            frames.Add("hud/start", retained.Capture(frames.Viewport));
            frames.Verify(passive.Request.Dismissible && back.Request.Dismissible && !locked.Request.Dismissible,
                "The passive, Back-dismissible and button-only requests reach the viewer with their flags.");
            RecordOverlays(frames.Preferences.ReducedMotion ? "shown" : "entering");
            if (!frames.Preferences.ReducedMotion)
            {
                scene.Step((frames.Theme.Motion.BaseMs + 10) / 1000d);
                RecordOverlays("shown");
            }

            locked.DismissFromPlayer();
            frames.Verify(locked.IsShown && dismissals == 0 &&
                back.ActivateButton(PresentationPages.OverlayCloseId).ErrorCode == ModErrorCode.InvalidState,
                "Back leaves the locked top overlay visible and the lower overlay cannot receive its button press.");
            Require(retained.SetContent(PresentationPages.HudStatus("Supplies loaded.")));
            var updated = retained.Capture(frames.Viewport);
            frames.Verify(updated.Nodes.Any(node => node.Id == "gallery-presentation-status" && node.SemanticLabel == "Supplies loaded."),
                "The HUD remains live under the stack and reports its updated status.");
            frames.Add("hud/updated-under-stack", updated);

            Require(locked.ActivateButton(PresentationPages.OverlayCloseId));
            frames.Verify(!locked.IsShown && back.IsShown && dismissals == 1,
                "The top overlay's enabled button closes it and preserves the overlay underneath.");
            if (!frames.Preferences.ReducedMotion)
            {
                var exiting = locked.Capture(frames.Viewport);
                frames.Verify(Part(exiting, "Overlay").State == "exiting", "The top overlay remains in the exit transition.");
                frames.Add("button-only/exiting", exiting);
                scene.Step((frames.Theme.Motion.FastMs + 10) / 1000d);
            }

            back.DismissFromPlayer();
            passive.DismissFromPlayer();
            frames.Verify(!back.IsShown && passive.IsShown && retained.IsShown && dismissals == 2,
                "Back closes the dismissible overlay once and leaves the passive overlay and HUD intact.");
            frames.Add("passive/after-back", passive.Capture(frames.Viewport));
            frames.Trace(new { dismissals, passive = passive.IsShown, hud = retained.IsShown });

            void Show(PresentationOverlay variant)
            {
                FakeUiSurface? surface = null;
                surface = scene.Open(PresentationPages.Overlay(variant, () => surface!.Hide(), theme));
                overlays.Add(variant, surface);
            }

            void RecordOverlays(string state)
            {
                var surfaceFill = frames.Theme.SelectScheme(hud: true, frames.Preferences.HighContrast).GetPair(UiPaintRole.Surface).Fill.ToString();
                foreach (var variant in PresentationPages.OverlayOrder)
                {
                    var tree = overlays[variant].Capture(frames.Viewport);
                    var panel = Part(tree, "Overlay");
                    var scrim = Part(tree, "OverlayScrim");
                    var title = Part(tree, "OverlayTitle");
                    frames.Verify(panel.State == state && panel.ThemeId == frames.Theme.Id &&
                        panel.FillColor == surfaceFill && title.FillColor == panel.FillColor && FillOpacity(panel) == 1f &&
                        scrim.Decorative && FillOpacity(scrim) == (state == "shown" ? .72f : 0f),
                        "Overlay text has an opaque HUD backplate and the independent scrim follows its transition.");
                    frames.Add(variant + "/" + state, tree);
                }
            }
        }

        private static void Toasts(GalleryScenario scene, CaptureFrames frames, UiThemeRef? theme)
        {
            using var hud = scene.Open(PresentationPages.RetainedHud(theme));
            void Show(UiTone tone) => Require(scene.ShowToast(PresentationPages.ToastMessage(tone), tone));
            using var controls = scene.Open(PresentationPages.ToastControls(Show,
                () =>
                {
                    foreach (var tone in PresentationPages.ToastTones) Show(tone);
                }, theme), local: true);
            frames.Controls(controls, PresentationPages.ToastStackId);
            Require(controls.ActivateButton(PresentationPages.ToastStackId));

            var hudTree = hud.Capture(frames.Viewport);
            frames.Verify(Part(hudTree, "Hud").ThemeId == frames.Theme.Id && scene.Ui.Themes.SelectedTheme?.Id == theme?.Id,
                "The surrounding scope and its retained HUD use the selected mod palette.");
            frames.Add("hud", hudTree);

            var tree = CaptureToasts(scene.Ui, frames.Viewport);
            var kit = UiThemePackParser.Default;
            var fill = kit.SelectScheme(hud: true, frames.Preferences.HighContrast).GetPair(UiPaintRole.Raised).Fill.ToString();
            var panels = tree.Nodes.Where(node => node.Kind == "Toast").ToArray();
            frames.Verify(panels.Length == 4 && panels.All(node => node.ThemeId == kit.Id && node.FillColor == fill) &&
                panels.Zip(panels.Skip(1)).All(pair => pair.Second.Y + pair.Second.Height < pair.First.Y) &&
                panels[0].Y + panels[0].Height < Part(hudTree, "Hud").Y,
                "The four kit toasts stack above the mod-themed bottom-right HUD.");
            frames.Verify(tree.Nodes.Where(node => node.Kind == "ToastLabel").Select(node => node.SemanticLabel)
                .SequenceEqual(PresentationPages.ToastTones.Select(PresentationPages.ToastMessage)) &&
                tree.Nodes.Where(node => node.Kind == "ToastGlyph").Select(node => node.SemanticLabel)
                    .SequenceEqual(new[] { "Check", "Warning", "Cross" }),
                "The four tone labels remain legible and the status glyphs are distinct.");
            frames.Add("stack", tree);
        }

        private sealed class GalleryScenario : IDisposable
        {
            private readonly FakeModContext host = new FakeModContext();
            private readonly FakePlayerDirectory? players;
            private readonly IDisposable? extension;
            private readonly IPlayer? guest;
            internal readonly FakeUiService Ui;
            internal bool IsGuest => guest != null;

            internal GalleryScenario(bool receiving, UiAccessibilityPreferences preferences, UiThemeRef? theme, string definition)
            {
                Ui = host.Ui;
                Configure(Ui, preferences, theme, definition);
                if (!receiving) return;

                players = new FakePlayerDirectory();
                extension = Require(host.Extensions.Register<IPlayerDirectory>(players));
                _ = new FakeGamemodeSession(host, new WorldReadiness(new WorldSceneIdentity(1, "Gallery"), TransformState.Identity));
                guest = players.Add(new ParticipantId("gallery-guest"), "Sam");
                Ui = players.State(guest.Id).Ui;
                ((IUiClockReceiver)Ui).FollowClock(() => host.Ui.ClockTime, () => "gallery-presentation");
                Configure(Ui, preferences, theme, definition);
            }

            private static void Configure(FakeUiService ui, UiAccessibilityPreferences preferences, UiThemeRef? theme, string definition)
            {
                Require(ui.ApplyAccessibility(preferences));
                if (theme == null) return;
                Require(ui.AddThemeFixture(definition));
                Require(ui.Themes.UseTheme(theme));
                ui.CompletePendingThemeRegistrations();
            }

            internal FakeUiSurface Open(UiSurfaceRequest request, bool local = false) =>
                (FakeUiSurface)Require(local || guest == null ? Ui.CreateSurface(request) : guest.Ui.CreateSurface(request));
            internal OperationResult<bool> ShowStinger(UiStingerRequest request) =>
                guest == null ? Ui.ShowStinger(request) : guest.Ui.ShowStinger(request);
            internal OperationResult<bool> BroadcastStinger(UiStingerRequest request) => host.Ui.ShowStinger(request);
            internal OperationResult<bool> ShowToast(string message, UiTone tone) =>
                guest == null ? Ui.ShowToast(message, tone) : guest.Ui.ShowToast(message, tone);

            internal void Step(double seconds) => host.AdvanceFrame(TimeSpan.FromSeconds(seconds));

            public void Dispose()
            {
                extension?.Dispose();
                players?.Dispose();
                host.Dispose();
            }
        }
    }
}
