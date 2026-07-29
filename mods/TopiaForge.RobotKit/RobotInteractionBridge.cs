using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.RobotKit
{
    // Native player interaction adapter for RobotKit custom callbacks. It implements GameCode's IInteractable so
    // PlayerController selection, reticle gating, and prompt display stay owned by the game.
    internal sealed class RobotInteractionBridge : MonoBehaviour, IInteractable
    {
        private IRobotAgent? agent;
        private RobotCustomInteraction? interaction;
        private IModLogger? logger;

        public GameObject GameObject => gameObject;

        public float ScreenRectExpansion => Mathf.Max(0f, interaction?.ScreenRectExpansion ?? 0f);

        public Behaviour AsComponent()
        {
            return this;
        }

        public void Configure(IRobotAgent owner, RobotCustomInteraction customInteraction, IModLogger ownerLogger)
        {
            agent = owner;
            interaction = customInteraction;
            logger = ownerLogger;
        }

        public bool CanInteract(Transform hand)
        {
            if (!isActiveAndEnabled || agent == null || interaction == null || hand == null)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(interaction.Prompt))
            {
                return false;
            }

            var distance = Vector3.Distance(hand.position, transform.position);
            var maxDistance = interaction.Distance > 0f ? interaction.Distance : 3f;
            if (distance > maxDistance)
            {
                return false;
            }

            if (interaction.CanInteract == null)
            {
                return true;
            }

            try
            {
                return interaction.CanInteract(BuildContext(hand, distance));
            }
            catch (Exception ex)
            {
                logger?.Error(ex, "RobotKit custom interaction gate failed.");
                return false;
            }
        }

        public UniTask OnInteractAttempt(Transform hand, CancellationToken ct)
        {
            if (agent == null || interaction == null || hand == null || ct.IsCancellationRequested)
            {
                return UniTask.CompletedTask;
            }

            try
            {
                interaction.Interact?.Invoke(BuildContext(hand, Vector3.Distance(hand.position, transform.position)));
            }
            catch (Exception ex)
            {
                logger?.Error(ex, "RobotKit custom interaction callback failed.");
            }

            return UniTask.CompletedTask;
        }

        private void Awake()
        {
            ListenableExt.WithState<IInteractable?>(this, UIState.InteractTarget, OnInteractTargetChanged);
        }

        private void OnInteractTargetChanged(IInteractable? target)
        {
            if (!ReferenceEquals(target, this) || interaction == null)
            {
                return;
            }

            UIState.InteractAction.Set(interaction.Prompt ?? string.Empty);
        }

        private RobotInteractionContext BuildContext(Transform hand, float distance)
        {
            var handPosition = hand.position;
            return new RobotInteractionContext(
                agent!,
                agent!.Position,
                new Vec3(handPosition.x, handPosition.y, handPosition.z),
                distance);
        }
    }
}
