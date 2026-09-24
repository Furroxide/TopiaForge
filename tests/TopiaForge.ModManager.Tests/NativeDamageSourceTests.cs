using System;
using System.Reflection;
using TopiaForge.Mods.GameBridge;

namespace TopiaForge.ModManager.Tests
{
    internal static class NativeDamageSourceTests
    {
        private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        internal static void Run()
        {
            LabelledBuildReceivesTheLabel();
            ObjectSourceBuildReceivesNoSourceObject();
            LabelWinsWhenBothShapesExist();
            LeadingParametersMustMatchExactly();
            UnsupportedSourcesAreUnavailable();
            Console.WriteLine("Native damage source tests passed (5 cases).");
        }

        private static void LabelledBuildReceivesTheLabel()
        {
            var method = Select(typeof(LabelledHealth));
            Assert(method != null && method.GetParameters()[1].ParameterType == typeof(string),
                "A build whose private ChangeHealth takes a string source must resolve.");
            var health = new LabelledHealth();
            method!.Invoke(health, new[] { -5f, NativeDamageSource.Argument(method, "zombie bite") });
            Assert(health.Delta == -5f && health.Source == "zombie bite", "The diagnostic label must reach the game.");
        }

        private static void ObjectSourceBuildReceivesNoSourceObject()
        {
            var method = Select(typeof(ObjectHealth));
            Assert(method != null && method.GetParameters()[1].ParameterType == typeof(SourceObject),
                "A build whose ChangeHealth takes the causing object must resolve.");
            var health = new ObjectHealth();
            method!.Invoke(health, new[] { 3f, NativeDamageSource.Argument(method, "healing station") });
            Assert(health.Calls == 1 && health.Delta == 3f && health.Source == null,
                "A label cannot become a game object, so the object-source build receives no source object.");
        }

        private static void LabelWinsWhenBothShapesExist()
        {
            var method = Select(typeof(BothHealth));
            Assert(method != null && method.GetParameters()[1].ParameterType == typeof(string),
                "When both shapes exist the labelled overload keeps the diagnostic source.");
        }

        private static void LeadingParametersMustMatchExactly()
        {
            var damage = NativeDamageSource.Select(typeof(DamageHealth), "Damage", Instance, typeof(SourceObject),
                typeof(float), typeof(DamageKind));
            Assert(damage != null && damage.GetParameters()[1].ParameterType == typeof(DamageKind),
                "The damage overload must match its exact leading float and damage-type parameters.");
            Assert(NativeDamageSource.Argument(damage!, "robot") == null, "An object source receives no object.");
        }

        private static void UnsupportedSourcesAreUnavailable()
        {
            Assert(Select(typeof(WrongHealth)) == null,
                "Integer sources, double deltas, generic methods and extra parameters are not supported shapes.");
        }

        private static MethodInfo? Select(Type health) =>
            NativeDamageSource.Select(health, "ChangeHealth", Instance, typeof(SourceObject), typeof(float));

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class SourceObject { }

        private enum DamageKind { Blunt }

        private sealed class LabelledHealth
        {
            internal float Delta;
            internal string? Source;
            private void ChangeHealth(float delta, string source) { Delta = delta; Source = source; }
        }

        private sealed class ObjectHealth
        {
            internal int Calls;
            internal float Delta;
            internal SourceObject? Source = new SourceObject();
            private void ChangeHealth(float delta, SourceObject? source) { Calls++; Delta = delta; Source = source; }
        }

        private sealed class BothHealth
        {
            private void ChangeHealth(float delta, SourceObject? source) { }
            private void ChangeHealth(float delta, string source) { }
        }

        private sealed class DamageHealth
        {
            public void Damage(float amount, int kind, SourceObject? source) { }
            public void Damage(float amount, DamageKind kind, SourceObject? source) { }
        }

        private sealed class WrongHealth
        {
            private void ChangeHealth(float delta, int source) { }
            private void ChangeHealth(double delta, string source) { }
            private void ChangeHealth<T>(float delta, string source) { }
            private void ChangeHealth(float delta, string source, bool extra) { }
        }
    }
}
