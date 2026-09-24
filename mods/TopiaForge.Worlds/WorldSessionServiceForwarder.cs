using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
namespace TopiaForge.Worlds
{
    internal sealed class WorldSessionServiceForwarder : IWorldSessionService, IOwnerContextBoundExtensionFactory
    {
        private readonly IWorldSessionService sessions;
        internal WorldSessionServiceForwarder(IWorldSessionService sessions) { this.sessions = sessions; }
        public WorldSessionSnapshot Current => sessions.Current;
        public event Action<WorldSessionSnapshot> StateChanged { add => sessions.StateChanged += value; remove => sessions.StateChanged -= value; }
        object IOwnerContextBoundExtensionFactory.CreateOwnerFacade(Type contractType, IModContext context)
        {
            if (contractType != typeof(IWorldSessionService) || !(context is IInternalWorldSessionContext runtime))
                throw new ArgumentException("The session observer requires its consuming runtime context.");
            return runtime.Sessions;
        }
    }
}
