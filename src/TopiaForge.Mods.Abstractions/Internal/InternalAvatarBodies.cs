using System;

namespace TopiaForge.Mods.Internal
{
    /// <summary>
    /// Makes the body a remote player's avatar wears (WS3.10): RobotKit offers a dormant robot of the game's own, and the
    /// runtime falls back to a tinted capsule when no provider offers one or the body cannot be made. A trusted first-party
    /// provider registers one through <see cref="IRuntimeReplication.RegisterAvatarBodyFactory"/>. Main thread.
    /// </summary>
    internal interface IInternalAvatarBodyFactory
    {
        /// <summary>
        /// Makes one body, or says why it cannot, as when the loaded scene holds no robot to copy. The body must be dormant:
        /// no brain, no locomotion, no conversation, every rigidbody kinematic and every collider off, since the runtime's
        /// avatar carries the kinematic capsule other players collide with. It must not throw.
        /// </summary>
        OperationResult<IInternalAvatarBody> CreateBody(InternalAvatarBodyRequest request);
    }

    /// <summary>One body an avatar wears. The runtime places it under the avatar, moves it, and disposes it when done. Main thread.</summary>
    internal interface IInternalAvatarBody : IDisposable
    {
        /// <summary>
        /// Gets the body's root object, an entity over a native object (<see cref="IInternalNativeObjectEntity"/>), which
        /// the runtime parents under the avatar with its feet at the avatar's origin.
        /// </summary>
        IEntity Root { get; }

        /// <summary>Gets whether the body still exists; one the game destroyed is made again.</summary>
        bool IsAlive { get; }

        /// <summary>
        /// Shows the body moving at <paramref name="speedMetersPerSecond"/> across the ground, crouching or not, as the
        /// player's replicated pose says: the animator's walk follows the speed. Called once a frame while the avatar is
        /// shown; it must not allocate or throw.
        /// </summary>
        void Animate(float speedMetersPerSecond, bool crouching);
    }

    /// <summary>What an avatar's body should look like. It names no player: an avatar shows nothing but its pose and colour.</summary>
    internal sealed class InternalAvatarBodyRequest
    {
        public InternalAvatarBodyRequest(float red, float green, float blue)
        {
            if (!IsUnit(red) || !IsUnit(green) || !IsUnit(blue)) throw new ArgumentOutOfRangeException(nameof(red), "A tint's channels are from zero to one.");
            Red = red;
            Green = green;
            Blue = blue;
        }

        /// <summary>Gets the tint's red channel, from zero to one: each player's avatar keeps one colour on every peer.</summary>
        public float Red { get; }

        public float Green { get; }

        public float Blue { get; }

        private static bool IsUnit(float value) => !float.IsNaN(value) && value >= 0f && value <= 1f;
    }

    /// <summary>
    /// An entity the runtime or a first-party provider made over a native game object, which a trusted provider may follow
    /// natively: RobotKit's Chase follows a remote player's avatar through it. The object is typed loosely so this assembly
    /// stays free of the engine; it is null once the object is gone. Main thread.
    /// </summary>
    internal interface IInternalNativeObjectEntity
    {
        object? NativeObject { get; }
    }
}
