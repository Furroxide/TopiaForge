namespace TopiaForge.Acceptance.Windows;

/// Windows read leases deny writes/deletes throughout the owned observation.
internal sealed class ProvisioningInputLease : IDisposable
{
    private readonly List<FileStream> streams = new();
    internal ProvisioningInputLease(ProvisioningLaunch input)
    {
        try
        {
            Add(input.InputPath, input.InputSha256, 4 * 1024 * 1024);
            long totalBytes = 0;
            foreach (var file in input.Files)
                totalBytes = ProvisioningGameInputs.AddLength(totalBytes,
                    Add(ProvisioningGameInputs.Child(input.GameRoot, file.Path), file.Sha256,
                        Math.Min(ProvisioningGameInputs.MaximumFileBytes, ProvisioningGameInputs.MaximumTotalBytes - totalBytes)));
            foreach (var name in new[] { "Robotopia.exe", "UnityPlayer.dll" })
                Add(Path.Combine(input.SourceGameRoot, name), input.Files.Single(file => file.Path == name).Sha256, ProvisioningGameInputs.MaximumFileBytes);
        }
        catch { Dispose(); throw; }
    }
    private long Add(string path, string digest, long maximum)
    {
        BoundedJson.PhysicalPath(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        streams.Add(stream);
        if (stream.Length > maximum || Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream)) != digest)
            throw new InvalidDataException("Provisioning input changed before acquiring its read lease.");
        return stream.Length;
    }
    public void Dispose() { foreach (var stream in streams) stream.Dispose(); streams.Clear(); }
}
