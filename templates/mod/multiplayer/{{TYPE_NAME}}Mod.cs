using TopiaForge.Mods;

namespace {{ASSEMBLY_NAME}}
{
    /// <summary>
    /// A count every player in a session shares. In a world, any player presses a key to add one; the host owns the
    /// count, decides every press, and replicates the result, so everyone sees the same number. Solo play, offline
    /// included, runs this same code in a one-player session in which the local player is the host.
    /// </summary>
    /// <remarks>
    /// The contract id is this mod's permanent wire identity: the generated wire ids and the checked-in contract lock
    /// derive from it, and it deliberately ignores class and namespace renames. Keep it equal to the mod id, and never
    /// change it after release, because copies from before and after a change refuse to join each other.
    /// </remarks>
    [MultiplayerContract(Id = "{{MOD_ID}}")]
    public sealed partial class {{TYPE_NAME}}Mod : TopiaForgeMod
    {
        /// <summary>The named input action a player presses to add one.</summary>
        internal const string PressActionName = "add-one";

        /// <summary>
        /// The most presses the host accepts from one player each second. Ten is about as fast as a person can tap a
        /// key, so a real press is rarely refused, while a macro or a modified client cannot flood the host. The host
        /// counts against its own clock, and the limit applies to its own player too.
        /// </summary>
        internal const int MaximumPressesPerSecond = 10;

        /// <summary>
        /// The largest encoded press request or response, in bytes. The request is empty and the response is the
        /// 4-byte state, so 64, the smallest limit TopiaForge accepts, is ample. The host refuses a larger payload
        /// before decoding it.
        /// </summary>
        internal const int MaximumPressPayloadBytes = 64;

        // TopiaForge reserves F5, F8 and F10, so a mod's default binding must avoid them.
        private const string DefaultPressKey = "K";

        /// <summary>
        /// The shared count. The host's copy is canonical: a joining player receives it in their first snapshot, and
        /// later changes coalesce into the host's regular updates, at most 10 a second in a live session, however fast
        /// presses arrive. Each new session starts again from this declared default.
        /// </summary>
        [ReplicatedState("count")]
        private ReplicatedState<CounterState> count = new ReplicatedState<CounterState>(new CounterState());

        // When, in unscaled seconds, the last refusal was shown to the player.
        private double lastRefusalShownAt = double.NegativeInfinity;

        /// <inheritdoc/>
        /// <remarks>
        /// TopiaForge binds the generated contract to the session before this runs, so the count is already live
        /// here. The manifest's required dependency on the multiplayer provider guarantees a session to bind to.
        /// </remarks>
        protected override void OnLoad()
        {
            var registered = Context.Input.RegisterAction(new InputActionDefinition(
                PressActionName,
                "Add one to the shared count",
                new[] { InputBinding.Key(DefaultPressKey) }));
            if (!registered.TryGetValue(out var pressAction))
            {
                Context.Logger.Error(
                    "Input registration failed (" + registered.ErrorCode + "): " + registered.ErrorMessage);
                return;
            }

            // One HUD for the mod's whole life. It lives as long as the mod, so it survives every scene load, and as a
            // gameplay HUD TopiaForge shows it in each world and hides it at the main menu and during loads by itself.
            var created = Context.Ui.CreateSurface(new UiSurfaceRequest(
                "count",
                "{{DISPLAY_NAME}}",
                Describe(count.Value),
                UiSurfaceKind.Hud,
                width: 260f,
                height: 96f));
            if (!created.TryGetValue(out var hud))
            {
                Context.Logger.Error("HUD creation failed (" + created.ErrorCode + "): " + created.ErrorMessage);
                return;
            }

            // The HUD shows the value this process holds, which every player converges on: the presser's prediction at
            // once, the host's canonical value as it arrives, and the declared default when a new session starts.
            Context.Lifetime.Track(count.SubscribeChanged(state => hud.SetBody(Describe(state))));
            Context.Events.SubscribeUpdate(_ =>
            {
                // A press counts only where the player can see its result: in a world, with the HUD up. The host's
                // answer comes back to ShowRefusal on this thread: at once for the host's own player, solo play
                // included, and a round trip later for anyone else. Nothing waits for it, so the frame never stalls
                // (TF1008), and it never runs once this mod has stopped.
                if (hud.IsVisible && pressAction.WasPressed)
                {
                    SubmitIncrement(new IncrementRequest(), ShowRefusal);
                }
            });

            Context.Logger.Info(
                "{{DISPLAY_NAME}} loaded. In a world, press " + DefaultPressKey
                + " to add one to the count every player shares.");
        }

        /// <summary>Adds one to the shared count for the player who pressed.</summary>
        /// <remarks>
        /// <para>
        /// The request is empty on purpose: a player only says "I pressed", and the host decides what a press is worth,
        /// so a modified client cannot add more than one. The handler works from the canonical count, and refuses a
        /// press with a stable error code rather than overflow it. A command that does carry data must also check every
        /// field here, because a client can send any value the generated codec accepts; <c>command.SenderId</c> is the
        /// transport-authenticated presser, for rules that depend on who pressed.
        /// </para>
        /// <para>
        /// Owner prediction runs the press on the presser's machine at once, so their count moves without waiting a
        /// round trip. The host then runs it canonically, and a press it refuses rolls back. Predicted code may only
        /// update replicated state and build results; the generator rejects clocks, <c>System.Random</c>, new GUIDs and
        /// local services here (TFMP005, TFMP006), and <c>command.CreateRandom()</c> is the random source both runs
        /// agree on.
        /// </para>
        /// <para>
        /// The two limits declare at most 10 presses x 64 bytes = 640 bytes a second from each player, about 2 percent
        /// of the 32 KiB a second that TFMP014 budgets for each connection. That budget sums every command and
        /// replicated object in the contract, so give each new one the smallest rate and payload it really needs.
        /// </para>
        /// </remarks>
        [MultiplayerCommand(
            "increment",
            Prediction = PredictionMode.Owner,
            MaximumPerSecond = MaximumPressesPerSecond,
            MaximumPayloadBytes = MaximumPressPayloadBytes)]
        private OperationResult<CounterState> Increment(MultiplayerCommandContext command, IncrementRequest request) =>
            count.Update(current => current.Count == int.MaxValue
                ? OperationResult<CounterState>.Failure(
                    ModErrorCode.Conflict,
                    "The shared count is already at its maximum.")
                : OperationResult<CounterState>.Success(new CounterState { Count = current.Count + 1 }));

        /// <summary>Tells the presser why the host refused a press. An accepted press needs no word: the HUD shows it.</summary>
        private void ShowRefusal(MultiplayerCommandConfirmation<CounterState> answer)
        {
            var code = answer.Result.ErrorCode;
            if (answer.Result.Succeeded || code == ModErrorCode.Cancelled)
            {
                // Cancelled means withdrawn before the host answered: the connection or the session went away. The
                // HUD already shows whatever the host counted, so there is nothing to add.
                return;
            }

            // Presses faster than the limit are refused in a burst, and every mod shares one toast queue, so a toast
            // for each refusal would bury other messages for many seconds. One warning a second says enough.
            var now = Context.Time.Frame.ElapsedTime;
            if (now - lastRefusalShownAt < 1d)
            {
                return;
            }

            lastRefusalShownAt = now;
            switch (code)
            {
                case ModErrorCode.RateLimited:
                    Context.Ui.ShowToast(
                        "Too fast: the host takes at most " + MaximumPressesPerSecond + " presses a second.",
                        UiTone.Warning);
                    return;
                case ModErrorCode.InvalidState:
                    Context.Ui.ShowToast("The session is not ready yet; press again in a moment.", UiTone.Warning);
                    return;
                default:
                    Context.Ui.ShowToast("The host refused the press: " + answer.Result.ErrorMessage, UiTone.Warning);
                    return;
            }
        }

        private static string Describe(CounterState state) => "Shared count: " + state.Count;
    }
}
