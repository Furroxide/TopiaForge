using System;
using System.Collections.Generic;
using TopiaForge.Mods;
namespace TopiaForge.Worlds
{
    internal interface ILocalWorldImportHost : IDisposable
    {
        bool IsAvailable { get; }
        bool CanScanFolder { get; }
        string GetDefaultImportFolder();
        IReadOnlyList<RoboWorldFile> ScanFolder(string folder);
        OperationResult<ILocalImportTransaction> Prepare(RoboWorldImportPlan plan,
            IReadOnlyList<WorldAssetOverride> overrides, WorldSceneIdentity scene);
    }
    internal interface ILocalImportTransaction : IDisposable
    {
        bool NativeEntered { get; }
        bool NativeReturned { get; }
        OperationResult<IDisposable> Import();
    }

    internal sealed class RoboWorldFile
    {
        public RoboWorldFile(string path, string fileName, string projectName, string loadError)
        { Path = path; FileName = fileName; ProjectName = projectName; LoadError = loadError; }
        public string Path { get; }
        public string FileName { get; }
        public string ProjectName { get; }
        public string LoadError { get; }
    }
}
