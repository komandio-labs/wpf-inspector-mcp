# WPF Inspector MCP

WPF Inspector MCP gives AI coding agents an in-process view of local WPF applications through the [Model Context Protocol](https://modelcontextprotocol.io/). It inspects the real WPF visual and logical trees, bindings, presentation surfaces, controls, and screenshots without adding a reference to the target application.

## Why this exists

Accessibility-only tools see the UIA tree. This server injects an inspection agent into a running WPF process, so it can inspect the real WPF tree, dependency bindings, data-context type, and popup surfaces.

The injector uses the target process's existing CoreCLR runtime:

```text
AI agent → stdio/MCP → WPF Inspector MCP
                         ↓
          window-ready local WPF PID
                         ↓
 project-owned native injector → target's existing CoreCLR runtime
                         ↓
      authenticated named-pipe inspection agent
```

The target starts normally. Attach occurs only after its WPF window is ready, avoiding interference with application startup and resource initialization.

## Capabilities

- Start a local WPF executable in a managed inspection session or attach to an explicit, already-running local PID.
- Inspect visible windows, presentation surfaces, bounded visual trees, logical trees, element layout/state, and bindings.
- Find elements by type, name, AutomationId, or rendered text.
- List interactive controls and use semantic WPF/UI Automation actions.
- Wait for UI states and capture managed-window screenshots.
- Detach safely: attached targets stay running and their original title is restored.

State-changing operations—semantic interaction, workflows, and real mouse clicks—require immediate user confirmation.

## Quick start

Requires Windows and the .NET 9 SDK.

```powershell
dotnet restore WpfInspectorMcp.sln
dotnet build WpfInspectorMcp.sln --no-restore
dotnet test WpfInspectorMcp.sln --no-build --no-restore
```

Register the server with an MCP client:

```json
{
  "mcpServers": {
    "wpf-inspector": {
      "command": "C:/path/to/WpfInspectorMcp.exe"
    }
  }
}
```

## Typical workflow

1. Launch the WPF application normally, or identify its explicit local PID.
2. Call `attach_wpf_inspection` after the window is visible and stable.
3. Read `get_wpf_roots` and `get_wpf_surfaces`.
4. Diagnose with element search, tree calls, details, and bindings.
5. Get confirmation immediately before any state-changing action.
6. Call `end_wpf_inspection` when finished.

## MCP tools

| Group | Tools |
| --- | --- |
| Session | `start_wpf_inspection`, `attach_wpf_inspection`, `end_wpf_inspection`, `get_inspection_windows` |
| Discovery | `get_wpf_roots`, `get_wpf_surfaces`, `get_visual_tree`, `get_logical_tree`, `find_wpf_elements` |
| Diagnostics | `get_wpf_element_details`, `get_wpf_bindings`, `get_wpf_interactive_elements` |
| Controlled interaction | `interact_with_wpf_element`, `wait_for_wpf_state`, `run_wpf_workflow` |
| Visual validation | `take_inspection_screenshot`, `click_inspection_window_point` |

## Safety model

- Local Windows x64 CoreCLR WPF processes only.
- Attach requires an explicit PID; callers cannot provide arbitrary agent DLL paths.
- Each session has a fresh random pipe name and 256-bit secret.
- Inspected windows receive an `[AI inspection]` title marker.
- The temporary injector is unloaded after activation. Detach stops the agent service without unsafe managed-assembly unloading.
- Use only with the target owner's authorization.

## Layout

```text
src/WpfInspectorMcp/          MCP server and session management
src/WpfInspector.Agent/       injected WPF inspection agent
src/WpfInspector.NativeInjector/ temporary native CoreCLR injector
samples/SampleWpfApp/         integration-test target and README sample
tests/WpfInspectorMcp.Tests/  unit and end-to-end tests
docs/assets/                  README images
```

## License and attribution

This standalone tool is distributed under the Microsoft Public License (MS-PL); see [LICENSE.txt](LICENSE.txt). See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for required attribution and trademark information.
