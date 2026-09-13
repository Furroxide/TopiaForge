using TopiaForge.Acceptance.Windows;

if (args.Length == 1 && args[0] == "--self-test")
{
    try { BrokerContractTests.Run(); LoopbackCaptureTests.Run(); ProvisioningContractTests.Run(); return 0; }
    catch (Exception error) { Console.Error.WriteLine("Broker contract test failed: " + error.Message); return 1; }
}
if (args.Length == 3 && args[0] == "--fixture-shutdown")
{
    try
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) throw new PlatformNotSupportedException("Windows x64 is required.");
        return ProvisioningFixtureShutdown.Run(args[1], args[2]);
    }
    catch (Exception error)
    {
        Console.Error.WriteLine("Fixture shutdown verification refused or stopped (" + error.GetType().Name + "): " + error.Message);
        return 1;
    }
}
if (args.Length != 2 || args[0] is not ("--request" or "--probe" or "--observe-runtime"))
{
    Console.Error.WriteLine("Usage: TopiaForge.Acceptance.Windows --request <private-request.json> | --probe <new-private-observation.json> | --observe-runtime <private-launch.json> | --fixture-shutdown <fixture-player.exe> <new-run-directory> | --self-test");
    return 64;
}
try
{
    if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) throw new PlatformNotSupportedException("Windows x64 is required.");
    if (!NativeMethods.SetProcessDpiAwarenessContext(new IntPtr(-4))) throw new InvalidOperationException("Per-monitor DPI awareness could not be established.");
    if (args[0] == "--probe")
    {
        var identity = WindowsIdentityProbe.Read(NativeMethods.GetCurrentProcess());
        WindowsIdentityProbe.RequireInteractiveDesktop(identity.SessionId);
        BoundedJson.WriteNew(args[1], new
        {
            schemaVersion = 1, kind = "sandbox-qa-host-observation-v1", observedAtUtc = DateTime.UtcNow,
            identity, defaultRenderEndpointId = LoopbackCapture.DescribeDefaultEndpoint(),
            microphoneOpened = false, isolationAdmitted = false
        });
        Console.WriteLine("Private host observation saved; this is not an isolation approval.");
        return 0;
    }
    if (args[0] == "--observe-runtime") return ProvisioningLauncher.Run(args[1]);
    var request = BrokerRequest.Read(args[1]);
    var profile = DeviceProfile.Read(request.DeviceProfilePath);
    var manifest = new DriverManifest(request.DriverManifestPath);
    if (!Directory.Exists(request.OutputRoot)) throw new InvalidDataException("Admitted run output does not exist.");
    foreach (var name in new[] { "broker-result.json", "broker-transcript.json" })
        if (File.Exists(BoundedJson.Child(request.OutputRoot, name))) throw new InvalidDataException("Immutable broker output already exists.");
    using var game = new OwnedGameProbe(request);
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(request.TimeoutSeconds));
    using var gracefulCancellation = new BrokerCancellation(request, cancellation);
    Console.CancelKeyPress += (_, item) => { item.Cancel = true; cancellation.Cancel(); };
    await using var pipe = new NativePipe(request, game);
    // Driver owns input cleanup, including its finally path after cancellation.
    var input = new WindowsInput(game, profile);
    var transcript = new BrokerTranscript(request);
    var driver = new BrokerDriver(request, profile, manifest, transcript, game, pipe, input);
    await driver.Execute(cancellation.Token);
    Console.WriteLine("Native observations retained. The independent verifier must evaluate them.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("Native broker refused or stopped (" + error.GetType().Name + "). Review the private run evidence.");
    return 1;
}
