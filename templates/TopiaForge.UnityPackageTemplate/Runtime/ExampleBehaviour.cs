using UnityEngine;

namespace TopiaForge.Example
{
    /// <summary>Replace this with your package's runtime code.</summary>
    public sealed class ExampleBehaviour : MonoBehaviour
    {
        [SerializeField] private string note = "Hello from a TopiaForge package.";

        public string Note => note;
    }
}
