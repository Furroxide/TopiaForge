#if !NET5_0_OR_GREATER
// Polyfill enabling C# init accessors / with-expressions on netstandard2.1. Excluded on
// net8.0 where the runtime provides it (the Core files are compile-included into the
// Unity-free test exe).
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
#endif
