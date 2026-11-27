using System;

namespace TopiaForge.Mods.Internal
{
    // Shared by the production adapter and testing kit; no Unity or network identity.
    internal sealed class UiLabelState : IUiWorldLabel
    {
        internal const int MaximumLabels = 64;
        private IEntity? entity;
        private Vec3 position;
        private Action<string>? update;
        private Action? release;
        private readonly Action? assertThread;
        internal UiLabelState(UiWorldLabelRequest request, Action<string> update, Action release, Action? assertThread = null)
        {
            Text = request.Text; entity = request.Entity; position = request.Position;
            this.update = update; this.release = release;
            this.assertThread = assertThread;
        }
        internal string Text { get; private set; }
        internal IEntity? Entity => entity;
        public bool IsAlive
        {
            get
            {
                assertThread?.Invoke();
                if (release == null) return false;
                return EntityAlive(entity);
            }
        }
        internal static bool EntityAlive(IEntity? value)
        { try { return value == null || value.IsAlive; } catch (Exception) { return false; } }
        internal bool TryPosition(out Vec3 value)
        {
            value = Vec3.Zero;
            if (!IsAlive) { Dispose(); return false; }
            try { value = entity == null ? position : entity.Position; }
            catch (Exception) { Dispose(); return false; }
            if (!value.IsFinite) { Dispose(); return false; }
            return true;
        }
        public void SetText(string text)
        {
            if (!IsAlive) return;
            UiWidgetValues.Text(text, nameof(text));
            if (text.Length > 32) throw new ArgumentException("A world label holds at most 32 characters.", nameof(text));
            if (Text == text) return;
            Text = text; update?.Invoke(text);
        }
        public void SetPosition(Vec3 value)
        {
            if (!IsAlive) return;
            if (!value.IsFinite) throw new ArgumentOutOfRangeException(nameof(value));
            entity = null; position = value;
        }
        public void Dispose()
        {
            assertThread?.Invoke();
            var old = release; release = null; update = null; entity = null;
            old?.Invoke();
        }
    }
}
