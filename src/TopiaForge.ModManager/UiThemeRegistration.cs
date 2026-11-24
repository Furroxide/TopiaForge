using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal interface IUiThemeNativeLoader
    {
        Task<OperationResult<UiThemeRegistration>> LoadAsync(UiThemeDescriptor descriptor, OwnerModLifetime owner);
    }

    /// <summary>A complete, audited registration. The attempt lifetime owns its backing resources.</summary>
    internal sealed class UiThemeRegistration
    {
        private readonly IReadOnlyDictionary<string, object> images;
        private readonly IReadOnlyDictionary<UiTextStyle, object> fonts;
        private Action? accepted;

        internal UiThemeRegistration(IReadOnlyDictionary<string, object> images,
            IReadOnlyDictionary<UiTextStyle, object> fonts, IReadOnlyDictionary<int, long> textures, Action? accepted = null)
        {
            this.images = images;
            this.fonts = fonts;
            Textures = textures;
            this.accepted = accepted;
        }

        internal IReadOnlyDictionary<int, long> Textures { get; }

        internal T? Image<T>(string id) where T : class => images.TryGetValue(id, out var value) ? value as T : null;

        internal T? Font<T>(UiTextStyle role) where T : class =>
            fonts.TryGetValue(role, out var value) ? value as T : null;

        internal void Commit()
        {
            var notify = accepted;
            accepted = null;
            notify?.Invoke();
        }
    }

    /// <summary>One caller's lease on a package-local registration.</summary>
    internal sealed class UiThemeDemand : IDisposable
    {
        private UiThemeAcquisitions? owner;
        private readonly Action<OperationResult<bool>>? settled;
        private readonly Action<UiThemeDemand>? detached;

        internal UiThemeDemand(UiThemeAcquisitions owner, UiThemeDescriptor descriptor,
            Action<OperationResult<bool>>? settled, Action<UiThemeDemand>? detached)
        {
            this.owner = owner;
            Descriptor = descriptor;
            this.settled = settled;
            this.detached = detached;
        }

        internal UiThemeDescriptor Descriptor { get; }
        internal UiThemeRegistration? Registration => owner?.Registration(Descriptor);
        internal OperationResult<bool>? Outcome => owner?.Outcome(Descriptor);

        internal void Notify(OperationResult<bool> outcome)
        {
            if (owner != null) settled?.Invoke(outcome);
        }

        public void Dispose()
        {
            var current = owner;
            if (current == null) return;
            owner = null;
            detached?.Invoke(this);
            current.Release(this);
        }
    }
}
