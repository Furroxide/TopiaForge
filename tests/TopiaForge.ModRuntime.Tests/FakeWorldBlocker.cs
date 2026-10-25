using TopiaForge.Mods;

namespace TopiaForge.ModManager.TestDoubles
{
    /// <summary>Engine test double; Unity's actual collider construction is checked separately by source syntax.</summary>
    internal sealed class FakeWorldBlocker : IBlocker
    {
        internal FakeWorldBlocker(FakeWorldObject target, BlockerRequest request)
        { Object = target; InitialTransform = request.Placement; Size = request.Size; BlocksSight = request.BlocksSight; }
        internal FakeWorldObject Object { get; }
        internal bool BlocksSight { get; }
        internal int DisposeCalls { get; private set; }
        public string Id => Object.Entity.Id;
        public string Name => Object.Name;
        public bool IsAlive => Object.IsAlive;
        public Vec3 Position => Object.Entity.Position;
        public TransformState InitialTransform { get; }
        public Vec3 Size { get; }
        public void Dispose() { DisposeCalls++; Object.Destroy(); }
    }
}
