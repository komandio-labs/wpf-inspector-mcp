using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace WpfInspectorMcp.Tests;

public sealed class IntegrationTests
{
    [Fact]
    public async Task McpServer_ClosesManagedInspectionWhenTheMcpSessionEnds()
    {
        var samplePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "SampleWpfApp", "bin", "Debug", "net9.0-windows", "SampleWpfApp.exe"));
        var serverPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "WpfInspectorMcp", "bin", "Debug", "net9.0-windows", "WpfInspectorMcp.exe"));
        var processId = 0;
        try
        {
            await using (var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions { Name = "lifecycle-test", Command = serverPath })))
            {
                var start = await client.CallToolAsync("start_wpf_inspection", new Dictionary<string, object?> { ["executablePath"] = samplePath });
                Assert.False(start.IsError is true, Text(start));
                processId = JsonNode.Parse(Text(start))!["processId"]!.GetValue<int>();
            }

            await AssertProcessStopsAsync(processId);
            processId = 0;
        }
        finally
        {
            if (processId != 0) await EnsureProcessStoppedAsync(processId);
        }
    }

    [Fact]
    public async Task McpServer_InspectsEveryManagedWpfCapabilityAndClosesTheApp()
    {
        var samplePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "SampleWpfApp", "bin", "Debug", "net9.0-windows", "SampleWpfApp.exe"));
        var serverPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "WpfInspectorMcp", "bin", "Debug", "net9.0-windows", "WpfInspectorMcp.exe"));
        Assert.True(File.Exists(samplePath), $"Sample app not found: {samplePath}");
        Assert.True(File.Exists(serverPath), $"MCP server not found: {serverPath}");

        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions { Name = "integration-test", Command = serverPath }));
        var toolNames = (await client.ListToolsAsync()).Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var expectedTool in new[]
        {
            "start_wpf_inspection", "end_wpf_inspection", "get_inspection_windows", "get_wpf_roots",
            "get_visual_tree", "get_logical_tree", "find_wpf_elements", "get_wpf_element_details", "get_wpf_bindings",
            "get_wpf_interactive_elements", "get_wpf_surfaces", "interact_with_wpf_element", "wait_for_wpf_state", "run_wpf_workflow",
            "take_inspection_screenshot", "click_inspection_window_point"
        })
            Assert.Contains(expectedTool, toolNames);

        var processId = 0;
        try
        {
            var start = await client.CallToolAsync("start_wpf_inspection", new Dictionary<string, object?> { ["executablePath"] = samplePath });
            Assert.False(start.IsError is true, Text(start));
            processId = JsonNode.Parse(Text(start))!["processId"]!.GetValue<int>();

            CallToolResult? windows = null;
            for (var attempt = 0; attempt < 10; attempt++)
            {
                await Task.Delay(250);
                windows = await client.CallToolAsync("get_inspection_windows", new Dictionary<string, object?> { ["processId"] = processId });
                if (Text(windows).Contains("[AI inspection] Sample Complex WPF-UI Application")) break;
            }
            Assert.NotNull(windows);
            Assert.Contains("[AI inspection] Sample Complex WPF-UI Application", Text(windows));

            var surfaces = await client.CallToolAsync("get_wpf_surfaces", new Dictionary<string, object?> { ["processId"] = processId });
            Assert.False(surfaces.IsError is true, Text(surfaces));
            Assert.Contains("presentationRoots", Text(surfaces));

            var roots = await client.CallToolAsync("get_wpf_roots", new Dictionary<string, object?> { ["processId"] = processId });
            Assert.False(roots.IsError is true, Text(roots));
            Assert.Contains("v:0", Text(roots));
            Assert.Contains("l:0", Text(roots));

            var visualTree = await client.CallToolAsync("get_visual_tree", new Dictionary<string, object?> { ["processId"] = processId, ["maxDepth"] = 8, ["maxChildren"] = 100 });
            Assert.False(visualTree.IsError is true, Text(visualTree));
            Assert.Contains("SampleWpfApp.MainWindow", Text(visualTree));

            var logicalTree = await client.CallToolAsync("get_logical_tree", new Dictionary<string, object?> { ["processId"] = processId, ["maxDepth"] = 8, ["maxChildren"] = 100 });
            Assert.False(logicalTree.IsError is true, Text(logicalTree));
            Assert.Contains("logical", Text(logicalTree));

            var find = await client.CallToolAsync("find_wpf_elements", new Dictionary<string, object?> { ["processId"] = processId, ["query"] = "SpeedReadout", ["tree"] = "visual" });
            Assert.False(find.IsError is true, Text(find));
            var speedReadoutId = FindMatchId(JsonNode.Parse(Text(find))!, "SpeedReadout");
            Assert.NotNull(speedReadoutId);

            var element = await client.CallToolAsync("get_wpf_element_details", new Dictionary<string, object?> { ["processId"] = processId, ["nodeId"] = speedReadoutId });
            Assert.False(element.IsError is true, Text(element));
            Assert.Contains("SpeedReadout", Text(element));
            Assert.Contains("Value", Text(element));

            var bindings = await client.CallToolAsync("get_wpf_bindings", new Dictionary<string, object?> { ["processId"] = processId, ["nodeId"] = speedReadoutId });
            Assert.False(bindings.IsError is true, Text(bindings));
            Assert.Contains("Text", Text(bindings));
            Assert.Contains("Value", Text(bindings));

            var interactive = await client.CallToolAsync("get_wpf_interactive_elements", new Dictionary<string, object?> { ["processId"] = processId, ["query"] = "NavCatalogBtn" });
            Assert.False(interactive.IsError is true, Text(interactive));
            Assert.Contains("invoke", Text(interactive));

            var catalogDetails = await client.CallToolAsync("get_wpf_element_details", new Dictionary<string, object?> { ["processId"] = processId, ["nodeId"] = FindMatchId(JsonNode.Parse(Text(await client.CallToolAsync("find_wpf_elements", new Dictionary<string, object?> { ["processId"] = processId, ["query"] = "NavCatalogBtn" })))!, "NavCatalogBtn") });
            Assert.False(catalogDetails.IsError is true, Text(catalogDetails));
            Assert.Contains("windowFrame", Text(catalogDetails));
            Assert.Contains("isEnabled", Text(catalogDetails));
            Assert.Contains("invoke", Text(catalogDetails));

            var invokeCatalog = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "NavCatalogBtn", ["action"] = "invoke" });
            Assert.False(invokeCatalog.IsError is true, Text(invokeCatalog));
            var catalogVisible = await client.CallToolAsync("wait_for_wpf_state", new Dictionary<string, object?> { ["processId"] = processId, ["query"] = "Adapter Package Catalog", ["condition"] = "visible", ["timeoutMs"] = 3000 });
            Assert.False(catalogVisible.IsError is true, Text(catalogVisible));

            var setSlider = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "SpeedSlider", ["action"] = "setRangeValue", ["value"] = "120" });
            Assert.False(setSlider.IsError is true, Text(setSlider));
            var speedUpdated = await client.CallToolAsync("wait_for_wpf_state", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "SpeedReadout", ["condition"] = "textEquals", ["expectedValue"] = "120 Hz", ["timeoutMs"] = 3000 });
            Assert.False(speedUpdated.IsError is true, Text(speedUpdated));
            var speedValue = await client.CallToolAsync("wait_for_wpf_state", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "SpeedSlider", ["condition"] = "valueEquals", ["expectedValue"] = "120", ["timeoutMs"] = 3000 });
            Assert.False(speedValue.IsError is true, Text(speedValue));

            var openSettings = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "NavSettingsBtn", ["action"] = "auto" });
            Assert.False(openSettings.IsError is true, Text(openSettings));
            var setText = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "ApiKeyInput", ["action"] = "setText", ["value"] = "test-license" });
            Assert.False(setText.IsError is true, Text(setText));
            var focus = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "ApiKeyInput", ["action"] = "focus" });
            Assert.False(focus.IsError is true, Text(focus));
            var focused = await client.CallToolAsync("wait_for_wpf_state", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "ApiKeyInput", ["condition"] = "focused" });
            Assert.False(focused.IsError is true, Text(focused));
            var key = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "ApiKeyInput", ["action"] = "sendKey", ["value"] = "Enter" });
            Assert.False(key.IsError is true, Text(key));
            var selectMode = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "ModeSelector", ["action"] = "select", ["value"] = "Performance" });
            Assert.False(selectMode.IsError is true, Text(selectMode));
            var setDate = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "MaintenanceDate", ["action"] = "setDate", ["value"] = "2026-12-31" });
            Assert.False(setDate.IsError is true, Text(setDate));
            var expandTree = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "DiagnosticsRoot", ["action"] = "expand" });
            Assert.False(expandTree.IsError is true, Text(expandTree));
            var collapseTree = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "DiagnosticsRoot", ["action"] = "collapse" });
            Assert.False(collapseTree.IsError is true, Text(collapseTree));

            var openDashboard = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "NavDashboardBtn", ["action"] = "auto" });
            Assert.False(openDashboard.IsError is true, Text(openDashboard));
            var dashboardButton = await client.CallToolAsync("find_wpf_elements", new Dictionary<string, object?> { ["processId"] = processId, ["query"] = "NavDashboardBtn" });
            var dashboardButtonId = FindMatchId(JsonNode.Parse(Text(dashboardButton))!, "NavDashboardBtn")!;
            var dashboardButtonDetails = await client.CallToolAsync("get_wpf_element_details", new Dictionary<string, object?> { ["processId"] = processId, ["nodeId"] = dashboardButtonId });
            var frame = JsonNode.Parse(Text(dashboardButtonDetails))!["bounds"]!["windowFrame"]!;
            var rawClick = await client.CallToolAsync("click_inspection_window_point", new Dictionary<string, object?> { ["processId"] = processId, ["x"] = (int)(frame["x"]!.GetValue<double>() + frame["width"]!.GetValue<double>() / 2), ["y"] = (int)(frame["y"]!.GetValue<double>() + frame["height"]!.GetValue<double>() / 2) });
            Assert.False(rawClick.IsError is true, Text(rawClick));
            var toggle = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "PerfToggle", ["action"] = "toggle", ["value"] = "false" });
            Assert.False(toggle.IsError is true, Text(toggle));
            Assert.Contains("isChecked", Text(toggle));
            var toggleState = await client.CallToolAsync("wait_for_wpf_state", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "PerfToggle", ["condition"] = "checked", ["expectedValue"] = "False" });
            Assert.False(toggleState.IsError is true, Text(toggleState));


            var workflowSteps = JsonSerializer.Deserialize<JsonElement[]>("""
                [
                  { "kind": "interact", "action": "invoke", "locator": { "automationId": "NavDashboardBtn" } },
                  { "kind": "wait", "condition": "visible", "locator": { "query": "System Analytics & Telemetry" }, "timeoutMs": 3000 },
                  { "kind": "assert", "condition": "visible", "locator": { "query": "System Analytics & Telemetry" } }
                ]
                """)!;
            var workflow = await client.CallToolAsync("run_wpf_workflow", new Dictionary<string, object?> { ["processId"] = processId, ["steps"] = workflowSteps });
            Assert.False(workflow.IsError is true, Text(workflow));
            Assert.Contains("completed", Text(workflow));

            var failedWorkflowSteps = JsonSerializer.Deserialize<JsonElement[]>("""[{ "kind": "assert", "condition": "textEquals", "locator": { "automationId": "StatusLabel" }, "expectedValue": "not the current status" }]""")!;
            var failedWorkflow = await client.CallToolAsync("run_wpf_workflow", new Dictionary<string, object?> { ["processId"] = processId, ["steps"] = failedWorkflowSteps });
            Assert.False(failedWorkflow.IsError is true, Text(failedWorkflow));
            Assert.Contains("failedStep", Text(failedWorkflow));

            var ambiguous = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["query"] = "Button", ["action"] = "focus" });
            Assert.True(ambiguous.IsError is true);
            var staleRevision = await client.CallToolAsync("get_wpf_element_details", new Dictionary<string, object?> { ["processId"] = processId, ["nodeId"] = dashboardButtonId, ["expectedRevision"] = "DEADBEEF" });
            Assert.True(staleRevision.IsError is true);

            var screenshot = await client.CallToolAsync("take_inspection_screenshot", new Dictionary<string, object?> { ["processId"] = processId });
            Assert.False(screenshot.IsError is true, Text(screenshot));
            var image = Assert.Single(screenshot.Content.OfType<ImageContentBlock>());
            var png = image.DecodedData.ToArray();
            Assert.True(png.Length > 1_000);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);

            var invalidTarget = await client.CallToolAsync("get_visual_tree", new Dictionary<string, object?> { ["processId"] = int.MaxValue });
            Assert.True(invalidTarget.IsError is true);
            var invalidClick = await client.CallToolAsync("click_inspection_window_point", new Dictionary<string, object?> { ["processId"] = processId, ["x"] = -1, ["y"] = 0 });
            Assert.True(invalidClick.IsError is true);

            var end = await client.CallToolAsync("end_wpf_inspection", new Dictionary<string, object?> { ["processId"] = processId });
            Assert.False(end.IsError is true, Text(end));
            await EnsureProcessStoppedAsync(processId);
            processId = 0;
        }
        finally
        {
            if (processId != 0)
            {
                try
                {
                    using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await client.CallToolAsync("end_wpf_inspection", new Dictionary<string, object?> { ["processId"] = processId }, cancellationToken: cleanupTimeout.Token);
                }
                catch { }
                await EnsureProcessStoppedAsync(processId);
            }
        }
    }

    private static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(result.Content[0]).Text;

    private static string? FindMatchId(JsonNode response, string name)
    {
        var matches = response["matches"]?.AsArray() ?? [];
        foreach (var match in matches.OfType<JsonObject>())
            if (string.Equals(match["name"]?.GetValue<string>(), name, StringComparison.Ordinal))
                return match["id"]?.GetValue<string>();
        return null;
    }

    private static async Task EnsureProcessStoppedAsync(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (ArgumentException) { }
    }

    private static async Task AssertProcessStopsAsync(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(process.HasExited, $"Managed inspection process {processId} was still running after the MCP session ended.");
        }
        catch (ArgumentException)
        {
            // The target exited before this check reached the process table.
        }
    }
}
