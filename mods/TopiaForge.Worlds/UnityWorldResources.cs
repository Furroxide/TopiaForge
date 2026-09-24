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
                if (current != null) UnityEngine.Object.Destroy(current);
            }
        }
    }
}
