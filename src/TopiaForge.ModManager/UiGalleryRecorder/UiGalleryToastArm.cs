using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;

namespace TopiaForge.ModManager
{
    /// <summary>Lets a verified Gallery launcher or HUD authorize its short-lived native toast observations.</summary>
    internal sealed class UiGalleryToastArm : IDisposable
    {
        private static readonly List<UiGalleryToastArm> Active = new List<UiGalleryToastArm>();
        private readonly string owner;
        private readonly bool remote;
        private readonly Func<bool> isEligible;
        private bool disposed;

        private UiGalleryToastArm(string owner, bool remote, string directory, Func<NetRealm>? viewer, Func<bool> isEligible)
        {
            this.owner = owner;
            this.remote = remote;
            this.isEligible = isEligible;
            Directory = directory;
            Viewer = viewer;
            Active.Add(this);
        }

        internal string Directory { get; }
        internal Func<NetRealm>? Viewer { get; }
        internal bool Eligible => !disposed && isEligible();

        internal static UiGalleryToastArm? Arm(string owner, bool remote, string fixture, string directory,
            Func<NetRealm>? viewer, Func<bool> isEligible)
        {
            if (!(owner == UiGalleryCaptureFixture.Owner && !remote && fixture == UiGalleryCaptureFixture.ToastControlsId ||
                owner == UiGalleryCaptureFixture.RemoteFixtureOwner && fixture == UiGalleryCaptureFixture.PresentationHudId))
                return null;
            return new UiGalleryToastArm(owner, remote, directory, viewer, isEligible);
        }

        internal static UiGalleryToastArm? Find(string owner, bool remote)
        {
            for (var index = Active.Count - 1; index >= 0; index--)
            {
                var arm = Active[index];
                if (arm.owner == owner && arm.remote == remote && arm.Eligible) return arm;
            }
            return null;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Active.Remove(this);
        }
    }
}
