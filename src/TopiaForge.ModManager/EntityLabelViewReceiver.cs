using System;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal interface IEntityLabelViewReceiver
    {
        void FollowEntityView(Func<IEntity, bool> visible);
    }
}
