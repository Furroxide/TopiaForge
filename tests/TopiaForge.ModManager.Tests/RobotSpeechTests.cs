using System;
using System.Linq;
using System.Text;
using System.Threading;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.RobotKit;

namespace TopiaForge.ModManager.Tests
{
    internal static class RobotSpeechTests
    {
        internal static void Run()
        {
            RobotSpeechNativeLifetimeTests.Run();
            PrivateEarshot(); Ownership(); MalformedAndDuplicates(); Lifetimes(); Recovery(); Quotas();
            WorldAndStream(); FreshJoin(); NativeRouting(); WorldWithdrawal(); Offline(); ProviderLifetime(); ExpiryAndBackpressure();
            Console.WriteLine("Robot speech: 13 focused groups passed.");
        }
        private static void Check(bool value, string reason)
        { if (!value) throw new InvalidOperationException("Robot speech: " + reason); }

        private static void PrivateEarshot()
        {
            using var rig = new RobotSpeechRig(2);
            rig.Move(rig.Guests[1], 50);
            rig.Log.Clear();
            rig.Robot.SayAsync("A private line."); rig.World.Frames(5);
            Check(rig.Canonical.SpeechPlays == 1 && rig.Guests[0].Body.SpeechPlays == 1 && rig.Guests[1].Body.SpeechPlays == 0,
                "only host and near recipient synthesize once");
            var lines = rig.Log.Where(p => p.Payload[1] == RobotSpeechWire.Line).ToArray();
            Check(lines.Length == 1 && lines[0].To.Equals(rig.Guests[0].Peer.Control.Id), "text is privately sent only to the effective recipient");
            var bytes = Encoding.UTF8.GetString(lines[0].Payload);
            Check(bytes.Contains("A private line.") && !bytes.Contains("listener0") && !bytes.Contains("listener1"), "no audience list is serialized");
            Check(NetworkRobotSpeech.Near(Vec3.Zero, new Vec3(20, 0, 0)) && !NetworkRobotSpeech.Near(Vec3.Zero, new Vec3(20.01f, 0, 0))
                && !NetworkRobotSpeech.Near(Vec3.Zero, new Vec3(float.NaN, 0, 0)) && !NetworkRobotSpeech.Near(Vec3.Zero, new Vec3(0, 21, 0)), "finite spherical earshot boundary");
            Check(rig.Guests.All(g => g.Body.BrainMode == RobotBrainMode.Dormant), "synthesis never awakens replica brains");
            Check(rig.World.Host.Log.Lines.Concat(rig.Guests.SelectMany(g => g.Peer.Log.Lines)).All(line => !line.Contains("A private line.")),
                "speech content never reaches runtime diagnostics");
            rig.Later(); rig.Canonical.Visible = false; var before = rig.Log.Count;
            Check(rig.Robot.SayAsync("Hidden.").Result.ErrorCode == ModErrorCode.Unavailable && rig.Log.Skip(before).All(p => p.Payload[1] != RobotSpeechWire.Line), "hidden bodies disclose nothing");
        }
        private static void Ownership()
        {
            using var rig = new RobotSpeechRig();
            var otherScope = new AuthorityMod(rig.World.Host, ModExecutionContext.GamemodeServer);
            Check(otherScope.Seam.SayAsync(rig.Canonical, RobotReplicationWire.Kind, "Wrong scope", default).Result.ErrorCode == ModErrorCode.NotAuthoritative,
                "even the same mod cannot borrow another scope's speech target");
            var guest = new AuthorityMod(rig.Guests[0].Peer, ModExecutionContext.Participant);
            Check(guest.Seam.SayAsync(rig.Guests[0].Body, RobotReplicationWire.Kind, "Guest", default).Result.ErrorCode == ModErrorCode.NotAuthoritative,
                "guest cannot originate NPC speech");
            Check(rig.Owner.Seam.SayAsync(rig.Canonical, "other.provider", "Wrong provider", default).Result.ErrorCode == ModErrorCode.NotAuthoritative,
                "provider kind is bound to ownership");
            var made = rig.Guests[0].Robots.Provider.Spawn(guest.Seam, new RobotAgentSpawnRequest(Vec3.Zero)).Value!;
            rig.World.Frames(30);
            var guestOwned = rig.Host.Robots.Agents.Values.Last();
            Check(rig.Owner.Seam.SayAsync(guestOwned, RobotReplicationWire.Kind, "Borrowed", default).Result.ErrorCode == ModErrorCode.NotAuthoritative,
                "guest-owned canonical body is excluded");
            made.Dispose(); otherScope.Lifetime.Dispose(); guest.Lifetime.Dispose();
        }
        private static void MalformedAndDuplicates()
        {
            using var rig = new RobotSpeechRig();
            var guest = rig.Guests[0]; guest.Peer.Paused = true;
            rig.Robot.SayAsync("Only once."); rig.World.Frames(5);
            var bytes = rig.LastLine;
            for (var size = 0; size < bytes.Length; size++)
            {
                var reader = new WorldWireReader(); reader.Reset(bytes.Take(size).ToArray());
                if (reader.TryReadHeader(out var type)) Check(!guest.Peer.World.Speech.Handle(type, rig.World.Host.Control.Id, reader), "every truncated frame is rejected");
            }
            var tail = bytes.Concat(new byte[] { 7 }).ToArray();
            var badUtf8 = (byte[])bytes.Clone(); badUtf8[badUtf8.Length - 1] = 0xff;
            var badVersion = (byte[])bytes.Clone(); badVersion[2] = 9;
            var oversized = new byte[RobotSpeechWire.MaximumBytes + 1]; Array.Copy(bytes, oversized, bytes.Length);
            foreach (var malformed in new[] { tail, badUtf8, badVersion, oversized })
            {
                var reader = new WorldWireReader(); reader.Reset(malformed); reader.TryReadHeader(out var type);
                Check(!guest.Peer.World.Speech.Handle(type, rig.World.Host.Control.Id, reader), "fresh malformed frames fail before duplicate suppression");
            }
            Check(guest.Body.SpeechPlays == 0, "malformed messages have no native side effects");
            var forged = new WorldWireReader(); forged.Reset(bytes); forged.TryReadHeader(out var kind);
            Check(!guest.Peer.World.Speech.Handle(kind, new ParticipantId("stranger"), forged), "authenticated host sender required");
            var hostReader = new WorldWireReader(); hostReader.Reset(bytes); hostReader.TryReadHeader(out kind);
            Check(!rig.World.Host.World.Speech.Handle(kind, guest.Peer.Control.Id, hostReader), "host rejects incoming speech from guests");
            guest.Peer.Paused = false; rig.World.Frames(5);
            Check(guest.Body.SpeechPlays == 1, "malformed frames do not consume the valid nonce");
            guest.Channel.Inject(rig.World.Host.Control.Id, bytes); rig.World.Frames(3);
            Check(guest.Body.SpeechPlays == 1, "duplicate nonce is suppressed");
        }
        private static void Lifetimes()
        {
            using var rig = new RobotSpeechRig();
            using var cancellation = new CancellationTokenSource();
            rig.Robot.SayAsync("First.", cancellation.Token); rig.World.Frames(5);
            cancellation.Cancel(); rig.World.Frames(5);
            Check(rig.Canonical.SpeechTokens[0].IsCancellationRequested && rig.Guests[0].Body.SpeechTokens[0].IsCancellationRequested,
                "caller cancellation reaches active native synthesis on both peers");
            Check(rig.Robot.SayAsync("Cancellation cannot evade pacing").Result.ErrorCode == ModErrorCode.RateLimited,
                "per-robot pace survives cancellation");
            rig.Later(); rig.Robot.SayAsync("Second."); rig.World.Frames(4);
            rig.Later(); rig.Robot.SayAsync("Replacement."); rig.World.Frames(4);
            Check(rig.Canonical.SpeechTokens[1].IsCancellationRequested && rig.Guests[0].Body.SpeechTokens[1].IsCancellationRequested,
                "retargeting a line cancels its predecessor");
            rig.Owner.Lifetime.Dispose(); rig.World.Frames(4);
            Check(rig.Canonical.SpeechTokens.Last().IsCancellationRequested && rig.Guests[0].Body.SpeechTokens.Last().IsCancellationRequested,
                "scope disposal cancels every active copy");
            Check(rig.Robot.SayAsync("After disposal").Result.ErrorCode == ModErrorCode.Cancelled, "disposed context rejects speech");
        }
        private static void Recovery()
        {
            using var rig = new RobotSpeechRig();
            rig.Canonical.MissingVoice = true;
            Check(rig.Robot.SayAsync("Unavailable").Result.ErrorCode == ModErrorCode.Unavailable, "missing native descriptor fails explicitly");
            Check(rig.Log.All(p => p.Payload[1] != RobotSpeechWire.Line), "missing host voice discloses nothing");
            rig.Canonical.MissingVoice = false; rig.Guests[0].Body.MissingVoice = true;
            rig.Robot.SayAsync("Fresh line"); rig.World.Frames(4);
            Check(rig.Guests[0].Body.SpeechPlays == 0, "missing guest binding does not invent a voice");
            rig.Guests[0].Body.MissingVoice = false; rig.World.Frames(4);
            Check(rig.Guests[0].Body.SpeechPlays == 0, "restoring a provider does not replay old text");
            rig.Later(); rig.Robot.SayAsync("Recovered"); rig.World.Frames(4);
            Check(rig.Guests[0].Body.SpeechPlays == 1, "fresh call recovers after native binding appears");
            rig.Later(); rig.Guests[0].Body.Voice = new string('b', 64);
            rig.Robot.SayAsync("Mismatched"); rig.World.Frames(4);
            Check(rig.Guests[0].Body.SpeechPlays == 1, "different bound model fingerprint cannot synthesize");
            var reader = new WorldWireReader(); reader.Reset(rig.LastLine);
            reader.TryReadHeader(out var type); reader.TryReadByte(out _); reader.TryReadUInt64(out var instance);
            reader.TryReadString(32, out var challenge); reader.TryReadUInt64(out var nonce); reader.TryReadVarUInt(uint.MaxValue, out _);
            reader.TryReadUInt64(out var stamp); reader.TryReadString(128, out var mod); reader.TryReadString(128, out var kind);
            reader.TryReadString(64, out var voice); reader.TryReadString(2048, out var text);
            var missing = new WorldWireWriter(RobotSpeechWire.MaximumBytes);
            RobotSpeechWire.Header(missing, type, instance, challenge, nonce + 1, uint.MaxValue);
            missing.WriteUInt64(stamp); missing.WriteString(mod); missing.WriteString(kind); missing.WriteString(voice); missing.WriteString(text);
            rig.Guests[0].Body.Voice = voice;
            reader.Reset(missing.Segment.ToArray()); reader.TryReadHeader(out type);
            Check(!rig.Guests[0].Peer.World.Speech.Handle(type, rig.World.Host.Control.Id, reader)
                && rig.Guests[0].Body.SpeechPlays == 1, "missing canonical descriptor drops a fresh valid line without native work");
        }
        private static void Quotas()
        {
            using var rig = new RobotSpeechRig();
            foreach (var text in new[] { "", "<b>markup</b>", new string('x', 513), "bad\0text", "\ud800" })
                Check(rig.Robot.SayAsync(text).Result.ErrorCode == ModErrorCode.InvalidArgument, "bounded plain text and strict UTF-8 required");
            rig.Robot.SayAsync(new string('x', 512));
            Check(rig.Robot.SayAsync("Too soon").Result.ErrorCode == ModErrorCode.RateLimited, "per-entity rate limit");
            var refused = 0;
            for (var i = 0; i < 80; i++) if (rig.Robot.SayAsync("Flood").Result.ErrorCode == ModErrorCode.RateLimited) refused++;
            Check(refused == 80 && rig.World.Host.World.Speech.ActiveCount <= 32, "bounded outstanding work with no deferred text queue");
            rig.World.Clock.Advance(16_000_000); rig.World.Frames(3);
            Check(rig.World.Host.World.Speech.ActiveCount == 0, "deadline releases retained work");
            using var full = new RobotSpeechRig(0);
            for (var i = 0; i < 32; i++)
            {
                if (i >= 4) full.World.Clock.Advance(500_000);
                var robot = full.Host.Robots.Provider.Spawn(full.Owner.Seam, new RobotAgentSpawnRequest(Vec3.Zero)).Value!;
                Check(!robot.SayAsync("Concurrent speech").IsCompleted, "native work admitted up to the active cap");
            }
            full.World.Clock.Advance(500_000);
            var overflow = full.Host.Robots.Provider.Spawn(full.Owner.Seam, new RobotAgentSpawnRequest(Vec3.Zero)).Value!;
            Check(overflow.SayAsync("Overflow").Result.ErrorCode == ModErrorCode.RateLimited && full.World.Host.World.Speech.ActiveCount == 32,
                "active cap is enforced independently of the replenished rate budget");
            var readiness = rig.Guests[0].Channel.Log!.Last(p => p.Payload[1] == RobotSpeechWire.Ready).Payload;
            var reader = new WorldWireReader(); reader.Reset(readiness);
            reader.TryReadHeader(out _); reader.TryReadByte(out _); reader.TryReadUInt64(out var instance);
            reader.TryReadString(32, out var challenge);
            var writer = new WorldWireWriter(RobotSpeechWire.MaximumBytes);
            RobotSpeechWire.Header(writer, RobotSpeechWire.Withdraw, instance, challenge, 0, 0);
            var accepted = 0;
            for (var i = 0; i < 12; i++)
            {
                reader.Reset(writer.Segment.ToArray()); reader.TryReadHeader(out var type);
                if (rig.World.Host.World.Speech.Handle(type, rig.Guests[0].Peer.Control.Id, reader)) accepted++;
            }
            Check(accepted > 0 && accepted <= 4, "withdrawal cannot reset its independent handshake budget");
        }
        private static void WorldAndStream()
        {
            using var rig = new RobotSpeechRig();
            rig.Robot.SayAsync("Before reconnect"); rig.World.Frames(5); var bytes = rig.LastLine;
            var guest = rig.Guests[0]; var first = guest.Body;
            rig.World.Network.LoseLink(guest.Peer.Control); rig.World.Frames(4);
            Check(first.SpeechTokens.Last().IsCancellationRequested, "lost stream cancels playback");
            rig.World.Network.Restore(guest.Peer.Control); rig.World.Frames(40);
            var before = guest.Robots.Agents.Values.Sum(a => a.SpeechPlays);
            guest.Channel.Inject(rig.World.Host.Control.Id, bytes); rig.World.Frames(5);
            Check(guest.Robots.Agents.Values.Sum(a => a.SpeechPlays) == before, "old challenge cannot replay into a new stream");
            rig.Later(); rig.Robot.SayAsync("Current stream"); rig.World.Frames(5);
            Check(guest.Body.SpeechPlays > 0, "fresh line after reconnect works");
            guest.CurrentWorld = "other-world"; rig.World.Frames(5);
            Check(guest.Body.SpeechTokens.Last().IsCancellationRequested, "world change cancels native playback");
            rig.Host.Scene++; rig.World.Frames(3);
            Check(rig.Canonical.SpeechTokens.Last().IsCancellationRequested, "host scene replacement cancels local playback");
        }
        private static void FreshJoin()
        {
            using var rig = new RobotSpeechRig();
            rig.Robot.SayAsync("No history"); rig.World.Frames(4);
            var late = rig.Add("late");
            Check(late.Body.SpeechPlays == 0 && rig.Log.Where(p => p.To.Equals(late.Peer.Control.Id)).All(p => p.Payload[1] != RobotSpeechWire.Line),
                "late join receives no historical speech");
            rig.Later(); rig.Robot.SayAsync("New speech"); rig.World.Frames(4);
            Check(late.Body.SpeechPlays == 1, "late join hears only a new authorized line");
            rig.Robot.Despawn(); rig.World.Frames(5);
            Check(late.Body.SpeechTokens.Last().IsCancellationRequested, "despawn invalidates the stable entity lifetime");
        }
        private static void NativeRouting()
        {
            using var rig = new RobotSpeechRig();
            Check(rig.Canonical.Route != null && rig.Guests[0].Body.Route == null, "only the author-owned native body has an originating route");
            rig.Canonical.Route!("Native author call", default); rig.World.Frames(4);
            Check(rig.Canonical.SpeechPlays == 1 && rig.Guests[0].Body.SpeechPlays == 1, "native author entry uses the same one-shot private path");
            rig.Move(rig.Host, 60); rig.Later(); rig.Robot.SayAsync("Host outside earshot"); rig.World.Frames(4);
            Check(rig.Canonical.SpeechPlays == 1 && rig.Guests[0].Body.SpeechPlays == 2, "host synthesizes only when locally authorized");
        }

        private static void WorldWithdrawal()
        {
            using var rig = new RobotSpeechRig();
            var guest = rig.Guests[0];
            guest.CurrentWorld = "other-world"; rig.World.Frames(5); rig.Log.Clear();
            rig.Robot.SayAsync("Wrong world"); rig.World.Frames(5);
            Check(guest.Body.SpeechPlays == 0 && rig.Log.All(p => p.Payload[1] != RobotSpeechWire.Line),
                "same-stream world withdrawal prevents text disclosure, not just playback");
            guest.CurrentWorld = "world-one"; rig.World.Frames(8); rig.Later();
            rig.Robot.SayAsync("Current world"); rig.World.Frames(4);
            Check(guest.Body.SpeechPlays == 1, "new challenge restores current-world delivery");
            guest.CurrentWorld = null; rig.World.Frames(5); rig.Later(); rig.Log.Clear();
            rig.Robot.SayAsync("At menu"); rig.World.Frames(4);
            Check(rig.Log.All(p => p.Payload[1] != RobotSpeechWire.Line), "menu withdrawal fails closed");
        }
        private static void Offline()
        {
            var world = new WorldRig(); world.UseScene(scene => scene.AddScene("Yard"));
            var host = new RobotSpeechRig.SpeechPeer(world, world.Host);
            var owner = new AuthorityMod(world.Host, ModExecutionContext.Process, networkClass: ModNetworkClass.Standalone);
            var robot = host.Robots.Provider.Spawn(owner.Seam, new RobotAgentSpawnRequest(Vec3.Zero)).Value!;
            robot.SayAsync("Solo"); world.Frames(3);
            Check(host.Body.SpeechPlays == 1 && host.Channel.Log!.Count == 0, "standalone speech remains local without wire traffic");
            var intruder = new AuthorityMod(world.Host, networkClass: ModNetworkClass.Standalone);
            Check(intruder.Seam.SayAsync(host.Body, RobotReplicationWire.Kind, "Other context", default).Result.ErrorCode == ModErrorCode.NotAuthoritative,
                "offline ownership is exact-context too");
            host.Body.Visible = false; world.Frames(2);
            Check(host.Body.SpeechTokens[0].IsCancellationRequested, "offline frame maintenance cancels hidden playback");
            owner.Lifetime.Dispose(); intruder.Lifetime.Dispose(); world.Host.World.Speech.Reset();
        }
        private static void ProviderLifetime()
        {
            using var rig = new RobotSpeechRig();
            rig.Robot.SayAsync("Provider lifetime"); rig.World.Frames(4);
            rig.World.Host.Lifetime.Dispose(); rig.World.Frames(4);
            Check(rig.Canonical.SpeechTokens[0].IsCancellationRequested && rig.Guests[0].Body.SpeechTokens[0].IsCancellationRequested,
                "provider disposal cancels outstanding speech");
            Check(rig.Robot.SayAsync("No provider").Result.ErrorCode == ModErrorCode.Unavailable,
                "missing provider descriptor returns explicit recovery instead of falling back");
        }
        private static void ExpiryAndBackpressure()
        {
            using var rig = new RobotSpeechRig();
            var guest = rig.Guests[0]; guest.Peer.Paused = true;
            rig.Robot.SayAsync("Delayed speech"); rig.World.Frames(3);
            rig.World.Clock.Advance(3_000_000); guest.Peer.Paused = false; rig.World.Frames(5);
            Check(guest.Body.SpeechPlays == 0, "receiver-issued freshness stamp rejects delayed lines without synchronized clocks");
            rig.World.Network.HostWindowMessages = 0; rig.Later(); rig.Robot.SayAsync("Backpressure"); rig.World.Frames(4);
            rig.World.Network.HostWindowMessages = 1024; rig.World.Frames(8);
            Check(guest.Body.SpeechPlays == 0, "a full window drops the event without stale replay");
            rig.Later();
            var robot = rig.Host.Robots.Provider.Spawn(rig.Owner.Seam, new RobotAgentSpawnRequest(Vec3.Zero)).Value!;
            robot.SayAsync("Immediate speech"); rig.World.Frames(6);
            Check(guest.Robots.Agents.Values.Last().SpeechPlays == 1, "spawn descriptor precedes immediate author speech automatically");
        }
    }
}
