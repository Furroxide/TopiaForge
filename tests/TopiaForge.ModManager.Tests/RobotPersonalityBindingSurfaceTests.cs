using System;
using TopiaForge.RobotKit;

namespace TopiaForge.ModManager.Tests
{
    internal static class RobotPersonalityBindingSurfaceTests
    {
        public static void Run()
        {
            AssertAvailable<CompletePersonality>();
            AssertUnavailable(
                typeof(MissingDefaultAgent<CompletePersonality>),
                typeof(CompletePersonality),
                "the default-personality snapshot getter is required");
            AssertUnavailable(
                typeof(MissingHackedAgent<CompletePersonality>),
                typeof(CompletePersonality),
                "the hacked-personality conflict getter is required");
            AssertUnavailable(
                typeof(MissingSetAgent<CompletePersonality>),
                typeof(CompletePersonality),
                "the personality apply/restore setter is required");
            AssertUnavailable(
                typeof(MissingClearAgent<CompletePersonality>),
                typeof(CompletePersonality),
                "the no-override restore method is required");
            AssertUnavailable(
                typeof(CompleteAgent<MissingCreatePersonality>),
                typeof(MissingCreatePersonality),
                "the isolated personality factory is required");
            AssertUnavailable(
                typeof(CompleteAgent<MissingTemperaturePersonality>),
                typeof(MissingTemperaturePersonality),
                "the personality temperature setter is required");
            Console.WriteLine("RobotPersonalityBindingSurfaceTests passed.");
        }

        private static void AssertAvailable<TPersonality>() where TPersonality : class
        {
            var surface = RobotPersonalityBindingSurface.TryCreate(
                typeof(CompleteAgent<TPersonality>),
                typeof(TPersonality),
                typeof(string[]));
            Assert(surface != null, "the complete apply-and-restore surface should be available");
        }

        private static void AssertUnavailable(Type agentType, Type personalityType, string message)
        {
            Assert(
                RobotPersonalityBindingSurface.TryCreate(agentType, personalityType, typeof(string[])) == null,
                message);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Robot personality bindings: " + message);
        }

        private sealed class CompleteAgent<TPersonality> where TPersonality : class
        {
            public TPersonality? DefaultPersonality { get; }
            public TPersonality? HackedPersonality { get; }
            public void SetHackedPersonality(TPersonality personality) { }
            public void ClearHackedPersonality() { }
        }

        private sealed class MissingDefaultAgent<TPersonality> where TPersonality : class
        {
            public TPersonality? HackedPersonality { get; }
            public void SetHackedPersonality(TPersonality personality) { }
            public void ClearHackedPersonality() { }
        }

        private sealed class MissingHackedAgent<TPersonality> where TPersonality : class
        {
            public TPersonality? DefaultPersonality { get; }
            public void SetHackedPersonality(TPersonality personality) { }
            public void ClearHackedPersonality() { }
        }

        private sealed class MissingSetAgent<TPersonality> where TPersonality : class
        {
            public TPersonality? DefaultPersonality { get; }
            public TPersonality? HackedPersonality { get; }
            public void ClearHackedPersonality() { }
        }

        private sealed class MissingClearAgent<TPersonality> where TPersonality : class
        {
            public TPersonality? DefaultPersonality { get; }
            public TPersonality? HackedPersonality { get; }
            public void SetHackedPersonality(TPersonality personality) { }
        }

        private sealed class CompletePersonality
        {
            public static CompletePersonality CreateHacked(CompletePersonality source, string[] bios) => new CompletePersonality();
            public void SetTemperature(float temperature) { }
        }

        private sealed class MissingCreatePersonality
        {
            public void SetTemperature(float temperature) { }
        }

        private sealed class MissingTemperaturePersonality
        {
            public static MissingTemperaturePersonality CreateHacked(
                MissingTemperaturePersonality source,
                string[] bios) => new MissingTemperaturePersonality();
        }
    }
}
