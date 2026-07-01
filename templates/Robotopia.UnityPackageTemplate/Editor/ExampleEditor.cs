using UnityEditor;
using UnityEngine;

namespace Robotopia.Example.Editor
{
    /// <summary>Replace this with your package's editor tooling.</summary>
    public static class ExampleEditor
    {
        [MenuItem("Robotopia/Example/Say Hello")]
        private static void SayHello()
        {
            Debug.Log("Hello from a QuantumWorks package's editor code.");
        }
    }
}
