using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods.Testing
{
    internal sealed class FakeUiThemeScope : IUiThemeService
    {
        private readonly FakeGameThread gameThread;

        internal FakeUiThemeScope(FakeModLifetime lifetime, FakeGameThread gameThread, FakeUiThemeScope? parent)
        {
            this.gameThread = gameThread;
            Parent = parent;
            Package = parent?.Package ?? new FakeUiThemePackage();
            Package.Attach(this);
            lifetime.StoppingToken.Register(() => Package.Stop(this));
        }

        internal FakeUiThemeScope? Parent { get; }
        internal FakeUiThemePackage Package { get; }
        internal UiThemeRef? Selection { get; set; }
        internal bool Stopped { get; set; }
        internal HashSet<FakeUiSurface> Surfaces { get; } = new HashSet<FakeUiSurface>();
        internal Dictionary<string, FakeUiThemePreload> Preloads { get; } =
            new Dictionary<string, FakeUiThemePreload>(StringComparer.Ordinal);

        public UiThemeRef? SelectedTheme
        {
            get
            {
                gameThread.AssertCurrent();
                return Package.Selection(this);
            }
        }

        public OperationResult<bool> UseTheme(UiThemeRef? theme)
        {
            gameThread.AssertCurrent();
            return Package.Select(this, theme);
        }

        public Task<OperationResult<bool>> PreloadAsync(UiThemeRef theme, CancellationToken cancellationToken = default)
        {
            gameThread.AssertCurrent();
            if (theme == null) throw new ArgumentNullException(nameof(theme));
            return Package.Preload(this, theme, cancellationToken);
        }
    }

    internal sealed class FakeUiThemePreload
    {
        internal bool Retained { get; set; }
        internal List<FakeUiThemeWaiter> Waiters { get; } = new List<FakeUiThemeWaiter>();
    }

    internal sealed class FakeUiThemeWaiter
    {
        internal TaskCompletionSource<OperationResult<bool>> Completion { get; } =
            new TaskCompletionSource<OperationResult<bool>>(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationTokenRegistration Cancellation { get; set; }

        internal void Complete(OperationResult<bool> result)
        {
            Completion.TrySetResult(result);
            // A cancellation callback may be waiting to enter the package lock.
            _ = Cancellation.DisposeAsync();
        }
    }
}
