using System;
using System.Threading;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal sealed partial class UnityNetworkWorldBackend
    {
        private bool droppingHeldItem;

        public IEntity? MotionRoot(IEntity entity)
        {
            if (!entities.TryGetGameObject(entity, out var gameObject) || gameObject == null) return null;
            UnityEngine.Rigidbody? root = null;
            for (var current = gameObject.transform; current != null; current = current.parent)
            {
                var body = current.GetComponent<UnityEngine.Rigidbody>();
                if (body != null) root = body;
            }
            return root != null ? entities.GetOrCreate(root.gameObject) : null;
        }

        public bool TryGetLocalPosition(out NetVector3 position)
        {
            position = default;
            var player = PlayerController.FindPlayer();
            if (player == null) return false;
            var current = player.transform.position;
            position = new NetVector3(current.x, current.y, current.z);
            return true;
        }

        public bool TryGetHeldEntity(out IEntity? entity)
        {
            var player = PlayerController.FindPlayer();
            var held = player != null ? player.HeldItem : null;
            entity = held != null ? entities.GetOrCreate(held.gameObject) : null;
            return entity != null;
        }

        public async void DropHeldEntity(IEntity entity)
        {
            if (droppingHeldItem) return;
            var player = PlayerController.FindPlayer();
            var held = player != null ? player.HeldItem : null;
            if (held == null || entities.GetOrCreate(held.gameObject).Id != entity.Id) return;
            droppingHeldItem = true;
            try
            {
                await held.Drop(DropMode.Drop, null, CancellationToken.None);
            }
            catch (Exception)
            {
                // A scene teardown may end the native item while it drops. No host text reaches a log here.
            }
            finally
            {
                droppingHeldItem = false;
            }
        }
    }
}
