using System;
using System.Collections.Generic;
using System.Threading;
using TopiaForge.Mods;

namespace TopiaForge.Worlds
{
    public sealed partial class WorldsService
    {
        public OperationResult<IWorldRegistration> RegisterWorld(
            WorldDefinition world,
            ICustomWorldContent? content = null)
        {
            ThrowIfDisposed();
            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            if (worldRegistrations.ContainsKey(world.Id))
            {
                return OperationResult<IWorldRegistration>.Failure(
                    ModErrorCode.Conflict,
                    "World '" + world.Id + "' is already registered.");
            }

            worlds.Add(world);
            // A plain re-registration means "this id is a normal world again" — drop any stale content link.
            customWorldContent.Remove(world.Id);
            if (content != null)
            {
                customWorldContent[world.Id] = content;
            }

            var registration = new Registration(this, world.Id, WorldRegistrationKind.World);
            worldRegistrations.Add(world.Id, registration);
            MarkCatalogDirty();
            return OperationResult<IWorldRegistration>.Success(registration);
        }

        public bool UnregisterWorld(string worldId)
        {
            if (disposed || string.IsNullOrWhiteSpace(worldId))
            {
                return false;
            }

            return worldRegistrations.TryGetValue(worldId, out var registration)
                && ReleaseRegistration(registration);
        }

        public OperationResult<IWorldRegistration> RegisterGamemode(GamemodeDefinition gamemode)
        {
            ThrowIfDisposed();
            if (gamemode == null)
            {
                throw new ArgumentNullException(nameof(gamemode));
            }

            if (gamemodeRegistrations.ContainsKey(gamemode.Id))
            {
                return OperationResult<IWorldRegistration>.Failure(
                    ModErrorCode.Conflict,
                    "Gamemode '" + gamemode.Id + "' is already registered.");
            }

            gamemodes.Add(gamemode);
            var registration = new Registration(this, gamemode.Id, WorldRegistrationKind.Gamemode);
            gamemodeRegistrations.Add(gamemode.Id, registration);
            MarkCatalogDirty();
            return OperationResult<IWorldRegistration>.Success(registration);
        }

        public OperationResult<IWorldRegistration> RegisterMenuEntry(GamemodeMenuEntry entry)
        {
            ThrowIfDisposed();
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            if (menuEntryRegistrations.ContainsKey(entry.Id))
            {
                return OperationResult<IWorldRegistration>.Failure(
                    ModErrorCode.Conflict,
                    "World menu entry '" + entry.Id + "' is already registered.");
            }

            menuEntries.Add(entry);
            var registration = new Registration(this, entry.Id, WorldRegistrationKind.MenuEntry);
            menuEntryRegistrations.Add(entry.Id, registration);
            MarkCatalogDirty();
            return OperationResult<IWorldRegistration>.Success(registration);
        }

        public bool UnregisterGamemode(string gamemodeId)
        {
            if (disposed || string.IsNullOrWhiteSpace(gamemodeId))
            {
                return false;
            }

            return gamemodeRegistrations.TryGetValue(gamemodeId, out var registration)
                && ReleaseRegistration(registration);
        }

        public bool UnregisterMenuEntry(string entryId)
        {
            return !disposed && !string.IsNullOrWhiteSpace(entryId)
                && menuEntryRegistrations.TryGetValue(entryId, out var registration)
                && ReleaseRegistration(registration);
        }

        private bool ReleaseRegistration(Registration registration)
        {
            if (disposed || !RegistrationMap(registration.Kind).TryGetValue(registration.Id, out var current)
                || !ReferenceEquals(current, registration))
            {
                registration.Deactivate();
                return false;
            }

            RegistrationMap(registration.Kind).Remove(registration.Id);
            registration.Deactivate();
            // Removals matter to the catalog as much as additions do: leaving them out would let the
            // published file keep offering a world or gamemode whose mod has already gone away.
            catalogDirty = true;
            switch (registration.Kind)
            {
                case WorldRegistrationKind.World:
                    worlds.RemoveAll(item => string.Equals(item.Id, registration.Id, StringComparison.OrdinalIgnoreCase));
                    customWorldContent.Remove(registration.Id);
                    worldCheckpoints.Remove(registration.Id);
                    if (CurrentSession != null
                        && string.Equals(CurrentSession.WorldId, registration.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        EndSession(WorldSessionEndReason.ProviderUnloading);
                    }

                    break;
                case WorldRegistrationKind.Gamemode:
                    gamemodes.RemoveAll(item => string.Equals(item.Id, registration.Id, StringComparison.OrdinalIgnoreCase));
                    if (CurrentSession != null
                        && string.Equals(CurrentSession.GamemodeId, registration.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        EndSession(WorldSessionEndReason.ProviderUnloading);
                    }

                    break;
                case WorldRegistrationKind.MenuEntry:
                    menuEntries.RemoveAll(item => string.Equals(item.Id, registration.Id, StringComparison.OrdinalIgnoreCase));
                    break;
            }

            return true;
        }

        private Dictionary<string, Registration> RegistrationMap(WorldRegistrationKind kind)
        {
            switch (kind)
            {
                case WorldRegistrationKind.World:
                    return worldRegistrations;
                case WorldRegistrationKind.Gamemode:
                    return gamemodeRegistrations;
                case WorldRegistrationKind.MenuEntry:
                    return menuEntryRegistrations;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        private static void DeactivateRegistrations(Dictionary<string, Registration> registrations)
        {
            foreach (var registration in registrations.Values)
            {
                registration.Deactivate();
            }

            registrations.Clear();
        }

        private sealed class Registration : IWorldRegistration
        {
            private WorldsService? owner;

            public Registration(WorldsService owner, string id, WorldRegistrationKind kind)
            {
                this.owner = owner;
                Id = id;
                Kind = kind;
            }

            public string Id { get; }
            public WorldRegistrationKind Kind { get; }
            public bool IsActive => owner != null;

            public void Dispose()
            {
                var current = Interlocked.Exchange(ref owner, null);
                current?.ReleaseRegistration(this);
            }

            public void Deactivate()
            {
                Interlocked.Exchange(ref owner, null);
            }
        }
    }
}
