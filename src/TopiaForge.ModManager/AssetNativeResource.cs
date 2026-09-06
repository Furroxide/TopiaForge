using System;
using System.Threading;
namespace TopiaForge.ModManager
{
    // Only the host thread touches native data. Pins retain backing bundle data until every prefab
    // request has really completed, even if its caller has cancelled or disposed the public bundle.
    internal sealed class AssetNativeResource<T> : IDisposable where T : class
    {
        private T? value;
        private readonly Action<T> release;
        private int pins;
        private bool disposing;
        internal AssetNativeResource(T value, Action<T> release) { this.value = value; this.release = release; }
        internal bool IsAlive => !disposing && value != null;
        internal Pin Acquire()
        {
            if (!IsAlive) throw new ObjectDisposedException(nameof(AssetNativeResource<T>));
            pins++; return new Pin(this, value!);
        }
        public void Dispose() { disposing = true; ReleaseIfUnpinned(); }
        private void ReleaseIfUnpinned()
        {
            if (disposing && pins == 0)
            {
                var current = value; value = null;
                if (current != null) release(current);
            }
        }
        internal sealed class Pin : IDisposable
        {
            private AssetNativeResource<T>? owner;
            internal Pin(AssetNativeResource<T> owner, T value) { this.owner = owner; Value = value; }
            internal T Value { get; }
            public void Dispose()
            {
                var current = Interlocked.Exchange(ref owner, null);
                if (current == null) return;
                current.pins--; current.ReleaseIfUnpinned();
            }
        }
    }
}
