using System.Collections.Generic;
using System.Globalization;
using TopiaForge.Mods;

namespace TopiaForge.ValidTestMod
{
    /// <summary>
    /// Spawns and changes crates as authority routing's runtime tests need (WS3.7). Its script spawns a crate as each scene
    /// loads, which a joined player's own run of it shadows. Its player's commands drop a crate, which a joined player
    /// predicts, and move or remove the newest crate the player dropped. Each result goes to the test's trace, with the
    /// handle's id, so the test can find the handle the mod holds.
    /// </summary>
    public sealed class RuntimeAuthorityMod : TopiaForgeMod
    {
        private readonly List<ISpawnedEntity> dropped = new List<ISpawnedEntity>();
        private readonly Dictionary<string, IPrefabAsset> crates = new Dictionary<string, IPrefabAsset>();
        private int scripted;

        protected override void OnLoad()
        {
            Context.Events.SubscribeSceneLoaded((SceneLoadEvent scene) =>
            {
                var at = new Vec3(10f + 3f * scripted, 1f, 0f);
                scripted++;
                var crate = Spawn("assets/world.bundle", at);
                RuntimeTrace.Write(crate.Succeeded ? "authority-scene-crate:" + crate.Value!.Id : "authority-scene-crate-failed:" + crate.ErrorCode);
            });

            Context.Commands.Register(new CommandDefinition("drop", "Drops a crate at x, from a bundle"), command =>
            {
                var arguments = command.Arguments;
                var x = arguments.Count > 0 ? float.Parse(arguments[0], CultureInfo.InvariantCulture) : 0f;
                var bundle = arguments.Count > 1 ? arguments[1] : "assets/world.bundle";
                var crate = Spawn(bundle, new Vec3(x, 1f, 0f));
                if (!crate.Succeeded) return Trace(OperationResult<string>.Failure(crate.ErrorCode, crate.ErrorMessage), "authority-drop-failed:" + crate.ErrorCode);
                dropped.Add(crate.Value!);
                return Trace(OperationResult<string>.Success(crate.Value!.Id), "authority-dropped:" + crate.Value!.Id);
            });

            Context.Commands.Register(new CommandDefinition("move", "Moves the newest crate to x"), command =>
            {
                if (dropped.Count == 0) return OperationResult<string>.Failure(ModErrorCode.NotFound, "Nothing dropped.");
                var x = float.Parse(command.Arguments[0], CultureInfo.InvariantCulture);
                var moved = Context.Entities.SetTransform(dropped[dropped.Count - 1], new TransformState(new Vec3(x, 1f, 0f), Quat.Identity, new Vec3(1f, 1f, 1f)));
                return Trace(moved.Succeeded ? OperationResult<string>.Success("moved") : OperationResult<string>.Failure(moved.ErrorCode, moved.ErrorMessage),
                    moved.Succeeded ? "authority-moved" : "authority-move-failed:" + moved.ErrorCode);
            });

            Context.Commands.Register(new CommandDefinition("remove", "Removes the newest crate"), _ =>
            {
                if (dropped.Count == 0) return OperationResult<string>.Failure(ModErrorCode.NotFound, "Nothing dropped.");
                var removed = Context.Entities.Destroy(dropped[dropped.Count - 1]);
                dropped.RemoveAt(dropped.Count - 1);
                return Trace(removed.Succeeded ? OperationResult<string>.Success("removed") : OperationResult<string>.Failure(removed.ErrorCode, removed.ErrorMessage),
                    removed.Succeeded ? "authority-removed" : "authority-remove-failed:" + removed.ErrorCode);
            });
        }

        private OperationResult<ISpawnedEntity> Spawn(string bundlePath, Vec3 at)
        {
            if (!crates.TryGetValue(bundlePath, out var crate))
            {
                // The tests' asset service loads at once, as a bundle already in memory does; the mod loads each bundle once, as
                // the game requires.
                var bundle = Context.Assets.LoadBundleAsync(bundlePath);
                if (!bundle.IsCompleted || !bundle.Result.TryGetValue(out var loaded) || loaded == null) return OperationResult<ISpawnedEntity>.Failure(ModErrorCode.NotFound, "No bundle.");
                var prefab = Context.Assets.LoadPrefabAsync(loaded, "Crate");
                if (!prefab.IsCompleted || !prefab.Result.TryGetValue(out crate) || crate == null) return OperationResult<ISpawnedEntity>.Failure(ModErrorCode.NotFound, "No crate.");
                crates.Add(bundlePath, crate);
            }

            return Context.Assets.Spawn(new AssetSpawnRequest(crate, new TransformState(at, Quat.Identity, new Vec3(1f, 1f, 1f))));
        }

        private static OperationResult<string> Trace(OperationResult<string> result, string line)
        {
            RuntimeTrace.Write(line);
            return result;
        }
    }
}
