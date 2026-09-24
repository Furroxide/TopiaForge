using System;
using System.Reflection;
using TopiaForge.ModManager;

namespace TopiaForge.ModManager.Tests
{
    internal static class NativePlayerTeleportTests
    {
        internal static void Run()
        {
            NativeStateSurvivesTheFollowingFrame();
            foreach (var enabled in new[] { false, true })
            {
                ControllerOwnershipSurvivesTeleport(enabled, false);
                ControllerOwnershipSurvivesTeleport(enabled, true);
            }
            UnsupportedSignaturesFailBeforeInvocation();
            Console.WriteLine("Native player teleport tests passed (6 cases).");
        }

        private static void NativeStateSurvivesTheFollowingFrame()
        {
            // Independent fixture: a native movement owner has state beyond its visible transform.
            var stale = new Movement { Position = 7, Facing = 10 };
            stale.NextFrame();
            Assert(stale.Position != 7 && stale.Facing != 10, "The fixture must expose a transform-only placement failure.");
            var movement = new Movement();
            Teleport(movement);
            movement.NextFrame();
            Assert(movement.Calls == 1 && movement.Position == 7 && movement.Facing == 10,
                "Placement must invoke native teleport so stale falling velocity and look direction cannot overwrite the spawn.");
        }

        private static void ControllerOwnershipSurvivesTeleport(bool enabled, bool fail)
        {
            var movement = new Movement { Enabled = enabled, Fail = fail };
            try
            {
                Teleport(movement);
                Assert(!fail, "A native teleport failure must propagate.");
            }
            catch (TargetInvocationException error) when (fail && error.InnerException is ApplicationException) { }
            Assert(movement.Enabled == enabled && movement.Calls == 1,
                "Native teleport must preserve the prior CharacterController enabled state on success and failure.");
        }

        private static void UnsupportedSignaturesFailBeforeInvocation()
        {
            foreach (var type in new[] { typeof(WrongRotation), typeof(WrongReturn), typeof(GenericOnly), typeof(StaticOnly) })
                Assert(NativeWorldReflection.PlayerTeleport(type, typeof(Position), typeof(Rotation)) == null,
                    "Only the exact public instance void TeleportTo(position, nullable rotation) contract is supported.");
        }

        private static void Teleport(Movement movement)
        {
            var method = NativeWorldReflection.PlayerTeleport(typeof(Movement), typeof(Position), typeof(Rotation));
            Assert(method != null, "The exact native teleport overload must resolve despite unrelated overloads.");
            NativeWorldReflection.InvokePlayerTeleport(movement, method!, new Position(7), new Rotation(10),
                () => movement.Enabled, value => movement.Enabled = value);
        }

        private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        public readonly struct Position { public Position(float value) { Value = value; } public float Value { get; } }
        public readonly struct Rotation { public Rotation(float value) { Value = value; } public float Value { get; } }
        public sealed class Movement
        {
            public bool Enabled = true;
            public bool Fail;
            public int Calls;
            public float Position;
            public float Facing;
            private float fallingVelocity = -4;
            private float lookDirection = 90;
            public void TeleportTo(Position position, Rotation? rotation)
            {
                Calls++;
                Enabled = false;
                if (Fail) throw new ApplicationException("fixture native failure");
                Position = position.Value;
                fallingVelocity = 0;
                lookDirection = rotation!.Value.Value;
                Enabled = true;
            }
            public void TeleportTo(Position position, Rotation rotation) => throw new InvalidOperationException("wrong overload");
            public void NextFrame() { Position += fallingVelocity * .02f; Facing = lookDirection; }
        }
        public sealed class WrongRotation { public void TeleportTo(Position position, Rotation rotation) => throw new Exception("must not invoke"); }
        public sealed class WrongReturn { public bool TeleportTo(Position position, Rotation? rotation) => throw new Exception("must not invoke"); }
        public sealed class GenericOnly { public void TeleportTo<T>(Position position, Rotation? rotation) => throw new Exception("must not invoke"); }
        public sealed class StaticOnly { public static void TeleportTo(Position position, Rotation? rotation) => throw new Exception("must not invoke"); }
    }
}
