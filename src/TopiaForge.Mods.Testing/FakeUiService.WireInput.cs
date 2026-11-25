using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Testing
{
    public sealed partial class FakeUiService
    {
        // This provider is the presenting package's definitions. The receiver's input registration is separate.
        internal Func<string, IReadOnlyList<InputBinding>?>? AuthorInputDefaults { get; set; }

        internal IReadOnlyList<InputBinding>? DefaultsForWire(string actionName) => AuthorInputDefaults?.Invoke(actionName);
    }
}
