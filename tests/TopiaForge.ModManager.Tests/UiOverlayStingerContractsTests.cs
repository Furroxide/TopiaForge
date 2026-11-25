using System;
using System.Reflection;
using TopiaForge.Mods;
using TopiaForge.TestSupport;

namespace TopiaForge.ModManager.Tests
{
    internal static class UiOverlayStingerContractsTests
    {
        internal static void Run()
        {
            OverlayCopiesKeepTheirCompleteSettings();
            NonDismissibleOverlaysRequireAReachableButton();
            RetainedOverlaysKeepTheInitialDeliveryClass();
            StingerRequestsValidateTheAuthoredFields();
            OlderHostsInheritVersionedDefaultMethods();
            StingersCoalesceWithinOneBoundedScope();
            StingersRetainExactDisplayTimesAndDrain();
            StingerQueueAllocatesNothingAtRest();
            Console.WriteLine("Overlay and stinger contracts passed: request copies, bounded admission, default hosts, queue, expiry and zero settled allocation.");
        }

        private static void OverlayCopiesKeepTheirCompleteSettings()
        {
            Require((int)UiSurfaceKind.Hud == 0 && (int)UiSurfaceKind.Window == 1 &&
                (int)UiSurfaceKind.FullscreenTool == 2 && (int)UiSurfaceKind.Overlay == 3,
                "Overlay appends the exact surface-kind value.");
            var content = new UiColumn(new UiText("Ready"), Confirm());
            var request = new UiSurfaceRequest("results", "Results", "Round complete", UiSurfaceKind.Overlay, 560f, 360f, content);
            var style = new UiThemeRef("field-results");
            var pinned = request.WithTheme(style).WithDismissible(false);
            var inherited = pinned.WithTheme(null);
            var reopened = pinned.WithDismissible(true);
            Require(request.Dismissible && request.Theme == null && pinned is { Dismissible: false, Theme: not null } &&
                inherited is { Dismissible: false, Theme: null } && reopened.Dismissible && ReferenceEquals(reopened.Theme, style),
                "Theme and dismissibility copies preserve each other's values and keep the source immutable.");
            foreach (var variant in new[] { pinned, inherited, reopened })
                Require(variant.Id == request.Id && variant.Title == request.Title && variant.Body == request.Body &&
                    variant.Kind == request.Kind && variant.Width == request.Width && variant.Height == request.Height &&
                    ReferenceEquals(variant.Content, content), "Overlay copies preserve authored dimensions and content identity.");
            Reject<InvalidOperationException>(() => request.WithHudAnchor(UiHudAnchor.BottomRight));
            Reject<InvalidOperationException>(() => request.WithHudVisibility(UiHudVisibility.Always));
            foreach (var kind in new[] { UiSurfaceKind.Hud, UiSurfaceKind.Window, UiSurfaceKind.FullscreenTool })
            {
                var old = new UiSurfaceRequest("old", "Old surface", "", kind);
                Require(old.Dismissible, "Existing surface requests preserve the default option value.");
                Reject<InvalidOperationException>(() => old.WithDismissible(false));
            }
            var hud = new UiSurfaceRequest("hud", "Status", "", UiSurfaceKind.Hud).WithHudAnchor(UiHudAnchor.TopRight)
                .WithHudVisibility(UiHudVisibility.Always).WithTheme(style);
            Require(hud is { HudAnchor: UiHudAnchor.TopRight, HudVisibility: UiHudVisibility.Always, Dismissible: true } &&
                ReferenceEquals(hud.Theme, style), "Existing HUD copies retain anchor, visibility and theme.");
            Reject<ArgumentOutOfRangeException>(() => new UiSurfaceRequest("bad", "Bad", "", (UiSurfaceKind)4));
            Reject<ArgumentOutOfRangeException>(() => new UiSurfaceRequest("bad", "Bad", "", UiSurfaceKind.Overlay, float.PositiveInfinity));
            Reject<ArgumentOutOfRangeException>(() => new UiSurfaceRequest("bad", "Bad", "", UiSurfaceKind.Overlay, height: float.PositiveInfinity));
        }

        private static void NonDismissibleOverlaysRequireAReachableButton()
        {
            var blocked = new UiSurfaceRequest("overlay", "Results", "", UiSurfaceKind.Overlay, content: new UiText("Summary"))
                .WithDismissible(false);
            Require(!blocked.Dismissible && UiOverlayAdmission.Check(blocked.Content, blocked.Dismissible).ErrorCode == ModErrorCode.InvalidArgument,
                "A non-dismissible request defers its enabled-button check to admission.");
            Require(UiOverlayAdmission.Check(new UiCheckbox("check", "Choose", false, _ => { }), false).ErrorCode == ModErrorCode.InvalidArgument &&
                UiOverlayAdmission.Check(Confirm(enabled: false), false).ErrorCode == ModErrorCode.InvalidArgument &&
                UiOverlayAdmission.Check(new UiCard(new UiPadding(Confirm(), new UiInsets(UiSpace.Sm)), disabled: true), false)
                    .ErrorCode == ModErrorCode.InvalidArgument,
                "A checkbox, disabled button or button beneath a disabled Card cannot satisfy the admission rule.");
            var pages = new[] { new UiTab("a", "Summary", new UiText("Summary")), new UiTab("b", "Confirm", Confirm()) };
            Require(UiOverlayAdmission.Check(new UiTabs("tabs", pages, "a", enabled: false), false).ErrorCode == ModErrorCode.InvalidArgument &&
                UiOverlayAdmission.Check(new UiTabs("tabs", pages, "b", enabled: false), false).Succeeded &&
                UiOverlayAdmission.Check(new UiTabs("tabs", pages, "a"), false).Succeeded,
                "A disabled tab selector exposes buttons only on its selected page; an enabled selector makes either page reachable.");
            Require(UiOverlayAdmission.Check(new UiRow(Confirm(), Confirm()), true).ErrorCode == ModErrorCode.InvalidArgument,
                "Overlay admission includes the shared bounded composition validation.");
            Require(UiOverlayAdmission.Check(new UiCard(new UiColumn(new UiText("Ready"), Confirm())), false) is
            { Succeeded: true, Value: UiOverlayContentKind.Interactive }, "A nested enabled button can provide the required action.");
        }

        private static void RetainedOverlaysKeepTheInitialDeliveryClass()
        {
            var readOnly = new UiColumn(new UiText("Ready"));
            var controls = new UiColumn(new UiText("Ready"), Confirm(enabled: false));
            Require(UiOverlayAdmission.Check(readOnly, true) is { Succeeded: true, Value: UiOverlayContentKind.PresentationOnly } &&
                UiOverlayAdmission.Check(controls, true) is { Succeeded: true, Value: UiOverlayContentKind.Interactive },
                "The fixed delivery class follows the authored control shape, including a disabled control.");
            Require(UiOverlayAdmission.Check(controls, true, UiOverlayContentKind.PresentationOnly).ErrorCode == ModErrorCode.InvalidArgument &&
                UiOverlayAdmission.Check(readOnly, true, UiOverlayContentKind.Interactive).ErrorCode == ModErrorCode.InvalidArgument &&
                UiOverlayAdmission.Check(Confirm(), true, UiOverlayContentKind.Interactive).Succeeded &&
                UiOverlayAdmission.Check(null, true, UiOverlayContentKind.PresentationOnly).Succeeded,
                "Retained overlay updates can change the tree within its initial class.");
        }

        private static void StingerRequestsValidateTheAuthoredFields()
        {
            var standard = new UiStingerRequest("Ready");
            Require(standard is { Title: "Ready", Subtitle: "", Glyph: null, Role: UiPaintRole.Primary, Style: null, DurationSeconds: 2.5 },
                "A stinger keeps the published defaults.");
            var authoredStyle = new UiStyleRef("announcement");
            var literal = new UiStingerRequest("<b>Ready</b>", "Line one\nLine two", UiGlyph.Play, UiPaintRole.Danger, authoredStyle, 6d);
            Require(literal.Title == "<b>Ready</b>" && literal.Subtitle == "Line one\nLine two" && ReferenceEquals(literal.Style, authoredStyle),
                "The request keeps authored text literally for plain rendering and resolves named styles at presentation time.");
            Require(new UiStingerRequest(new string('T', 40), new string('S', 80), UiGlyph.Check, UiPaintRole.Surface,
                durationSeconds: 1d).DurationSeconds == 1d, "The bounded field endpoints are accepted.");
            Reject<ArgumentNullException>(() => new UiStingerRequest(null!));
            Reject<ArgumentNullException>(() => new UiStingerRequest("Title", null!));
            Reject<ArgumentException>(() => new UiStingerRequest(""));
            Reject<ArgumentException>(() => new UiStingerRequest("   "));
            Reject<ArgumentException>(() => new UiStingerRequest(new string('x', 41)));
            Reject<ArgumentException>(() => new UiStingerRequest("Title", new string('x', 81)));
            Reject<ArgumentOutOfRangeException>(() => new UiStingerRequest("Title", glyph: (UiGlyph)31));
            Reject<ArgumentOutOfRangeException>(() => new UiStingerRequest("Title", role: (UiPaintRole)(-1)));
            foreach (var duration in new[] { double.NaN, double.PositiveInfinity, .999d, 6.001d })
                Reject<ArgumentOutOfRangeException>(() => new UiStingerRequest("Title", durationSeconds: duration));
        }

        private static void OlderHostsInheritVersionedDefaultMethods()
        {
            var host = new LegacyUi();
            var request = new UiStingerRequest("Advertised");
            var local = ((IUiService)host).ShowStinger(request);
            var remote = ((IPlayerUi)host).ShowStinger(request);
            Require(local.ErrorCode == ModErrorCode.Unavailable && remote.ErrorCode == ModErrorCode.Unavailable &&
                local.ErrorMessage == "This UI host does not provide stingers. Update its implementation." &&
                remote.ErrorMessage == "This player UI does not provide stingers. Update its implementation." &&
                ReferenceEquals(local, ((IUiService)host).ShowStinger(request)) &&
                ReferenceEquals(remote, ((IPlayerUi)host).ShowStinger(request)),
                "Older host implementations retain compatible default methods with cached unavailable results.");
            var requestType = typeof(UiStingerRequest);
            Gated(requestType);
            foreach (var constructor in requestType.GetConstructors()) Gated(constructor);
            foreach (var property in requestType.GetProperties()) Gated(property);
            Gated(typeof(UiSurfaceKind).GetField(nameof(UiSurfaceKind.Overlay))!);
            Gated(typeof(UiSurfaceRequest).GetProperty(nameof(UiSurfaceRequest.Dismissible))!);
            Gated(typeof(UiSurfaceRequest).GetMethod(nameof(UiSurfaceRequest.WithDismissible))!);
            Gated(typeof(IUiService).GetMethod(nameof(IUiService.ShowStinger))!);
            Gated(typeof(IPlayerUi).GetMethod(nameof(IPlayerUi.ShowStinger))!);
        }

        private static void StingersCoalesceWithinOneBoundedScope()
        {
            var queue = new UiStingerQueue<int>();
            var start = new UiStingerRequest("Ready", "Check in", UiGlyph.Check);
            Require(queue.Enqueue(start, 1, 0d, out var expired) == UiStingerQueueResult.Started && !expired.HasValue &&
                queue.Active is { Presentation: 1 } && queue.ShownAt == 0d,
                "The first announcement starts at once and owns its own presentation entry.");
            Require(queue.Enqueue(new UiStingerRequest("Ready", "Check in", UiGlyph.Check,
                    style: new UiStyleRef("different"), durationSeconds: 6d), 99, 1d, out _) == UiStingerQueueResult.Coalesced &&
                queue.Active is { Presentation: 1 } && ReferenceEquals(queue.Active.Value.Request, start) && queue.ShownAt == 0d,
                "Style and duration do not change a duplicate's identity or restart its original presentation.");
            var second = new UiStingerRequest("Ready", "Hold", UiGlyph.Check);
            var third = new UiStingerRequest("Ready", "Check in", UiGlyph.Flag);
            var fourth = new UiStingerRequest("Ready", "Check in", UiGlyph.Check, UiPaintRole.Accent);
            Require(queue.Enqueue(second, 2, 1d, out _) == UiStingerQueueResult.Queued &&
                queue.Enqueue(third, 3, 1d, out _) == UiStingerQueueResult.Queued &&
                queue.Enqueue(fourth, 4, 1d, out _) == UiStingerQueueResult.Queued && queue.WaitingCount == 3,
                "Changing the subtitle, glyph or role creates a distinct waiting event.");
            Require(queue.Enqueue(new UiStingerRequest("Other", "Check in", UiGlyph.Check), 5, 1d, out _) == UiStingerQueueResult.RateLimited &&
                queue.Enqueue(new UiStingerRequest("Ready", "Hold", UiGlyph.Check), 99, 1d, out _) == UiStingerQueueResult.Coalesced &&
                queue.WaitingCount == 3, "A full queue refuses a fourth waiting event and still coalesces a duplicate.");
            var anotherScope = new UiStingerQueue<int>();
            Require(anotherScope.Enqueue(start, 8, 1d, out _) == UiStingerQueueResult.Started &&
                anotherScope.Active is { Presentation: 8 } && queue.Active is { Presentation: 1 },
                "Each scope coalesces and queues independently.");
        }

        private static void StingersRetainExactDisplayTimesAndDrain()
        {
            var queue = new UiStingerQueue<int>();
            queue.Enqueue(new UiStingerRequest("First", durationSeconds: 1d), 1, 10d, out _);
            queue.Enqueue(new UiStingerRequest("Second", durationSeconds: 2d), 2, 10d, out _);
            queue.Enqueue(new UiStingerRequest("Third", durationSeconds: 3d), 3, 10d, out _);
            Require(!queue.Advance(10.999d, out _) && queue.Advance(11d, out var first) && first is { Presentation: 1 } &&
                queue.Active is { Presentation: 2 } && queue.ShownAt == 11d, "Expiry is inclusive and promotes the first waiting entry with a fresh clock.");
            Require(queue.Enqueue(new UiStingerRequest("Fourth"), 4, 15d, out var second) == UiStingerQueueResult.Queued &&
                second is { Presentation: 2 } && queue.Active is { Presentation: 3 } && queue.ShownAt == 15d &&
                !queue.Advance(17.999d, out _), "An offer after the deadline releases its expired lease and gives the promoted item its full duration.");
            Require(queue.TryDrain(out var active) && active.Presentation == 3 &&
                queue.TryDrain(out var waiting) && waiting.Presentation == 4 && !queue.TryDrain(out _) &&
                queue.Active == null && queue.WaitingCount == 0 && !queue.Advance(30d, out _),
                "Scope stop drains the current and queued presentations in their authored order.");
            Require(queue.Enqueue(new UiStingerRequest("Closed"), 5, 31d, out _) == UiStingerQueueResult.Cancelled,
                "Stopping the scope closes admission.");
        }

        private static void StingerQueueAllocatesNothingAtRest()
        {
            var queue = new UiStingerQueue<int>();
            queue.Enqueue(new UiStingerRequest("Ready"), 1, 10d, out _);
            var bytes = AllocationProbe.Measure("settled stinger queue", () =>
            {
                if (queue.Advance(11d, out var expired) || expired.HasValue || queue.Active is not { Presentation: 1 })
                    throw new InvalidOperationException("A settled queue changes no presentation state.");
            }, frames: 10_000, warmups: 128);
            Require(bytes == 0, "A settled queue allocates zero bytes over 10000 advances.");
        }

        private static UiButton Confirm(bool enabled = true) => new UiButton("confirm", "Confirm", () => { }, enabled: enabled);

        private static void Gated(MemberInfo member) => Require(member.GetCustomAttribute<RequiresTopiaForgeAttribute>()?.Version == UiKitRelease.K2,
            "Every new public contract member carries the assigned K2 release gate.");

        private static void Reject<T>(Action construct) where T : Exception
        {
            try { construct(); }
            catch (T) { return; }
            throw new InvalidOperationException("Expected " + typeof(T).Name + " for the invalid overlay or stinger request.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class LegacyUi : IUiService, IPlayerUi
        {
            public UiAccessibilityPreferences Accessibility => UiAccessibilityPreferences.Default;
            public OperationResult<UiAccessibilityPreferences> ApplyAccessibility(UiAccessibilityPreferences preferences) => throw new NotSupportedException();
            public OperationResult<IUiSurface> CreateSurface(UiSurfaceRequest request) => throw new NotSupportedException();
            public OperationResult<IUiModal> ShowModal(UiModalRequest request, Action<bool> completed) => throw new NotSupportedException();
            public OperationResult<bool> ShowToast(string message, UiTone tone = UiTone.Neutral) => throw new NotSupportedException();
        }
    }
}
