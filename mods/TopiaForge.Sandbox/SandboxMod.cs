using System;
using TopiaForge.Mods;

namespace TopiaForge.Sandbox
{
    /// <summary>Owns persistent Sandbox configuration and command names; declared factories own gameplay.</summary>
    public sealed class SandboxMod : TopiaForgeMod
    {
        /// <summary>Gets the Sandbox package's creator gamemode declaration.</summary>
        public const string GamemodeId = "io.github.furroxide.topiaforge.sandbox.creator";

        private static readonly ConfigDefinition<SandboxConfig> ConfigContract = new ConfigDefinition<SandboxConfig>(
            2, () => new SandboxConfig(), validate: null, migrate: (storedVersion, value) =>
            {
                if (storedVersion < 2 && string.Equals(value.SpawnMenuKey, "Q", StringComparison.OrdinalIgnoreCase))
                    value.SpawnMenuKey = "F5";
                return OperationResult<SandboxConfig>.Success(value);
            });

        /// <inheritdoc />
        protected override void OnLoad()
        {
            LoadConfiguration(Context);
            Register("sandbox-spawn", "Spawn a safe RobotKit robot at the aim point.", target => target.SpawnRobot());
            Register("sandbox-undo", "Remove the most recently spawned sandbox robot.", target => target.Undo());
            Register("sandbox-clear", "Remove every robot spawned by this Sandbox session.", target => target.CleanUpEverything());
            Register("sandbox-status", "Describe the active Sandbox session.", target => target.Status());
            Register("sandbox-end", "Run End Session & Restore for the Sandbox creator session.", target => target.EndWorkbench());
            Context.Logger.Info("Sandbox configuration and commands loaded; gameplay starts through its declared launch target.");
        }

        internal static SandboxConfig LoadConfiguration(IModContext context)
        {
            var loaded = context.Config.Load(ConfigContract);
            if (!loaded.TryGetValue(out var value))
            {
                context.Logger.Warn("Sandbox config could not be loaded: " + loaded.ErrorMessage);
                value = new SandboxConfig();
            }
            value.Normalize();
            var saved = context.Config.Save(ConfigContract, value);
            if (!saved.Succeeded) context.Logger.Warn("Sandbox config normalization could not be saved: " + saved.ErrorMessage);
            return value;
        }

        private void Register(string name, string description, Func<SandboxSessionCommands, OperationResult<string>> handler)
        {
            var result = Context.Commands.Register(new CommandDefinition(name, description), invocation =>
                Context.TryGetExtension<SandboxSessionCommands>(out var target)
                    ? handler(target) : SandboxSessionCommands.Inactive());
            if (!result.Succeeded) Context.Logger.Warn("Could not register /" + name + ": " + result.ErrorMessage);
        }
    }
}
