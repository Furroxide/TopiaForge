using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class TestingKitTests
    {
        internal static void RunWorldWidgets(bool report = true)
        {
            WorldWidgetValues();
            WorldWidgetCountdown();
            WorldWidgetLabels();
            WorldWidgetOwnership();
            WorldWidgetAllocation();
            WorldWidgetCompatibility();
            if (report) Console.WriteLine("World widgets passed: values, automatic countdown, local label lifecycle, visibility, pool bounds, 10 reloads and 10,000 settled reads.");
        }
        private static void WorldWidgetValues()
        {
            foreach (var fraction in new[] { float.NaN, float.PositiveInfinity, -0.1f, 1.1f })
                AssertThrows<ArgumentOutOfRangeException>(() => new UiProgress(fraction), "progress rejects a non-finite or unbounded value");
            foreach (var end in new[] { double.NaN, double.PositiveInfinity, -1.0 })
                AssertThrows<ArgumentOutOfRangeException>(() => new UiCountdown(end), "countdown rejects a non-finite or negative deadline");
            foreach (var distance in new[] { float.NaN, float.PositiveInfinity, 0.1f, 1001f })
                AssertThrows<ArgumentOutOfRangeException>(() => UiWorldLabelRequest.At(Vec3.Zero, "Marker", distance), "label range is bounded");
            AssertThrows<ArgumentException>(() => UiWorldLabelRequest.At(Vec3.Zero, new string('x', 33)), "world label text fits its native view");
            AssertThrows<ArgumentOutOfRangeException>(() => UiWorldLabelRequest.At(new Vec3(float.NaN, 0, 0), "Marker"), "position must be finite");
            AssertThrows<ArgumentOutOfRangeException>(() => new UiProgress(0.5f, tone: (UiTone)99), "semantic tone is validated");
            var timer = new UiCountdown(10.1);
            Assert(timer.RemainingSeconds(9.2) == 1 && timer.RemainingSeconds(10.1) == 0 && timer.RemainingSeconds(12) == 0, "countdown rounds up and never goes negative");
            Assert(new UiCountdown(double.MaxValue).RemainingSeconds(0) == int.MaxValue, "distant deadline cannot overflow");
            _ = new UiSurfaceRequest("widgets", "Widgets", "", UiSurfaceKind.Hud,
                content: new UiColumn(new UiProgress(1), new UiCountdown(5)));
            AssertThrows<ArgumentException>(() => new UiSurfaceRequest("duplicate", "Widgets", "",
                content: new UiColumn(new UiProgress(0, id: "same"), new UiCountdown(5, id: "same"))), "new nodes preserve composition id validation");
        }
        private static void WorldWidgetCountdown()
        {
            using var context = new FakeModContext();
            context.Scenes.Load("Courtyard");
            context.AdvanceFrame(TimeSpan.FromSeconds(2));
            var countdown = new UiCountdown(context.Ui.ClockTime + 3, "Remaining", id: "round-time");
            var surface = Created(context, new UiSurfaceRequest("timer", "Round", "", UiSurfaceKind.Hud,
                content: new UiColumn(new UiProgress(0.25f, "Tasks"), countdown)));
            Assert(surface.TryGetCountdownSeconds("round-time", out var seconds) && seconds == 3, "new countdown reads its owning clock");
            var composition = surface.Content;
            context.AdvanceFrame(TimeSpan.FromSeconds(1.2));
            Assert(surface.TryGetCountdownSeconds("round-time", out seconds) && seconds == 2 && ReferenceEquals(composition, surface.Content),
                "clock advances without author frame code or replacing the tree");
            context.Time.AdvanceFrame(0, 2); // The world is frozen; its unscaled UI clock continues.
            Assert(surface.TryGetCountdownSeconds("round-time", out seconds) && seconds == 0, "a frozen world does not freeze the UI clock");
            surface.SetContent(new UiText("Finished"));
            Assert(!surface.TryGetCountdownSeconds("round-time", out _), "replacing the tree ends the old timer");
            surface.Dispose();
            Assert(!surface.TryGetCountdownSeconds("round-time", out _), "disposed surfaces expose no timer");
            using var other = new FakeModContext();
            Assert(other.Ui.ClockTime == 0, "a new session scope starts its own local clock");
        }
        private static void WorldWidgetLabels()
        {
            using var context = new FakeModContext();
            Assert(context.Ui.Labels.Add(UiWorldLabelRequest.At(Vec3.Zero, "Marker")).ErrorCode == ModErrorCode.InvalidState, "menu labels are refused");
            context.Scenes.Load("Courtyard");
            var entity = new FakeEntity("marker", "Marker", new Vec3(0, 0, 10));
            var result = context.Ui.Labels.Add(UiWorldLabelRequest.For(entity, "Panel", maximumDistance: 30));
            Assert(result.Succeeded, "entity label created");
            var label = (FakeUiWorldLabel)result.Value!;
            Assert(label.TryGetPosition(out var position) && position == entity.Position, "label follows its entity");
            entity.Position = new Vec3(0, 0, 28);
            Assert(label.TryGetPosition(out position) && position == entity.Position, "movement requires no author label update");
            Assert(Math.Abs(label.OpacityFrom(Vec3.Zero) - 0.4f) < 0.001, "label fades at the far boundary");
            label.Occluded = true;
            Assert(label.OpacityFrom(Vec3.Zero) == 0, "solid occlusion hides by default");
            var visible = (FakeUiWorldLabel)context.Ui.Labels.Add(UiWorldLabelRequest.At(new Vec3(0, 0, 10), "Always", occlusion: false)).Value!;
            visible.Occluded = true;
            Assert(visible.OpacityFrom(Vec3.Zero) == 1, "occlusion has an explicit local opt-out");
            label.SetPosition(new Vec3(1, 2, 3)); entity.Destroy();
            Assert(label.TryGetPosition(out position) && position == new Vec3(1, 2, 3), "moving detaches an entity anchor");
            label.SetText("Updated"); Assert(label.Text == "Updated", "text updates are captured");
            var dead = new FakeEntity("gone", "Gone", Vec3.Zero); dead.Destroy();
            Assert(context.Ui.Labels.Add(UiWorldLabelRequest.For(dead, "Gone")).ErrorCode == ModErrorCode.NotFound, "dead entity refuses");
            context.Scenes.Load("NextWorld");
            Assert(!label.IsAlive && !visible.IsAlive && context.Ui.Labels.Count == 0, "scene replacement releases labels");
        }
        private static void WorldWidgetOwnership()
        {
            for (var reload = 0; reload < 10; reload++)
            {
                var context = new FakeModContext(); context.Scenes.Load("Courtyard");
                using var other = new FakeModContext(); other.Scenes.Load("Courtyard");
                var handles = new IUiWorldLabel[64];
                for (var index = 0; index < handles.Length; index++) handles[index] = context.Ui.Labels.Add(UiWorldLabelRequest.At(new Vec3(0, 0, 10), "Marker")).Value!;
                Assert(context.Ui.Labels.Add(UiWorldLabelRequest.At(Vec3.Zero, "Overflow")).ErrorCode == ModErrorCode.Unavailable, "owner pool is bounded");
                handles[0].Dispose(); handles[0].Dispose();
                var replacement = (FakeUiWorldLabel)context.Ui.Labels.Add(UiWorldLabelRequest.At(new Vec3(0, 0, 10), "Replacement")).Value!;
                handles[0].SetText("Stale");
                Assert(replacement.Text == "Replacement" && other.Ui.Labels.Count == 0, "released handles and other scopes cannot alter reused views");
                var entity = new FakeEntity("temporary", "Temporary", Vec3.Zero);
                handles[1].Dispose();
                var follows = context.Ui.Labels.Add(UiWorldLabelRequest.For(entity, "Temporary")).Value!;
                entity.Destroy(); context.AdvanceFrame(TimeSpan.Zero);
                Assert(!follows.IsAlive && context.Ui.Labels.Count == 63, "despawn returns the view to the pool automatically");
                context.Dispose(); context.AssertNoLeaks();
                Assert(!replacement.IsAlive && context.Ui.Labels.Count == 0, "scope teardown releases all labels");
                Assert(context.Ui.Labels.Add(UiWorldLabelRequest.At(Vec3.Zero, "Late")).ErrorCode == ModErrorCode.Cancelled, "stopped scope cannot allocate");
            }
        }
        private static void WorldWidgetAllocation()
        {
            using var context = new FakeModContext(); context.Scenes.Load("Courtyard");
            var label = (FakeUiWorldLabel)context.Ui.Labels.Add(UiWorldLabelRequest.At(new Vec3(0, 0, 10), "Marker")).Value!;
            var timer = new UiCountdown(30);
            for (var index = 0; index < 512; index++) { _ = label.OpacityFrom(Vec3.Zero); _ = timer.RemainingSeconds(2); label.SetText("Marker"); }
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < 10000; index++) { _ = label.OpacityFrom(Vec3.Zero); _ = timer.RemainingSeconds(2); label.SetText("Marker"); }
            Assert(GC.GetAllocatedBytesForCurrentThread() == before, "settled label and countdown model reads allocate zero bytes");
        }
        private static void WorldWidgetCompatibility()
        {
            IUiService legacy = new LegacyWidgetHost();
            Assert(legacy.Labels.Add(UiWorldLabelRequest.At(Vec3.Zero, "Old host")).ErrorCode == ModErrorCode.Unavailable,
                "an existing UI implementation can still load and refuses unsupported labels");
            AssertThrows<NotSupportedException>(() => _ = legacy.ClockTime, "an old host cannot silently freeze countdowns");
            using var context = new FakeModContext(); context.Scenes.Load("Courtyard");
            Assert(context.Ui.Labels.Add(UiWorldLabelRequest.For(new BrokenLabelEntity(true), "Broken")).ErrorCode == ModErrorCode.NotFound,
                "a throwing entity lifetime query refuses without leaking the exception");
            var label = (FakeUiWorldLabel)context.Ui.Labels.Add(UiWorldLabelRequest.For(new BrokenLabelEntity(false), "Broken")).Value!;
            Assert(!label.TryGetPosition(out _) && !label.IsAlive && context.Ui.Labels.Count == 0,
                "a throwing position query releases the pooled view");
            var canAccess = true;
            var released = 0;
            var updates = 0;
            var state = new TopiaForge.Mods.Internal.UiLabelState(UiWorldLabelRequest.At(Vec3.Zero, "Thread"),
                _ => updates++, () => released++, () => { if (!canAccess) throw new InvalidOperationException("Wrong thread."); });
            state.SetText("Thread"); Assert(updates == 0, "unchanged text does not reach the native renderer");
            state.SetText("Changed"); Assert(updates == 1, "changed text reaches the native renderer once");
            canAccess = false;
            AssertThrows<InvalidOperationException>(() => state.SetText("Wrong"), "native label text checks thread ownership");
            AssertThrows<InvalidOperationException>(() => state.SetPosition(Vec3.Zero), "native label position checks thread ownership");
            AssertThrows<InvalidOperationException>(() => state.Dispose(), "native label release checks thread ownership");
            canAccess = true; Assert(state.IsAlive && released == 0, "wrong-thread calls cannot change label lifetime");
            state.Dispose(); Assert(released == 1, "owning thread can release the label");
        }
        private sealed class BrokenLabelEntity : IEntity
        {
            private readonly bool throwAlive;
            internal BrokenLabelEntity(bool throwAlive) { this.throwAlive = throwAlive; }
            public string Id => "broken";
            public string Name => "Broken";
            public bool IsAlive => throwAlive ? throw new InvalidOperationException("private entity details") : true;
            public Vec3 Position => throw new InvalidOperationException("private position details");
        }
        // Deliberately implements only the pre-widget contract.
        private sealed class LegacyWidgetHost : IUiService
        {
            public UiAccessibilityPreferences Accessibility => UiAccessibilityPreferences.Default;
            public OperationResult<UiAccessibilityPreferences> ApplyAccessibility(UiAccessibilityPreferences preferences) =>
                OperationResult<UiAccessibilityPreferences>.Success(preferences);
            public OperationResult<IUiSurface> CreateSurface(UiSurfaceRequest request) =>
                OperationResult<IUiSurface>.Failure(ModErrorCode.Unavailable, "No renderer.");
            public OperationResult<IUiModal> ShowModal(UiModalRequest request, Action<bool> completed) =>
                OperationResult<IUiModal>.Failure(ModErrorCode.Unavailable, "No renderer.");
            public OperationResult<bool> ShowToast(string message, UiTone tone = UiTone.Neutral) => OperationResult<bool>.Success(true);
        }
    }
}
