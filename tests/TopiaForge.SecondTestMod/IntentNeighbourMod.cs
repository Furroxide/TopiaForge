using System;
using TopiaForge.Mods;

namespace TopiaForge.SecondTestMod
{
    /// <summary>
    /// The intent mod's neighbour (WS3.4): its Update runs in the same frames, its published service and its command run
    /// from inside the intent mod's code, and it spawns from each so a test can see that none of the intent mod's intent
    /// reaches its own code, except through the command the player's intent ran.
    /// </summary>
    public sealed class IntentNeighbourMod : TopiaForgeMod
    {
        protected override void OnLoad()
        {
            Context.Events.SubscribeUpdate(_ => Spawn("neighbour-update"));
            Context.Extensions.Register<IProgress<string>>(new Poke(this));
            Context.Commands.Register(new CommandDefinition("ping", "Ping"), _ =>
            {
                Spawn("neighbour-command");
                return OperationResult<string>.Success("pong");
            });
        }

        private void Spawn(string name) => Context.Assets.Spawn(new AssetSpawnRequest(new NeighbourPrefab(name), TransformState.Identity));

        private sealed class Poke : IProgress<string>
        {
            private readonly IntentNeighbourMod owner;

            internal Poke(IntentNeighbourMod owner)
            {
                this.owner = owner;
            }

            public void Report(string value) => owner.Spawn("neighbour-callback-" + value);
        }

        private sealed class NeighbourPrefab : IPrefabAsset
        {
            internal NeighbourPrefab(string name)
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
}
