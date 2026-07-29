using System;
using System.Reflection;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.RobotKit
{
    internal static class RobotPersonalityBridge
    {
        public static Type? AgentType { get; } = Type.GetType("LLMAgent, GameCode", throwOnError: false);
        public static Type? RobotBodyType { get; } = Type.GetType("RobotBody, GameCode", throwOnError: false);
        private static Type? PersonalityType { get; } = Type.GetType("PersonalityAsset, GameCode", throwOnError: false);
        private static RobotPersonalityBindingSurface? Bindings { get; } =
            RobotPersonalityBindingSurface.TryCreate(AgentType, PersonalityType, typeof(TextAsset[]));

        public static bool IsAvailable => Bindings != null
            && AgentType != null
            && typeof(Component).IsAssignableFrom(AgentType)
            && PersonalityType != null
            && typeof(UnityEngine.Object).IsAssignableFrom(PersonalityType);

        public static object? GetHackedPersonality(Component agent)
        {
            return agent == null || Bindings == null ? null : Bindings.HackedPersonality.GetValue(agent, null);
        }

        public static bool IsCurrent(Component agent, object? temporary)
        {
            return temporary is TemporaryPersonality owned
                && ReferenceEquals(GetHackedPersonality(agent), owned.Asset);
        }

        public static OperationResult<object> Apply(Component agent, RobotPersonalityDraft draft)
        {
            if (!IsAvailable || agent == null || draft == null)
            {
                return OperationResult<object>.Failure(ModErrorCode.Unavailable, "Native robot personality editing is unavailable.");
            }

            object? created = null;
            TextAsset? bio = null;
            var previous = GetHackedPersonality(agent);
            try
            {
                var source = Bindings!.DefaultPersonality.GetValue(agent, null);
                if (source == null)
                {
                    return OperationResult<object>.Failure(ModErrorCode.Unavailable, "Robot has no default personality template.");
                }

                bio = new TextAsset(draft.DisplayName + "\n\n" + draft.Instructions);
                bio.name = draft.DisplayName;
                created = Bindings.CreateHacked.Invoke(null, new object[]
                {
                    source,
                    new[] { bio }
                });
                if (created == null)
                {
                    UnityEngine.Object.Destroy(bio);
                    return OperationResult<object>.Failure(ModErrorCode.External, "Robotopia did not create a temporary personality.");
                }

                Bindings.SetTemperature.Invoke(created, new object[] { draft.Temperature });
                Bindings.SetHackedPersonality.Invoke(agent, new[] { created });
                return OperationResult<object>.Success(new TemporaryPersonality(created, bio));
            }
            catch (Exception exception)
            {
                Restore(agent, previous);
                if (created is UnityEngine.Object asset)
                {
                    UnityEngine.Object.Destroy(asset);
                }
                if (bio != null)
                {
                    UnityEngine.Object.Destroy(bio);
                }
                return OperationResult<object>.Failure(ModErrorCode.External, Unwrap(exception).Message);
            }
        }

        public static void Restore(Component agent, object? originalHackedPersonality)
        {
            if (agent == null || Bindings == null)
            {
                return;
            }

            if (originalHackedPersonality != null)
            {
                Bindings.SetHackedPersonality.Invoke(agent, new[] { originalHackedPersonality });
            }
            else
            {
                Bindings.ClearHackedPersonality.Invoke(agent, Array.Empty<object>());
            }
        }

        public static void DestroyTemporary(object? temporary)
        {
            if (!(temporary is TemporaryPersonality owned))
            {
                return;
            }

            if (owned.Asset is UnityEngine.Object asset)
            {
                UnityEngine.Object.Destroy(asset);
            }
            if (owned.Bio != null)
            {
                UnityEngine.Object.Destroy(owned.Bio);
            }
        }

        private static Exception Unwrap(Exception exception)
        {
            return exception is TargetInvocationException invocation && invocation.InnerException != null
                ? invocation.InnerException
                : exception;
        }

        private sealed class TemporaryPersonality
        {
            public TemporaryPersonality(object asset, TextAsset bio)
            {
                Asset = asset;
                Bio = bio;
            }

            public object Asset { get; }
            public TextAsset Bio { get; }
        }
    }
}
