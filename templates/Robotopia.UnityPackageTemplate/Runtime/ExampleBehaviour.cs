using UnityEngine;

namespace Robotopia.Example
{
    /// <summary>Replace this with your package's runtime code.</summary>
    public sealed class ExampleBehaviour : MonoBehaviour
    {
        [SerializeField] private string note = "Hello from a QuantumWorks package.";

        public string Note => note;
    }
}
