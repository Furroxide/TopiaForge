using System;
using System.IO;
using System.Runtime.Serialization;
using System.Xml;

namespace TopiaForge.ModManager.Core
{
    /// <summary>A malformed latest document is preserved; recovery never grants permission to replace it.</summary>
    public sealed class ManagerStateStartupStore
    {
        private readonly string path;
        private ManagerStateStartupStore(string path, ManagerState state, string failure)
        { this.path = path; State = state; Failure = failure; }
        public ManagerState State { get; }
        public string Failure { get; }
        public bool CanSave => Failure.Length == 0;
        public static ManagerStateStartupStore Open(string path)
        {
            try
            {
                var state = ManagerStateLaunchPersistence.Load(path);
                state.Normalize();
                return new ManagerStateStartupStore(path, state, string.Empty);
            }
            catch (Exception error) when (error is InvalidDataException || error is SerializationException
                || error is XmlException || error is FormatException || error is ArgumentException)
            {
                var state = ManagerStateLaunchPersistence.Parse("{}"); state.Normalize();
                return new ManagerStateStartupStore(path, state,
                    "Manager state needs repair and remains read-only: " + path + ". " + error.Message);
            }
        }
        public bool Save()
        {
            if (!CanSave) return false;
            JsonUtil.SaveJsonObject(path, ManagerStateLaunchPersistence.Serialize(State));
            return true;
        }
    }
}
