using System;
using System.Reflection;

namespace TopiaForge.RobotKit
{
    internal sealed class RobotPersonalityBindingSurface
    {
        private const BindingFlags InstanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags StaticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        private RobotPersonalityBindingSurface(
            PropertyInfo defaultPersonality,
            PropertyInfo hackedPersonality,
            MethodInfo createHacked,
            MethodInfo setTemperature,
            MethodInfo setHackedPersonality,
            MethodInfo clearHackedPersonality)
        {
            DefaultPersonality = defaultPersonality;
            HackedPersonality = hackedPersonality;
            CreateHacked = createHacked;
            SetTemperature = setTemperature;
            SetHackedPersonality = setHackedPersonality;
            ClearHackedPersonality = clearHackedPersonality;
        }

        public PropertyInfo DefaultPersonality { get; }
        public PropertyInfo HackedPersonality { get; }
        public MethodInfo CreateHacked { get; }
        public MethodInfo SetTemperature { get; }
        public MethodInfo SetHackedPersonality { get; }
        public MethodInfo ClearHackedPersonality { get; }

        public static RobotPersonalityBindingSurface? TryCreate(
            Type? agentType,
            Type? personalityType,
            Type? bioArrayType)
        {
            if (agentType == null || personalityType == null || bioArrayType == null)
            {
                return null;
            }

            try
            {
                var defaultPersonality = FindReadablePersonality(agentType, "DefaultPersonality", personalityType);
                var hackedPersonality = FindReadablePersonality(agentType, "HackedPersonality", personalityType);
                var createHacked = FindMethod(
                    personalityType,
                    "CreateHacked",
                    StaticFlags,
                    personalityType,
                    personalityType,
                    bioArrayType);
                var setTemperature = FindMethod(
                    personalityType,
                    "SetTemperature",
                    InstanceFlags,
                    typeof(void),
                    typeof(float));
                var setHackedPersonality = FindMethod(
                    agentType,
                    "SetHackedPersonality",
                    InstanceFlags,
                    typeof(void),
                    personalityType);
                var clearHackedPersonality = FindMethod(
                    agentType,
                    "ClearHackedPersonality",
                    InstanceFlags,
                    typeof(void));

                return defaultPersonality == null
                    || hackedPersonality == null
                    || createHacked == null
                    || setTemperature == null
                    || setHackedPersonality == null
                    || clearHackedPersonality == null
                        ? null
                        : new RobotPersonalityBindingSurface(
                            defaultPersonality,
                            hackedPersonality,
                            createHacked,
                            setTemperature,
                            setHackedPersonality,
                            clearHackedPersonality);
            }
            catch (AmbiguousMatchException)
            {
                return null;
            }
        }

        private static PropertyInfo? FindReadablePersonality(
            Type ownerType,
            string name,
            Type personalityType)
        {
            var property = ownerType.GetProperty(name, InstanceFlags);
            return property != null
                && property.PropertyType == personalityType
                && property.GetIndexParameters().Length == 0
                && property.GetGetMethod(nonPublic: true) != null
                    ? property
                    : null;
        }

        private static MethodInfo? FindMethod(
            Type ownerType,
            string name,
            BindingFlags flags,
            Type returnType,
            params Type[] parameterTypes)
        {
            var method = ownerType.GetMethod(name, flags, null, parameterTypes, null);
            return method != null && method.ReturnType == returnType ? method : null;
        }
    }
}
