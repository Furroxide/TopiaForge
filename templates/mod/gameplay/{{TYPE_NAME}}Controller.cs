using TopiaForge.Mods;

namespace {{ASSEMBLY_NAME}}
{
    /// <summary>Reads a named action and reports the entity under the player's center-screen aim ray.</summary>
    internal sealed class {{TYPE_NAME}}Controller
    {
        internal const string ScanActionName = "scan-aim-target";

        private readonly IModContext context;
        private readonly {{TYPE_NAME}}Config config;
        private readonly IInputAction? scanAction;

        public bool IsActive => scanAction != null;

        public {{TYPE_NAME}}Controller(IModContext context, {{TYPE_NAME}}Config config)
        {
            this.context = context;
            this.config = config;
            var registered = context.Input.RegisterAction(new InputActionDefinition(
                ScanActionName,
                "Scan aim target",
                new[] { InputBinding.Key(config.ActionKey) }));
            if (!registered.TryGetValue(out scanAction))
            {
                context.Logger.Error(
                    "Input registration failed (" + registered.ErrorCode + "): " + registered.ErrorMessage);
                return;
            }

            context.Events.SubscribeUpdate(OnUpdate);
        }

        private void OnUpdate(float deltaTime)
        {
            if (scanAction?.WasPressed != true)
            {
                return;
            }

            if (!context.Player.TryGetSnapshot(out var player) || player == null)
            {
                context.Ui.ShowToast("The player camera is not available yet.", UiTone.Warning);
                return;
            }

            if (!context.Physics.TryRaycast(
                    player.AimRay,
                    config.MaximumRange,
                    out var hit) || hit == null)
            {
                context.Ui.ShowToast("Nothing is under the crosshair.");
                return;
            }

            var message = "Aimed at " + hit.Entity.Name + " (" + hit.Distance.ToString("0.0") + "m).";
            context.Logger.Info(message);
            context.Ui.ShowToast(message, UiTone.Success);
        }
    }
}
