using UnityEditor;
using UnityEngine;

namespace TopiaForge.Example.Editor
{
    /// <summary>Replace this with your package's editor tooling.</summary>
    public static class ExampleEditor
    {
        [MenuItem("TopiaForge/Example/Say Hello")]
        private static void SayHello()
        {
            Debug.Log("Hello from a TopiaForge package's editor code.");
        }
    }
}
