using System;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// One remote player's avatar (WS3.10): a root with a kinematic capsule collider, registered as an entity, wearing the
    /// body a first-party provider offers, a dormant robot of the game's own from RobotKit, or else a capsule in the
    /// player's colour. A body that cannot be made is tried again every five seconds, and one the game destroyed is made
    /// again. The avatar moves to the player's interpolated pose each frame, before the mods' LateUpdate, and its body's
    /// walk follows the speed it moves at. Main thread only; drawing allocates nothing.
    /// </summary>
    internal sealed class RemotePlayerAvatar : IDisposable
    {
        /// <summary>How long after a body could not be made the avatar asks again.</summary>
        internal const long BodyRetryMicros = 5_000_000;

        // How much of each frame's measured speed the shown speed takes: enough to follow a stop within a few frames.
        private const float SpeedSmoothing = 0.3f;

        private readonly IPresenceAvatarObject avatar;
        private IInternalAvatarBody? body;
        private bool hasDrawn;
        private NetVector3 drawn;
        private long nextBodyMicros;
        private bool disposed;

        internal RemotePlayerAvatar(IPresenceAvatarObject avatar, PresenceTint tint)
        {
            this.avatar = avatar ?? throw new ArgumentNullException(nameof(avatar));
            Tint = tint;
        }

        internal PresenceTint Tint { get; }

        internal IEntity Entity => avatar.Entity;

        internal bool IsAlive => !disposed && avatar.IsAlive;

        /// <summary>Gets whether the avatar wears a provider's body rather than its capsule.</summary>
        internal bool IsWearingBody => body != null && avatar.IsWearingBody;

        internal NetVector3 HeadPosition => avatar.HeadPosition;

        /// <summary>Gets where the avatar was last drawn.</summary>
        internal NetVector3 Feet => drawn;

        /// <summary>Gets the speed across the ground the avatar shows, in metres a second.</summary>
        internal float Speed { get; private set; }

        /// <summary>Gets how many bodies the factory could not make, threw for, or the avatar could not wear.</summary>
        internal int BodyFailures { get; private set; }

        /// <summary>Gets the type of what the factory threw last time it threw, for presence to report once; null until it throws.</summary>
        internal string? BodyError { get; private set; }

        /// <summary>
        /// Wears a body from <paramref name="factory"/> when one is due: at once, and again five seconds after one could not
        /// be made. A body the game destroyed comes off first. A factory that throws is a failure like any other.
        /// </summary>
        internal void Dress(IInternalAvatarBodyFactory? factory, long nowMicros)
        {
            if (disposed) return;
            if (body != null && !body.IsAlive)
            {
                Undress();
            }

            if (body != null || factory == null || nowMicros < nextBodyMicros) return;
            nextBodyMicros = nowMicros + BodyRetryMicros;
            OperationResult<IInternalAvatarBody> made;
            try
            {
                made = factory.CreateBody(Tint.ToBodyRequest());
            }
            catch (Exception exception)
            {
                BodyFailures++;
                BodyError = exception.GetType().Name;
                return;
            }

            if (made == null || !made.TryGetValue(out var created) || created == null)
            {
                BodyFailures++;
                return;
            }

            bool worn;
            try
            {
                worn = avatar.Wear(created);
            }
            catch (Exception)
            {
                worn = false;
            }

            if (!worn)
            {
                BodyFailures++;
                Release(created);
                return;
            }

            body = created;
        }

        /// <summary>Takes the body off, as when the scene changes under it; the next <see cref="Dress"/> makes another at once.</summary>
        internal void Undress()
        {
            var worn = body;
            body = null;
            nextBodyMicros = 0;
            if (worn == null) return;
            try
            {
                avatar.TakeOffBody();
            }
            finally
            {
                Release(worn);
            }
        }

        /// <summary>Draws the avatar with its feet at <paramref name="feet"/>, facing <paramref name="yawDegrees"/>.</summary>
        internal void Draw(NetVector3 feet, float yawDegrees, bool crouching, float deltaSeconds)
        {
            if (disposed) return;
            if (hasDrawn && deltaSeconds > 0f)
            {
                var dx = (double)feet.X - drawn.X;
                var dz = (double)feet.Z - drawn.Z;
                var measured = (float)(Math.Sqrt(dx * dx + dz * dz) / deltaSeconds);
                Speed += (Math.Min(measured, 64f) - Speed) * SpeedSmoothing;
            }

            hasDrawn = true;
            drawn = feet;
            avatar.Place(feet, yawDegrees);
            avatar.Animate(Speed, crouching);
            body?.Animate(Speed, crouching);
        }

        public void Dispose()
        {
            if (disposed) return;
            try
            {
                Undress();
            }
            finally
            {
                disposed = true;
                avatar.Dispose();
            }
        }

        /// <summary>The way a body faces, in degrees about the vertical, from where the player looks.</summary>
        internal static float YawOf(NetQuaternion view)
        {
            double x = view.X, y = view.Y, z = view.Z, w = view.W;
            var forwardX = 2.0 * (x * z + w * y);
            var forwardZ = 1.0 - 2.0 * (x * x + y * y);
            if (Math.Abs(forwardX) < 1e-9 && Math.Abs(forwardZ) < 1e-9) return 0f;
            return (float)(Math.Atan2(forwardX, forwardZ) * (180.0 / Math.PI));
        }

        private static void Release(IInternalAvatarBody worn)
        {
            try
            {
                worn.Dispose();
            }
            catch (Exception)
            {
                // A body that fails to go is its provider's to report; the avatar goes on without it.
            }
        }
    }
}
