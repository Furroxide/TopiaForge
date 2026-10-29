using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class AvatarAppearanceTests
    {
        private static int assertions;
        private static readonly AvatarAppearance Red = new AvatarAppearance(new RgbaColor(1, 0, 0), new RgbaColor(0.1f, 0.2f, 0.3f), "Cap");
        private static readonly AvatarAppearance Blue = new AvatarAppearance(new RgbaColor(0, 0, 1), RgbaColor.White, "", AvatarAccessorySlot.Glasses);
        internal static void Run()
        {
            assertions = 0;
            PoweredDownCollision();
            ValuesAndFake();
            LeasesAndBounds();
            WireAndPrivacy();
            ReplicationAndRestoration();
            AuthorityAndIncarnations();
            BodyAndBackpressure();
            BoundedPublication();
            Console.WriteLine("AvatarAppearanceTests passed: " + assertions + " assertions.");
        }
        private static void Check(bool condition, string message)
        { assertions++; if (!condition) throw new InvalidOperationException("Avatar appearance: " + message); }
        private static IPlayerAppearanceLease Lease(OperationResult<IPlayerAppearanceLease> result)
        { Check(result.Succeeded, "lease succeeds: " + result.ErrorMessage); return result.Value!; }
        private static void Throws(Action action)
        {
            try { action(); } catch (ArgumentException) { assertions++; return; }
            throw new InvalidOperationException("Invalid appearance was accepted.");
        }
        private static void ValuesAndFake()
        {
            Throws(() => new AvatarAppearance(new RgbaColor(float.NaN, 0, 0), RgbaColor.White));
            Throws(() => new AvatarAppearance(RgbaColor.Clear, RgbaColor.White));
            Throws(() => new AvatarAppearance(RgbaColor.White, new RgbaColor(0, float.PositiveInfinity, 0)));
            Throws(() => new AvatarAppearance(RgbaColor.White, RgbaColor.White, "../asset"));
            Throws(() => new AvatarAppearance(RgbaColor.White, RgbaColor.White, new string('a', 65)));
            Throws(() => new AvatarAppearance(RgbaColor.White, RgbaColor.White, accessorySlot: (AvatarAccessorySlot)99));
            using var fake = new FakePlayerDirectory();
            var target = fake.Add(new ParticipantId("target"), "Sam");
            var source = fake.Local!;
            Check(target.Appearance.TryGet(out var original), "fake original is readable");
            using var sourceLook = Lease(source.Appearance.Set(Red));
            var copy = Lease(target.Appearance.CopyFrom(source));
            using var sourceChanged = Lease(source.Appearance.Set(Blue));
            Check(target.Appearance.TryGet(out var copied) && Red.Equals(copied), "copy snapshots instead of following source");
            copy.Dispose();
            Check(target.Appearance.TryGet(out var restored) && original!.Equals(restored), "copy disposal restores target original");
            using var foreign = new FakePlayerDirectory();
            Check(target.Appearance.CopyFrom(foreign.Local!).ErrorCode == ModErrorCode.InvalidArgument, "cross-directory copy refused");
            fake.IsAuthoritative = false;
            Check(source.Appearance.Set(Red).ErrorCode == ModErrorCode.NotAuthoritative, "client cannot change its own look");
            Check(target.Appearance.SetNameplate("Hidden", false).ErrorCode == ModErrorCode.NotAuthoritative, "client nameplate refused");
            Check(target.Appearance.SetPoweredDown().ErrorCode == ModErrorCode.NotAuthoritative, "client pose refused");
            fake.IsAuthoritative = true;
            var retained = target.Appearance;
            using var old = Lease(retained.Set(Red));
            fake.Remove(target.Id);
            var replacement = fake.Add(target.Id, "Sam");
            Check(!old.IsActive && retained.Set(Blue).ErrorCode == ModErrorCode.NotFound, "old appearance handle stays retired");
            Check(replacement.Appearance.TryGet(out var fresh) && original!.Equals(fresh), "id reuse starts with original");
            fake.ReplaceGeneration();
            Check(!replacement.Appearance.TryGet(out _), "generation replacement retires appearance facade");
        }
        private static void LeasesAndBounds()
        {
            using var outer = new FakeModLifetime();
            using var inner = new FakeModLifetime();
            var original = new AvatarAppearance(RgbaColor.White, RgbaColor.White);
            var changed = 0;
            var state = new AvatarPresentation(original, () => changed++);
            var red = Lease(state.Add(outer, 0, Red));
            var blue = Lease(state.Add(inner, 0, Blue));
            var hidden = Lease(state.Add(outer, 1, name: "Alias", flag: false));
            var down = Lease(state.Add(inner, 2));
            Check(state.Current.Equals(Blue) && !state.Visible && state.PoweredDown, "independent latest-wins features compose");
            red.Dispose(); red.Dispose();
            Check(state.Current.Equals(Blue), "out-of-order and double disposal preserve newer lease");
            inner.Dispose();
            Check(state.Current.Equals(original) && !state.PoweredDown && !state.Visible, "scope restores only its features");
            outer.Dispose();
            Check(state.Visible && state.Name == null && !blue.IsActive && !hidden.IsActive && !down.IsActive, "all features restore on owning scope disposal");
            using var bounded = new FakeModLifetime();
            var held = new List<IPlayerAppearanceLease>();
            for (var i = 0; i < AvatarPresentation.MaximumLeases; i++) held.Add(Lease(state.Add(bounded, 0, Red)));
            Check(state.Add(bounded, 0, Blue).ErrorCode == ModErrorCode.Conflict, "lease work bounded per participant");
            var before = changed;
            held[0].Dispose();
            Check(changed == before, "equal effective state is dirty-checked");
            state.Retire();
            Check(bounded.TrackedResourceCount == 0 && !held[1].IsActive && state.Current.Equals(original), "retirement releases tracked leases and restores original");
        }
    }
}
