using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class MultiplayerReplication
    {
        private const string AvatarBodyKey = "avatar.body";

        private readonly Dictionary<string, Registration<IInternalAvatarBodyFactory>> avatarBodies =
            new Dictionary<string, Registration<IInternalAvatarBodyFactory>>(StringComparer.Ordinal);

        /// <summary>Gets the factory a first-party provider registered for avatars' bodies (WS3.10), or null.</summary>
        internal IInternalAvatarBodyFactory? AvatarBodyFactory =>
            avatarBodies.TryGetValue(AvatarBodyKey, out var registration) ? registration.Value : null;

        internal Registration<IInternalAvatarBodyFactory>? AddAvatarBodyFactory(string ownerModId, IInternalAvatarBodyFactory factory)
        {
            if (avatarBodies.ContainsKey(AvatarBodyKey)) return null;
            var registration = new Registration<IInternalAvatarBodyFactory>(avatarBodies, AvatarBodyKey, ownerModId, factory);
            avatarBodies.Add(AvatarBodyKey, registration);
            return registration;
        }
    }

    internal sealed partial class RuntimeReplication
    {
        public OperationResult<IDisposable> RegisterAvatarBodyFactory(IInternalAvatarBodyFactory factory)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            UnityMainThreadGuard.AssertCurrent();
            var refused = Admit<IDisposable>();
            if (refused != null) return refused;
            var registration = services.AddAvatarBodyFactory(OwnerModId, factory);
            if (registration == null)
            {
                return OperationResult<IDisposable>.Failure(ModErrorCode.Conflict, "Another provider already makes the bodies players' avatars wear.");
            }

            return TrackRegistration(registration);
        }
    }
}
