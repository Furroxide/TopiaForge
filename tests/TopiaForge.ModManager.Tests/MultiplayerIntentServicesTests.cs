using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.MultiplayerModelAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The sources of intent in real mod contexts (WS3.4): input-active frames through the input decorator, scheduled
    /// callbacks through the scheduler decorator, interactions through the interaction decorator, and commands through the
    /// command service, each as its mod's replication seam reports it, and the frames' cost.
    /// </summary>
    internal static class MultiplayerIntentServicesTests
    {
        private const string Builder = "io.example.builder";
        private const string Neighbour = "io.example.neighbour";

        internal static void Run(string root)
        {
            InputActiveFramesCarryTheModsOwnIntent(Path.Combine(root, "frames"));
            SessionScopesInputCountsForTheirMod(Path.Combine(root, "scopes"));
            InputActionsPassThroughAndEndWithTheirLifetime(Path.Combine(root, "input"));
            ADisposeTheHostRefusesChangesNothing();
            ScheduledCallbacksCaptureWhereTheyWereScheduled(Path.Combine(root, "scheduler"));
            InteractionsRunWithTheInteractionIntent(Path.Combine(root, "interactions"));
            InputActiveFramesAllocateNothing(Path.Combine(root, "allocation"));
            Console.WriteLine("MultiplayerIntentServicesTests passed.");
        }

        private static void InputActiveFramesCarryTheModsOwnIntent(string root)
        {
            using var world = new IntentWorld(root, InternalSessionRole.Client, Builder, Neighbour);
            var builder = world.Contexts[0];
            var neighbour = world.Contexts[1];
            var fire = Fire(builder);
            var trace = new List<string>();
            builder.Events.SubscribeUpdate(_ => trace.Add("update:" + Current(builder)));
            builder.Events.SubscribeLateUpdate(_ => trace.Add("late:" + Current(builder)));
            builder.Events.SubscribeFixedUpdate(_ => trace.Add("fixed:" + Current(builder)));
            neighbour.Events.SubscribeUpdate(_ => trace.Add("neighbour:" + Current(neighbour)));
            var action = world.Host.Action(Builder, "fire");

            world.Frame(trace, "quiet");
            action.Hold(true);
            world.Frame(trace, "pressed");
            world.Frame(trace, "held");
            action.Hold(false);
            world.Frame(trace, "released");
            world.Frame(trace, "quiet");
            Sequence(new[]
            {
                "frame:quiet", "fixed:none", "update:none", "neighbour:none", "late:none",
                "frame:pressed", "fixed:none", "update:InputActiveFrame/local", "neighbour:none", "late:InputActiveFrame/local",
                "frame:held", "fixed:none", "update:InputActiveFrame/local", "neighbour:none", "late:InputActiveFrame/local",
                "frame:released", "fixed:none", "update:InputActiveFrame/local", "neighbour:none", "late:InputActiveFrame/local",
                "frame:quiet", "fixed:none", "update:none", "neighbour:none", "late:none"
            }, trace, "an edge or a held action makes its mod's Update and LateUpdate input-active; FixedUpdate, a later quiet frame " +
                      "and another mod's Update in the same frame never are");
            True(world.Intents.Scope.Flowing == null, "nothing flows between frames");
            GC.KeepAlive(fire);

            // Offline the same input opens nothing.
            world.GoOffline();
            trace.Clear();
            action.Hold(true);
            world.Frame(trace, "offline");
            Sequence(new[] { "frame:offline", "fixed:none", "update:none", "neighbour:none", "late:none" }, trace,
                "offline every route is canonical, so input opens no scope");
        }

        private static void SessionScopesInputCountsForTheirMod(string root)
        {
            using var world = new IntentWorld(root, InternalSessionRole.ListenHost, Builder);
            var builder = world.Contexts[0];
            using var dispatcher = new HostDispatcher();
            var scope = world.Scope(builder, "session-1", dispatcher, ModExecutionContext.GamemodeServer);
            var aim = scope.Context.Input.RegisterAction(new InputActionDefinition("aim", "Aim", new[] { InputBinding.Key("Q") })).Value!;
            var trace = new List<string>();
            builder.Events.SubscribeUpdate(_ => trace.Add("package:" + Current(builder)));
            scope.Context.Events.SubscribeUpdate(_ => trace.Add("scope:" + Current(scope.Context)));
            world.Host.Action(Builder, "aim").Hold(true);
            world.Frame(trace, "aiming");
            Sequence(new[] { "frame:aiming", "package:InputActiveFrame/local", "scope:InputActiveFrame/local" }, trace,
                "an action a session scope registers makes the whole mod's frame input-active, on a host too");
            True(builder.HasActiveInput, "the mod's activity counts the scope's action");
            scope.BeginStop();
            scope.Dispose();
            True(!builder.HasActiveInput && world.Host.Action(Builder, "aim").Disposed, "and the scope's end ends the action and its count");
            GC.KeepAlive(aim);
        }

        private static void InputActionsPassThroughAndEndWithTheirLifetime(string root)
        {
            using var world = new IntentWorld(root, InternalSessionRole.Client, Builder);
            var builder = world.Contexts[0];
            var service = (NetInputService)builder.Input;
            True(service.Inner != null && !service.IsUiFocused && service.GetConflicts().Count == 0, "the input service passes its reads through");
            var action = Fire(builder);
            var fake = world.Host.Action(Builder, "fire");
            True(action is NetInputAction tracked && ReferenceEquals(tracked.Inner, fake), "the mod's action reads the host's");
            fake.Hold(true);
            world.Host.BeginFrame();
            True(action.Name == "fire" && action.IsHeld && action.WasPressed && !action.WasReleased && action.Value == 1f && action.Bindings.Count == 1,
                "every member is the host's action's own");
            True(action.Rebind(new[] { InputBinding.Key("E") }).Succeeded && fake.Bindings[0].Control == "E" && action.ResetBindings().Succeeded,
                "and rebinding reaches it");
            True(builder.HasActiveInput, "a held action makes its mod's input active");
            fake.ThrowOnRead = true;
            True(!builder.HasActiveInput, "an action that cannot be read counts as idle");
            var trace = new List<string>();
            builder.Events.SubscribeUpdate(_ => trace.Add(Current(builder)));
            world.Frame(trace, "unreadable");
            Sequence(new[] { "frame:unreadable", "none" }, trace, "and its mod's Update still runs");
            fake.ThrowOnRead = false;
            action.Dispose();
            True(fake.Disposed && !builder.HasActiveInput, "disposing the mod's action ends the host's and stops counting it");
            action.Dispose();

            var second = builder.Input.RegisterAction(new InputActionDefinition("jump", "Jump", new[] { InputBinding.Key("J") })).Value!;
            world.Host.Action(Builder, "jump").Hold(true);
            world.Host.BeginFrame();
            True(builder.HasActiveInput, "a second action counts");
            builder.BeginStopping();
            var refused = builder.Input.RegisterAction(new InputActionDefinition("late", "Late", new[] { InputBinding.Key("L") }));
            True(!refused.Succeeded && refused.ErrorCode == ModErrorCode.Cancelled && world.Host.Action(Builder, "late").Disposed,
                "an action registered as the mod stops is refused, and the host's is ended");
            world.Release(builder);
            True(world.Host.Action(Builder, "jump").Disposed && !builder.HasActiveInput, "the end of the lifetime ends every action and its count");
            GC.KeepAlive(second);
        }

        /// <summary>
        /// The game's own action refuses a dispose off the main thread and says to dispose it there instead. The decorator
        /// leaves everything as it was until the host's action agrees, its activity list and its lifetime lease included, so
        /// the list is only ever the main thread's, the retry the error asks for ends the action, and so does the mod's
        /// unload when it never retries.
        /// </summary>
        private static void ADisposeTheHostRefusesChangesNothing()
        {
            var lifetime = new OwnerModLifetime();
            var activity = new ModInputActivity();
            var host = new MainThreadAction();
            var action = Tracked(host, activity, lifetime);
            var refused = DisposeOffTheMainThread(action);
            True(refused != null && !host.Disposed, "the game's action refuses a dispose off the main thread");
            True(activity.Count == 1, "and the refused dispose leaves the action counted, without touching the activity list off the main thread");
            action.Dispose();
            True(host.Disposed && activity.Count == 0, "so disposing it again on the main thread, as the error says to, ends it and stops counting it");
            True(DisposeOffTheMainThread(action) == null, "and once it has ended, disposing it again, from any thread, changes nothing");

            // A mod that never retries: the refused dispose left the action in its lifetime, so the mod's unload ends it.
            var abandoned = new MainThreadAction();
            var kept = Tracked(abandoned, activity, lifetime);
            True(DisposeOffTheMainThread(kept) != null && activity.Count == 1, "another action's refused dispose leaves it counted");
            lifetime.Dispose();
            True(abandoned.Disposed && activity.Count == 0, "and the mod's unload still ends it, since the refusal left it in its lifetime");
        }

        private static NetInputAction Tracked(IInputAction host, ModInputActivity activity, OwnerModLifetime lifetime)
        {
            var action = new NetInputAction(host, activity);
            action.AttachLease(lifetime.Track(action));
            activity.Add(action);
            return action;
        }

        private static InvalidOperationException? DisposeOffTheMainThread(IDisposable action) =>
            Task.Run(() =>
            {
                try
                {
                    action.Dispose();
                    return null;
                }
                catch (InvalidOperationException exception)
                {
                    return exception;
                }
            }).GetAwaiter().GetResult();

        private static void ScheduledCallbacksCaptureWhereTheyWereScheduled(string root)
        {
            using var world = new IntentWorld(root, InternalSessionRole.Client, Builder, Neighbour);
            var builder = world.Contexts[0];
            var neighbour = world.Contexts[1];
            var trace = new List<string>();
            var intents = world.Intents;
            var ui = intents.LocalRecord(ref uiSlot, Builder, IntentSource.UiCallback);
            Action scripted = () => trace.Add("scripted:" + Current(builder));
            True(((NetScheduler)builder.Scheduler).Inner != null, "the scheduler is the decorator around the host's");
            builder.Scheduler.NextFrame(scripted);
            using (intents.Scope.Enter(ui))
            {
                builder.Scheduler.NextFrame(() => trace.Add("next-frame:" + Current(builder)));
                builder.Scheduler.After(TimeSpan.FromSeconds(0.15), () => trace.Add("after:" + Current(builder)));
                builder.Scheduler.Every(TimeSpan.FromSeconds(0.1), () => trace.Add("every:" + Current(builder)));
                neighbour.Scheduler.NextFrame(() => trace.Add("neighbour:" + Current(neighbour)));
                Step(builder, "coroutine", 3, trace);

                // Background work the click started hands its result back as the SDK says to: through the scheduler.
                Task.Run(() =>
                {
                    trace.Add("on-worker:" + Current(builder));
                    builder.Scheduler.NextFrame(() => trace.Add("from-worker:" + Current(builder)));
                    neighbour.Scheduler.NextFrame(() => trace.Add("neighbour-from-worker:" + Current(neighbour)));
                }).GetAwaiter().GetResult();
            }

            Task.Run(() => builder.Scheduler.NextFrame(() => trace.Add("scripted-worker:" + Current(builder)))).GetAwaiter().GetResult();
            True(intents.Scope.Flowing == null, "the scope ended before any of them ran");
            world.Host.BeginFrame(0.1);
            world.Host.BeginFrame(0.1);
            world.Host.BeginFrame(0.1);
            var seen = trace.ToArray();
            foreach (var expected in new[]
                     {
                         "scripted:none", "next-frame:UiCallback/local", "neighbour:none", "after:UiCallback/local",
                         "coroutine-1:UiCallback/local", "coroutine-2:UiCallback/local", "coroutine-3:UiCallback/local",
                         "on-worker:none", "from-worker:UiCallback/local", "neighbour-from-worker:none", "scripted-worker:none"
                     })
            {
                True(seen.Contains(expected), "scheduled work runs as " + expected + " (" + string.Join(", ", seen) + ")");
            }

            True(seen.Count(line => line == "every:UiCallback/local") == 3, "a repeating callback scheduled on intent carries it each time (" + string.Join(", ", seen) + ")");
            True(!seen.Any(line => line.StartsWith("every:none", StringComparison.Ordinal)), "and never loses it while the realm lasts");

            // A coroutine stepped through the scheduler from scripted code carries nothing.
            trace.Clear();
            Step(builder, "scripted-coroutine", 2, trace);
            world.Host.BeginFrame(0.01);
            world.Host.BeginFrame(0.01);
            True(trace.Contains("scripted-coroutine-1:none") && trace.Contains("scripted-coroutine-2:none"), "scripted steps stay scripted");

            // A change of realm ends what was captured before it.
            trace.Clear();
            world.Control.Role = InternalSessionRole.Solo;
            Seam.Frame(world.Runtime);
            world.Control.Role = InternalSessionRole.Client;
            Seam.Frame(world.Runtime);
            world.Host.BeginFrame(0.1);
            True(trace.Contains("every:none") && !trace.Contains("every:UiCallback/local"), "after leaving and joining again the repeating callback runs scripted");
            Throws<ArgumentNullException>(() => builder.Scheduler.NextFrame(null!), "a missing action reaches the host's scheduler, which refuses it");
        }

        private static IntentRecord? uiSlot;

        private static void Step(ModContext context, string name, int remaining, List<string> trace, int step = 1)
        {
            context.Scheduler.NextFrame(() =>
            {
                trace.Add(name + "-" + step + ":" + Current(context));
                if (remaining > 1) Step(context, name, remaining - 1, trace, step + 1);
            });
        }

        private static void InteractionsRunWithTheInteractionIntent(string root)
        {
            using var world = new IntentWorld(root, InternalSessionRole.Client, Builder, Neighbour);
            var builder = world.Contexts[0];
            var neighbour = world.Contexts[1];
            var trace = new List<string>();
            Action<InteractionEvent> handler = _ =>
            {
                trace.Add("builder:" + Current(builder));
                trace.Add("neighbour-inside:" + Current(neighbour));
                builder.Assets.Spawn(new AssetSpawnRequest(new IntentPrefab("crate"), TransformState.Identity));
            };
            builder.Interactions.Register(new SeamEntity(), new InteractableDefinition("Use"), handler);
            var registered = world.Host.Interactions.Single();
            True(!ReferenceEquals(registered.Handler, handler) && ((NetInteractionService)builder.Interactions).Inner != null,
                "the host is handed the decorator's handler, which runs the mod's");
            world.Host.Interact(Builder);
            Sequence(new[] { "builder:InteractionHandler/local", "neighbour-inside:none" }, trace,
                "the player's interaction runs its mod's handler with the interaction intent, and another mod's code inside it sees none");
            True(world.Intents.Scope.Flowing == null, "and the scope ends with the handler");
            True(world.Host.Spawns.SequenceEqual(new[] { (Builder, "crate", "InteractionHandler/local") }),
                "a spawn the handler makes through the asset decorator reaches the host carrying that intent, the question authority routing asks");
            True(!builder.Interactions.TryGetFocused(out var focused) && focused == null, "the focus query passes through");
            Throws<ArgumentNullException>(() => builder.Interactions.Register(new SeamEntity(), new InteractableDefinition("Use"), null!),
                "a missing handler reaches the host's service, which refuses it");
            world.GoOffline();
            trace.Clear();
            world.Host.Interact(Builder);
            Sequence(new[] { "builder:none", "neighbour-inside:none" }, trace, "offline the handler runs as it always has");
        }

        private static void InputActiveFramesAllocateNothing(string root)
        {
            using var world = new IntentWorld(root, InternalSessionRole.Solo, Builder);
            var builder = world.Contexts[0];
            var fire = Fire(builder);
            var reads = 0;
            builder.Events.SubscribeUpdate(_ => { if (fire.IsHeld) reads++; });
            builder.Events.SubscribeLateUpdate(_ => { if (fire.IsHeld) reads++; });
            var action = world.Host.Action(Builder, "fire");
            action.Hold(true);
            var offline = Measure(world);
            True(offline == 0, "offline, a frame with held input allocates nothing (" + offline + " bytes)");
            world.Control.Role = InternalSessionRole.Client;
            Seam.Frame(world.Runtime);
            action.Hold(false);
            var atRest = Measure(world);
            True(atRest == 0, "online at rest, a frame allocates nothing (" + atRest + " bytes)");
            action.Hold(true);
            var held = Measure(world);
            True(held == 0, "online, opening the scope on every input-active frame allocates nothing in the steady state (" + held + " bytes)");
            True(reads > 0, "the mod read its input meanwhile");
        }

        private static long Measure(IntentWorld world)
        {
            Frames(world, 1_000);
            var fewest = long.MaxValue;
            for (var pass = 0; pass < 3 && fewest != 0; pass++) fewest = Math.Min(fewest, Frames(world, 10_000));
            return fewest;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static long Frames(IntentWorld world, int count)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < count; index++) world.Tick();
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        internal static IInputAction Fire(ModContext context) =>
            context.Input.RegisterAction(new InputActionDefinition("fire", "Fire", new[] { InputBinding.Key("Space") })).Value!;

        internal static string Current(ModContext context)
        {
            var intent = ((IInternalReplicationContext)context).Replication.CurrentIntent;
            return !intent.IsPlayerIntent ? "none" : intent.Source + (intent.IsLocalPlayer ? "/local" : "/" + intent.ParticipantId);
        }

        /// <summary>An action that, as the game's does, may be disposed only on the thread that made it.</summary>
        private sealed class MainThreadAction : IInputAction
        {
            private readonly int owner = Environment.CurrentManagedThreadId;

            internal bool Disposed { get; private set; }

            public string Name => "fire";

            public IReadOnlyList<InputBinding> Bindings => Array.Empty<InputBinding>();

            public float Value => 0f;

            public bool IsHeld => false;

            public bool WasPressed => false;

            public bool WasReleased => false;

            public OperationResult<bool> Rebind(IEnumerable<InputBinding> bindings) => OperationResult<bool>.Success(true);

            public OperationResult<bool> ResetBindings() => OperationResult<bool>.Success(true);

            public void Dispose()
            {
                if (Environment.CurrentManagedThreadId != owner)
                {
                    throw new InvalidOperationException("Dispose must be called on the game thread. Use Context.Scheduler.NextFrame from a background task.");
                }

                Disposed = true;
            }
        }
    }
}
