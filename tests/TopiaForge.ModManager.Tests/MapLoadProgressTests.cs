using System;
using System.Threading;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.Worlds.Maps;
using static TopiaForge.ModManager.Tests.MapDocumentTests;

namespace TopiaForge.ModManager.Tests
{
    internal static class MapLoadProgressTests
    {
        internal static void Run()
        {
            QueuedLifetime();
            CompetingTransition(dispose: true);
            CompetingTransition(dispose: false);
            UiErrorsPropagate();
            Console.WriteLine("Map loading progress passed: queued lifetime, captured context, concurrent transitions and UI errors.");
        }

        private static void QueuedLifetime()
        {
            using var pump = new MainThreadPump();
            using var context = new FakeModContext();
            using var progress = new MapLoadProgress(context.Ui);
            var surface = context.Ui.Surfaces[0];
            // Reporting from a worker must still wait for the creating context to pump.
            var reporter = new Thread(() => progress.Download.Report(1025));
            reporter.Start();
            Assert(reporter.Join(TimeSpan.FromSeconds(5)), "worker report finishes without UI dispatch");
            Assert(pump.Pending == 1 && surface.Body == "Downloading the map", "report preserves captured context");
            pump.RunPending();
            Assert(surface.Body == "Downloading the map (2 KiB)", "live byte progress remains useful");
            progress.Download.Report(4096);
            progress.Building();
            pump.RunPending();
            Assert(surface.Body == "Building the map", "queued download cannot overwrite building");
            progress.Dispose();
            progress.Dispose();
            progress.Building();
            progress.Download.Report(8192);
            pump.RunPending();
            Assert(context.Ui.Surfaces.Count == 0, "disposed progress cannot recreate a surface");

            using var cancelled = new MapLoadProgress(context.Ui);
            var cancelledSurface = context.Ui.Surfaces[0];
            cancelled.Download.Report(1024);
            cancelled.Dispose();
            Assert(pump.RunPending() == 1 && cancelledSurface.Body == "Downloading the map",
                "a queued download after early disposal never touches the disposed fake surface");
        }

        private static void CompetingTransition(bool dispose)
        {
            using var pump = new MainThreadPump();
            var ui = new RecordingUi();
            using var progress = new MapLoadProgress(ui);
            Exception? failure = null;
            var competitor = new Thread(() =>
            {
                try { if (dispose) progress.Dispose(); else progress.Building(); }
                catch (Exception error) { failure = error; }
            })
            { IsBackground = true };
            var waitedForSetter = false;
            ui.Surface.BeforeBody = () =>
            {
                ui.Surface.BeforeBody = null;
                competitor.Start();
                // Observe actual lock contention or completion, rather than assuming scheduling
                // from a sleep. The setter remains on the context that owns the callback.
                Assert(SpinWait.SpinUntil(() =>
                    (competitor.ThreadState & (ThreadState.WaitSleepJoin | ThreadState.Stopped)) != 0,
                    TimeSpan.FromSeconds(5)), "competing transition blocks or finishes");
                waitedForSetter = (competitor.ThreadState & ThreadState.WaitSleepJoin) != 0;
            };
            progress.Download.Report(2048);
            try { pump.RunPending(); }
            finally
            {
                Assert(competitor.Join(TimeSpan.FromSeconds(5)), "competing transition completes after setter returns");
            }
            Assert(failure == null && waitedForSetter, "transition must wait for the entire in-flight UI update");
            Assert(dispose ? ui.Surface.Disposals == 1 : ui.Surface.Body == "Building the map",
                "disposal is exclusive and building remains the final visible phase");
            progress.Download.Report(4096);
            pump.RunPending();
            Assert(dispose ? ui.Surface.Writes == 1 : ui.Surface.Body == "Building the map",
                "later reports cannot revive a completed phase");
        }

        private static void UiErrorsPropagate()
        {
            using var pump = new MainThreadPump();
            var ui = new RecordingUi();
            using var progress = new MapLoadProgress(ui);
            var expected = new InvalidOperationException("Synthetic UI failure.");
            ui.Surface.BeforeBody = () => throw expected;
            progress.Download.Report(1);
            AssertThrowsSame(pump.RunPending, expected);
            AssertThrowsSame(() => { progress.Building(); return 0; }, expected);
            ui.Surface.DisposeFailure = expected;
            AssertThrowsSame(() => { progress.Dispose(); return 0; }, expected);
            progress.Dispose();
            Assert(ui.Surface.Disposals == 1, "failed disposal is not retried on a detached surface");
        }

        private static void AssertThrowsSame(Func<int> action, Exception expected)
        {
            try { action(); }
            catch (Exception error) when (ReferenceEquals(error, expected)) { return; }
            throw new InvalidOperationException("UI errors must propagate unchanged.");
        }

        private sealed class RecordingUi : IUiService
        {
            internal readonly RecordingSurface Surface = new RecordingSurface();
            public UiAccessibilityPreferences Accessibility => UiAccessibilityPreferences.Default;
            public OperationResult<UiAccessibilityPreferences> ApplyAccessibility(UiAccessibilityPreferences preferences) => throw new NotSupportedException();
            public OperationResult<IUiSurface> CreateSurface(UiSurfaceRequest request) => OperationResult<IUiSurface>.Success(Surface);
            public OperationResult<IUiModal> ShowModal(UiModalRequest request, Action<bool> completed) => throw new NotSupportedException();
            public OperationResult<bool> ShowToast(string message, UiTone tone = UiTone.Neutral) => throw new NotSupportedException();
        }

        private sealed class RecordingSurface : IUiSurface
        {
            internal Action? BeforeBody;
            internal Exception? DisposeFailure;
            internal string Body = "Downloading the map";
            internal int Writes, Disposals;
            public string Id => "map-loading";
            public bool IsVisible => Disposals == 0;
            public void Show() => throw new NotSupportedException();
            public void Hide() => throw new NotSupportedException();
            public OperationResult<bool> SetContent(UiNode content) => throw new NotSupportedException();
            public void SetBody(string body)
            {
                BeforeBody?.Invoke();
                if (Disposals != 0) throw new ObjectDisposedException(nameof(RecordingSurface));
                Body = body;
                Writes++;
            }
            public void Dispose()
            {
                Disposals++;
                if (DisposeFailure != null) throw DisposeFailure;
            }
        }
    }
}
