using System.Collections.Concurrent;
using TopiaForge.ModManager;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The in-game screens over one player composed as the game composes it: the runtime and its realm over the real
/// provider, the launcher's channels in a real staging store, and the background queue on worker threads, as in the
/// game, so this thread is the frame's. Live joins are polled on a pool thread when a test says, as the intake's timer
/// polls them.
/// </summary>
internal sealed class PeerScreens : IMultiplayerSessionEnvironment, IDisposable
{
    private readonly IInternalMultiplayerControl control;
    private readonly MultiplayerRuntime composition;
    private readonly MultiplayerRuntimeHost runtime;
    private readonly IMultiplayerSession session;
    private Action<bool, string>? menu;

    private PeerScreens(IInternalMultiplayerControl control, MultiplayerRuntime composition, MultiplayerRuntimeHost runtime, IMultiplayerSession session,
        string? requestId, string? packageSetDigest, string? serviceOrigin)
    {
        this.control = control;
        this.composition = composition;
        this.runtime = runtime;
        this.session = session;
        composition.SessionPhaseSource = () => Phase;
        Root = Directory.CreateTempSubdirectory("TopiaForgeLaunchedSession-").FullName;
        var paths = new ManagerPaths(Root);
        paths.EnsureCreated();
        Store = new LaunchStagingStore(paths);
        Channels = new NotingChannels(new StagingLauncherChannels(Store, requestId, packageSetDigest, serviceOrigin));
        Presenter = new MultiplayerSessionsPresenter(this, Settings, MultiplayerWorkSchedulers.Background);
        Presenter.Start();
        Presenter.Intake?.Dispose();
    }

    internal string Root { get; }

    internal LaunchStagingStore Store { get; }

    internal NotingChannels Channels { get; }

    internal HeldSettings Settings { get; } = new();

    internal MultiplayerSessionsPresenter Presenter { get; }

    /// <summary>Gets or sets the local session's phase, as the orchestrator's realm phase reads it.</summary>
    internal SessionPhase Phase { get; set; } = SessionPhase.Idle;

    internal List<string> Notices { get; } = new();

    internal List<string> Opened { get; } = new();

    /// <summary>Gets or sets the session this game was launched to host or join, which the screens take once.</summary>
    internal ILaunchedSession? Launched { get; set; }

    /// <summary>Gets whether the screens asked the game to return to the main menu, which it has not done yet.</summary>
    internal bool MenuRequested => menu != null;

    public IInternalMultiplayerControl? Control => control;

    public NetRealm Realm => composition.Realm;

    public IReadOnlyList<string> StandaloneMods => composition.StandaloneMods;

    public MultiplayerSessionSnapshot? SessionSnapshot => session.Snapshot;

    public InternalContentReadiness ContentReadiness => runtime.ContentReadiness;

    public string DisplayName
    {
        get => runtime.PreferredDisplayName;
        set => runtime.PreferredDisplayName = value;
    }

    public string CurrentGameText => string.Empty;

    public HostTargetChoice? CurrentGame => null;

    public string GameBuild => "2545";

    public string RuntimeVersion => "0.2.0";

    public long UnixSeconds => 1_800_000_000;

    public ILauncherChannels? Launcher => Channels;

    public SessionPhase LocalPhase => Phase;

    internal static PeerScreens Of(ControlPeer peer, string? requestId = null, string? packageSetDigest = null, string? serviceOrigin = null) =>
        new(peer.Control, peer.Composition, peer.Runtime, peer.Session, requestId, packageSetDigest, serviceOrigin);

    internal static PeerScreens Of(InternetPeer peer, string? requestId = null, string? packageSetDigest = null, string? serviceOrigin = null) =>
        new(peer.Control, peer.Composition, peer.Runtime, peer.Provider, requestId, packageSetDigest, serviceOrigin);

    /// <summary>The screens' part of a frame, and the toasts they raised.</summary>
    internal void Frame()
    {
        Presenter.Tick(0.016f);
        while (Presenter.TryTakeNotice(out var notice)) Notices.Add(notice.Text);
    }

    /// <summary>Looks for a staged live join on a pool thread, as the intake's timer does.</summary>
    internal void PollJoins() => Task.Run(() => Presenter.Intake!.Poll()).Wait();

    /// <summary>The game finishes the return to the main menu the screens asked for.</summary>
    internal void ReturnedToMenu()
    {
        Phase = SessionPhase.Idle;
        var done = menu;
        menu = null;
        done?.Invoke(true, "Returned to the main menu.");
    }

    public NetRealmTransition EvaluateRealmChange(NetRealm target) => composition.EvaluateRealmChange(target);

    public MultiplayerRealmChange OpenToMultiplayer(InternalHostSettings settings) => composition.OpenToMultiplayer(settings);

    public MultiplayerRealmChange Join(IInternalJoinTarget target, InternalJoinSettings settings) => composition.Join(target, settings);

    public MultiplayerRealmChange Leave() => composition.Leave();

    public void RetryContentIdentification() => runtime.Inventory.RetryIdentification();

    public IReadOnlyList<HostTargetChoice> ReadHostTargets() => Array.Empty<HostTargetChoice>();

    public void Launch(HostTargetChoice choice, Action<bool, string> done) => done(false, "No game starts in these tests.");

    public void ReturnToMainMenu(Action<bool, string> done) => menu = done;

    public void OpenUrl(string url) => Opened.Add(url);

    public void CopyToClipboard(string text)
    {
    }

    public ILaunchedSession? TakeLaunchedSession()
    {
        var launched = Launched;
        Launched = null;
        return launched;
    }

    /// <summary>Gets every text this game left for the launcher and the player: the staged files and the toasts.</summary>
    internal IEnumerable<string> Written() =>
        Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Select(File.ReadAllText).Concat(Notices).Concat(Opened);

    public void Dispose()
    {
        Presenter.Stop();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>The staging store's channels, noting the name and the thread of every call.</summary>
internal sealed class NotingChannels : ILauncherChannels
{
    private readonly ILauncherChannels inner;

    internal NotingChannels(ILauncherChannels inner)
    {
        this.inner = inner;
    }

    internal ConcurrentQueue<(string Call, int Thread)> Calls { get; } = new();

    public string? RequestId => inner.RequestId;

    public string? PackageSetDigest => inner.PackageSetDigest;

    public string? ServiceOrigin => inner.ServiceOrigin;

    public MultiplayerPreferencesProjection? ReadPreferences() => Note(nameof(ReadPreferences), inner.ReadPreferences);

    public MultiplayerPreferencesJournal? ReadPreferencesJournal() => Note(nameof(ReadPreferencesJournal), inner.ReadPreferencesJournal);

    public MultiplayerPreferencesJournal RecordPreferenceChange(Func<int, MultiplayerPreferenceChange> change) =>
        Note(nameof(RecordPreferenceChange), () => inner.RecordPreferenceChange(change));

    public void WriteHandoff(SessionHandoff handoff) => Note(nameof(WriteHandoff), () => inner.WriteHandoff(handoff));

    public SessionJoinRequest? TakeJoin() => Note(nameof(TakeJoin), inner.TakeJoin);

    public void WriteProgress(LaunchProgress progress) => Note(nameof(WriteProgress), () => inner.WriteProgress(progress));

    public void WriteOutcome(LaunchOutcome outcome) => Note(nameof(WriteOutcome), () => inner.WriteOutcome(outcome));

    private T Note<T>(string call, Func<T> run)
    {
        Calls.Enqueue((call, Environment.CurrentManagedThreadId));
        return run();
    }

    private void Note(string call, Action run)
    {
        Calls.Enqueue((call, Environment.CurrentManagedThreadId));
        run();
    }
}

/// <summary>This game's own settings, kept in memory; the background queue loads and saves them.</summary>
internal sealed class HeldSettings : IMultiplayerSettingsStore
{
    private MultiplayerSettings settings = MultiplayerSettings.Default;

    public MultiplayerSettings Load() => Volatile.Read(ref settings);

    public void Save(MultiplayerSettings value) => Volatile.Write(ref settings, value);
}

/// <summary>
/// A launched session's records under its request, as the runtime's publisher writes them: progress and outcomes V2 in
/// the staging store, under one sequence, with no gamemode session behind them.
/// </summary>
internal sealed class StoredLaunch : ISessionLaunchSink
{
    private readonly LaunchStagingStore store;
    private readonly LaunchSessionIdentity identity;
    private int sequence = -1;

    internal StoredLaunch(LaunchStagingStore store, LaunchSessionIdentity identity)
    {
        this.store = store;
        this.identity = identity;
    }

    public bool HasResult => Outcome != null;

    internal List<LaunchProgressSession> Blocks { get; } = new();

    internal SessionLaunchResult? Outcome { get; private set; }

    internal LaunchOutcomeMultiplayer? End { get; private set; }

    public void Progress(LaunchProgressSession block, string? phase)
    {
        Blocks.Add(block);
        store.WriteProgress(new LaunchProgress(identity.RequestId, ++sequence, phase ?? "idle", null, null, block));
    }

    public void Result(SessionLaunchResult result, string? phase)
    {
        if (Outcome != null) return;
        Outcome = result;
        var at = result.Status == "succeeded" ? "idle" : phase ?? "idle";
        store.WriteOutcome(new LaunchOutcome("launch", identity.RequestId, ++sequence, at, result.Status, Array.Empty<LaunchBlock>(), null, identity.Command,
            result.Error, result.Multiplayer));
    }

    public void Ended(LaunchOutcomeMultiplayer block, bool clean)
    {
        End = block;
        store.WriteOutcome(new LaunchOutcome("session", identity.RequestId, ++sequence, "idle", clean ? "succeeded" : "failed", Array.Empty<LaunchBlock>(),
            null, null, null, block));
    }

    /// <summary>Says how the launch went, for a failed assertion: never an address, since none is held here.</summary>
    public override string ToString() =>
        Outcome == null ? "unsettled (" + string.Join(", ", Blocks.Select(block => block.State)) + ")"
            : Outcome.Status + " " + (Outcome.Multiplayer.Failure ?? Outcome.Error?.Code ?? string.Empty) + ": " + (Outcome.Multiplayer.Detail ?? Outcome.Error?.Message);
}
