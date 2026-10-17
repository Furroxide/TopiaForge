using System;
using System.Collections.Generic;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>What CONNECT does with a finished check of the typed server.</summary>
    internal sealed partial class ConnectPresenter
    {
        private void Checked(IInternalServerQuery finished)
        {
            InternalLiveFailure failure;
            InternalServerInfo? server;
            IReadOnlyList<string> problems;
            string? changed;
            try
            {
                failure = finished.Failure;
                server = finished.Server;
                problems = finished.ContentProblems;
                changed = finished.ChangedServerId;
            }
            catch (Exception)
            {
                failure = InternalLiveFailure.NotFound;
                server = null;
                problems = Array.Empty<string>();
                changed = null;
            }
            finally
            {
                try
                {
                    finished.Dispose();
                }
                catch (Exception)
                {
                    // A finished check holds nothing worth keeping.
                }
            }

            var chosen = target;
            if (chosen == null) return;
            switch (failure)
            {
                case InternalLiveFailure.DirectNotAllowed when !queryAllowsDirect:
                    // Nothing beyond the local network was contacted; only the player's choice for this session may change that.
                    Result("Waiting for your choice.", ScreenTone.Neutral);
                    context.Privacy.EnsureDirect(() => Check(chosen, allowDirect: true), () => Result(NothingContacted, ScreenTone.Neutral));
                    break;
                case InternalLiveFailure.None:
                    launcherLink = LauncherJoinLink.ForTyped(address, server?.ServerId);
                    var title = server == null ? "Direct session" : HostText.ForDisplay(server.Name);
                    Result("Joining " + (title.Length == 0 ? "the session" : title) + ".", ScreenTone.Success);
                    joins.Begin(new JoinRequest(chosen, title.Length == 0 ? "Direct session" : title, queryAllowsDirect, TakePassword(), trustedServerId, launcherLink));
                    break;
                case InternalLiveFailure.ContentMismatch:
                    launcherLink = LauncherJoinLink.ForTyped(address, server?.ServerId);
                    Result(NeedsText(problems), ScreenTone.Warning);
                    break;
                case InternalLiveFailure.HostIdentityChanged when InternalText.IsServerId(changed):
                    Result(TrustBody, ScreenTone.Warning);
                    context.Ask(PrivacyPrompts.Destructive(TrustTitle, TrustBody, TrustConfirm, confirmed =>
                    {
                        if (!confirmed || target != chosen) return;
                        trustedServerId = changed;
                        launcherLink = LauncherJoinLink.ForTyped(address, changed);
                        joins.Begin(new JoinRequest(chosen, "Direct session", queryAllowsDirect, TakePassword(), changed, launcherLink));
                    }));
                    break;
                default:
                    var message = LiveFailureMessages.ForJoin(failure, InternalAdmissionCode.Other);
                    Result(message.Title + ". " + message.Message, ScreenTone.Danger);
                    break;
            }
        }

        /// <summary>Hands the typed password to a join once, then forgets it.</summary>
        private string? TakePassword()
        {
            var taken = password.Length == 0 ? null : password;
            password = string.Empty;
            passwordError = string.Empty;
            return taken;
        }

        private static string NeedsText(IReadOnlyList<string> problems)
        {
            var count = Math.Max(problems.Count, 1);
            var lines = new List<string>(Math.Min(problems.Count, 4));
            for (var index = 0; index < problems.Count && index < 4; index++) lines.Add(TopiaForgePlainText.Sanitize(problems[index], HostText.LineChars));
            var more = problems.Count > 4 ? " And " + (problems.Count - 4) + " more." : string.Empty;
            return "Needs " + Plural(count, "change", "changes") + ": " + string.Join(" ", lines) + more +
                   " The TopiaForge launcher can install the host's exact mods.";
        }
    }
}
