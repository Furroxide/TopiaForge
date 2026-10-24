using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.ValidTestMod
{
    /// <summary>
    /// A gamemode's package that uses Chronos (WS3.13). It hands the runtime tests each context it loads in, so a test can
    /// make its gamemode server and participant scopes from it and resolve time control through each, as the gamemode's
    /// controllers do, then drive time from there.
    /// </summary>
    public sealed class RuntimeTimeConsumerMod : TopiaForgeMod
    {
        private static readonly List<IModContext> Loaded = new List<IModContext>();

        /// <summary>Returns the contexts this mod has loaded in since the last call, in load order, and forgets them.</summary>
        public static IModContext[] TakeLoaded()
        {
            var taken = Loaded.ToArray();
            Loaded.Clear();
            return taken;
        }

        protected override void OnLoad() => Loaded.Add(Context);
    }
}
