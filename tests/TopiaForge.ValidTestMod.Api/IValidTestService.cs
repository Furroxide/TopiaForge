namespace TopiaForge.ValidTestMod
{
    /// <summary>Contract fixture for a service mod's separately exported API assembly.</summary>
    public interface IValidTestService
    {
        /// <summary>Returns the supplied message.</summary>
        string Ping(string message);
    }
}
