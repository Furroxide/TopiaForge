# Package answer fixture

`case.json` pins a complete `TFD1 package` response and its original archive. `answerBase64` is the ASCII line
`TFD1 200 <length>` plus LF, followed immediately by `archiveBase64`'s decoded bytes. `length` and `sha256` describe
that archive. The ZIP is stored without compression and has a fixed timestamp. Its minimal manifest is framing
test data, not an installable mod.

`HostPackageTests` serves the archive through `LiveHost` and `DescriptorEndpoint` over loopback and compares the entire
answer byte for byte. The launcher's `Tfd1PackageReader` can replay the same `answerBase64`, whole or split at any byte,
and must return `Tfd1PackageComplete(length)` and precisely the decoded archive. Keep the fixture when adding this
case to the launcher's reader suite; do not generate a separate Dart answer.

The endpoint and launcher both give one transfer two minutes. Their status line fits within 64 bytes, and its length
is a positive decimal 64-bit integer, without leading zeros. This fixture changes neither rule.
