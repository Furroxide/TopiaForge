using System;
using UnityEngine;

namespace TopiaForge.Worlds
{
    internal static class UnityWorldResources
    {
        public static T Own<T>(WorldResourceScope scope, T value) where T : UnityEngine.Object
        {
            if (value == null) throw new InvalidOperationException("Native world allocation returned no object.");
            scope.Add(new NativeObject(value));
            return value;
        }
        private sealed class NativeObject : IDisposable
        {
            private UnityEngine.Object? value;
            public NativeObject(UnityEngine.Object value) { this.value = value; }
            public void Dispose()
            {
                var current = value;
                value = null;
                if (current != null)
                {
                    // Destroy is deferred until the end of the frame. Stop lights and callbacks before
                    // restoring borrowed scene state or constructing a replacement arena in this frame.
                    if (current is GameObject gameObject) gameObject.SetActive(false);
                    UnityEngine.Object.Destroy(current);
                }
            }
        }
    }
}
