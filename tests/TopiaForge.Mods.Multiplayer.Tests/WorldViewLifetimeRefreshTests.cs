using TopiaForge.ModManager;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using FakeEntity = TopiaForge.Mods.Testing.FakeEntity;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static class WorldViewLifetimeRefreshTests
{
    internal static void Run()
    {
        UnityMainThreadGuard.CaptureCurrentThread();
        RunCache();
        Reconnect(ReconnectCase.Normal);
        Reconnect(ReconnectCase.Quiet);
        Reconnect(ReconnectCase.PartialBaseline);
        Reconnect(ReconnectCase.ReplacementAvatar);
        Reconnect(ReconnectCase.MixedRoster);
        Reconnect(ReconnectCase.ViewerLinkLoss);
        Reconnect(ReconnectCase.RootLostInGrace);
        Reconnect(ReconnectCase.RootLostHeldBack);
        Reconnect(ReconnectCase.GraceExpiry);
        Reconnect(ReconnectCase.LateJoiner);
        Reconnect(ReconnectCase.LateJoinerHeldBack);
        Reconnect(ReconnectCase.UnreadableFirstView);
        Reconnect(ReconnectCase.WorldChange);
        Reconnect(ReconnectCase.SessionReplaced);
        Reconnect(ReconnectCase.WorldChangeHostFirst);
        Reconnect(ReconnectCase.RefusedTarget);
        Reconnect(ReconnectCase.RendererEnding);
        Console.WriteLine("Engine lifetime world-view refresh and cached references passed.");
    }

    internal static void RunCache() { UnityMainThreadGuard.CaptureCurrentThread(); Reconnect(ReconnectCase.Cache); }

    // Quiet stops the subject sending across the rejoin to pin that view delivery never stands in for a pose. Every
    // other case keeps the subject sending, so its first pose after the rejoin races the view refresh.
    // ViewerLinkLoss cuts and restores the observer's own link, RootLostInGrace destroys the subject's roots while
    // they are away, GraceExpiry lets them leave and join again, and LateJoiner adds a viewer after the hide. The
    // HeldBack variants hold the host's views back, so the subject's poses arrive first. UnreadableFirstView spoils a
    // late joiner's first view; WorldChange and SessionReplaced move the observer to a world or session with no view,
    // and WorldChangeHostFirst moves the host first. RefusedTarget has the host's renderer refuse one hidden object for
    // good, and RendererEnding lets the host's renderer stop being alive before its world is unbound.
    private enum ReconnectCase
    {
        Normal, Quiet, PartialBaseline, Cache, ReplacementAvatar, MixedRoster, ViewerLinkLoss, RootLostInGrace, RootLostHeldBack,
        GraceExpiry, LateJoiner, LateJoinerHeldBack, UnreadableFirstView, WorldChange, SessionReplaced, WorldChangeHostFirst,
        RefusedTarget, RendererEnding
    }

    private static void Reconnect(ReconnectCase scenario)
    {
        var mixedRoster = scenario == ReconnectCase.MixedRoster;
        var quiet = scenario == ReconnectCase.Quiet;
        using var engine = new EngineHarness(MultiplayerProcessKind.Interactive, options =>
        {
            if (mixedRoster || scenario == ReconnectCase.GraceExpiry) options.ParticipantGraceMicros = 500_000;
        });
        var guest = engine.AddClient("subject");
        var watcher = engine.AddClient("watcher");
        engine.Flush();
        using var host = new Node(engine.Host, engine);
        using var subject = new Node(guest.Engine, engine);
        using var observer = new Node(watcher.Engine, engine);
        var nodes = new List<Node> { host, subject, observer };
        var viewers = new List<Node> { host, observer };
        var watching = false;
        var tolerateUndrawn = false;
        var displayName = Row(engine.Host, subject.Id).DisplayName;
        // What a viewer sees of the hidden subject: their current root, if presence has one, is held by the
        // renderer, and their name is not drawn. Where a native hide fails, an unheld root must stay undrawn.
        void CheckHidden(Node node)
        {
            var where = scenario + " at tick " + engine.Tick + ": the " + (node == host ? "host" : node == observer ? "observer" : "late joiner");
            if (node.Presence.TryGetAvatar(subject.Id, out var root) && root != null)
                Check(node.Renderer.Hidden.Contains(root) || tolerateUndrawn && !PresenceVisible(node, root),
                    where + " holds the hidden subject's current root");
            Check(!HasNametag(node, displayName), where + " draws no name for the hidden subject");
        }
        // The runtime's order at 60 frames a second: receive, views after the mods' Update, send, then draw.
        void Frames(int count)
        {
            for (var frame = 0; frame < count; frame++)
            {
                engine.Advance();
                foreach (var node in nodes) node.Runtime.BeforeModsUpdate(1f / 60);
                foreach (var node in nodes) { node.Control.Views!.Frame(); node.Services.Views.Run(1f / 60); }
                foreach (var node in nodes) if (node.SendEnabled) node.Presence.Send.Run(1f / 60);
                foreach (var node in nodes) node.Presence.Avatars.Run(1f / 60);
                if (watching) foreach (var viewer in viewers) CheckHidden(viewer);
            }
        }
        Frames(40);
        var original = Avatar(host, subject.Id);
        var watched = Avatar(observer, subject.Id);
        var originalLifetime = Row(engine.Host, subject.Id).LifetimeId;
        Check(AvatarShown(host, original) && AvatarShown(observer, watched)
            && HasNametag(host, displayName) && HasNametag(observer, displayName),
            "host and observer both present the subject before link loss");
        void CheckSuppressed(string stage)
        {
            Check(!PresenceVisible(host, original) && !PresenceVisible(observer, watched)
                && !HasNametag(host, displayName) && !HasNametag(observer, displayName),
                "host and observer suppress avatar and nametag " + stage);
        }
        var made = host.Players.TryCreateReference(original, out var oldReference);
        Check(made && observer.Players.TryResolveReference(oldReference, out var remote) && ReferenceEquals(remote, watched),
            "canonical engine reference resolves the observer's exact current root");
        using var scope = new FakeModLifetime();
        var seam = host.Services.Bind("example.mode", new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.GamemodeServer), scope);
        // A target hidden before the subject comes first in the host's apply order.
        var refusedProp = new FakeEntity("refused-prop", "Authored", Vec3.Zero);
        IViewLease? refusedLease = null;
        if (scenario == ReconnectCase.RefusedTarget)
        {
            host.Binding.Add(refusedProp);
            refusedLease = Lease(seam.PlayerWorldView(host.Renderer, host.Id.Value).Hide(refusedProp));
        }
        using var local = Lease(seam.PlayerWorldView(host.Renderer, host.Id.Value).Hide(original));
        using var privateView = Lease(seam.PlayerWorldView(host.Renderer, observer.Id.Value).Hide(original));
        Frames(4);
        Check(host.Renderer.Hidden.Contains(original) && observer.Renderer.Hidden.Contains(watched), "one outer lease hides the current root on host and observer");
        watching = true;
        var foreign = new FakeEntity(original.Id, "Foreign", Vec3.Zero);
        Check(!seam.PlayerWorldView(host.Renderer, observer.Id.Value).Hide(foreign).Succeeded,
            "same-ID foreign entity cannot borrow a player's canonical reference");

        if (scenario == ReconnectCase.ReplacementAvatar)
        {
            // The holds reach a root replaced inside one lifetime before its first draw.
            host.Backend.Avatars.Single(avatar => ReferenceEquals(avatar.Entity, original)).Destroyed = true;
            observer.Backend.Avatars.Single(avatar => ReferenceEquals(avatar.Entity, watched)).Destroyed = true;
            Frames(20);
            var replacement = Avatar(host, subject.Id);
            var watchedReplacement = Avatar(observer, subject.Id);
            Check(!ReferenceEquals(original, replacement) && !ReferenceEquals(watched, watchedReplacement)
                && Row(engine.Host, subject.Id).LifetimeId == originalLifetime
                && host.Players.TryResolveReference(oldReference, out var currentRoot) && ReferenceEquals(currentRoot, replacement)
                && observer.Players.TryResolveReference(oldReference, out var currentWatched) && ReferenceEquals(currentWatched, watchedReplacement),
                "destroyed roots are replaced in the same admission and resolve through the original canonical name");
            Check(!host.Renderer.Hidden.Contains(original) && host.Renderer.Hidden.Contains(replacement)
                && !observer.Renderer.Hidden.Contains(watched) && observer.Renderer.Hidden.Contains(watchedReplacement),
                "the existing host and observer hides have already followed the replacement roots");
            original = replacement;
            watched = watchedReplacement;
            // A native hide that throws while the observer follows a replacement leaves no hold untracked: the old
            // hold is kept and released by the retry that hides the new root. The new root is not drawn until then.
            tolerateUndrawn = true;
            observer.Renderer.FailNextHide = true;
            observer.Backend.Avatars.Single(avatar => ReferenceEquals(avatar.Entity, watched)).Destroyed = true;
            Frames(20);
            tolerateUndrawn = false;
            var retried = Avatar(observer, subject.Id);
            Check(!observer.Renderer.FailNextHide && !ReferenceEquals(retried, watched)
                && !observer.Renderer.Hidden.Contains(watched) && observer.Renderer.Hidden.Contains(retried) && PresenceVisible(observer, retried),
                "a failed native hide keeps tracking the old hold until the retry replaces it");
            Check(observer.Log.ViewFailures == 1, "a failed view on a new avatar is reported once");
            watched = retried;
            // A renderer that refuses a hold, rather than throwing, also leaves the new root undrawn until it is held,
            // on the host and on the observer. The observer's earlier report was cleared by its success.
            tolerateUndrawn = true;
            host.Renderer.RefuseNextHide = true; observer.Renderer.RefuseNextHide = true;
            host.Backend.Avatars.Single(avatar => ReferenceEquals(avatar.Entity, original)).Destroyed = true;
            observer.Backend.Avatars.Single(avatar => ReferenceEquals(avatar.Entity, watched)).Destroyed = true;
            Frames(20);
            tolerateUndrawn = false;
            original = Avatar(host, subject.Id); watched = Avatar(observer, subject.Id);
            Check(!host.Renderer.RefuseNextHide && !observer.Renderer.RefuseNextHide
                && host.Renderer.Hidden.Contains(original) && PresenceVisible(host, original)
                && observer.Renderer.Hidden.Contains(watched) && PresenceVisible(observer, watched),
                "a refused hold is asked again and the new roots are drawn only once held");
            Check(host.Log.ViewFailures == 1 && observer.Log.ViewFailures == 2, "each new failure after a success is reported again");
        }

        if (scenario == ReconnectCase.ViewerLinkLoss)
        {
            // A view message that cannot be read leaves the last view holding until the next link.
            Check(host.Control.Views!.Send(new ArraySegment<byte>(new byte[] { 0xFF, 0xFF }), InternalSystemTarget.Only(observer.Id)).Succeeded,
                "the host sends one unreadable view message");
            Frames(20);
            Check(observer.Renderer.Hidden.Contains(watched) && PresenceVisible(observer, watched), "an unreadable view message keeps the last view");
            // Presence keeps drawing the last poses while the observer's own link is down; the last view holds.
            engine.Network.Sever(watcher.Link);
            Frames(20);
            // The host changes the observer's view during the outage; only the new link can deliver it.
            var outageProp = new FakeEntity("outage-prop", "Authored", Vec3.Zero);
            var observedProp = new FakeEntity(outageProp.Id, "Authored", Vec3.Zero);
            host.Binding.Add(outageProp); observer.Binding.Add(observedProp);
            using var outageHide = Lease(seam.PlayerWorldView(host.Renderer, observer.Id.Value).Hide(outageProp));
            Frames(20);
            Check(PresenceVisible(observer, watched) && privateView.IsActive && !observer.Renderer.Hidden.Contains(observedProp),
                "the observer still draws the subject from its last poses while its own link is down");
            engine.Join(watcher.Engine, "watcher"); engine.Flush();
            Frames(40);
            var rejoined = Avatar(observer, subject.Id);
            Check(Row(engine.Host, observer.Id).IsConnected && privateView.IsActive && PresenceVisible(observer, rejoined)
                && observer.Renderer.Hidden.Contains(rejoined) && observer.Renderer.Hidden.Contains(observedProp),
                "the observer's new link replaces the last view with the complete current one");
            return;
        }
        if (scenario == ReconnectCase.RootLostInGrace || scenario == ReconnectCase.RootLostHeldBack)
        {
            // The game destroys the subject's roots while they are away; presence makes new ones after the rejoin.
            // Held back, the host's renamed view waits behind exhausted credits while the subject's poses flow.
            var heldBack = scenario == ReconnectCase.RootLostHeldBack;
            if (heldBack) host.Control.Views!.SendLimit = 0;
            engine.Network.Sever(guest.Link); Frames(3);
            host.Backend.Avatars.Single(avatar => ReferenceEquals(avatar.Entity, original)).Destroyed = true;
            observer.Backend.Avatars.Single(avatar => ReferenceEquals(avatar.Entity, watched)).Destroyed = true;
            Frames(30);
            Check(!host.Presence.TryGetAvatar(subject.Id, out _) && !observer.Presence.TryGetAvatar(subject.Id, out _),
                "no root is made for the subject while they are away");
            engine.Join(subject.Engine, "subject"); engine.Flush();
            Frames(60);
            Check(PresenceVisible(host, Avatar(host, subject.Id)) && PresenceVisible(observer, Avatar(observer, subject.Id))
                && local.IsActive && privateView.IsActive && (!heldBack || host.Control.Views!.SendLimit == 0),
                heldBack ? "the observer draws the rejoined subject's new root before the renamed view, and holds it"
                    : "the rejoined subject's new roots are drawn by presence and stay held");
            if (heldBack)
            {
                host.Control.Views!.SendLimit = int.MaxValue;
                Frames(20);
                Check(observer.Renderer.Hidden.Contains(Avatar(observer, subject.Id)), "the renamed view takes over the observer's hold");
            }
            return;
        }
        if (scenario == ReconnectCase.GraceExpiry)
        {
            // The subject's grace runs out: they left. Their next admission is a new player the old leases do not hide.
            engine.Network.Sever(guest.Link);
            Frames(45);
            Check(!engine.Host.Snapshot.TryGetParticipant(subject.Id, out _) && !host.Presence.TryGetAvatar(subject.Id, out _),
                "the subject's grace expires and their root is gone");
            watching = false;
            var again = engine.AddClient("subject"); engine.Flush();
            using var returned = new Node(again.Engine, engine);
            nodes.Remove(subject); nodes.Add(returned);
            Frames(60);
            Check(local.IsActive && privateView.IsActive && AvatarShown(host, Avatar(host, subject.Id))
                && AvatarShown(observer, Avatar(observer, subject.Id)),
                "a player who left and joined again is not hidden by the leases taken for their earlier admission");
            return;
        }
        if (scenario == ReconnectCase.WorldChangeHostFirst)
        {
            // The host's view of the new world arrives while the observer is still bound to the old one. It counts
            // once the observer binds the new world, even if the host never sends again.
            watching = false;
            host.World = "world-two";
            using var hostTwo = new Renderer(host);
            host.Services.Views.Activate(new Binding(hostTwo, "world-two"));
            Frames(30);
            observer.World = "world-two";
            using var observerTwo = new Renderer(observer);
            observer.Services.Views.Activate(new Binding(observerTwo, "world-two"));
            Frames(60);
            Check(AvatarShown(observer, Avatar(observer, subject.Id)), "a view that arrived before the observer bound its world still counts for it");
            return;
        }
        if (scenario == ReconnectCase.RefusedTarget)
        {
            // The renderer refuses the prop's replacement for good. The subject's target comes after it: their new
            // root is still held before it is drawn, and the refusal is asked again only on the 0.25 s retry.
            host.Renderer.RefusedIds.Add(refusedProp.Id);
            var refusedReplacement = new FakeEntity(refusedProp.Id, "Authored", Vec3.Zero);
            host.Binding.Replace(refusedReplacement);
            host.Backend.Avatars.Single(avatar => ReferenceEquals(avatar.Entity, original)).Destroyed = true;
            var refusals = host.Renderer.Refusals;
            Frames(60);
            var replacement = Avatar(host, subject.Id);
            Check(host.Renderer.Hidden.Contains(replacement) && PresenceVisible(host, replacement) && host.Renderer.Refusals - refusals <= 6,
                "a hidden avatar after a refused target is held, and the refusal costs only its retries ("
                + (host.Renderer.Refusals - refusals) + " refusals in 60 frames)");
            // While a new root itself is refused it stays undrawn, and nothing is asked every frame.
            tolerateUndrawn = true;
            host.Renderer.RefuseAll = true;
            host.Backend.Avatars.Single(avatar => ReferenceEquals(avatar.Entity, replacement)).Destroyed = true;
            refusals = host.Renderer.Refusals;
            Frames(60);
            Check(!PresenceVisible(host, Avatar(host, subject.Id)) && host.Renderer.Refusals - refusals <= 16,
                "a refused new root waits undrawn without per-frame work (" + (host.Renderer.Refusals - refusals) + " refusals in 60 frames)");
            host.Renderer.RefuseAll = false;
            Frames(20);
            tolerateUndrawn = false;
            // Only a refusal of a lease's own target fails that lease.
            var otherProp = new FakeEntity("other-prop", "Authored", Vec3.Zero);
            host.Binding.Add(otherProp);
            using var otherHide = Lease(seam.PlayerWorldView(host.Renderer, host.Id.Value).Hide(otherProp));
            Check(host.Renderer.Hidden.Contains(otherProp), "a new lease is held while another target stays refused");
            Check(!seam.PlayerWorldView(host.Renderer, host.Id.Value).Hide(refusedReplacement).Succeeded,
                "a lease whose own target is refused fails");
            Check(host.Renderer.Hidden.Contains(Avatar(host, subject.Id)) && PresenceVisible(host, Avatar(host, subject.Id)),
                "the refused root is held and drawn once the renderer accepts it");
            refusedLease!.Dispose();
            return;
        }
        if (scenario == ReconnectCase.RendererEnding)
        {
            // The host's renderer stops being alive before its world is unbound. The host sends no view meanwhile, so
            // the observer keeps holding what the host still leases.
            viewers.Remove(host);
            host.Renderer.Ending = true;
            local.Dispose();
            Frames(20);
            host.Renderer.Ending = false;
            Frames(20);
            Check(observer.Renderer.Hidden.Contains(watched) && privateView.IsActive, "the observer keeps the hold the host still leases");
            return;
        }
        if (scenario == ReconnectCase.WorldChange)
        {
            // The observer loads another world first: it draws nobody there until the host's view of that world arrives.
            watching = false;
            observer.World = "world-two";
            using var observerTwo = new Renderer(observer);
            observer.Services.Views.Activate(new Binding(observerTwo, "world-two"));
            // A further view of the host's old world does not count for the observer's new one.
            privateView.Dispose();
            Frames(60);
            Check(observer.Presence.TryGetPose(subject.Id, out _) && !observer.Presence.TryGetAvatar(subject.Id, out _),
                "the observer draws nobody in its new world before the host's view of that world");
            host.World = "world-two";
            using var hostTwo = new Renderer(host);
            host.Services.Views.Activate(new Binding(hostTwo, "world-two"));
            Frames(60);
            Check(AvatarShown(observer, Avatar(observer, subject.Id)), "the host's view of the new world lets the observer draw again");
            return;
        }
        if (scenario == ReconnectCase.SessionReplaced)
        {
            // A replaced session waits for its own first complete view, on the same link.
            watching = false;
            host.Control.Views!.SendLimit = 0;
            engine.Host.ReplaceSession(new MultiplayerSessionId("next-round"), new SessionSeed(2));
            Frames(60);
            Check(observer.Control.SessionSnapshot.Id.Value == "next-round" && observer.Presence.TryGetPose(subject.Id, out _)
                && !observer.Presence.TryGetAvatar(subject.Id, out _), "the observer draws nobody in the new session before its first view");
            host.Control.Views.SendLimit = int.MaxValue;
            Frames(60);
            Check(AvatarShown(observer, Avatar(observer, subject.Id)), "the new session's first view lets the observer draw again");
            return;
        }
        if (scenario == ReconnectCase.LateJoiner || scenario == ReconnectCase.LateJoinerHeldBack || scenario == ReconnectCase.UnreadableFirstView)
        {
            // Hide the subject from everyone, then a viewer joins: their first avatar of the subject is never drawn.
            // Held back, the late joiner has poses before any view and draws nobody until the first complete one.
            using var everyone = Lease(seam.WorldView(host.Renderer).Hide(original));
            var unreadable = scenario == ReconnectCase.UnreadableFirstView;
            var heldBack = scenario == ReconnectCase.LateJoinerHeldBack || unreadable;
            if (heldBack) host.Control.Views!.SendLimit = 0;
            var late = engine.AddClient("late"); engine.Flush();
            using var lateNode = new Node(late.Engine, engine);
            nodes.Add(lateNode); viewers.Add(lateNode);
            Frames(60);
            if (heldBack)
            {
                Check(lateNode.Presence.TryGetPose(subject.Id, out _) && !lateNode.Presence.TryGetAvatar(subject.Id, out _),
                    "the late joiner has the subject's poses and draws nobody before the first complete view");
                host.Control.Views!.SendLimit = int.MaxValue;
                if (unreadable)
                {
                    // The first message the late joiner gets cannot be read: it waits, says so once, and the next link
                    // brings a view it can read.
                    Check(host.Control.Views.Send(new ArraySegment<byte>(new byte[] { 0xFF, 0xFF }), InternalSystemTarget.Only(lateNode.Id)).Succeeded,
                        "the host sends one unreadable view message first");
                    Frames(240);
                    Check(!lateNode.Presence.TryGetAvatar(subject.Id, out _) && lateNode.Log.Unreadable == 1 && lateNode.Log.ViewWaits == 1,
                        "an unreadable first view withholds the late joiner's avatars for that link and is reported once");
                    engine.Network.Sever(late.Link); Frames(3);
                    engine.Join(late.Engine, "late"); engine.Flush();
                    Frames(60);
                    Check(lateNode.Log.Unreadable == 1 && lateNode.Log.ViewWaits == 1, "recovery adds no further reports");
                }
                else Frames(30);
            }
            var seen = Avatar(lateNode, subject.Id);
            Check(PresenceVisible(lateNode, seen) && lateNode.Renderer.Hidden.Contains(seen),
                "the late joiner's presence draws the subject while the hold that reached the new root keeps it hidden");
            return;
        }

        if (scenario == ReconnectCase.Cache)
        {
            Idle(host, observer, oldReference);
            IdleAcrossRosterPublications(host);
        }
        if (scenario == ReconnectCase.Cache || mixedRoster)
        {
            if (mixedRoster)
            {
                engine.Network.Sever(watcher.Link);
                Frames(3);
                Check(!Row(engine.Host, observer.Id).IsConnected && privateView.IsActive,
                    "the other viewer's scoped lease survives while that viewer is in grace");
                engine.Advance(35);
                Check(!engine.Host.Snapshot.TryGetParticipant(observer.Id, out _), "the other viewer expires before the next runtime roster read");
            }
            // Actual replacement can be published before the consumer samples the intermediate lost-link roster.
            engine.Network.Sever(guest.Link); engine.Advance(3);
            engine.Join(subject.Engine, "subject"); engine.Flush();
            foreach (var node in nodes) node.Runtime.BeforeModsUpdate(1f / 60);
            Check(Row(engine.Host, subject.Id).LifetimeId != originalLifetime && !host.Players.TryResolveReference(oldReference, out _),
                "coalesced engine reconnect retires cached reference without an intermediate disconnect sample");
            if (mixedRoster)
                Check(!PresenceVisible(host, original) && !HasNametag(host, displayName), "host presence is suppressed on the mixed roster receive frame");
            else CheckSuppressed("on the coalesced reconnect receive frame");
            var calls = host.Renderer.HideCalls;
            var releases = host.Renderer.ReleaseCalls;
            host.Services.Views.Run(mixedRoster ? 0 : .25f);
            Check(host.Renderer.Hidden.Contains(original) && host.Renderer.HideCalls == calls && host.Renderer.ReleaseCalls == releases,
                "name refresh occurs before local reconciliation can release an admitted retained-root hold");
            if (mixedRoster)
            {
                Check(local.IsActive && !privateView.IsActive, "the same publication removes only the expired viewer's lease");
                host.Services.Views.Run(.25f);
                Check(host.Renderer.HideCalls == calls && host.Renderer.ReleaseCalls == releases,
                    "the later retry has no dropped host hold to reacquire");
            }
            return;
        }

        var extras = new List<IViewLease>();
        var partialBaseline = scenario == ReconnectCase.PartialBaseline;
        if (partialBaseline)
        {
            for (var index = 0; index < 40; index++)
            {
                var authored = new FakeEntity("authored-" + index, "Authored", Vec3.Zero);
                host.Binding.Add(authored); observer.Binding.Add(new FakeEntity(authored.Id, "Authored", Vec3.Zero));
                extras.Add(Lease(seam.PlayerWorldView(host.Renderer, observer.Id.Value).Hide(authored)));
            }
            var begins = host.Control.Views!.Begins;
            host.Control.Views.SendLimit = 1; host.Control.Views.Frame(); host.Services.Views.Run(0);
            // Controlled equivalent publications isolate consumer churn; they do not synthesize admission lifetimes.
            for (var publication = 0; publication < 6; publication++)
            {
                host.Control.SnapshotOverride = Equivalent(engine.Host.Snapshot);
                host.Control.Views.Frame(); host.Services.Views.Run(0);
                engine.Advance(); observer.Runtime.BeforeModsUpdate(1f / 60); observer.Services.Views.Run(0);
            }
            Check(host.Control.Views.Begins == begins + 1 && observer.Renderer.Hidden.Count == 41,
                "equivalent roster publications preserve partial-baseline progress without redundant begin frames");
            host.Control.SnapshotOverride = null;
            // Start another genuinely dirty partial baseline for the independent lifetime/fragments regression.
            var additional = new FakeEntity("authored-final", "Authored", Vec3.Zero);
            host.Binding.Add(additional); observer.Binding.Add(new FakeEntity(additional.Id, "Authored", Vec3.Zero));
            extras.Add(Lease(seam.PlayerWorldView(host.Renderer, observer.Id.Value).Hide(additional)));
            host.Control.Views.Frame(); host.Services.Views.Run(0);
            Check(host.Control.Views.Sent == 1, "a multi-chunk baseline is partially enqueued before backpressure");
            host.Control.Views.SendLimit = 0;
        }
        subject.SendEnabled = !quiet;
        engine.Network.Sever(guest.Link); Frames(3);
        Check(!Row(engine.Host, subject.Id).IsConnected && !host.Players.TryResolveReference(oldReference, out _)
            && !host.Services.Views.IsCurrentAvatar(original, host.Renderer), "disconnected canonical reference and native admission refuse retained root");
        Check(!observer.Players.TryResolveReference(oldReference, out _)
            && !observer.Services.Views.IsCurrentAvatar(watched, observer.Renderer),
            "disconnected canonical reference and native admission also refuse the observer's retained root");
        CheckSuppressed("during disconnect");
        // Longer than the views' 0.25 s retry, so every timer-driven reconciliation runs inside the grace.
        Frames(30);
        CheckSuppressed("through reconnect grace");
        engine.Join(subject.Engine, "subject"); engine.Flush();
        foreach (var node in nodes) node.Runtime.BeforeModsUpdate(1f / 60);
        // Observe the replacement at the real receive boundary before host send may assign a fresh index.
        foreach (var node in new[] { host, observer })
            Check(!node.Presence.TryGetPose(subject.Id, out _) && node.Presence.TryGetPeer(subject.Id, out var replaced)
                && replaced!.Index == 0 && (replaced.Flags & (PresenceFlags.Talking | PresenceFlags.Typing)) == 0,
                "replacement admission resumes views without fabricating gameplay pose or index");
        CheckSuppressed("on the rejoin receive frame");
        Frames(3);
        if (quiet) CheckSuppressed("after rejoin without a fresh pose");
        else
        {
            for (var frame = 0; frame < 30 && !(PresenceVisible(host, original) && PresenceVisible(observer, watched)); frame++) Frames(1);
            Check(PresenceVisible(host, original) && PresenceVisible(observer, watched)
                && (!partialBaseline || host.Control.Views!.SendLimit == 0),
                partialBaseline ? "fresh poses reach host and observer while the renamed view baseline is still held back"
                    : "fresh poses reach host and observer right after the rejoin");
        }
        var current = Row(engine.Host, subject.Id);
        Check(current.IsConnected && current.LifetimeId != originalLifetime, "production engine rejoin issues a genuinely fresh lifetime");
        Check(ReferenceEquals(Avatar(host, subject.Id), original), "grace rejoin retains the exact validated avatar root");
        Check(!host.Players.TryResolveReference(oldReference, out _) && !observer.Players.TryResolveReference(oldReference, out _),
            "retired opaque reference remains refused on both processes after rejoin");
        made = host.Players.TryCreateReference(original, out var freshReference);
        Check(made && !freshReference.Equals(oldReference) && host.Players.TryResolveReference(freshReference, out var refreshed)
            && ReferenceEquals(refreshed, original), "canonical cache refreshes by actual engine-issued lifetime");
        if (quiet) Check(!host.Presence.TryGetPose(subject.Id, out _), "view delivery accepts no replacement gameplay pose");
        host.Control.Views!.SendLimit = int.MaxValue; Frames(6);
        Check(host.Renderer.Hidden.Contains(original), "lifetime name refresh precedes host native-hold reconciliation");
        Check(observer.Renderer.Hidden.Contains(Avatar(observer, subject.Id)),
            partialBaseline ? "fresh lifetime view restarts partial baseline without stale wire fragments" : "fresh lifetime reference reaches observer without outer lease reacquisition");
        Check(local.IsActive && privateView.IsActive, "actual lifetime recovery preserves original outer leases");
        if (quiet) CheckSuppressed("after view delivery without a fresh pose");
        var savedWorld = host.World; host.World = "another-world";
        Check(!host.Services.Views.IsCurrentAvatar(original, host.Renderer), "world mismatch cannot admit retained root"); host.World = savedWorld;
        host.SceneVersion++;
        Check(!host.Services.Views.IsCurrentAvatar(original, host.Renderer), "scene mismatch cannot admit retained root"); host.SceneVersion--;

        Idle(host, observer, freshReference);
        subject.SendEnabled = true;
        for (var frame = 0; frame < 60 && !(host.Presence.TryGetPose(subject.Id, out _)
            && observer.Presence.TryGetPose(subject.Id, out _)); frame++) Frames(1);
        Check(host.Presence.TryGetPose(subject.Id, out _) && observer.Presence.TryGetPose(subject.Id, out _)
            && PresenceVisible(host, original) && PresenceVisible(observer, watched)
            && !AvatarShown(host, original) && !AvatarShown(observer, watched),
            "fresh poses restore presence while the existing renderer holds keep both avatars hidden");
        watching = false;
        local.Dispose(); privateView.Dispose(); Frames(6);
        Check(AvatarShown(host, original) && AvatarShown(observer, watched)
            && HasNametag(host, displayName) && HasNametag(observer, displayName),
            "releasing the recovered outer leases shows both avatars and nametags");
        foreach (var extra in extras) extra.Dispose();
    }

    private static void Idle(Node host, Node observer, InternalNetRef reference)
    {
        for (var index = 0; index < 200; index++)
        {
            host.Players.TryResolveReference(reference, out _);
            host.Services.Views.Run(.25f); observer.Services.Views.Run(.25f);
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 10_000; index++)
        {
            host.Players.TryResolveReference(reference, out _);
            host.Services.Views.Run(.25f); observer.Services.Views.Run(.25f);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        if (allocated != 0)
            throw new InvalidOperationException("Lifetime view: unchanged hidden views and canonical references retain exact-zero idle allocation (allocated "
                + allocated + " bytes).");
    }

    private static void IdleAcrossRosterPublications(Node host)
    {
        var snapshot = host.Engine.Snapshot;
        // Allocate the equivalent snapshots before the probe. The runtime sees a new object on every publication.
        var first = Equivalent(snapshot);
        var second = Equivalent(snapshot);
        void Publish(int index)
        {
            host.Control.SnapshotOverride = (index & 1) == 0 ? first : second;
            host.Services.Views.Run(.25f);
        }
        try
        {
            for (var index = 0; index < 200; index++) Publish(index);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < 10_000; index++) Publish(index);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (allocated != 0)
                throw new InvalidOperationException("Lifetime view: distinct stable-admission roster publications allocate " + allocated + " bytes.");
        }
        finally { host.Control.SnapshotOverride = null; }
    }

    private static MultiplayerSessionSnapshot Equivalent(MultiplayerSessionSnapshot source) => new(source.Id, source.State,
        source.ProcessKind, source.ExecutionSides, source.LocalParticipantId, source.Participants, source.Tick, source.Seed,
        source.HostParticipantId, source.EndReason);

    private static MultiplayerParticipant Row(SessionEngine engine, ParticipantId id) => engine.Snapshot.Participants.Single(player => player.Id.Equals(id));
    private static IEntity Avatar(Node node, ParticipantId id) => node.Presence.TryGetAvatar(id, out var value) && value != null
        ? value : throw new InvalidOperationException("Expected validated engine avatar.");
    private static bool AvatarShown(Node node, IEntity entity) =>
        PresenceVisible(node, entity) && !node.Renderer.Hidden.Contains(entity);

    private static bool PresenceVisible(Node node, IEntity entity) =>
        node.Backend.Avatars.Any(avatar => avatar.IsAlive && ReferenceEquals(avatar.Entity, entity) && avatar.IsVisible);

    private static bool HasNametag(Node node, string displayName)
    {
        for (var slot = 0; slot < node.Presence.NametagCount; slot++)
            if (node.Presence.TryGetNametag(slot, out var tag) && tag.DisplayName == displayName) return true;
        return false;
    }

    private static IViewLease Lease(OperationResult<IViewLease> result) => result.TryGetValue(out var lease) ? lease
        : throw new InvalidOperationException("Expected view lease: " + result.ErrorCode);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("Lifetime view: " + message); }

    private sealed class Node : IDisposable
    {
        internal readonly SessionEngine Engine;
        internal readonly ParticipantId Id;
        internal readonly ReferenceEngineControl Control;
        internal readonly MultiplayerRuntime Runtime;
        internal readonly MultiplayerReplication Services;
        internal readonly MultiplayerPresence Presence;
        internal readonly FakePresenceBackend Backend = new() { RecordTrails = false };
        internal readonly MultiplayerPlayers Players;
        internal readonly Renderer Renderer;
        internal readonly Binding Binding;
        internal readonly QuietLog Log = new();
        internal bool SendEnabled = true;
        internal string? World = "world-one";
        internal int SceneVersion;
        internal Node(SessionEngine engine, EngineHarness harness)
        {
            Engine = engine; Id = engine.Snapshot.LocalParticipantId!.Value; Control = new ReferenceEngineControl(engine);
            var log = Log; Runtime = new MultiplayerRuntime(() => Control, new MultiplayerRuntimeHost("2545", "0.2.0", log), log);
            Runtime.SessionPhaseSource = () => SessionPhase.Running;
            Services = new MultiplayerReplication(Runtime);
            Presence = MultiplayerPresence.Install(Runtime, Services, Backend, () => harness.NowMicros);
            Presence.RunningWorld = () => World; Presence.RunningSpawn = () => TransformState.Identity; Presence.SceneVersion = () => SceneVersion;
            Players = MultiplayerPlayers.Install(Runtime, Presence); Services.Players = Players;
            Check(Runtime.Attach(Control), "real engine control attaches"); Runtime.BeforeModsUpdate(0);
            Renderer = new Renderer(this); Binding = new Binding(Renderer); Services.Views.Activate(Binding);
        }
        public void Dispose() { Runtime.Disconnect(); Renderer.Dispose(); }
    }

    private sealed class Binding(Renderer renderer, string world = "world-one") : IInternalWorldViewBinding
    {
        private readonly Dictionary<string, IEntity> authored = new(StringComparer.Ordinal);
        internal void Add(IEntity entity) => authored.Add(entity.Id, entity);
        internal void Replace(IEntity entity) => authored[entity.Id] = entity;
        public string WorldId => world;
        public IInternalWorldViewRenderer Renderer => renderer;
        public bool TryNameAuthored(IEntity entity, out string scene, out string id)
        { scene = "scene"; id = entity.Id; return authored.TryGetValue(id, out var current) && ReferenceEquals(current, entity); }
        public IEntity? ResolveAuthored(string scene, string id) => scene == "scene" && authored.TryGetValue(id, out var entity) ? entity : null;
        internal bool Owns(IEntity entity) => authored.TryGetValue(entity.Id, out var current) && ReferenceEquals(current, entity);
    }
    private sealed class Renderer(Node owner) : IInternalWorldViewRenderer
    {
        internal readonly HashSet<IEntity> Hidden = new();
        internal int HideCalls;
        internal int ReleaseCalls;
        internal bool FailNextHide;
        internal bool RefuseNextHide;
        private bool alive = true;
        internal bool Ending;
        internal bool RefuseAll;
        internal readonly HashSet<string> RefusedIds = new();
        internal int Refusals;
        public bool IsAlive => alive && !Ending;
        public bool FogAvailable => true;
        public bool EnvironmentAvailable => true;
        public IReadOnlyList<string> Presets => Array.Empty<string>();
        public event Action Stopped = delegate { };
        public bool HasPreset(string preset) => false;
        public OperationResult<bool> SetEnvironment(string? preset) => OperationResult<bool>.Success(true);
        public OperationResult<bool> SetFog(VisionFog? fog) => OperationResult<bool>.Success(true);
        public OperationResult<IDisposable> Hide(IEntity entity)
        {
            if (FailNextHide) { FailNextHide = false; throw new InvalidOperationException("Simulated native hide failure."); }
            if (RefuseNextHide) { RefuseNextHide = false; return OperationResult<IDisposable>.Failure(ModErrorCode.Unavailable, "Simulated refusal."); }
            if (RefuseAll || RefusedIds.Contains(entity.Id))
            { Refusals++; return OperationResult<IDisposable>.Failure(ModErrorCode.Unavailable, "Simulated steady refusal."); }
            if (!entity.IsAlive || !(owner.Binding.Owns(entity) || owner.Services.Views.IsCurrentAvatar(entity, this)))
                return OperationResult<IDisposable>.Failure(ModErrorCode.Unavailable, "Target is not current.");
            HideCalls++; Hidden.Add(entity); return OperationResult<IDisposable>.Success(new Hold(this, entity));
        }
        public void Dispose() { if (!alive) return; alive = false; Stopped(); Hidden.Clear(); }
        private sealed class Hold(Renderer renderer, IEntity entity) : IDisposable
        {
            private bool active = true;
            public void Dispose()
            {
                if (!active) return;
                active = false;
                renderer.ReleaseCalls++;
                renderer.Hidden.Remove(entity);
            }
        }
    }
    private sealed class QuietLog : IMultiplayerLog
    {
        public void Info(string message) { }
        internal int ViewFailures;
        internal int Unreadable;
        internal int ViewWaits;
        public void Warn(string message)
        {
            if (message.Contains("could not hold a new avatar")) ViewFailures++;
            if (message.Contains("could not be read")) Unreadable++;
            if (message.Contains("not drawn until the host's world view arrives")) ViewWaits++;
        }
        public void Error(string message) => throw new InvalidOperationException(message);
    }
}
