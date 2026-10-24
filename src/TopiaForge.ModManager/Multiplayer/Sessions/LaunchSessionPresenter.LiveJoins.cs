using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>Live joins: the joins the launcher stages for this game while it runs, each acted on once.</summary>
    internal sealed partial class LaunchSessionPresenter
    {
        internal const int RememberedJoins = 32;
        internal const string WrongMods = "This game's mods are not the ones the TopiaForge launcher planned the join for.";
        internal const string OtherService = "This game reaches another TopiaForge service than the join names. Start the join from the TopiaForge launcher again.";
        internal const string LiveJoinArrived = "Joining the session the TopiaForge launcher asked for.";

        private readonly Queue<string> handledJoins = new Queue<string>(RememberedJoins);

        /// <summary>
        /// A live join the launcher staged for this running game: checked against the packages this game runs and the service it
        /// reaches, planned by the provider, and started as a launch. A join id seen before is never acted on twice. Anything
        /// that stops it is reported under its join id, as a launch's is under its request.
        /// </summary>
        internal void Offer(SessionJoinRequest join)
        {
            if (join == null) throw new ArgumentNullException(nameof(join));
            var channels = context.Environment.Launcher;
            if (channels == null || Handled(join.JoinId)) return;
            var identity = new LaunchSessionIdentity(join.JoinId, "join", join.Session.IntentId);
            var records = new LiveJoinRecords(channels, queue, identity, () => context.Environment.LocalPhase);
            if (!string.Equals(join.PackageSetDigest, channels.PackageSetDigest, StringComparison.Ordinal))
            {
                records.Result(SessionLaunchRecords.Failure(identity, "admission-changed", WrongMods), null);
                return;
            }

            var target = join.Session.Target;
            if (target != null && target.NeedsService && !string.Equals(join.Session.ServiceOrigin, channels.ServiceOrigin, StringComparison.Ordinal))
            {
                records.Result(SessionLaunchRecords.Refused(identity, ModErrorCode.Unavailable, OtherService), null);
                return;
            }

            var control = context.Environment.Control;
            if (control == null)
            {
                records.Result(SessionLaunchRecords.Refused(identity, ModErrorCode.Unavailable, SessionLaunch.ProviderMissing), null);
                return;
            }

            OperationResult<IInternalLaunchPlan> planned;
            try
            {
                planned = control.PlanLaunchSession(LaunchSessionOffer.ToSession(join.Session));
            }
            catch (Exception exception)
            {
                planned = OperationResult<IInternalLaunchPlan>.Failure(ModErrorCode.External,
                    "The multiplayer provider could not plan the join (" + exception.GetType().Name + ").");
            }

            if (!planned.TryGetValue(out var plan) || plan == null)
            {
                records.Result(SessionLaunchRecords.Refused(identity, planned.ErrorCode, planned.ErrorMessage), null);
                return;
            }

            var launched = new LaunchedSession(identity, plan, records, () => LaunchedGame.Ready, liveJoin: true, handoff: LauncherHandoff.ForTarget(target));
            Begin(launched);

            // Only a join that goes ahead is announced; one turned away while another launch starts says so in its records.
            if (ReferenceEquals(current, launched)) context.Notify(LiveJoinArrived, ScreenTone.Neutral);
        }

        /// <summary>Remembers <paramref name="joinId"/>; true when it was seen before, so a replayed join is never acted on.</summary>
        private bool Handled(string joinId)
        {
            if (handledJoins.Contains(joinId)) return true;
            if (handledJoins.Count >= RememberedJoins) handledJoins.Dequeue();
            handledJoins.Enqueue(joinId);
            return false;
        }
    }
}
