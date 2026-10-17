extern alias provider;

using TopiaForge.ModManager;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods.Internal;
using provider::TopiaForge.Multiplayer.Network;
using Control = provider::TopiaForge.Multiplayer.InternalControl;
using Provider = provider::TopiaForge.Multiplayer.MultiplayerProvider;
using RoutedDiagnostics = provider::TopiaForge.Multiplayer.RoutedDiagnostics;
using RuntimeContent = provider::TopiaForge.Multiplayer.RuntimeContent;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>One package a test peer runs, with the archive hash its install receipt records.</summary>
internal sealed record PackageSpec(string Id, string Version, string Sha256, string? Mode = null, string? Presence = null)
{
    internal const string LockFile = ModMultiplayerMetadata.ContractLockFileName;

    /// <summary>A mod without a multiplayer block, shared implicitly.</summary>
    internal static PackageSpec Implicit(string id, string version, char hash) => new(id, version, new string(hash, 64));

    /// <summary>A session mod with the canonical contract lock as its synchronized file.</summary>
    internal static PackageSpec Session(string id, string version, char hash, string presence = "required") =>
        new(id, version, new string(hash, 64), ModMultiplayerMetadata.SessionMode, presence);

    internal static PackageSpec Local(string id, string version, char hash) => new(id, version, new string(hash, 64), ModMultiplayerMetadata.ClientLocalMode);

    internal static PackageSpec Standalone(string id, string version, char hash) => new(id, version, new string(hash, 64), ModMultiplayerMetadata.StandaloneMode);

    internal ModManifest ToManifest()
    {
        var manifest = new ModManifest { SchemaVersion = 6, Id = Id, Name = DisplayName, Version = Version };
        if (Mode == null) return manifest;
        manifest.Multiplayer = new ModMultiplayerMetadata { Mode = Mode };
        if (Mode != ModMultiplayerMetadata.SessionMode) return manifest;
        manifest.Multiplayer.Presence = Presence ?? ModMultiplayerMetadata.RequiredPresence;
        manifest.Multiplayer.Protocol = new ModMultiplayerProtocol { Version = "1.0.0", PeerVersionRange = ">=1.0.0 <2.0.0" };
        manifest.Multiplayer.SynchronizedFiles = new List<string> { LockFile };
        manifest.Hashes[LockFile] = new string(Sha256[0] == 'f' ? 'e' : 'f', 64);
        return manifest;
    }

    /// <summary>Gets the name players see in problems: the id's last part, capitalised.</summary>
    internal string DisplayName
    {
        get
        {
            var last = Id.Substring(Id.LastIndexOf('.') + 1);
            return char.ToUpperInvariant(last[0]) + last.Substring(1);
        }
    }

    /// <summary>Writes the installed package: a folder with an install receipt naming its archive's hash.</summary>
    internal string Install(string root)
    {
        var folder = Path.Combine(root, Id + "-" + Version + "-" + Sha256.Substring(0, 8));
        Directory.CreateDirectory(folder);
        JsonUtil.SaveFile(Path.Combine(folder, PackageInstallReceipt.FileName), new PackageInstallReceipt
        {
            ModId = Id,
            Version = Version,
            SourceFile = Id + ".topiaforgemod",
            SourceSha256 = Sha256,
            InstalledAtUtc = "2026-10-14T00:00:00.0000000Z"
        });
        return folder;
    }
}

/// <summary>Records what the runtime host logs.</summary>
internal sealed class RecordingMultiplayerLog : IMultiplayerLog
{
    internal List<string> Lines { get; } = new();

    public void Info(string message) => Lines.Add("info: " + message);

    public void Warn(string message) => Lines.Add("warn: " + message);

    public void Error(string message) => Lines.Add("error: " + message);
}

/// <summary>Records what a mod logger writes.</summary>
internal sealed class RecordingModLogger : IModLogger
{
    internal List<string> Lines { get; } = new();

    public void Debug(string message) => Lines.Add("debug: " + message);

    public void Info(string message) => Lines.Add("info: " + message);

    public void Warn(string message) => Lines.Add("warn: " + message);

    public void Error(string message) => Lines.Add("error: " + message);

    public void Error(Exception exception, string message) => Lines.Add("error: " + message + " " + exception);
}

/// <summary>A game engine's background setting, recorded.</summary>
internal sealed class RecordingPlatform : IMultiplayerPlatform
{
    internal List<bool> Changes { get; } = new();

    private bool value = true;

    public bool RunInBackground
    {
        get => value;
        set
        {
            this.value = value;
            Changes.Add(value);
        }
    }
}

/// <summary>
/// A runtime that presents its real content but skips its own check of the host's packages, as a modified game could:
/// only the host's admission then stands between it and the session.
/// </summary>
internal sealed class UncheckedRuntimeHost : IInternalMultiplayerRuntimeHost
{
    private readonly MultiplayerRuntimeHost inner;

    internal UncheckedRuntimeHost(MultiplayerRuntimeHost inner)
    {
        this.inner = inner;
    }

    public string GameBuild => inner.GameBuild;

    public string RuntimeVersion => inner.RuntimeVersion;

    public string DisplayName => inner.DisplayName;

    public IReadOnlyList<InternalContentDescriptor> LocalContent => inner.LocalContent;

    public InternalContentVerdict EvaluateAdmission(InternalContentOffer offer) => inner.EvaluateAdmission(offer);

    public InternalContentVerdict CheckHostContent(IReadOnlyList<InternalHostPackage> hostPackages) => InternalContentVerdict.Admitted;

    public void SetRunInBackground(bool enabled) => inner.SetRunInBackground(enabled);

    public void ReportDiagnostic(InternalMultiplayerDiagnostic diagnostic) => inner.ReportDiagnostic(diagnostic);

    public string? ServiceOrigin => inner.ServiceOrigin;
}

/// <summary>
/// One peer as the game composes it: the provider with live sessions on the deterministic network, its internal
/// control, and ModManager's runtime host and composition root attached to it.
/// </summary>
internal sealed class ControlPeer
{
    internal ControlPeer(
        string name,
        VirtualDatagramNetwork network,
        SteppedResolver resolver,
        string packageRoot,
        IReadOnlyList<PackageSpec> packages,
        bool live = true,
        LiveJoinTimings? timings = null,
        string gameBuild = "2545")
    {
        Name = name;
        Factory = new SteppedNetworkFactory(network, name);
        var content = new RuntimeContent(gameBuild, "0.2.0");
        var diagnostics = new RoutedDiagnostics(null);
        var setup = live ? new LiveSessionsSetup(Files, content) { Factory = Factory, Resolver = resolver, JoinTimings = timings } : null;
        Provider = new Provider(LiveRig.ProviderId, diagnostics, () => network.Now, setup);
        Control = new Control(Provider, content, diagnostics);
        Runtime = new MultiplayerRuntimeHost(gameBuild, "0.2.0", Log, Platform);
        foreach (var package in packages) Runtime.Inventory.Add(package.ToManifest(), package.Install(Path.Combine(packageRoot, name)));
        Composition = new MultiplayerRuntime(() => Control, Runtime, Log);
    }

    internal string Name { get; }
    internal SteppedNetworkFactory Factory { get; }
    internal MemoryDataFiles Files { get; } = new();
    internal Provider Provider { get; }
    internal Control Control { get; }
    internal MultiplayerRuntimeHost Runtime { get; }
    internal MultiplayerRuntime Composition { get; }
    internal RecordingMultiplayerLog Log { get; } = new();
    internal RecordingPlatform Platform { get; } = new();

    /// <summary>Gets the provider's session as a consuming mod sees it.</summary>
    internal IMultiplayerSession Session => Provider;

    /// <summary>Gets the runtime a test attached in place of the composed one, if any.</summary>
    internal IInternalMultiplayerRuntimeHost? Replacement { get; private set; }

    /// <summary>Attaches <paramref name="runtime"/> to the control in place of the composed runtime; frames then pump the control directly.</summary>
    internal void UseRuntime(IInternalMultiplayerRuntimeHost runtime)
    {
        Composition.Disconnect();
        if (!Control.AttachRuntime(runtime)) throw new InvalidOperationException("Assertion failed: the replacement runtime attaches");
        Replacement = runtime;
    }

    /// <summary>Runs one frame the way ModRuntime does: the composition root around an empty mod update.</summary>
    internal void Frame()
    {
        if (Replacement != null)
        {
            Control.Pump();
            Control.Flush();
            return;
        }

        Composition.BeforeModsUpdate(0.016f);
        Composition.AfterModsUpdate(0.016f);
        Composition.BeforeModsLateUpdate(new GameTimeSample(GameLoopPhase.Late, 0.016f, 0.016f, 0d, 0));
    }

    internal ParticipantId IdOf(string displayName) =>
        Session.Snapshot.Participants.Single(participant => participant.DisplayName == displayName).Id;

    internal IInternalJoinTarget Target(string text = "host.test:27250")
    {
        if (!Control.TryCreateJoinTarget(text, out var target)) throw new InvalidOperationException("Assertion failed: the target parses");
        return target!;
    }
}

/// <summary>A listen host and joining peers on one deterministic network, each composed as the game composes it.</summary>
internal sealed class ControlRig : IDisposable
{
    private readonly string packageRoot = Directory.CreateTempSubdirectory("TopiaForgeControlRig-").FullName;

    internal ControlRig(int seed, params PackageSpec[] hostPackages)
        : this(seed, "2545", hostPackages)
    {
    }

    /// <summary>Creates a rig whose host announces <paramref name="hostBuild"/>, as a host on another build, or a hostile one, would.</summary>
    internal ControlRig(int seed, string hostBuild, params PackageSpec[] hostPackages)
    {
        Network = new VirtualDatagramNetwork(seed);
        Host = Add("host", hostPackages, hostBuild);
        Resolver.Host = () => Host.Provider.Live!.Host?.Descriptor is { } descriptor && Host.Factory.Networks.Count > 0
            ? (Host.Factory.Networks[^1].Node, descriptor)
            : null;
    }

    internal VirtualDatagramNetwork Network { get; }
    internal SteppedResolver Resolver { get; } = new();
    internal ControlPeer Host { get; }
    internal List<ControlPeer> Peers { get; } = new();

    /// <summary>Gets the folder the peers' installed packages live in, for a peer a test composes itself.</summary>
    internal string PackageRoot => packageRoot;

    /// <summary>Gets or sets how long the peers added from now on wait for their host; null uses the production waits.</summary>
    internal LiveJoinTimings? Timings { get; set; }

    internal ControlPeer AddPeer(string name, params PackageSpec[] packages) => Add(name, packages, "2545");

    /// <summary>
    /// Runs the network and every peer's frame until <paramref name="condition"/> holds or virtual time runs out, a frame
    /// every <paramref name="frameMicros"/>; minutes of virtual time run at a coarser frame.
    /// </summary>
    internal bool Run(Func<bool> condition, long maximumMicros = 10_000_000, long frameMicros = 5_000)
    {
        var deadline = Network.Now + maximumMicros;
        var spins = 0;
        while (Network.Now < deadline)
        {
            Host.Frame();
            foreach (var peer in Peers) peer.Frame();
            if (condition()) return true;

            // Identity loading and password keys finish on background threads; give them a moment of real time.
            if (++spins % 20 == 0) Thread.Sleep(1);
            Network.RunFor(frameMicros);
        }

        return condition();
    }

    /// <summary>Opens the host's session through the SPI and waits until it serves.</summary>
    internal void StartHosting(InternalHostSettings settings)
    {
        var started = Host.Control.StartHosting(settings);
        if (!started.Succeeded) throw new InvalidOperationException("Assertion failed: hosting starts (" + started.ErrorMessage + ")");
        if (!Run(() => Host.Control.GetStatus().HostPhase == InternalHostPhase.Hosting)) throw new InvalidOperationException("Assertion failed: the host serves");
    }

    /// <summary>Joins <paramref name="peer"/> through the SPI and waits for the session.</summary>
    internal void JoinAndWait(ControlPeer peer, InternalJoinSettings? settings = null)
    {
        var joined = peer.Control.Join(peer.Target(), settings ?? new InternalJoinSettings());
        if (!joined.Succeeded) throw new InvalidOperationException("Assertion failed: the join starts (" + joined.ErrorMessage + ")");
        if (!Run(() => peer.Control.GetStatus().JoinPhase == InternalJoinPhase.InSession))
        {
            var status = peer.Control.GetStatus();
            throw new InvalidOperationException("Assertion failed: " + peer.Name + " joins (" + status.JoinPhase + ", " + status.JoinFailure + ": " + status.JoinDetail + ")");
        }
    }

    public void Dispose()
    {
        foreach (var peer in Peers)
        {
            peer.Composition.Disconnect();
            peer.Control.Dispose();
            peer.Provider.Dispose();
        }

        Host.Composition.Disconnect();
        Host.Control.Dispose();
        Host.Provider.Dispose();
        try
        {
            Directory.Delete(packageRoot, true);
        }
        catch (IOException)
        {
            // A temporary folder that cannot go now goes with the machine's next clean-up.
        }
    }

    private ControlPeer Add(string name, PackageSpec[] packages, string gameBuild)
    {
        var peer = new ControlPeer(name, Network, Resolver, packageRoot, packages, timings: Timings, gameBuild: gameBuild);
        if (!peer.Composition.Connect()) throw new InvalidOperationException("Assertion failed: the runtime attaches to " + name);
        if (name != "host") Peers.Add(peer);
        return peer;
    }
}
