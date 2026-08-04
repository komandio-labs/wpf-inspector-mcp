# WPF Inspector MCP Server

A Windows WPF-inspection MCP server built with the official C# MCP SDK. It uses a managed inspection-session model: the server launches the requested WPF application, loads a small in-process agent at startup, visibly prefixes its window title with `[AI inspection]`, and closes the application when the session ends.

## Safety and lifecycle

- The server never attaches to, screenshots, clicks, or traverses an arbitrary running process.
- `start_wpf_inspection` requires an absolute `.exe` path and returns its managed PID.
- Every subsequent inspection tool requires that PID and rejects processes not launched by this MCP-server session.
- `end_wpf_inspection` closes the managed target explicitly.
- If the MCP client disconnects or the server exits, every managed target is closed automatically.
- The in-process agent communicates over a per-session named pipe protected by a random secret. Pipe messages are bounded, length-prefixed, and time-limited.

## Tools

| Tool | Purpose |
| --- | --- |
| `start_wpf_inspection` | Launch a WPF executable under AI inspection. |
| `end_wpf_inspection` | End one session and close its target app. |
| `get_inspection_windows` | List the target's visible marked windows. |
| `get_wpf_roots` | Return WPF window roots and visual/logical root IDs. |
| `get_visual_tree` | Traverse a bounded visual-tree subtree using `v:` IDs. |
| `get_logical_tree` | Traverse a bounded logical-tree subtree using `l:` IDs. |
| `find_wpf_elements` | Find elements by name, automation ID, type, or rendered text. |
| `get_wpf_element_details` | Read type, layout, visibility, data-context type, and local bindings. |
| `get_wpf_bindings` | Read local WPF binding expressions for an element. |
| `get_wpf_interactive_elements` | List visible enabled controls with locators, bounds, and semantic capabilities. |
| `interact_with_wpf_element` | Invoke, select, focus, toggle, set text, or set a range value by element identity. |
| `wait_for_wpf_state` | Wait for an element to exist, become visible/enabled, disappear, or match text. |
| `run_wpf_workflow` | Execute bounded semantic interact/wait/assert steps with a trace. |
| `take_inspection_screenshot` | Capture a managed target window as MCP image content. |
| `click_inspection_window_point` | Click inside a managed target window; requires explicit user confirmation. |

Tree calls are deliberately bounded. A useful AI workflow is: start the session → discover interactive elements or search → invoke controls by AutomationId/name/node ID → wait for the expected state → inspect focused details/bindings → take a screenshot only when visual validation is useful → end the session.

## Build and test

```powershell
dotnet build tools/wpf-inspector-mcp/WpfInspectorMcp.sln --no-restore
dotnet test tools/wpf-inspector-mcp/WpfInspectorMcp.sln --no-restore
```

The integration tests launch the sample WPF application and verify every MCP tool, the visible inspection marker, tree traversal, binding inspection, screenshot output, explicit cleanup, and cleanup on MCP disconnection.

## MCP configuration

```json
{
  "mcpServers": {
    "wpf-inspector": {
      "command": "C:\\path\\to\\wpf-inspector-mcp\\src\\WpfInspectorMcp\\bin\\Debug\\net9.0-windows\\WpfInspectorMcp.exe"
    }
  }
}
```
