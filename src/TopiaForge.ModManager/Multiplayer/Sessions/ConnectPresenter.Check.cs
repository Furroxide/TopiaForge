using System;
using System.Collections.Generic;
using TopiaForge.Mods.Internal;

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

            var chosen = checking;
            var invite = checkingInvite;
            checking = null;
            checkingInvite = null;
            if (chosen == null) return;
            switch (failure)
            {
                case InternalLiveFailure.DirectNotAllowed when !queryAllowsDirect:
                    // Nothing beyond the local network was contacted; only the player's choice for this session may change that.
                    Result("Waiting for your choice.", ScreenTone.Neutral);
                    context.Privacy.EnsureDirect(() => Check(chosen, allowDirect: true, invite), () => Result(NothingContacted, ScreenTone.Neutral));
                    break;
                case InternalLiveFailure.None:
                    launcherLink = LinkFor(invite, server?.ServerId);
                    var title = server == null ? string.Empty : HostText.ForDisplay(server.Name);
                    if (title.Length == 0) title = UnnamedTitle(chosen.Kind);
                    Result("Joining " + title + ".", ScreenTone.Success);
                    joins.Begin(new JoinRequest(chosen, title, JoinAllowsDirect(chosen), TakePassword(), trustedServerId, LauncherHandoff.ForLink(launcherLink)));
                    break;
                case InternalLiveFailure.ContentMismatch:
                    launcherLink = LinkFor(invite, server?.ServerId);
                    Result(NeedsText(problems), ScreenTone.Warning);
                    break;
                case InternalLiveFailure.HostIdentityChanged when InternalText.IsServerId(changed):
                case InternalLiveFailure.KnownHostsUnreadable when InternalText.IsServerId(changed):
                    // The record could not be read to check the server: the player decides, as for a changed server.
                    var body = failure == InternalLiveFailure.HostIdentityChanged ? TrustBody : LiveFailureMessages.UncheckedServer;
                    Result(body, ScreenTone.Warning);
                    context.Ask(PrivacyPrompts.Destructive(TrustTitle, body, TrustConfirm, confirmed =>
                    {
                        if (!confirmed || target != chosen) return;
                        trustedServerId = changed;
                        launcherLink = LauncherJoinLink.ForTyped(address, changed);
                        joins.Begin(new JoinRequest(chosen, "Direct session", queryAllowsDirect, TakePassword(), changed, LauncherHandoff.ForLink(launcherLink)));
                    }));
                    break;
                default:
                    var message = LiveFailureMessages.ForJoin(failure, InternalAdmissionCode.Other);
                    Result(message.Title + ". " + message.Message, ScreenTone.Danger);
                    break;
            }
        }

        /// <summary>
        /// Gets whether the join may take a direct path: a typed host as its check allowed; an invite or a listed server, which
        /// the service resolves, as the player's Hide my IP says, so it goes through the relay unless the player turned it off.
        /// </summary>
        private bool JoinAllowsDirect(IInternalJoinTarget chosen) =>
            chosen.Kind == InternalJoinTargetKind.Direct ? queryAllowsDirect : !context.Settings.Current.HideMyIp;

        /// <summary>Gets the launcher's link: the invite the player typed, or what they typed as the address, pinned by the server found.</summary>
        private string? LinkFor(string? invite, string? serverId) =>
            invite != null ? LauncherJoinLink.ForInvite(invite) : LauncherJoinLink.ForTyped(address, serverId);

        /// <summary>Names a session whose host gave no name, by how it was reached; never by an address or an invite code.</summary>
        internal static string UnnamedTitle(InternalJoinTargetKind kind) =>
            kind == InternalJoinTargetKind.Invite ? "Invited session"
            : kind == InternalJoinTargetKind.ListedServer ? "Listed server"
            : kind == InternalJoinTargetKind.Lan ? "LAN session"
            : "Direct session";

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
            for (var index = 0; index < problems.Count && index < 4; index++) lines.Add(HostText.ForOwnLine(problems[index]));
            var more = problems.Count > 4 ? " And " + (problems.Count - 4) + " more." : string.Empty;
            return "Needs " + Plural(count, "change", "changes") + ": " + string.Join(" ", lines) + more +
                   " The TopiaForge launcher can install the host's exact mods.";
        }
    }
}
