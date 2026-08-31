using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KomandioLabs.WpfInspector.Mcp;

[McpServerToolType]
public sealed class InspectorTools
{
    private static readonly ConcurrentDictionary<int, ManagedProcess> Inspections = new();

    [McpServerTool, Description("Starts a WPF executable normally, then attaches the trusted inspection agent after WPF has initialized. The application is visibly marked '[AI inspection]' and is automatically closed when this MCP server exits. Requires an absolute existing .exe path.")]
    public static CallToolResult StartWpfInspection(
        [Description("Required absolute path to the WPF executable.")] string executablePath,
        [Description("Optional command-line arguments supplied verbatim to the target application.")] string? arguments = null,
        [Description("Optional absolute working directory. Defaults to the executable's directory.")] string? workingDirectory = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath)) return Error("executablePath must be an absolute path.");
            var fullPath = Path.GetFullPath(executablePath);
            if (!fullPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath)) return Error("executablePath must name an existing .exe file.");
            var directory = string.IsNullOrWhiteSpace(workingDirectory) ? Path.GetDirectoryName(fullPath)! : Path.GetFullPath(workingDirectory);
            if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory)) return Error("workingDirectory must be an existing absolute directory.");
            var agentPath = RuntimeAssets.Agent;

            var pipeName = $"wpf-inspector-{Guid.NewGuid():N}";
            var secret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            var startInfo = CreateTargetStartInfo(fullPath, arguments, directory);
            var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Windows did not start the target process.");
            AttachAfterWpfStarts(process, agentPath, pipeName, secret);
            Inspections[process.Id] = new ManagedProcess(process, pipeName, secret, true);
            Log($"Started inspection session for PID {process.Id}; agent pipe {pipeName}.");
            return Text(JsonSerializer.Serialize(new { processId = process.Id, processName = process.ProcessName, executablePath = fullPath, titlePrefix = "[AI inspection]" }));
        }
        catch (Exception exception) { return Error($"Could not start the WPF inspection session: {exception.Message}"); }
    }

    [McpServerTool, Description("Attaches the trusted WPF inspection agent to a specified already-running local CoreCLR WPF process. This changes the target process state; require explicit user confirmation immediately before calling it. The target must be a same-user, non-elevated x64 process.")]
    public static CallToolResult AttachWpfInspection([Description("Exact PID of the local WPF process to inspect.")] int processId)
    {
        try
        {
            if (Inspections.ContainsKey(processId)) return Error("This process already has a managed inspection session.");
            var process = Process.GetProcessById(processId);
            var agentPath = RuntimeAssets.Agent;
            var pipeName = $"wpf-inspector-{Guid.NewGuid():N}";
            var secret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            AttachAfterWpfStarts(process, agentPath, pipeName, secret);
            Inspections[process.Id] = new ManagedProcess(process, pipeName, secret, false);
            Log($"Attached inspection session to PID {process.Id}.");
            return Text(JsonSerializer.Serialize(new { processId = process.Id, processName = process.ProcessName, titlePrefix = "[AI inspection]" }));
        }
        catch (Exception exception) { return Error($"Could not attach the WPF inspection session: {exception.Message}"); }
    }

    [McpServerTool, Description("Ends one managed AI-inspection session and closes its WPF application. Only accepts a PID returned by start_wpf_inspection.")]
    public static CallToolResult EndWpfInspection([Description("PID returned by start_wpf_inspection.")] int processId) =>
        StopInspection(processId, "Ended");

    [McpServerTool, Description("Lists visible windows for one managed AI-inspection session. The title includes '[AI inspection]' while the session is active.")]
    public static CallToolResult GetInspectionWindows([Description("PID returned by start_wpf_inspection.")] int processId)
    {
        if (!TryGetInspection(processId, out _)) return Error("This MCP server does not manage that inspection process.");
        return Text(Win32Api.SerializeWindows(Win32Api.GetVisibleWindowsForProcessId(processId)));
    }

    [McpServerTool, Description("Returns the live WPF window roots for one managed AI-inspection session.")]
    public static Task<CallToolResult> GetWpfRoots([Description("PID returned by start_wpf_inspection.")] int processId, CancellationToken cancellationToken = default) =>
        RequestAgentAsync(processId, "roots", null, cancellationToken);

    [McpServerTool, Description("Returns a bounded subtree of the live WPF visual tree. Start with roots or visual_tree without rootId, then follow returned v: node IDs. Increase maxDepth only as needed.")]
    public static Task<CallToolResult> GetVisualTree(
        [Description("PID returned by start_wpf_inspection.")] int processId,
        [Description("Optional v: node ID returned by a prior tree call. Omit to return every WPF window root.")] string? rootId = null,
        [Description("Maximum descendant depth, from 0 through 8. Defaults to 3.")] int maxDepth = 3,
        [Description("Maximum direct children per returned node, from 1 through 250. Defaults to 100.")] int maxChildren = 100,
        CancellationToken cancellationToken = default) =>
        RequestAgentAsync(processId, "visual_tree", new { rootId, maxDepth, maxChildren }, cancellationToken);

    [McpServerTool, Description("Returns a bounded subtree of the live WPF logical tree. Start with roots or logical_tree without rootId, then follow returned l: node IDs. Increase maxDepth only as needed.")]
    public static Task<CallToolResult> GetLogicalTree(
        [Description("PID returned by start_wpf_inspection.")] int processId,
        [Description("Optional l: node ID returned by a prior tree call. Omit to return every WPF window root.")] string? rootId = null,
        [Description("Maximum descendant depth, from 0 through 8. Defaults to 3.")] int maxDepth = 3,
        [Description("Maximum direct children per returned node, from 1 through 250. Defaults to 100.")] int maxChildren = 100,
        CancellationToken cancellationToken = default) =>
        RequestAgentAsync(processId, "logical_tree", new { rootId, maxDepth, maxChildren }, cancellationToken);

    [McpServerTool, Description("Finds live WPF elements by name, automation ID, type name, or rendered text. Returns stable v: or l: node IDs for focused tree, detail, and binding calls.")]
    public static Task<CallToolResult> FindWpfElements(
        [Description("PID returned by start_wpf_inspection.")] int processId,
        [Description("Case-insensitive text to match against an element name, automation ID, type, or rendered text.")] string query,
        [Description("Tree to search: visual (default) or logical.")] string tree = "visual",
        [Description("Maximum matches returned, from 1 through 100. Defaults to 50.")] int maxResults = 50,
        CancellationToken cancellationToken = default) =>
        RequestAgentAsync(processId, "find_elements", new { query, tree, maxResults }, cancellationToken);

    [McpServerTool, Description("Returns properties, layout, data-context type, and local bindings for a live WPF element identified by a v: or l: node ID.")]
    public static Task<CallToolResult> GetWpfElementDetails(
        [Description("PID returned by start_wpf_inspection.")] int processId,
        [Description("v: or l: node ID returned by a tree call.")] string nodeId,
        string? expectedRevision = null,
        CancellationToken cancellationToken = default) =>
        RequestAgentAsync(processId, "element_details", new { nodeId, expectedRevision }, cancellationToken);

    [McpServerTool, Description("Returns local WPF binding expressions for a live element identified by a v: or l: node ID.")]
    public static Task<CallToolResult> GetWpfBindings(
        [Description("PID returned by start_wpf_inspection.")] int processId,
        [Description("v: or l: node ID returned by a tree call.")] string nodeId,
        CancellationToken cancellationToken = default) =>
        RequestAgentAsync(processId, "bindings", new { nodeId }, cancellationToken);

    [McpServerTool, Description("Lists visible, enabled WPF controls that support semantic automation, including stable locators, bounds, and supported actions.")]
    public static Task<CallToolResult> GetWpfInteractiveElements(int processId, string? query = null, int maxResults = 100, CancellationToken cancellationToken = default) =>
        RequestAgentAsync(processId, "interactive_elements", new { query, maxResults }, cancellationToken);

    [McpServerTool, Description("Lists managed WPF windows and live presentation roots, including popup roots when WPF exposes them.")]
    public static Task<CallToolResult> GetWpfSurfaces(int processId, CancellationToken cancellationToken = default) =>
        RequestAgentAsync(processId, "surfaces", null, cancellationToken);

    [McpServerTool, Description("Performs a semantic action on a managed WPF element. Use nodeId or locator (automationId, name, or query). Supported actions: auto, invoke, select, setText, setRangeValue, toggle, focus, sendKey, expand, collapse, scroll. For ScrollViewer, scroll accepts lineUp, lineDown, pageUp, pageDown, top, bottom, left, right, or an absolute vertical:<offset>/horizontal:<offset>. This changes application state; require explicit user confirmation immediately before calling it.")]
    public static Task<CallToolResult> InteractWithWpfElement(int processId, string action = "auto", string? nodeId = null, string? automationId = null, string? name = null, string? query = null, string? value = null, string? expectedRevision = null, CancellationToken cancellationToken = default) =>
        RequestAgentAsync(processId, "interact", new { action, nodeId, locator = new { automationId, name, query }, value, expectedRevision }, cancellationToken);

    [McpServerTool, Description("Waits until a managed WPF element matches a state condition without taking screenshots. Conditions: exists, gone, visible, hidden, enabled, disabled, textEquals.")]
    public static Task<CallToolResult> WaitForWpfState(int processId, string condition, string? nodeId = null, string? automationId = null, string? name = null, string? query = null, string? expectedValue = null, int timeoutMs = 2000, string? expectedRevision = null, CancellationToken cancellationToken = default) =>
        RequestAgentAsync(processId, "wait_for_state", new { condition, nodeId, locator = new { automationId, name, query }, expectedValue, timeoutMs, expectedRevision }, cancellationToken);

    [McpServerTool, Description("Runs up to 25 semantic WPF interact, wait, and assert steps as one bounded workflow. Each step is an object with kind and the same locator/action fields as the individual tools. This can change application state; require explicit user confirmation immediately before calling it.")]
    public static Task<CallToolResult> RunWpfWorkflow(int processId, JsonElement[] steps, CancellationToken cancellationToken = default) =>
        RequestAgentAsync(processId, "run_workflow", new { steps }, cancellationToken);

    [McpServerTool, Description("Captures a visible managed-inspection window as MCP image content. This brings the app window to the foreground.")]
    public static async Task<CallToolResult> TakeInspectionScreenshot(
        [Description("PID returned by start_wpf_inspection.")] int processId,
        [Description("Optional case-insensitive substring that must match the window title.")] string? windowTitle = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetInspection(processId, out var inspection)) return Error("This MCP server does not manage that inspection process.");
        if (!Win32Api.IsValidWindowTitleFilter(windowTitle)) return Error("windowTitle must be at most 256 characters.");

        try
        {
            var response = await InspectionAgentClient.RequestAsync(inspection.PipeName, inspection.Secret, "screenshot", new { windowTitle }, cancellationToken);
            using var document = JsonDocument.Parse(response);
            if (document.RootElement.TryGetProperty("error", out var error))
                return Error(error.GetString() ?? "The inspection agent returned an unknown error.");

            var title = document.RootElement.GetProperty("title").GetString() ?? "Window";
            var width = document.RootElement.GetProperty("width").GetInt32();
            var height = document.RootElement.GetProperty("height").GetInt32();
            var pngBase64 = document.RootElement.GetProperty("pngBase64").GetString()!;
            var png = Convert.FromBase64String(pngBase64);

            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = $"Captured '{title}' (PID {processId}, {width}x{height})." }, ImageContentBlock.FromBytes(png, "image/png")]
            };
        }
        catch (Exception agentException)
        {
            if (!Win32Api.TryFindVisibleWindow(processId, windowTitle, out var window, out var error))
                return Error($"Could not capture the selected window: {agentException.Message}. Fallback search failed: {error}");
            try
            {
                var png = Win32Api.CaptureWindowByHandle((nint)window.Handle);
                return new CallToolResult
                {
                    Content = [new TextContentBlock { Text = $"Captured '{window.Title}' (PID {window.ProcessId}, {window.Width}x{window.Height})." }, ImageContentBlock.FromBytes(png, "image/png")]
                };
            }
            catch (Exception exception) { return Error($"Could not capture the selected window: {exception.Message}"); }
        }
    }

    [McpServerTool, Description("Clicks a point inside a visible managed-inspection window. This moves the real mouse and can change application state; require explicit user confirmation immediately before calling it.")]
    public static CallToolResult ClickInspectionWindowPoint(
        [Description("PID returned by start_wpf_inspection.")] int processId,
        [Description("Horizontal pixel offset from the window's top-left; must be inside the window.")] int x,
        [Description("Vertical pixel offset from the window's top-left; must be inside the window.")] int y,
        [Description("Optional case-insensitive substring that must match the window title.")] string? windowTitle = null)
    {
        if (!TryGetInspection(processId, out _)) return Error("This MCP server does not manage that inspection process.");
        if (!Win32Api.IsValidWindowTitleFilter(windowTitle)) return Error("windowTitle must be at most 256 characters.");
        if (!Win32Api.TryFindVisibleWindow(processId, windowTitle, out var window, out var error)) return Error(error);
        if (x < 0 || y < 0 || x >= window.Width || y >= window.Height) return Error($"The point ({x}, {y}) is outside the selected window ({window.Width}x{window.Height}).");
        return Text(Win32Api.ClickWindowPoint((nint)window.Handle, window, x, y));
    }

    internal static void EndAllInspections()
    {
        foreach (var processId in Inspections.Keys) StopInspection(processId, "Automatically ended");
    }

    private static async Task<CallToolResult> RequestAgentAsync(int processId, string operation, object? arguments, CancellationToken cancellationToken)
    {
        if (!TryGetInspection(processId, out var inspection)) return Error("This MCP server does not manage that inspection process.");
        try
        {
            var response = await InspectionAgentClient.RequestAsync(inspection.PipeName, inspection.Secret, operation, arguments, cancellationToken);
            using var document = JsonDocument.Parse(response);
            if (document.RootElement.TryGetProperty("error", out var error)) return Error(error.GetString() ?? "The inspection agent returned an unknown error.");
            return Text(response);
        }
        catch (Exception exception) { return Error($"Could not contact the WPF inspection agent: {exception.Message}"); }
    }

    private static bool TryGetInspection(int processId, out ManagedProcess inspection)
    {
        if (!Inspections.TryGetValue(processId, out inspection!)) return false;
        try
        {
            if (!inspection.Process.HasExited) return true;
        }
        catch { }
        Inspections.TryRemove(processId, out _);
        inspection.Process.Dispose();
        return false;
    }

    private static CallToolResult StopInspection(int processId, string action)
    {
        if (!Inspections.TryRemove(processId, out var inspection)) return Error("This MCP server does not manage that inspection process.");
        try
        {
            if (!inspection.Process.HasExited)
            {
                try { InspectionAgentClient.RequestAsync(inspection.PipeName, inspection.Secret, "stop", null, CancellationToken.None).GetAwaiter().GetResult(); }
                catch { /* Process exit remains the safe cleanup fallback for owned sessions. */ }
            }
            if (!inspection.Process.HasExited && inspection.OwnsProcess) inspection.Process.Kill(entireProcessTree: true);
            Log($"{action} inspection session for PID {processId}.");
            return Text(inspection.OwnsProcess ? $"{action} managed inspection process {processId}." : $"{action} inspection session for process {processId}; the target remains running.");
        }
        catch (Exception exception) { return Error($"Could not end managed inspection process {processId}: {exception.Message}"); }
        finally { inspection.Process.Dispose(); }
    }

    private static void Log(string message) => Console.Error.WriteLine($"[{DateTimeOffset.UtcNow:O}] KomandioLabs.WpfInspector.Mcp {message}");
    private static CallToolResult Text(string text) => new() { Content = [new TextContentBlock { Text = text }] };
    private static CallToolResult Error(string message) => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };

    // The inspection agent is injected only after WPF has opened a window.  Strip
    // every legacy launch-hook value explicitly: ProcessStartInfo otherwise
    // inherits its host's environment and a stale value makes some WPF apps fail
    // in FontCache before they can become inspectable.
    internal static ProcessStartInfo CreateTargetStartInfo(string executablePath, string? arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = arguments ?? string.Empty,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false
        };
        startInfo.Environment.Remove("DOTNET_STARTUP_HOOKS");
        startInfo.Environment.Remove("WPF_INSPECTOR_PIPE");
        startInfo.Environment.Remove("WPF_INSPECTOR_SECRET");
        // Codex starts MCP servers with a deliberately small environment. WPF's
        // FontCache uses WINDIR while constructing its font URI, so restore the
        // standard Windows value for an inspected desktop application.
        startInfo.Environment["WINDIR"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return startInfo;
    }

    private static void AttachAfterWpfStarts(Process process, string agentPath, string pipeName, string secret)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        Exception? last = null;
        while (DateTime.UtcNow < deadline && !process.HasExited)
        {
            try
            {
                process.Refresh();
                if (!Win32Api.GetVisibleWindowsForProcessId(process.Id).Any())
                {
                    Thread.Sleep(100);
                    continue;
                }
                NativeInspectionInjector.Attach(process, agentPath, pipeName, secret);
                return;
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or Win32Exception or TimeoutException)
            {
                last = exception;
                Thread.Sleep(150);
            }
        }
        throw new InvalidOperationException("The application did not become an inspectable, visible CoreCLR WPF process within 15 seconds.", last);
    }

    private sealed record ManagedProcess(Process Process, string PipeName, string Secret, bool OwnsProcess);
}
