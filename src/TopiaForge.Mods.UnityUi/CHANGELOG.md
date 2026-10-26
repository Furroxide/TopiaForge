# Changelog

## Unreleased

- Added `TopiaForgeWidget.PushDismissable(ITopiaForgeDismissable)` for custom compositions. It uses the same selection-scoped navigation claim as a kit window, rooted in that widget; focused text fields own typing separately. Closing a composition still removes it through `TopiaForgeDismissStack.Remove`.
- Programmatic text-field `Focus()` now waits for every held key to be released, including when the field is already selected. Moving selection away cancels the pending start.
