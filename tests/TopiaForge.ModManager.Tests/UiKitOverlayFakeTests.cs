using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static class UiKitOverlayFakeTests
    {
        private static readonly UiCaptureViewport Standard = new UiCaptureViewport(1920, 1080);
        private static readonly UiCaptureViewport Wide = new UiCaptureViewport(2560, 1440);
        private static readonly Func<FakeUiService, UiCaptureViewport, UiTreeCapture> StingerCapture = CaptureMethod("CaptureStinger");
        private static readonly Func<FakeUiService, UiCaptureViewport, UiTreeCapture> ToastCapture = CaptureMethod("CaptureToasts");
        private static readonly Func<FakeUiService, UiSurfaceRequest, UiWireEnvelope, OperationResult<IUiSurface>> ReceiveSurface =
            typeof(FakeUiService).GetMethod("CreateReceivedSurface", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Func<FakeUiService, UiSurfaceRequest, UiWireEnvelope, OperationResult<IUiSurface>>>();
        private static readonly Func<UiCapturedNode, float?> FillOpacity = typeof(UiCapturedNode)
            .GetProperty("FillOpacity", BindingFlags.Instance | BindingFlags.NonPublic)!.GetMethod!.CreateDelegate<Func<UiCapturedNode, float?>>();
        private static readonly Action<FakeUiService> AdvanceToasts = typeof(FakeUiService)
            .GetMethod("AdvanceToasts", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null)!
            .CreateDelegate<Action<FakeUiService>>();
        private static readonly Func<UiTreeCapture, bool?> StingerChain = typeof(UiTreeCapture)
            .GetProperty("UsesDeclaredStingerFaces", BindingFlags.Instance | BindingFlags.NonPublic)!.GetMethod!
            .CreateDelegate<Func<UiTreeCapture, bool?>>();
        private static readonly Func<FakeStinger, bool> AcceptedStingerChain = typeof(FakeStinger)
            .GetProperty("UsesDeclaredFaces", BindingFlags.Instance | BindingFlags.NonPublic)!.GetMethod!
            .CreateDelegate<Func<FakeStinger, bool>>();
        private const string FieldTheme = """
            {"schema":1,"id":"field",
             "hud":{"colors":{"surface":{"fill":"#022714","on":"#FFFFFF"}}},
             "styles":[{"id":"notice","target":"stinger","radius":"small","border":"strong","shadow":"none","fill":"primary"}]}
            """;

        internal static void Run()
        {
            OverlayMotionPreservesOpaqueTextAndReleasesFocus();
            OverlayAdmissionAndVisibilityAreTransactional();
            TargetedOverlayAdmitsWithTheWireEnvelope();
            ReceivedOverlaysKeepSeparateAuthorQuota();
            StingersAdvanceCoalesceAndDrain();
            ALongFrameDrainsCompletedStingers();
            StingerDeliveryIsPerViewerAndNeverReplayed();
            StingerCapturesKeepTheAdmittedFontChain();
            ToastsKeepTheKitThemeAndBoundedStack();
            TallHudQueuesAndPausesToasts();
            CapturesStayReadableAcrossViewports();
            SettledPresentationAllocatesNothing();
            Console.WriteLine("K2 fake overlays, stingers and toasts passed for admission, presentation, queue timing and accessibility.");
        }

        private static void OverlayMotionPreservesOpaqueTextAndReleasesFocus()
        {
            using var context = new FakeModContext();
            var ui = context.Ui;
            SelectFieldTheme(ui);
            var action = Make(context.Input.RegisterAction(new InputActionDefinition("fire", "Fire",
                new[] { InputBinding.Key("F") }, suppressWhileUiFocused: true)));
            var sampled = (FakeInputAction)action;
            using var lower = Overlay(ui, "lower", Confirm());
            sampled.SetState(1f, wasPressed: true, wasReleased: false);
            Check(context.Input.IsUiFocused && !sampled.WasPressed, "The top interactive overlay suppresses scoped gameplay input.");
            using var upper = Overlay(ui, "upper", Confirm());
            Check(lower.ActivateButton("confirm").ErrorCode == ModErrorCode.InvalidState &&
                upper.ActivateButton("confirm").Succeeded, "The newest interactive overlay owns the controls.");
            var entering = upper.Capture(Standard);
            var start = Part(entering, "Overlay");
            Check(start.State == "entering" && FillOpacity(Part(entering, "OverlayScrim")) == 0f,
                "The entry starts with a transparent scrim and a lowered column.");
            context.AdvanceFrame(TimeSpan.FromSeconds(.05));
            var mid = upper.Capture(Standard);
            Check(FillOpacity(Part(mid, "OverlayScrim")) is > 0f and < .72f && Part(mid, "Overlay").Y < start.Y,
                "Overlay motion raises the column while fading in the scrim.");
            context.AdvanceFrame(TimeSpan.FromSeconds(.5));
            var settled = upper.Capture(Standard);
            var panel = Part(settled, "Overlay");
            var title = Part(settled, "OverlayTitle");
            Check(panel is { State: "shown", ThemeId: "field" } && FillOpacity(panel) == 1f &&
                FillOpacity(Part(settled, "OverlayScrim")) == .72f && title.FillColor == panel.FillColor &&
                title.FillColor == "#022714" && title.TextHeightPixels == UiThemePackParser.Default.GetText(UiTextStyle.Title).ReferenceSize,
                "Overlay text uses Title on the opaque gameplay surface while the scrim stops at 72 percent.");
            Accessible(settled);

            var dismissals = 0;
            upper.Dismissed += () => dismissals++;
            using (var modal = Make(ui.ShowModal(new UiModalRequest("Higher prompt", "Confirm"), _ => { })))
            {
                upper.DismissFromPlayer();
                Check(upper.IsShown && dismissals == 0, "A higher modal keeps Back and leaves the overlay alone.");
            }
            upper.DismissFromPlayer();
            upper.DismissFromPlayer();
            Check(!upper.IsShown && dismissals == 1 && lower.ActivateButton("confirm").Succeeded,
                "One Back releases focus to the previous overlay and raises one dismissal.");
            Check(Part(upper.Capture(Standard), "Overlay") is { State: "exiting", IsRendered: true },
                "Hide keeps the overlay capture visible while the exit runs.");
            var subscriptionsBeforeExit = context.Events.ActiveSubscriptionCount;
            upper.Dispose();
            Check(Part(ui.Capture("upper", Standard), "Overlay") is { State: "exiting", IsRendered: true } &&
                context.Events.ActiveSubscriptionCount == subscriptionsBeforeExit,
                "Disposal preserves an in-flight overlay exit without adding an SDK subscription.");
            context.AdvanceFrame(TimeSpan.FromSeconds(.5));
            try { _ = ui.Capture("upper", Standard); }
            catch (KeyNotFoundException) { return; }
            throw new InvalidOperationException("The disposed overlay stays findable after its exit ended.");
        }

        private static void OverlayAdmissionAndVisibilityAreTransactional()
        {
            using var context = new FakeModContext();
            var ui = context.Ui;
            Check(ui.ApplyAccessibility(new UiAccessibilityPreferences(reducedMotion: true)).Succeeded, "Set reduced motion.");
            var initial = new UiText("Summary", id: "summary");
            using var passive = Overlay(ui, "passive", initial);
            passive.DismissFromPlayer();
            Check(passive.IsShown && !context.Input.IsUiFocused, "The presentation-only overlay has no Back or focus ownership.");
            Check(passive.SetContent(Confirm()).ErrorCode == ModErrorCode.InvalidArgument && ReferenceEquals(passive.Content, initial),
                "A passive overlay cannot become interactive in a retained update.");
            Check(ui.CreateSurface(Request("blocked", Confirm(enabled: false)).WithDismissible(false)).ErrorCode == ModErrorCode.InvalidArgument,
                "Non-dismissible admission rejects a disabled button.");
            using var locked = MakeOverlay(ui.CreateSurface(Request("locked", Confirm()).WithDismissible(false)));
            var dismissed = 0;
            locked.Dismissed += () => dismissed++;
            locked.DismissFromPlayer();
            Check(locked.IsShown && dismissed == 0, "Back cannot close a non-dismissible overlay.");
            var timer = new UiCountdown(20, "New timer");
            Check(locked.SetContent(new UiColumn(timer, new UiCheckbox("check", "Check", false, _ => { }))).ErrorCode == ModErrorCode.InvalidArgument &&
                timer.ClockEpoch == null && locked.Content is UiButton, "A rejected retained overlay keeps its old controls and leaves candidate clocks unbound.");
            using var third = Overlay(ui, "third", Confirm());
            using var fourth = Overlay(ui, "fourth", Confirm());
            Check(ui.CreateSurface(Request("fifth", Confirm())).ErrorCode == ModErrorCode.RateLimited, "A scope admits four live overlays.");
            third.Hide();
            Check(ui.CreateSurface(Request("fifth", Confirm())).ErrorCode == ModErrorCode.RateLimited,
                "A hidden overlay keeps its scope lease.");
            fourth.Dispose();
            using var replacement = Overlay(ui, "replacement", new UiText("Updated"));
            Check(!context.Input.IsUiFocused && Part(replacement.Capture(Standard), "Overlay").State == "shown",
                "Reduced motion skips entry and exit, and a top passive overlay does not take focus.");
            try
            {
                replacement.SetBody(new string('Z', 3000));
                throw new InvalidOperationException("An overlay admitted body text outside its protected layout.");
            }
            catch (ArgumentException)
            {
                Check(replacement.Body == "Summary", "An unfitting overlay body leaves the previous body in place.");
            }
        }

        private static void StingersAdvanceCoalesceAndDrain()
        {
            using var context = new FakeModContext();
            var ui = context.Ui;
            SelectFieldTheme(ui);
            Check(ui.ShowStinger(new UiStingerRequest("A\nB\nC")).ErrorCode == ModErrorCode.InvalidArgument,
                "Three title lines cannot enter the stinger queue.");
            Check(ui.ShowStinger(new UiStingerRequest("Ready", role: UiPaintRole.Danger, style: new UiStyleRef("notice"))).ErrorCode ==
                ModErrorCode.InvalidArgument, "A named stinger style cannot change the requested role.");
            var baseline = context.Lifetime.TrackedResourceCount;
            var first = new UiStingerRequest("<b>Ready</b>", "Round starts", UiGlyph.Check, style: new UiStyleRef("notice"), durationSeconds: 1d);
            Check(ui.ShowStinger(first).Succeeded && ui.ActiveStinger is { ShownAt: 0d, Broadcast: false } &&
                context.Events.ActiveSubscriptionCount == 0 && context.Lifetime.TrackedResourceCount == baseline,
                "A local stinger starts immediately and keeps the SDK subscription baseline.");
            var tree = StingerCapture(ui, Standard);
            Check(Part(tree, "Stinger") is { Announcement: "<b>Ready</b>. Round starts", ThemeId: "field", StyleId: "notice", State: "entering" } &&
                Part(tree, "StingerTitle").SemanticLabel == first.Title && Part(tree, "StingerGlyph").SemanticLabel == "Check",
                "The capture preserves literal announcement text, the named style and the visible cue.");
            Accessible(tree);
            foreach (var title in new[] { "Second", "Third", "Fourth" })
                Check(ui.ShowStinger(new UiStingerRequest(title, durationSeconds: 1d)).Succeeded, "Queue a unique announcement.");
            Check(ui.Stingers.Count == 4 && double.IsNaN(ui.Stingers[1].ShownAt) &&
                ui.ShowStinger(new UiStingerRequest("Third", durationSeconds: 3d)).Succeeded && ui.Stingers.Count == 4,
                "Coalescing includes waiting announcements and preserves the original duration.");
            Check(ui.ShowStinger(new UiStingerRequest("Fifth")).ErrorCode == ModErrorCode.RateLimited, "A fourth waiting announcement is rate limited.");
            context.AdvanceFrame(TimeSpan.FromSeconds(1));
            Check(ReferenceEquals(ui.ActiveStinger?.Request, first) && Part(StingerCapture(ui, Standard), "Stinger").State == "exiting",
                "The exit holds the active stinger until the slow preset completes.");
            var history = ui.Stingers;
            context.AdvanceFrame(TimeSpan.FromSeconds(.5));
            Check(Math.Abs(history[1].ShownAt - 1.24d) < 1e-9 && ui.ActiveStinger is { Request.Title: "Second" },
                "The host frame promotes the next stinger before another UI read.");
            ui.ApplyAccessibility(new UiAccessibilityPreferences(reducedMotion: true));
            context.AdvanceFrame(TimeSpan.FromSeconds(1));
            Check(ui.ActiveStinger is { Request.Title: "Third" } third && Math.Abs(third.ShownAt - 2.24d) < 1e-9 &&
                Part(StingerCapture(ui, Standard), "Stinger").State == "shown", "Reduced motion promotes at expiry without a fade.");
            context.Dispose();
            Check(ui.ActiveStinger == null && ui.Stingers.Count == 4 &&
                ui.ShowStinger(first).ErrorCode == ModErrorCode.Cancelled, "Stopping clears displayed and waiting announcements and retains the test history.");
        }

        private static void ALongFrameDrainsCompletedStingers()
        {
            using var context = new FakeModContext();
            var ui = context.Ui;
            SelectFieldTheme(ui);
            foreach (var title in new[] { "One", "Two", "Three" })
                Check(ui.ShowStinger(new UiStingerRequest(title, durationSeconds: 1d)).Succeeded, "Admit a distinct timed announcement.");

            context.AdvanceFrame(TimeSpan.FromSeconds(10));
            Check(ui.ActiveStinger == null && ui.Stingers[0].ShownAt == 0d && Math.Abs(ui.Stingers[1].ShownAt - 1.24d) < 1e-9 &&
                Math.Abs(ui.Stingers[2].ShownAt - 2.48d) < 1e-9, "One frame processes every completed presentation at its actual boundary.");
            foreach (var title in new[] { "Four", "Five", "Six", "Seven" })
                Check(ui.ShowStinger(new UiStingerRequest(title, durationSeconds: 1d)).Succeeded, "A drained queue has all four slots available.");
        }

        private static void TargetedOverlayAdmitsWithTheWireEnvelope()
        {
            using var players = new FakePlayerDirectory();
            var guest = players.Add(new ParticipantId("guest"), "Sam");
            var calls = 0;
            var retained = new UiButton("close", "Continue", () => calls++);
            using var surface = MakeOverlay(guest.Ui.CreateSurface(Request("targeted", retained).WithDismissible(false)));
            Check(surface.ActivateButton("close").Succeeded && calls == 1, "A targeted overlay with only legacy node tags still admits its full envelope.");
            Check(surface.SetContent(new UiText("Updates")).ErrorCode == ModErrorCode.InvalidArgument &&
                ReferenceEquals(surface.Content, retained) && surface.ActivateButton("close").Succeeded && calls == 2,
                "A failed targeted overlay update retains its original callback and tree.");
        }

        private static void ReceivedOverlaysKeepSeparateAuthorQuota()
        {
            using var context = new FakeModContext();
            var ui = context.Ui;
            var shared = new UiWireEnvelope("io.example.shared", new byte[32], 1UL << 63, null, new byte[32]);
            var targeted = new UiWireEnvelope("io.example.targeted", new byte[32], 1, null, new byte[32]);
            var content = new UiText("Outcome");
            using var received = MakeOverlay(ReceiveSurface(ui, Request("shared", content), shared));
            var exiting = MakeOverlay(ReceiveSurface(ui, Request("received-exit", content), shared));
            exiting.Dispose();
            Check(Part(ui.Capture("received-exit", Standard), "Overlay").State == "exiting",
                "An admitted receiver overlay retains its exit.");
            using var first = Overlay(ui, "local-one", content);
            using var second = Overlay(ui, "local-two", content);
            using var third = Overlay(ui, "local-three", content);
            var fourth = ui.CreateSurface(Request("local-four", content));
            Check(fourth.Succeeded, "Incoming active and exiting overlays leave all four author slots available.");
            using var last = MakeOverlay(fourth);
            Check(ui.CreateSurface(Request("local-five", content)).ErrorCode == ModErrorCode.RateLimited,
                "The local author still has a four-overlay quota.");

            var delivered = ReceiveSurface(ui, Request("targeted", Confirm()).WithDismissible(false), targeted);
            Check(delivered.Succeeded, "A full local quota still accepts a validated incoming overlay.");
            using var interactive = MakeOverlay(delivered);
            interactive.DismissFromPlayer();
            Check(interactive.IsShown && ReceiveSurface(ui, Request("invalid-received", content).WithDismissible(false), targeted).ErrorCode
                == ModErrorCode.InvalidArgument, "Incoming overlays still enforce interactive class and non-dismissible admission.");

            last.Dispose();
            using var replacement = Overlay(ui, "local-five", content);
            Check(received.IsShown && replacement.IsShown && Part(last.Capture(Standard), "Overlay").State == "exiting",
                "Disposal frees the author slot immediately while the local exit and incoming overlay stay visible.");
        }

        private static void StingerDeliveryIsPerViewerAndNeverReplayed()
        {
            using var context = new FakeModContext();
            using var players = new FakePlayerDirectory();
            using var extension = Make(context.Extensions.Register<IPlayerDirectory>(players));
            var world = new WorldReadiness(new WorldSceneIdentity(1, "Arena"), TransformState.Identity);
            _ = new FakeGamemodeSession(context, world);
            var id = new ParticipantId("guest");
            var guest = players.Add(id, "Sam");
            var broadcast = new UiStingerRequest("Broadcast");
            Check(context.Ui.ShowStinger(broadcast).Succeeded && context.Ui.ActiveStinger?.Broadcast == true &&
                players.State(id).Ui.ActiveStinger is { Broadcast: true, Request.Title: "Broadcast" },
                "The gamemode server records one broadcast locally and delivers it to the current guest.");
            var late = new ParticipantId("late");
            players.Add(late, "Sam");
            Check(players.State(late).Ui.Stingers.Count == 0, "Late joiners see no previous announcements.");
            var targeted = guest.Ui.ShowStinger(new UiStingerRequest("Private"));
            Check(targeted.Succeeded, targeted.ErrorMessage);
            Check(players.State(id).Ui.Stingers.Count == 2 &&
                !players.State(id).Ui.Stingers[1].Broadcast && players.State(late).Ui.Stingers.Count == 0 && context.Ui.Stingers.Count == 1,
                "A targeted announcement reaches only that player with targeted provenance.");
            players.Remove(id);
            players.Add(id, "Sam");
            Check(!guest.Ui.ShowStinger(new UiStingerRequest("Stale")).Succeeded && players.State(id).Ui.Stingers.Count == 0,
                "A rejoined participant gets a new queue and the retired handle cannot target it.");
            Check(context.Ui.ShowStinger(new UiStingerRequest("Next")).Succeeded && players.State(id).Ui.ActiveStinger != null,
                "Present new announcements before a generation change.");
            players.ReplaceGeneration();
            Check(players.State(id).Ui.ActiveStinger == null && context.Ui.ShowStinger(new UiStingerRequest("New generation")).Succeeded &&
                players.State(id).Ui.ActiveStinger?.Request.Title == "New generation",
                "Changing generation clears old guest presentations and accepts future announcements.");
        }

        private static void StingerCapturesKeepTheAdmittedFontChain()
        {
            using var context = new FakeModContext();
            using var players = new FakePlayerDirectory();
            using var extension = Make(context.Extensions.Register<IPlayerDirectory>(players));
            _ = new FakeGamemodeSession(context, new WorldReadiness(new WorldSceneIdentity(1, "Arena"), TransformState.Identity));
            var id = new ParticipantId("font-viewer");
            players.Add(id, "Sam");
            var source = context.Ui;
            var viewer = players.State(id).Ui;
            Select(source);
            Select(viewer);
            viewer.CompletePendingThemeRegistrations();
            var opening = new UiStingerRequest("Pending face", durationSeconds: 1d);
            Check(source.ShowStinger(opening).Succeeded && source.ShowStinger(new UiStingerRequest("Waiting face")).Succeeded,
                "A pending Fake author can send a visible and waiting event.");
            var sending = StingerCapture(source, Standard);
            var receiving = StingerCapture(viewer, Standard);
            Check(StingerChain(sending) == false && StingerChain(receiving) == false &&
                Part(sending, "StingerTitle").Text == "PENDING FACE" && Part(receiving, "StingerTitle").Text == "PENDING FACE",
                "Even a ready Fake guest captures kit faces with the authenticated casing of a pending sender.");
            source.CompletePendingThemeRegistrations();
            var held = StingerCapture(source, Standard);
            Check(StingerChain(held) == false && Part(held, "StingerTitle").Width == Part(sending, "StingerTitle").Width &&
                !AcceptedStingerChain(source.Stingers[1]) && !AcceptedStingerChain(viewer.Stingers[1]),
                "Fake acquisition preserves active captured layout and the waiting entries' selected chain.");
            Check(source.ShowStinger(new UiStingerRequest("Future face")).Succeeded &&
                AcceptedStingerChain(source.Stingers[2]) && AcceptedStingerChain(viewer.Stingers[2]),
                "Only a future event uses the newly registered Fake declared chain.");

            static void Select(FakeUiService ui)
            {
                const string definition = """
                    {"schema":1,"id":"stinger-fonts","fonts":[{"id":"brand","bundlePath":"ui/fonts.bundle","assetName":"Brand",
                     "licensePath":"LICENSE"}],"typography":{"banner":{"font":"brand","case":"upper","trackingEm":0.04}}}
                    """;
                Check(ui.AddThemeFixture(definition).Succeeded, "Declare the identical simulated font catalogue.");
                ui.AddAssetFixture(new FakeUiAssetMetadata("ui/fonts.bundle", "Brand", new string('c', 64), 32, 32, 4096,
                    ascender: 8f, descender: -2f));
                Check(ui.Themes.UseTheme(new UiThemeRef("stinger-fonts")).Succeeded, "Select the Fake font catalogue.");
            }
        }

        private static void ToastsKeepTheKitThemeAndBoundedStack()
        {
            using var context = new FakeModContext();
            var ui = context.Ui;
            SelectFieldTheme(ui);
            ui.ApplyAccessibility(new UiAccessibilityPreferences(highContrast: true, reducedMotion: true));
            var request = new UiSurfaceRequest("hud", "Status", "", UiSurfaceKind.Hud, width: 360f, height: 130f)
                .WithHudAnchor(UiHudAnchor.BottomRight).WithHudVisibility(UiHudVisibility.Always);
            using var hud = MakeOverlay(ui.CreateSurface(request));
            foreach (var tone in new[] { UiTone.Neutral, UiTone.Success, UiTone.Warning, UiTone.Danger })
                Check(ui.ShowToast(tone.ToString(), tone).Succeeded, "Admit one toast for each semantic tone.");
            Check(ui.ShowToast("Invalid", (UiTone)7).ErrorCode == ModErrorCode.InvalidArgument, "Toast rejects tones above Danger.");
            var tree = ToastCapture(ui, Standard);
            var rootList = tree.Nodes.Where(node => node.Kind == "Toast").ToArray();
            Check(rootList.Length == 4 && rootList.All(node => node.ThemeId == UiThemePackParser.Default.Id && node.BorderWidthPixels == 3f) &&
                rootList[0].Y + rootList[0].Height < hud.Capture(Standard).Nodes[0].Y,
                "Only four kit-themed toasts show and the stack starts above the lower-right HUD.");
            Check(tree.Nodes.Where(node => node.Kind == "ToastGlyph").Select(node => node.SemanticLabel)
                .SequenceEqual(new[] { "Check", "Warning", "Cross" }), "Success, Warning and Danger use distinct glyphs.");
            Accessible(tree);
            for (var index = 4; index <= 68; index++) ui.ShowToast("Queued " + index);
            context.AdvanceFrame(TimeSpan.FromSeconds(3.5));
            Check(ToastCapture(ui, Standard).Nodes.Where(node => node.Kind == "ToastLabel").Select(node => node.Text)
                .SequenceEqual(new[] { "Queued 5", "Queued 6", "Queued 7", "Queued 8" }),
                "The 64-entry queue drops only the oldest waiting toast and promotes the next four.");

            using var separate = new FakeModLifetime();
            var toastUi = new FakeUiService(separate);
            toastUi.ShowToast("<b>Literal</b>\nTwo\nThree\nFour");
            toastUi.ShowToast(new string('X', 511) + "😀extra");
            var bounded = Part(ToastCapture(toastUi, Standard), "ToastLabel", first: true);
            Check(bounded.Text.StartsWith("<b>Literal</b>", StringComparison.Ordinal) && bounded.Text.EndsWith("…", StringComparison.Ordinal) &&
                !bounded.Text.Contains("Four", StringComparison.Ordinal) && toastUi.Toasts[1].Message.Length == 511,
                "Toast draws literal text, bounds it to three lines and truncates at a complete 512-unit boundary.");
        }

        private static void TallHudQueuesAndPausesToasts()
        {
            using var context = new FakeModContext();
            var ui = context.Ui;
            ui.ApplyAccessibility(new UiAccessibilityPreferences(uiScale: 2f, reducedMotion: true));
            var request = new UiSurfaceRequest("tall-toast-dock", "Status", "", UiSurfaceKind.Hud, width: 320f, height: 340f)
                .WithHudAnchor(UiHudAnchor.BottomRight).WithHudVisibility(UiHudVisibility.Always);
            using var hud = MakeOverlay(ui.CreateSurface(request));
            foreach (var title in new[] { "First", "Second", "Third", "Fourth" }) ui.ShowToast(title, UiTone.Success);

            var visible = ToastCapture(ui, Standard);
            Check(ToastLabels(visible).SequenceEqual(new[] { "First", "Second" }) &&
                visible.Nodes.Where(node => node.Kind == "Toast").All(node => node.IsRendered && !node.Clipped && node.Y >= 0f),
                "Fake toast admission keeps the non-fitting pair out of the captured stack.");
            context.AdvanceFrame(TimeSpan.FromSeconds(3.5));
            Check(ToastLabels(ToastCapture(ui, Standard)).SequenceEqual(new[] { "Third", "Fourth" }),
                "The waiting pair still has a full lifetime when a visible slot becomes available.");
            context.AdvanceFrame(TimeSpan.FromSeconds(3.5));

            hud.Hide();
            foreach (var title in new[] { "One", "Two", "Three", "Four" }) ui.ShowToast(title);
            context.AdvanceFrame(TimeSpan.FromSeconds(1));
            hud.Show();
            Check(ToastLabels(ToastCapture(ui, Standard)).SequenceEqual(new[] { "One", "Two" }),
                "The fake hides a previously shown toast when the dock takes its space.");
            context.AdvanceFrame(TimeSpan.FromSeconds(1));
            hud.Hide();
            Check(ToastLabels(ToastCapture(ui, Standard)).SequenceEqual(new[] { "One", "Two", "Three", "Four" }),
                "The modeled HUD closing restores the original hidden suffix.");
            context.AdvanceFrame(TimeSpan.FromSeconds(1.51));
            Check(ToastLabels(ToastCapture(ui, Standard)).SequenceEqual(new[] { "Three", "Four" }),
                "Only the retained visible pair counted down while the dock was shown.");
            context.AdvanceFrame(TimeSpan.FromSeconds(1));
            Check(!ToastLabels(ToastCapture(ui, Standard)).Any(), "The recovered pair expires after its remaining visible time.");

            static IEnumerable<string> ToastLabels(UiTreeCapture capture) =>
                capture.Nodes.Where(node => node.Kind == "ToastLabel").Select(node => node.Text);
        }

        private static void CapturesStayReadableAcrossViewports()
        {
            foreach (var viewport in new[] { Standard, Wide })
                foreach (var scale in new[] { 1f, 2f })
                    foreach (var high in new[] { false, true })
                        foreach (var reduced in new[] { false, true })
                            foreach (var field in new[] { false, true })
                            {
                                using var lifetime = new FakeModLifetime();
                                var ui = new FakeUiService(lifetime);
                                if (field) SelectFieldTheme(ui);
                                ui.ApplyAccessibility(new UiAccessibilityPreferences(uiScale: scale, highContrast: high, reducedMotion: reduced));
                                using var overlay = Overlay(ui, "matrix", new UiColumn(new UiText("Ready"), Confirm()));
                                Accessible(overlay.Capture(viewport));
                                foreach (var paint in Enum.GetValues<UiPaintRole>())
                                {
                                    using var child = new FakeModLifetime();
                                    var m = new FakeUiService(child);
                                    if (field) SelectFieldTheme(m);
                                    m.ApplyAccessibility(ui.Accessibility);
                                    Check(m.ShowStinger(new UiStingerRequest("Round clear", "Ready for the next round", UiGlyph.Check, paint)).Succeeded,
                                        "Admit each stinger paint role.");
                                    Accessible(StingerCapture(m, viewport));
                                }
                                ui.ShowToast("Ready", UiTone.Warning);
                                Accessible(ToastCapture(ui, viewport));
                            }
        }

        private static void SettledPresentationAllocatesNothing()
        {
            using var context = new FakeModContext();
            var ui = context.Ui;
            using var overlay = Overlay(ui, "idle", Confirm());
            ui.ShowStinger(new UiStingerRequest("Ready", durationSeconds: 6d));
            ui.ShowToast("Ready");
            context.AdvanceFrame(TimeSpan.FromSeconds(1));
            for (var index = 0; index < 4096; index++) Read();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < 4096; index++) Read();
            Check(GC.GetAllocatedBytesForCurrentThread() == before, "Settled overlay focus, stinger access and toast advancement allocate zero bytes.");

            void Read()
            {
                if (!context.Input.IsUiFocused || ui.ActiveStinger == null || !overlay.SetContent(overlay.Content!).Succeeded)
                    throw new InvalidOperationException("Settled presentation changed state.");
                AdvanceToasts(ui);
            }
        }

        private static void SelectFieldTheme(FakeUiService ui)
        {
            var declared = ui.AddThemeFixture(FieldTheme);
            Check(declared.Succeeded, declared.ErrorMessage);
            var selected = ui.Themes.UseTheme(new UiThemeRef("field"));
            Check(selected.Succeeded, selected.ErrorMessage);
            ui.CompletePendingThemeRegistrations();
        }

        private static UiSurfaceRequest Request(string id, UiNode content) =>
            new UiSurfaceRequest(id, "Round results", "Summary", UiSurfaceKind.Overlay, 540f, 300f, content);
        private static FakeUiSurface Overlay(FakeUiService ui, string id, UiNode content) => MakeOverlay(ui.CreateSurface(Request(id, content)));
        private static FakeUiSurface MakeOverlay(OperationResult<IUiSurface> value) => (FakeUiSurface)Make(value);
        private static UiButton Confirm(bool enabled = true) => new UiButton("confirm", "Continue", () => { }, enabled: enabled);
        private static UiCapturedNode Part(UiTreeCapture tree, string kind, bool first = false) =>
            first ? tree.Nodes.First(node => node.Kind == kind) : tree.Nodes.Single(node => node.Kind == kind);
        private static Func<FakeUiService, UiCaptureViewport, UiTreeCapture> CaptureMethod(string name) =>
            typeof(FakeUiService).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Func<FakeUiService, UiCaptureViewport, UiTreeCapture>>();
        private static void Accessible(UiTreeCapture tree) =>
            Check(tree.CheckAccessibility().Count == 0,
                string.Join("; ", tree.CheckAccessibility().Select(issue => issue.Path + " " + issue.Message)));
        private static T Make<T>(OperationResult<T> result) where T : notnull =>
            result.TryGetValue(out var value) ? value : throw new InvalidOperationException(result.ErrorMessage);
        private static void Check(bool value, string failure)
        {
            if (!value) throw new InvalidOperationException(failure);
        }
    }
}
