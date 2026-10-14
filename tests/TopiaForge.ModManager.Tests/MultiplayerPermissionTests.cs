using System;
using TopiaForge.ModManager.Core.Multiplayer;
using static TopiaForge.ModManager.Tests.MultiplayerModelAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>Integer token buckets and the per-player request policy (WS3.5).</summary>
    internal static class MultiplayerPermissionTests
    {
        private const int Remote = 2;
        private const int Other = 3;

        public static void Run()
        {
            TokenBucketsRefillExactly();
            TokenBucketsResistClockAbuse();
            DefaultRulesProtectOtherPlayersAndTheWorld();
            PoliciesAreImmutable();
            TheHostPlayerIsNeverLimited();
            RemotePlayersAreRateLimitedAndQuotaBound();
            FloodingForbiddenRequestsCostsTokens();
            Console.WriteLine("MultiplayerPermissionTests passed.");
        }

        private static void TokenBucketsRefillExactly()
        {
            var bucket = new TokenBucket(3, 2, 0);
            Equal(3_000_000L, bucket.AvailableMicroTokens(0), "starts full");
            True(bucket.TryTake(0) && bucket.TryTake(0) && bucket.TryTake(0), "the burst");
            True(!bucket.TryTake(0), "then empty");
            Equal(500_000L, bucket.MicrosUntilAvailable(0), "two tokens a second is one per half second");
            Equal(1_000_000L, bucket.MicrosUntilAvailable(0, 2), "two tokens take a second");
            True(!bucket.TryTake(499_999), "one microsecond short");
            Equal(999_998L, bucket.AvailableMicroTokens(499_999), "micro-tokens accrue at the rate");
            True(bucket.TryTake(500_000), "exactly on time");
            Equal(0L, bucket.MicrosUntilAvailable(3_600_000_000L, 3), "a long idle refills to capacity");
            Equal(3_000_000L, bucket.AvailableMicroTokens(3_600_000_000L), "and no further");
            True(bucket.TryTake(3_600_000_000L, 3), "a multi-token cost");
            Throws<ArgumentOutOfRangeException>(() => bucket.TryTake(3_600_000_000L, 4), "a cost above capacity can never succeed");

            var allowance = new TokenBucket(2, 0, 0);
            True(allowance.TryTake(0) && allowance.TryTake(10), "a fixed allowance");
            True(!allowance.TryTake(1_000_000_000_000L), "never refills");
            Equal(long.MaxValue, allowance.MicrosUntilAvailable(5), "and says so");
        }

        private static void TokenBucketsResistClockAbuse()
        {
            var bucket = new TokenBucket(1, 1, 1_000_000);
            True(bucket.TryTake(1_000_000), "the token");
            True(!bucket.TryTake(500_000), "time running backwards refills nothing");
            True(!bucket.TryTake(1_999_999), "and does not move the refill point back");
            True(bucket.TryTake(2_000_000), "one second after the last real time");

            var partial = new TokenBucket(2, 1, 0);
            True(partial.TryTake(1_000_000), "one of two tokens");
            True(partial.TryTake(0), "time running backwards drains nothing either");
            True(!partial.TryTake(0), "both are spent");
            Throws<ArgumentOutOfRangeException>(() => bucket.TryTake(-1), "negative time");
            Throws<ArgumentOutOfRangeException>(() => new TokenBucket(0, 1, 0), "capacity zero");
            Throws<ArgumentOutOfRangeException>(() => new TokenBucket(1, -1, 0), "negative rate");
            Throws<ArgumentOutOfRangeException>(() => new TokenBucket(TokenBucket.MaximumCapacity + 1, 1, 0), "capacity bound");
            Throws<ArgumentOutOfRangeException>(() => new TokenBucket(1, TokenBucket.MaximumTokensPerSecond + 1, 0), "rate bound");

            var extreme = new TokenBucket(TokenBucket.MaximumCapacity, TokenBucket.MaximumTokensPerSecond, 0);
            True(extreme.TryTake(0, TokenBucket.MaximumCapacity), "drain the largest bucket");
            Equal(TokenBucket.MaximumCapacity * TokenBucket.MicroTokensPerToken, extreme.AvailableMicroTokens(long.MaxValue), "the largest refill cannot overflow");
        }

        private static void DefaultRulesProtectOtherPlayersAndTheWorld()
        {
            var policy = PermissionPolicy.Default;
            True(policy.Permits(NetRequestKind.Spawn, NetTargetRelation.None), "players may spawn");
            Expect(NetRequestKind.SetTransform, self: true, server: false, other: false);
            Expect(NetRequestKind.Destroy, self: true, server: false, other: false);
            Expect(NetRequestKind.MotionGrab, self: true, server: true, other: true);
            Expect(NetRequestKind.Interaction, self: true, server: true, other: true);
            Equal(64, policy.MaximumLiveSpawnsPerPlayer, "spawn quota");
            Equal(8, policy.LimitFor(NetRequestKind.Spawn).Burst, "spawn burst");
            Equal(4, policy.LimitFor(NetRequestKind.Spawn).PerSecond, "spawn rate");
            Equal(30, policy.LimitFor(NetRequestKind.SetTransform).PerSecond, "transform rate");
            Throws<ArgumentException>(() => policy.Permits(NetRequestKind.Spawn, NetTargetRelation.Self), "a spawn has no target");
            Throws<ArgumentException>(() => policy.Permits(NetRequestKind.Destroy, NetTargetRelation.None), "a destroy needs a target");
            Throws<ArgumentOutOfRangeException>(() => policy.Permits((NetRequestKind)9, NetTargetRelation.Self), "undefined kind");
            Throws<ArgumentOutOfRangeException>(() => policy.LimitFor((NetRequestKind)9), "undefined kind limit");

            void Expect(NetRequestKind kind, bool self, bool server, bool other)
            {
                Equal(self, policy.Permits(kind, NetTargetRelation.Self), kind + " on one's own spawn");
                Equal(server, policy.Permits(kind, NetTargetRelation.Server), kind + " on the world");
                Equal(other, policy.Permits(kind, NetTargetRelation.OtherPlayer), kind + " on another player's spawn");
            }
        }

        private static void PoliciesAreImmutable()
        {
            var original = PermissionPolicy.Default;
            var lenient = original.WithRule(NetRequestKind.Destroy, NetTargetRelation.Server, true);
            True(lenient.Permits(NetRequestKind.Destroy, NetTargetRelation.Server), "the copy allows it");
            True(!original.Permits(NetRequestKind.Destroy, NetTargetRelation.Server), "the original does not");
            var slow = original.WithLimit(NetRequestKind.Spawn, new NetRequestLimit(1, 0));
            Equal(1, slow.LimitFor(NetRequestKind.Spawn).Burst, "a copy with a new limit");
            Equal(8, original.LimitFor(NetRequestKind.Spawn).Burst, "the original limit");
            Equal(0, original.WithMaximumLiveSpawns(0).MaximumLiveSpawnsPerPlayer, "a copy with no spawns allowed");
            Throws<ArgumentException>(() => original.WithLimit(NetRequestKind.Spawn, default), "an empty limit");
            Throws<ArgumentOutOfRangeException>(() => original.WithMaximumLiveSpawns(-1), "a negative quota");
            Throws<ArgumentOutOfRangeException>(() => new NetRequestLimit(0, 1), "burst zero");
        }

        private static void TheHostPlayerIsNeverLimited()
        {
            var limiter = new PlayerRequestLimiter(PermissionPolicy.Default);
            for (var index = 0; index < 1000; index++)
            {
                Equal(NetPermission.Allowed, limiter.Evaluate(1, true, NetRequestKind.Destroy, NetTargetRelation.OtherPlayer, 0, 0), "the host is canonical");
            }

            Equal(NetPermission.Allowed, limiter.Evaluate(1, true, NetRequestKind.Spawn, NetTargetRelation.None, 10_000, 0), "and has no quota");
            Equal(0, limiter.TrackedParticipants, "the host never gets buckets");
        }

        private static void RemotePlayersAreRateLimitedAndQuotaBound()
        {
            var limiter = new PlayerRequestLimiter(PermissionPolicy.Default);
            for (var index = 0; index < 8; index++)
            {
                Equal(NetPermission.Allowed, limiter.Evaluate(Remote, false, NetRequestKind.Spawn, NetTargetRelation.None, index, 0), "spawn burst " + index);
            }

            Equal(NetPermission.RateLimited, limiter.Evaluate(Remote, false, NetRequestKind.Spawn, NetTargetRelation.None, 8, 0), "past the burst");
            Equal(NetPermission.Allowed, limiter.Evaluate(Remote, false, NetRequestKind.Interaction, NetTargetRelation.Server, 8, 0), "kinds have separate buckets");
            Equal(NetPermission.Allowed, limiter.Evaluate(Other, false, NetRequestKind.Spawn, NetTargetRelation.None, 0, 0), "players have separate buckets");
            Equal(NetPermission.RateLimited, limiter.Evaluate(Remote, false, NetRequestKind.Spawn, NetTargetRelation.None, 8, 249_999), "a quarter second minus one");
            Equal(NetPermission.Allowed, limiter.Evaluate(Remote, false, NetRequestKind.Spawn, NetTargetRelation.None, 8, 250_000), "four a second");
            Equal(NetPermission.QuotaExceeded, limiter.Evaluate(Remote, false, NetRequestKind.Spawn, NetTargetRelation.None, 64, 10_000_000), "the live-spawn quota");
            Equal(NetPermission.Allowed, limiter.Evaluate(Remote, false, NetRequestKind.Spawn, NetTargetRelation.None, 63, 10_000_000), "just under the quota");
            Equal(NetPermission.NotPermitted, limiter.Evaluate(Remote, false, NetRequestKind.Destroy, NetTargetRelation.OtherPlayer, 0, 10_000_000), "another player's spawn");
            Equal(NetPermission.NotPermitted, limiter.Evaluate(Remote, false, NetRequestKind.SetTransform, NetTargetRelation.Server, 0, 10_000_000), "the world");
            Equal(NetPermission.Allowed, limiter.Evaluate(Remote, false, NetRequestKind.Destroy, NetTargetRelation.Self, 0, 10_000_000), "one's own spawn");
            Equal(2, limiter.TrackedParticipants, "two remote players tracked");
            True(limiter.Forget(Remote) && limiter.TrackedParticipants == 1, "a departed player is forgotten");
            Throws<ArgumentOutOfRangeException>(() => limiter.Evaluate(0, false, NetRequestKind.Interaction, NetTargetRelation.Self, 0, 0), "participant zero");
            Throws<ArgumentOutOfRangeException>(() => limiter.Evaluate(Remote, false, NetRequestKind.Spawn, NetTargetRelation.None, -1, 0), "negative spawns");
            Throws<ArgumentException>(() => limiter.Evaluate(Remote, false, NetRequestKind.Spawn, NetTargetRelation.Self, 0, 0), "a spawn with a target");
            Throws<ArgumentNullException>(() => new PlayerRequestLimiter(null!), "policy");
        }

        private static void FloodingForbiddenRequestsCostsTokens()
        {
            var limiter = new PlayerRequestLimiter(PermissionPolicy.Default);
            for (var index = 0; index < 8; index++)
            {
                Equal(NetPermission.NotPermitted, limiter.Evaluate(Remote, false, NetRequestKind.Destroy, NetTargetRelation.OtherPlayer, 0, 0), "forbidden " + index);
            }

            Equal(NetPermission.RateLimited, limiter.Evaluate(Remote, false, NetRequestKind.Destroy, NetTargetRelation.Self, 0, 0), "the flood spent the player's destroy budget");
            Equal(NetPermission.Allowed, limiter.Evaluate(Other, false, NetRequestKind.Destroy, NetTargetRelation.Self, 0, 0), "and nobody else's");
            for (var index = 0; index < 1000; index++) limiter.Evaluate(Remote, false, NetRequestKind.Destroy, NetTargetRelation.OtherPlayer, 0, 1);
            Equal(NetPermission.RateLimited, limiter.Evaluate(Remote, false, NetRequestKind.Destroy, NetTargetRelation.Self, 0, 1), "a sustained flood stays throttled");
            Equal(NetPermission.Allowed, limiter.Evaluate(Remote, false, NetRequestKind.Destroy, NetTargetRelation.Self, 0, 250_001), "one token later");
        }
    }
}
