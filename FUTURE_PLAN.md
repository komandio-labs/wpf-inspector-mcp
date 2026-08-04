# Future plan

The managed inspection-session workflow is complete. The following are deliberate follow-up improvements.

## Richer inspection

- Inspect styles, resources, templates, triggers, commands, validation errors, and accessibility patterns.
- Expand binding inspection beyond locally assigned bindings to include template and style bindings where WPF exposes them.
- Expose structured window, dialog, popup, and focus state.

## Safe interaction

- Invoke controls by stable node ID rather than only screen coordinates.
- Add explicit, confirm-before-action operations for text input, selection, toggles, and waiting for a UI state.

## Operational maturity

- Add configurable structured diagnostics and an inspection-agent health/status tool.
- Package a versioned, self-contained release with CI and client-configuration examples.
- Commit the currently untracked repository contents as the initial repository history.

## Deliberate boundary

The server does not attach to arbitrary already-running processes. It launches, visibly marks, owns, and closes the WPF app it inspects. Supporting an attach mode would require a separate, explicitly authorized process-injection design.
