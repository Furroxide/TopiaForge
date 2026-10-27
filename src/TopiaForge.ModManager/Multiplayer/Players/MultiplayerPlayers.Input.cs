using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal sealed partial class MultiplayerPlayers
    {
        private readonly Dictionary<ParticipantId, TokenBucket> inputBudgets = new Dictionary<ParticipantId, TokenBucket>();
        private TokenBucket? outgoingInputBudget;
        private int canonicalTickSources;

        internal void BindTickSource(PlayerContext context, IMultiplayerSession session)
        {
            if (context.TickSource != null) return;
            canonicalTickSources++;
            context.TickSource = session.SubscribeTick(tick => AdvanceInputs(tick.Tick.Value));
        }

        private void AdvanceInputs()
        {
            if (canonicalTickSources != 0 && IsHost) return;
            AdvanceInputs((ulong)Math.Max(0, Now / 50_000));
        }

        private void AdvanceInputs(ulong tick)
        {
            for (var i = 0; i < contexts.Count; i++)
                foreach (var input in contexts[i].Inputs.Values) input.Advance(tick, Now);
        }

        private void SendInputs()
        {
            var local = Snapshot?.LocalParticipantId;
            if (!local.HasValue) return;
            outgoingInputBudget ??= new TokenBucket(8192, 4096, Now);
            for (var c = 0; c < contexts.Count; c++)
            {
                var context = contexts[c];
                if (context.Lifetime.IsStopping) continue;
                context.PruneActions();
                var actions = context.Activity.Actions;
                var count = Math.Min(RemotePlayerInput.MaximumActions, actions.Count);
                for (var a = 0; a < count; a++)
                {
                    var action = actions[a];
                    if (action.Name.Length == 0 || action.Name.Length > 128 || !ReferenceEquals(FindInputOwner(context.Mod, action.Name), context)) continue;
                    var held = action.IsHeld;
                    var pressed = action.WasPressed;
                    var released = action.WasReleased;
                    for (var i = 0; i < contexts.Count; i++)
                        if (contexts[i].Mod == context.Mod) contexts[i].InputState(local.Value).Take(action.Name, held, pressed, released, Now);
                    if (IsHost) continue;
                    if (!context.SentInputs.TryGetValue(action.Name, out var sent))
                        context.SentInputs.Add(action.Name, sent = new SentInput());
                    sent.Pressed |= pressed;
                    sent.Released |= released;
                    var changed = sent.Held != held || sent.Pressed || sent.Released;
                    if ((!changed && (!held || Now < sent.Heartbeat)) || Now < sent.NextSend) continue;
                    Begin(1, context.Mod);
                    Writer.WriteString(action.Name);
                    Writer.WriteByte((byte)((held ? 1 : 0) | (sent.Pressed ? 2 : 0) | (sent.Released ? 4 : 0)));
                    if (Channel == null || !Channel.CanSend(Mods.Internal.InternalSystemTarget.Host, Writer.Length) ||
                        !outgoingInputBudget.TryTake(Now, Writer.Length)) continue;
                    if (!Send(local.Value).Succeeded) continue;
                    sent.Held = held;
                    sent.Pressed = sent.Released = false;
                    sent.Heartbeat = Now + 1_000_000;
                    sent.NextSend = Now + 50_000;
                }
            }
        }

        private bool ReceiveInput(PlayerContext context, ParticipantId sender, WorldWireReader r)
        {
            if (!IsHost || !Present(sender) || IsLocal(sender)) return false;
            if (!r.TryReadString(512, out var name) || name.Length == 0 || name.Length > 128 || !r.TryReadByte(out var bits) || bits > 7 || !r.AtEnd) return false;
            if (FindInputOwner(context.Mod, name) == null) return false;
            if (!inputBudgets.TryGetValue(sender, out var budget)) inputBudgets.Add(sender, budget = new TokenBucket(16384, 8192, Now));
            if (!budget.TryTake(Now, 32 + name.Length * 4 + context.Mod.Length * 4)) return false;
            // The sender is the only player whose input this packet can name. No payload field can override it.
            for (var i = 0; i < contexts.Count; i++)
                if (contexts[i].Mod == context.Mod) contexts[i].InputState(sender).Take(name, (bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0, Now);
            return true;
        }

        private PlayerContext? FindInputOwner(string mod, string name)
        {
            for (var c = 0; c < contexts.Count; c++)
            {
                var context = contexts[c];
                if (context.Mod != mod || context.Lifetime.IsStopping) continue;
                var actions = context.Activity.Actions;
                for (var i = 0; i < Math.Min(actions.Count, RemotePlayerInput.MaximumActions); i++)
                    if (actions[i].Name == name) return context;
            }
            return null;
        }

        internal sealed class SentInput
        {
            internal bool Held, Pressed, Released;
            internal long Heartbeat, NextSend;
        }
    }

    internal sealed partial class PlayerContext
    {
        internal IDisposable? TickSource;
        private ulong inputVersion;
        internal void PruneActions()
        {
            if (inputVersion == Activity.Version) return;
            inputVersion = Activity.Version;
            foreach (var input in Inputs.Values) input.Retain(Activity.Actions);
            SentInputs.Clear();
        }
        internal readonly Dictionary<string, MultiplayerPlayers.SentInput> SentInputs = new Dictionary<string, MultiplayerPlayers.SentInput>(StringComparer.Ordinal);
    }
}
