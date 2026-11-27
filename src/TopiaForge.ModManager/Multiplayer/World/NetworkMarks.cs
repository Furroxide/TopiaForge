namespace TopiaForge.ModManager
{
    /// <summary>
    /// How many network marks carry an id now, in the whole process. Every mark counts itself in when it is given an id
    /// and out when the id goes or the mark is destroyed, so a scene census only looks for marks when one may exist:
    /// offline and in single player there are none, and a scene's load skips the search. A mark the engine destroys
    /// without telling it, as it does one that never woke, stays counted, which only costs the census its shortcut; none
    /// is ever left out. Main thread only.
    /// </summary>
    internal static class NetworkMarks
    {
        private static int live;

        /// <summary>Gets how many marks are counted as carrying an id.</summary>
        internal static int Live => live;

        /// <summary>Gets whether any mark may carry an id, so a census must look for marks.</summary>
        internal static bool AnyLive => live > 0;

        /// <summary>Counts a mark's id changing from <paramref name="previous"/> to <paramref name="next"/>; zero is no id.</summary>
        internal static void Changed(uint previous, uint next)
        {
            if (previous == 0 && next != 0) live++;
            else if (previous != 0 && next == 0 && live > 0) live--;
        }
    }
}
