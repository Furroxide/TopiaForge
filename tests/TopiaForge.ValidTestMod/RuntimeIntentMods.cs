using System;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.ValidTestMod
{
    /// <summary>
    /// Acts on each source of player intent, so a test can see what intent the runtime attributes to what it does (WS3.4).
    /// Each spawn is named after what made it: its input, a callback its input scheduled, a modal's confirmation and that
    /// confirmation's continuation, a command, an interaction, its neighbour's code it runs, and a scripted timer.
    /// </summary>
    public sealed class RuntimeIntentMod : TopiaForgeMod
    {
        private IInputAction? fire;
        private IProgress<string>? neighbour;

        protected override void OnLoad()
        {
            fire = Context.Input.RegisterAction(new InputActionDefinition("fire", "Fire", new[] { InputBinding.Key("Space") })).Value;
            Context.Extensions.TryGet(out neighbour);
            Context.Events.SubscribeUpdate(_ => OnUpdate());
            Context.Events.SubscribeLateUpdate(_ =>
            {
                if (fire!.WasPressed) Spawn("input-late");
            });
            Context.Events.SubscribeFixedUpdate(_ =>
            {
                if (fire!.IsHeld) Spawn("input-fixed");
            });
            Context.Scheduler.Every(TimeSpan.FromSeconds(1), () => Spawn("timer"));
            Context.Commands.Register(new CommandDefinition("boost", "Boost"), _ =>
            {
                Spawn("command");
                return OperationResult<string>.Success("boosted");
            });
            Context.Ui.ShowModal(new UiModalRequest("Build here?", "Places a crate."), confirmed => _ = OnConfirmedAsync(confirmed));
            var anchor = Spawn("anchor");
            if (anchor != null) Context.Interactions.Register(anchor, new InteractableDefinition("Use"), _ => Spawn("interaction"));
        }

        private void OnUpdate()
        {
            if (!fire!.WasPressed) return;
            Spawn("input");
            neighbour?.Report("input");
            Context.Scheduler.NextFrame(() => Spawn("input-next-frame"));
        }

        private async Task OnConfirmedAsync(bool confirmed)
        {
            try
            {
                Spawn(confirmed ? "ui" : "ui-cancelled");
                neighbour?.Report("ui");
                Context.Commands.TryExecute("tests.intent-neighbour:ping", Array.Empty<string>(), out _);
                await Context.Scheduler.DelayAsync(TimeSpan.Zero);
                Spawn("ui-continuation");
            }
            catch (Exception exception)
            {
                Context.Logger.Error(exception, "The confirmation failed.");
            }
        }

        private ISpawnedEntity? Spawn(string name)
        {
            var spawned = Context.Assets.Spawn(new AssetSpawnRequest(new NamedPrefab(name), TransformState.Identity));
            return spawned.Succeeded ? spawned.Value : null;
        }
    }

    /// <summary>Reads its input in Update and LateUpdate and allocates nothing, so a test can measure what intent tracking costs a frame.</summary>
    public sealed class RuntimeIntentFrameMod : TopiaForgeMod
    {
        private IInputAction? hold;

        public int Reads { get; private set; }

        protected override void OnLoad()
        {
            hold = Context.Input.RegisterAction(new InputActionDefinition("hold", "Hold", new[] { InputBinding.Key("H") })).Value;
            Context.Events.SubscribeUpdate(_ =>
            {
                if (hold!.IsHeld) Reads++;
            });
            Context.Events.SubscribeLateUpdate(_ =>
            {
                if (hold!.IsHeld) Reads++;
            });
        }
    }

    internal sealed class NamedPrefab : IPrefabAsset
    {
        internal NamedPrefab(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public bool IsAlive => true;

        public void Dispose()
        {
        }
    }
}
