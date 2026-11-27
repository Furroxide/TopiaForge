using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal sealed class GalleryPresentationUiProbe : IUiService, IPlayerUi, IUiThemeService
    {
        private readonly UiStingerQueue<UiStingerRequest> stingers = new();
        private readonly Func<UiThemeRef?> eventTheme;

        internal GalleryPresentationUiProbe(Func<UiThemeRef?>? eventTheme = null) =>
            this.eventTheme = eventTheme ?? (() => SelectedTheme);

        public double ClockTime { get; private set; } = 100d;
        public UiAccessibilityPreferences Accessibility => UiAccessibilityPreferences.Default;
        public IUiThemeService Themes => this;
        public UiThemeRef? SelectedTheme { get; private set; }
        internal int ThemeSelections { get; private set; }
        internal bool RefuseNextSurface { get; set; }
        internal Action? AfterCreate { get; set; }
        internal Func<int>? ScopeOverlayCount { get; set; }
        internal int LiveOverlayCount => Created.Count(surface => !surface.Disposed && surface.Request.Kind == UiSurfaceKind.Overlay);
        internal List<GalleryPresentationSurfaceProbe> Created { get; } = new();
        internal List<(UiStingerRequest Request, string? Theme, UiStingerQueueResult Result)> Offers { get; } = new();
        internal List<(string Message, UiTone Tone)> Toasts { get; } = new();
        internal IReadOnlyList<GalleryPresentationSurfaceProbe> Open => Created.Where(surface => !surface.Disposed).ToArray();
        internal UiStingerRequest? Active => stingers.Active?.Request;

        public OperationResult<bool> UseTheme(UiThemeRef? theme)
        {
            ThemeSelections++;
            SelectedTheme = theme;
            return OperationResult<bool>.Success(true);
        }

        public Task<OperationResult<bool>> PreloadAsync(UiThemeRef theme, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<bool>.Success(true));

        public OperationResult<IUiSurface> CreateSurface(UiSurfaceRequest request)
        {
            if (RefuseNextSurface)
            {
                RefuseNextSurface = false;
                return OperationResult<IUiSurface>.Failure(ModErrorCode.Unavailable, "The test receiver refused the surface.");
            }

            var contentKind = UiOverlayContentKind.PresentationOnly;
            if (request.Kind == UiSurfaceKind.Overlay)
            {
                var admission = UiOverlayAdmission.Check(request.Content, request.Dismissible);
                if (!admission.TryGetValue(out contentKind))
                    return OperationResult<IUiSurface>.Failure(admission.ErrorCode, admission.ErrorMessage);
                if (ScopeOverlayCount?.Invoke() >= UiOverlayLayout.MaximumLive)
                    return OperationResult<IUiSurface>.Failure(ModErrorCode.RateLimited, "The paired author scope is full.");
            }
            var surface = new GalleryPresentationSurfaceProbe(request, contentKind);
            Created.Add(surface);
            AfterCreate?.Invoke();
            return OperationResult<IUiSurface>.Success(surface);
        }

        public OperationResult<bool> ShowStinger(UiStingerRequest request)
        {
            var result = stingers.Enqueue(request, request, ClockTime, out _);
            Offers.Add((request, eventTheme()?.Id, result));
            return result == UiStingerQueueResult.RateLimited
                ? OperationResult<bool>.Failure(ModErrorCode.RateLimited, "Three test announcements are waiting.")
                : OperationResult<bool>.Success(true);
        }

        internal void Advance(double seconds)
        {
            ClockTime += seconds;
            stingers.Advance(ClockTime, out _);
        }

        public OperationResult<bool> ShowToast(string message, UiTone tone = UiTone.Neutral)
        {
            Toasts.Add((message, tone));
            return OperationResult<bool>.Success(true);
        }

        public OperationResult<UiAccessibilityPreferences> ApplyAccessibility(UiAccessibilityPreferences preferences) =>
            throw new InvalidOperationException("The paired presentation fixture leaves accessibility viewer-local.");

        public OperationResult<IUiModal> ShowModal(UiModalRequest request, Action<bool> completed) =>
            throw new InvalidOperationException("The paired presentation fixture does not open trust surfaces.");
    }

    internal sealed class GalleryPresentationSurfaceProbe : IUiSurface, IUiSurfaceDismissalSource
    {
        internal GalleryPresentationSurfaceProbe(UiSurfaceRequest request, UiOverlayContentKind contentKind)
        {
            Request = request;
            ContentKind = contentKind;
            Content = request.Content;
        }

        internal UiSurfaceRequest Request { get; }
        internal UiOverlayContentKind ContentKind { get; }
        internal UiNode? Content { get; private set; }
        internal bool Disposed { get; private set; }
        internal int Hides { get; private set; }
        internal int Updates { get; private set; }
        public string Id => Request.Id;
        public bool IsVisible { get; private set; } = true;
        public event Action? Dismissed;

        public void Show() => IsVisible = !Disposed;

        public void Hide()
        {
            Hides++;
            IsVisible = false;
            Dismissed?.Invoke();
        }

        public void Dispose()
        {
            Disposed = true;
            IsVisible = false;
        }

        public void SetBody(string body) => throw new InvalidOperationException("The paired presentation fixture has a fixed body.");

        public OperationResult<bool> SetContent(UiNode content)
        {
            if (Disposed) throw new InvalidOperationException("An expired fixture cannot update its HUD.");
            Content = content;
            Updates++;
            return OperationResult<bool>.Success(true);
        }

        internal bool Back()
        {
            if (!IsVisible || ContentKind != UiOverlayContentKind.Interactive || !Request.Dismissible) return false;
            Hide();
            return true;
        }
    }

    public class GalleryPresentationServiceProxy : DispatchProxy
    {
        private object inner = null!;
        private object replacement = null!;
        private string getter = null!;

        internal static T With<T>(T original, string property, object value) where T : class
        {
            var wrapped = Create<T, GalleryPresentationServiceProxy>();
            var proxy = (GalleryPresentationServiceProxy)(object)wrapped;
            proxy.inner = original;
            proxy.replacement = value;
            proxy.getter = "get_" + property;
            return wrapped;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == getter ? replacement : targetMethod?.Invoke(inner, args);
    }
}
