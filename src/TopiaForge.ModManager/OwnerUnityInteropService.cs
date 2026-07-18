using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Interop.Unity;
using UnityEngine;

namespace TopiaForge.ModManager
{
    internal sealed class OwnerUnityInteropService : IUnityInteropService
    {
        private readonly UnityEntityRegistry entities;

        public OwnerUnityInteropService(UnityEntityRegistry entities)
        {
            this.entities = entities ?? throw new ArgumentNullException(nameof(entities));
        }

        public bool TryGetGameObject(IEntity entity, out GameObject? gameObject)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (entity is UnityEntityRegistry.UnityEntity unityEntity
                && ReferenceEquals(unityEntity.Owner, entities)
                && unityEntity.IsAlive)
            {
                gameObject = unityEntity.GameObject;
                return gameObject != null;
            }

            gameObject = null;
            return false;
        }

        public OperationResult<IEntity> Wrap(GameObject gameObject)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (gameObject == null)
            {
                throw new ArgumentNullException(nameof(gameObject));
            }

            try
            {
                return OperationResult<IEntity>.Success(entities.GetOrCreate(gameObject));
            }
            catch (MissingReferenceException)
            {
                return OperationResult<IEntity>.Failure(ModErrorCode.NotFound, "The Unity object was destroyed.");
            }
            catch (ObjectDisposedException)
            {
                return OperationResult<IEntity>.Failure(ModErrorCode.InvalidState, "The TopiaForge entity registry is stopping.");
            }
        }

        public bool TryGetComponent<T>(IEntity entity, out T? component) where T : Component
        {
            UnityMainThreadGuard.AssertCurrent();
            if (TryGetGameObject(entity, out var gameObject) && gameObject != null)
            {
                component = gameObject.GetComponent<T>();
                return component != null;
            }

            component = null;
            return false;
        }
    }
}
