using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NUnit.Framework;

namespace KomandioLabs.WpfInspector.Mcp.Tests;

[NonParallelizable]
public sealed class IntegrationTests
{
    [Test]
    public async Task McpServer_StartsAndEndsExplicitWpfTargetWhenConfigured()
    {
        var targetPath = Environment.GetEnvironmentVariable("WPF_INSPECTOR_VALIDATION_TARGET");
        if (string.IsNullOrWhiteSpace(targetPath)) return;

        var executablePath = Path.GetFullPath(targetPath);
        Assert.True(File.Exists(executablePath), $"Validation target not found: {executablePath}");
        var serverPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "KomandioLabs.WpfInspector.Mcp", "bin", BuildConfiguration, "net10.0-windows", "wpfinspectmcp.exe"));
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions { Name = "configured-target-test", Command = serverPath }));

        var processId = 0;
        try
        {
            var start = await client.CallToolAsync("start_wpf_inspection", new Dictionary<string, object?> { ["executablePath"] = executablePath });
            Assert.False(start.IsError is true, Text(start));
            processId = JsonNode.Parse(Text(start))!["processId"]!.GetValue<int>();

            var roots = await client.CallToolAsync("get_wpf_roots", new Dictionary<string, object?> { ["processId"] = processId });
            Assert.False(roots.IsError is true, Text(roots));

            var end = await client.CallToolAsync("end_wpf_inspection", new Dictionary<string, object?> { ["processId"] = processId });
            Assert.False(end.IsError is true, Text(end));
            await AssertProcessStopsAsync(processId);
            processId = 0;
        }
        finally
        {
            if (processId != 0) await EnsureProcessStoppedAsync(processId);
        }
    }

    [Test]
    public async Task McpServer_AttachesToAndDetachesFromAnAlreadyRunningSample()
    {
        var samplePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "KomandioLabs.WpfInspector.Sample", "bin", BuildConfiguration, "net8.0-windows", "KomandioLabs.WpfInspector.Sample.exe"));
        var serverPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "KomandioLabs.WpfInspector.Mcp", "bin", BuildConfiguration, "net10.0-windows", "wpfinspectmcp.exe"));
        using var sample = Process.Start(new ProcessStartInfo(samplePath) { UseShellExecute = false })!;
        try
        {
            await Task.Delay(750);
            await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions { Name = "attach-test", Command = serverPath }));
            var attached = await client.CallToolAsync("attach_wpf_inspection", new Dictionary<string, object?> { ["processId"] = sample.Id });
            Assert.False(attached.IsError is true, Text(attached));
            var roots = await client.CallToolAsync("get_wpf_roots", new Dictionary<string, object?> { ["processId"] = sample.Id });
            Assert.False(roots.IsError is true, Text(roots));
            var detached = await client.CallToolAsync("end_wpf_inspection", new Dictionary<string, object?> { ["processId"] = sample.Id });
            Assert.False(detached.IsError is true, Text(detached));
            Assert.False(sample.HasExited);
        }
        finally { await EnsureProcessStoppedAsync(sample.Id); }
    }

    [Test]
    public async Task McpServer_ClosesManagedInspectionWhenTheMcpSessionEnds()
    {
        var samplePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "KomandioLabs.WpfInspector.Sample", "bin", BuildConfiguration, "net8.0-windows", "KomandioLabs.WpfInspector.Sample.exe"));
        var serverPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "KomandioLabs.WpfInspector.Mcp", "bin", BuildConfiguration, "net10.0-windows", "wpfinspectmcp.exe"));
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

    [Test]
    public async Task McpServer_InspectsEveryManagedWpfCapabilityAndClosesTheApp()
    {
        var samplePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "KomandioLabs.WpfInspector.Sample", "bin", BuildConfiguration, "net8.0-windows", "KomandioLabs.WpfInspector.Sample.exe"));
        var serverPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "KomandioLabs.WpfInspector.Mcp", "bin", BuildConfiguration, "net10.0-windows", "wpfinspectmcp.exe"));
        Assert.True(File.Exists(samplePath), $"Sample app not found: {samplePath}");
        Assert.True(File.Exists(serverPath), $"MCP server not found: {serverPath}");

        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions { Name = "integration-test", Command = serverPath }));
        var toolNames = (await client.ListToolsAsync()).Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var expectedTool in new[]
        {
            "start_wpf_inspection", "attach_wpf_inspection", "end_wpf_inspection", "get_inspection_windows", "get_wpf_roots",
            "get_visual_tree", "get_logical_tree", "find_wpf_elements", "get_wpf_element_details", "get_wpf_bindings",
            "get_wpf_interactive_elements", "get_wpf_surfaces", "interact_with_wpf_element", "wait_for_wpf_state", "run_wpf_workflow",
            "take_inspection_screenshot", "click_inspection_window_point"
        })
            Assert.That(toolNames, Does.Contain(expectedTool));

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
            Assert.That(Text(windows), Does.Contain("[AI inspection] Sample Complex WPF-UI Application"));

            var surfaces = await client.CallToolAsync("get_wpf_surfaces", new Dictionary<string, object?> { ["processId"] = processId });
            Assert.False(surfaces.IsError is true, Text(surfaces));
            Assert.That(Text(surfaces), Does.Contain("presentationRoots"));

            var roots = await client.CallToolAsync("get_wpf_roots", new Dictionary<string, object?> { ["processId"] = processId });
            Assert.False(roots.IsError is true, Text(roots));
            Assert.That(Text(roots), Does.Contain("v:0"));
            Assert.That(Text(roots), Does.Contain("l:0"));

            var visualTree = await client.CallToolAsync("get_visual_tree", new Dictionary<string, object?> { ["processId"] = processId, ["maxDepth"] = 8, ["maxChildren"] = 100 });
            Assert.False(visualTree.IsError is true, Text(visualTree));
            Assert.That(Text(visualTree), Does.Contain("KomandioLabs.WpfInspector.Sample.MainWindow"));

            var logicalTree = await client.CallToolAsync("get_logical_tree", new Dictionary<string, object?> { ["processId"] = processId, ["maxDepth"] = 8, ["maxChildren"] = 100 });
            Assert.False(logicalTree.IsError is true, Text(logicalTree));
            Assert.That(Text(logicalTree), Does.Contain("logical"));

            var find = await client.CallToolAsync("find_wpf_elements", new Dictionary<string, object?> { ["processId"] = processId, ["query"] = "SpeedReadout", ["tree"] = "visual" });
            Assert.False(find.IsError is true, Text(find));
            var speedReadoutId = FindMatchId(JsonNode.Parse(Text(find))!, "SpeedReadout");
            Assert.NotNull(speedReadoutId);

            var element = await client.CallToolAsync("get_wpf_element_details", new Dictionary<string, object?> { ["processId"] = processId, ["nodeId"] = speedReadoutId });
            Assert.False(element.IsError is true, Text(element));
            Assert.That(Text(element), Does.Contain("SpeedReadout"));
            Assert.That(Text(element), Does.Contain("Value"));

            var bindings = await client.CallToolAsync("get_wpf_bindings", new Dictionary<string, object?> { ["processId"] = processId, ["nodeId"] = speedReadoutId });
            Assert.False(bindings.IsError is true, Text(bindings));
            Assert.That(Text(bindings), Does.Contain("Text"));
            Assert.That(Text(bindings), Does.Contain("Value"));

            var interactive = await client.CallToolAsync("get_wpf_interactive_elements", new Dictionary<string, object?> { ["processId"] = processId, ["query"] = "NavCollectionBtn" });
            Assert.False(interactive.IsError is true, Text(interactive));
            Assert.That(Text(interactive), Does.Contain("invoke"));

            var collectionDetails = await client.CallToolAsync("get_wpf_element_details", new Dictionary<string, object?> { ["processId"] = processId, ["nodeId"] = FindMatchId(JsonNode.Parse(Text(await client.CallToolAsync("find_wpf_elements", new Dictionary<string, object?> { ["processId"] = processId, ["query"] = "NavCollectionBtn" })))!, "NavCollectionBtn") });
            Assert.False(collectionDetails.IsError is true, Text(collectionDetails));
            Assert.That(Text(collectionDetails), Does.Contain("windowFrame"));
            Assert.That(Text(collectionDetails), Does.Contain("isEnabled"));
            Assert.That(Text(collectionDetails), Does.Contain("invoke"));

            var invokeCollection = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "NavCollectionBtn", ["action"] = "invoke" });
            Assert.False(invokeCollection.IsError is true, Text(invokeCollection));

            var revisionBeforeSemanticNavigation = JsonNode.Parse(Text(await client.CallToolAsync("get_wpf_roots", new Dictionary<string, object?> { ["processId"] = processId })))!["uiRevision"]!.GetValue<string>();
            var invokeSemanticNavigation = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "SemanticNavigationItem", ["action"] = "invoke" });
            Assert.False(invokeSemanticNavigation.IsError is true, Text(invokeSemanticNavigation));
            var revisionAfterSemanticNavigation = JsonNode.Parse(Text(invokeSemanticNavigation))!["uiRevision"]!.GetValue<string>();
            Assert.That(revisionAfterSemanticNavigation, Is.Not.EqualTo(revisionBeforeSemanticNavigation));
            var semanticNavigationInvoked = await client.CallToolAsync("wait_for_wpf_state", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "SemanticNavigationCount", ["condition"] = "textEquals", ["expectedValue"] = "1", ["timeoutMs"] = 3000 });
            Assert.False(semanticNavigationInvoked.IsError is true, Text(semanticNavigationInvoked));
            var collectionVisible = await client.CallToolAsync("wait_for_wpf_state", new Dictionary<string, object?> { ["processId"] = processId, ["query"] = "Collection Workspace", ["condition"] = "visible", ["timeoutMs"] = 3000 });
            Assert.False(collectionVisible.IsError is true, Text(collectionVisible));
            var activeCollection = await client.CallToolAsync("get_wpf_element_details", new Dictionary<string, object?> { ["processId"] = processId, ["nodeId"] = FindMatchId(JsonNode.Parse(Text(await client.CallToolAsync("find_wpf_elements", new Dictionary<string, object?> { ["processId"] = processId, ["query"] = "NavCollectionBtn" })))!, "NavCollectionBtn") });
            Assert.That(Text(activeCollection), Does.Contain("Primary"));
            var inactiveDashboard = await client.CallToolAsync("get_wpf_element_details", new Dictionary<string, object?> { ["processId"] = processId, ["nodeId"] = FindMatchId(JsonNode.Parse(Text(await client.CallToolAsync("find_wpf_elements", new Dictionary<string, object?> { ["processId"] = processId, ["query"] = "NavDashboardBtn" })))!, "NavDashboardBtn") });
            Assert.That(Text(inactiveDashboard), Does.Contain("Secondary"));
            var selectCollection = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "CollectionListView", ["action"] = "select", ["value"] = "Quarterly Planning" });
            Assert.False(selectCollection.IsError is true, Text(selectCollection));
            foreach (var (actionId, title) in new[]
            {
                ("OpenQuarterlyPlanning", "Quarterly Planning"), ("OpenResearchNotes", "Research Notes"),
                ("OpenDesignReview", "Design Review"), ("OpenTeamRetrospective", "Team Retrospective")
            })
            {
                var openRecord = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = actionId, ["action"] = "invoke" });
                Assert.False(openRecord.IsError is true, Text(openRecord));
                var recordOpened = await client.CallToolAsync("wait_for_wpf_state", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "StatusLabel", ["condition"] = "textEquals", ["expectedValue"] = $"Status: Opened {title}", ["timeoutMs"] = 3000 });
                Assert.False(recordOpened.IsError is true, Text(recordOpened));
            }

            var setSlider = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "SpeedSlider", ["action"] = "setRangeValue", ["value"] = "120" });
            Assert.False(setSlider.IsError is true, Text(setSlider));
            var speedUpdated = await client.CallToolAsync("wait_for_wpf_state", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "SpeedReadout", ["condition"] = "textEquals", ["expectedValue"] = "120 Hz", ["timeoutMs"] = 3000 });
            Assert.False(speedUpdated.IsError is true, Text(speedUpdated));
            var speedValue = await client.CallToolAsync("wait_for_wpf_state", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "SpeedSlider", ["condition"] = "valueEquals", ["expectedValue"] = "120", ["timeoutMs"] = 3000 });
            Assert.False(speedValue.IsError is true, Text(speedValue));

            var openSettings = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "NavSettingsBtn", ["action"] = "auto" });
            Assert.False(openSettings.IsError is true, Text(openSettings));
            var dashboardHidden = await client.CallToolAsync("wait_for_wpf_state", new Dictionary<string, object?> { ["processId"] = processId, ["query"] = "System Analytics & Telemetry", ["condition"] = "hidden", ["timeoutMs"] = 3000 });
            Assert.False(dashboardHidden.IsError is true, Text(dashboardHidden));
            var settingsExists = await client.CallToolAsync("wait_for_wpf_state", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "ProfileList", ["condition"] = "exists", ["timeoutMs"] = 3000 });
            Assert.False(settingsExists.IsError is true, Text(settingsExists));
            var activeSettings = await client.CallToolAsync("get_wpf_element_details", new Dictionary<string, object?> { ["processId"] = processId, ["nodeId"] = FindMatchId(JsonNode.Parse(Text(await client.CallToolAsync("find_wpf_elements", new Dictionary<string, object?> { ["processId"] = processId, ["query"] = "NavSettingsBtn" })))!, "NavSettingsBtn") });
            Assert.That(Text(activeSettings), Does.Contain("Primary"));
            var resetEnabled = await client.CallToolAsync("wait_for_wpf_state", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "ResetFormButton", ["condition"] = "enabled", ["timeoutMs"] = 3000 });
            Assert.False(resetEnabled.IsError is true, Text(resetEnabled));
            var disabledAction = await client.CallToolAsync("wait_for_wpf_state", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "DisabledActionButton", ["condition"] = "disabled", ["timeoutMs"] = 3000 });
            Assert.False(disabledAction.IsError is true, Text(disabledAction));
            var saveSettings = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "SaveSettingsButton", ["action"] = "invoke" });
            Assert.False(saveSettings.IsError is true, Text(saveSettings));
            var settingsSaved = await client.CallToolAsync("wait_for_wpf_state", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "StatusLabel", ["condition"] = "textEquals", ["expectedValue"] = "Status: Settings Saved", ["timeoutMs"] = 3000 });
            Assert.False(settingsSaved.IsError is true, Text(settingsSaved));
            var setText = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "SampleTextInput", ["action"] = "setText", ["value"] = "sample value" });
            Assert.False(setText.IsError is true, Text(setText));
            var setName = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "NameInput", ["action"] = "setText", ["value"] = "Grace Hopper" });
            Assert.False(setName.IsError is true, Text(setName));
            Assert.That(Text(setName), Does.Contain("Grace Hopper"));
            var setNotes = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "NotesInput", ["action"] = "setText", ["value"] = "First line\nSecond line" });
            Assert.False(setNotes.IsError is true, Text(setNotes));
            var setSecret = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "SecretInput", ["action"] = "setText", ["value"] = "test-secret" });
            Assert.False(setSecret.IsError is true, Text(setSecret));
            var clearRequired = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "RequiredInput", ["action"] = "setText", ["value"] = "" });
            Assert.False(clearRequired.IsError is true, Text(clearRequired));
            var requiredInvalid = await client.CallToolAsync("wait_for_wpf_state", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "RequiredInput", ["condition"] = "validationHasError", ["timeoutMs"] = 3000 });
            Assert.False(requiredInvalid.IsError is true, Text(requiredInvalid));
            var focus = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "SampleTextInput", ["action"] = "focus" });
            Assert.False(focus.IsError is true, Text(focus));
            var focused = await client.CallToolAsync("wait_for_wpf_state", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "SampleTextInput", ["condition"] = "focused" });
            Assert.False(focused.IsError is true, Text(focused));
            var key = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "SampleTextInput", ["action"] = "sendKey", ["value"] = "Enter" });
            Assert.False(key.IsError is true, Text(key));
            var selectMode = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "ModeSelector", ["action"] = "select", ["value"] = "Performance" });
            Assert.False(selectMode.IsError is true, Text(selectMode));
            var selectProfile = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "ProfileList", ["action"] = "select", ["value"] = "Builder" });
            Assert.False(selectProfile.IsError is true, Text(selectProfile));
            var selectTab = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "WorkspaceTabs", ["action"] = "select", ["value"] = "Telemetry" });
            Assert.False(selectTab.IsError is true, Text(selectTab));
            var setDate = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "MaintenanceDate", ["action"] = "setDate", ["value"] = "2026-12-31" });
            Assert.False(setDate.IsError is true, Text(setDate));
            var setVolume = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "VolumeSlider", ["action"] = "setRangeValue", ["value"] = "80" });
            Assert.False(setVolume.IsError is true, Text(setVolume));
            var volumeValue = await client.CallToolAsync("wait_for_wpf_state", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "VolumeSlider", ["condition"] = "valueEquals", ["expectedValue"] = "80", ["timeoutMs"] = 3000 });
            Assert.False(volumeValue.IsError is true, Text(volumeValue));
            var chooseRadio = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "RadioBeta", ["action"] = "toggle", ["value"] = "true" });
            Assert.False(chooseRadio.IsError is true, Text(chooseRadio));
            var radioChecked = await client.CallToolAsync("wait_for_wpf_state", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "RadioBeta", ["condition"] = "checked", ["expectedValue"] = "True", ["timeoutMs"] = 3000 });
            Assert.False(radioChecked.IsError is true, Text(radioChecked));
            var toggleThreeState = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "TriStateCheck", ["action"] = "toggle", ["value"] = "true" });
            Assert.False(toggleThreeState.IsError is true, Text(toggleThreeState));
            foreach (var checkId in new[] { "VisualLoggingCheck", "HardwareAccelerationCheck" })
            {
                var toggleCheck = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = checkId, ["action"] = "toggle", ["value"] = "false" });
                Assert.False(toggleCheck.IsError is true, Text(toggleCheck));
                var checkState = await client.CallToolAsync("wait_for_wpf_state", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = checkId, ["condition"] = "checked", ["expectedValue"] = "False", ["timeoutMs"] = 3000 });
                Assert.False(checkState.IsError is true, Text(checkState));
            }
            foreach (var progressId in new[] { "TaskProgress", "MemoryProgress" })
            {
                var progressDetails = await client.CallToolAsync("get_wpf_element_details", new Dictionary<string, object?> { ["processId"] = processId, ["nodeId"] = FindMatchId(JsonNode.Parse(Text(await client.CallToolAsync("find_wpf_elements", new Dictionary<string, object?> { ["processId"] = processId, ["query"] = progressId })))!, progressId) });
                Assert.False(progressDetails.IsError is true, Text(progressDetails));
            }
            var ringDetails = await client.CallToolAsync("get_wpf_element_details", new Dictionary<string, object?> { ["processId"] = processId, ["nodeId"] = FindMatchId(JsonNode.Parse(Text(await client.CallToolAsync("find_wpf_elements", new Dictionary<string, object?> { ["processId"] = processId, ["query"] = "ActiveProgressRing" })))!, "ActiveProgressRing") });
            Assert.False(ringDetails.IsError is true, Text(ringDetails));
            var expandTree = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "DiagnosticsRoot", ["action"] = "expand" });
            Assert.False(expandTree.IsError is true, Text(expandTree));
            var collapseTree = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "DiagnosticsRoot", ["action"] = "collapse" });
            Assert.False(collapseTree.IsError is true, Text(collapseTree));
            var expandExpander = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "AdvancedExpander", ["action"] = "expand" });
            Assert.False(expandExpander.IsError is true, Text(expandExpander));
            var collapseExpander = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "AdvancedExpander", ["action"] = "collapse" });
            Assert.False(collapseExpander.IsError is true, Text(collapseExpander));
            var expandDeepTree = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "DeepRoot", ["action"] = "expand" });
            Assert.False(expandDeepTree.IsError is true, Text(expandDeepTree));
            var resetForm = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "ResetFormButton", ["action"] = "invoke" });
            Assert.False(resetForm.IsError is true, Text(resetForm));

            var openDashboard = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "NavDashboardBtn", ["action"] = "auto" });
            Assert.False(openDashboard.IsError is true, Text(openDashboard));
            var toggleHighlight = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "ToggleHighlightButton", ["action"] = "invoke" });
            Assert.False(toggleHighlight.IsError is true, Text(toggleHighlight));
            var titleBarDetails = await client.CallToolAsync("get_wpf_element_details", new Dictionary<string, object?> { ["processId"] = processId, ["nodeId"] = FindMatchId(JsonNode.Parse(Text(await client.CallToolAsync("find_wpf_elements", new Dictionary<string, object?> { ["processId"] = processId, ["query"] = "SampleTitleBar" })))!, "SampleTitleBar") });
            Assert.False(titleBarDetails.IsError is true, Text(titleBarDetails));
            var dashboardButton = await client.CallToolAsync("find_wpf_elements", new Dictionary<string, object?> { ["processId"] = processId, ["query"] = "NavDashboardBtn" });
            var dashboardButtonId = FindMatchId(JsonNode.Parse(Text(dashboardButton))!, "NavDashboardBtn")!;
            // Real mouse clicks require immediate user consent; validate only the
            // safe rejection path in this unattended integration suite.
            var toggle = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "PerfToggle", ["action"] = "toggle", ["value"] = "false" });
            Assert.False(toggle.IsError is true, Text(toggle));
            Assert.That(Text(toggle), Does.Contain("isChecked"));
            var toggleState = await client.CallToolAsync("wait_for_wpf_state", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "PerfToggle", ["condition"] = "checked", ["expectedValue"] = "False" });
            Assert.False(toggleState.IsError is true, Text(toggleState));

            var openModal = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "OpenModalBtn", ["action"] = "invoke" });
            Assert.False(openModal.IsError is true, Text(openModal));
            var modalVisible = await client.CallToolAsync("wait_for_wpf_state", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "ConfirmModalBtn", ["condition"] = "visible", ["timeoutMs"] = 3000 });
            Assert.False(modalVisible.IsError is true, Text(modalVisible));
            var confirmModal = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "ConfirmModalBtn", ["action"] = "invoke" });
            Assert.False(confirmModal.IsError is true, Text(confirmModal));
            var modalHidden = await client.CallToolAsync("wait_for_wpf_state", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "ConfirmModalBtn", ["condition"] = "hidden", ["timeoutMs"] = 3000 });
            Assert.False(modalHidden.IsError is true, Text(modalHidden));
            var reopenModal = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "OpenModalBtn", ["action"] = "invoke" });
            Assert.False(reopenModal.IsError is true, Text(reopenModal));
            var cancelModal = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "CancelModalButton", ["action"] = "invoke" });
            Assert.False(cancelModal.IsError is true, Text(cancelModal));

            var openShowDialog = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "OpenDialogWindowBtn", ["action"] = "invoke" });
            Assert.False(openShowDialog.IsError is true, Text(openShowDialog));
            var modalWindows = await client.CallToolAsync("get_inspection_windows", new Dictionary<string, object?> { ["processId"] = processId });
            Assert.False(modalWindows.IsError is true, Text(modalWindows));
            Assert.That(Text(modalWindows), Does.Contain("Modal Test Dialog"));
            var modalRoots = await client.CallToolAsync("get_wpf_roots", new Dictionary<string, object?> { ["processId"] = processId });
            Assert.False(modalRoots.IsError is true, Text(modalRoots));
            Assert.That(Text(modalRoots), Does.Contain("Modal Test Dialog"));
            var closeModalDialog = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "CloseModalDialogBtn", ["action"] = "invoke" });
            Assert.False(closeModalDialog.IsError is true, Text(closeModalDialog));
            var modalDialogDismissed = await client.CallToolAsync("wait_for_wpf_state", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "StatusLabel", ["condition"] = "textEquals", ["expectedValue"] = "Status: Modal Dialog Confirmed", ["timeoutMs"] = 3000 });
            Assert.False(modalDialogDismissed.IsError is true, Text(modalDialogDismissed));

            var openDrawer = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "OpenDrawerBtn", ["action"] = "invoke" });
            Assert.False(openDrawer.IsError is true, Text(openDrawer));
            var drawerVisible = await client.CallToolAsync("wait_for_wpf_state", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "CloseDrawerButton", ["condition"] = "visible", ["timeoutMs"] = 3000 });
            Assert.False(drawerVisible.IsError is true, Text(drawerVisible));
            var closeDrawer = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "CloseDrawerButton", ["action"] = "invoke" });
            Assert.False(closeDrawer.IsError is true, Text(closeDrawer));
            var drawerHidden = await client.CallToolAsync("wait_for_wpf_state", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "CloseDrawerButton", ["condition"] = "hidden", ["timeoutMs"] = 3000 });
            Assert.False(drawerHidden.IsError is true, Text(drawerHidden));

            var openPopup = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "OpenPopupButton", ["action"] = "invoke" });
            Assert.False(openPopup.IsError is true, Text(openPopup));
            var popupSurfaces = await client.CallToolAsync("get_wpf_surfaces", new Dictionary<string, object?> { ["processId"] = processId });
            Assert.False(popupSurfaces.IsError is true, Text(popupSurfaces));
            Assert.That(Text(popupSurfaces), Does.Contain("isPopup"));
            var closePopup = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "ClosePopupButton", ["action"] = "invoke" });
            Assert.False(closePopup.IsError is true, Text(closePopup));
            var popupGone = await client.CallToolAsync("wait_for_wpf_state", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "ClosePopupButton", ["condition"] = "gone", ["timeoutMs"] = 3000 });
            Assert.False(popupGone.IsError is true, Text(popupGone));


            var workflowSteps = JsonSerializer.Deserialize<JsonElement[]>("""
                [
                  { "kind": "interact", "action": "invoke", "locator": { "automationId": "NavDashboardBtn" } },
                  { "kind": "wait", "condition": "visible", "locator": { "query": "System Analytics & Telemetry" }, "timeoutMs": 3000 },
                  { "kind": "assert", "condition": "visible", "locator": { "query": "System Analytics & Telemetry" } },
                  { "kind": "interact", "action": "invoke", "locator": { "automationId": "NavCollectionBtn" } },
                  { "kind": "wait", "condition": "visible", "locator": { "query": "Collection Workspace" }, "timeoutMs": 3000 }
                ]
                """)!;
            var workflow = await client.CallToolAsync("run_wpf_workflow", new Dictionary<string, object?> { ["processId"] = processId, ["steps"] = workflowSteps });
            Assert.False(workflow.IsError is true, Text(workflow));
            Assert.That(Text(workflow), Does.Contain("completed"));

            var failedWorkflowSteps = JsonSerializer.Deserialize<JsonElement[]>("""[{ "kind": "assert", "condition": "textEquals", "locator": { "automationId": "StatusLabel" }, "expectedValue": "not the current status" }]""")!;
            var failedWorkflow = await client.CallToolAsync("run_wpf_workflow", new Dictionary<string, object?> { ["processId"] = processId, ["steps"] = failedWorkflowSteps });
            Assert.False(failedWorkflow.IsError is true, Text(failedWorkflow));
            Assert.That(Text(failedWorkflow), Does.Contain("failedStep"));

            var ambiguous = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["query"] = "Button", ["action"] = "focus" });
            Assert.True(ambiguous.IsError is true);
            var staleRevision = await client.CallToolAsync("get_wpf_element_details", new Dictionary<string, object?> { ["processId"] = processId, ["nodeId"] = dashboardButtonId, ["expectedRevision"] = "DEADBEEF" });
            Assert.True(staleRevision.IsError is true);

            var screenshot = await client.CallToolAsync("take_inspection_screenshot", new Dictionary<string, object?> { ["processId"] = processId });
            Assert.False(screenshot.IsError is true, Text(screenshot));
            var images = screenshot.Content.OfType<ImageContentBlock>().ToArray();
            Assert.That(images, Has.Exactly(1).Items);
            var image = images[0];
            var png = image.DecodedData.ToArray();
            Assert.True(png.Length > 1_000);
            Assert.That(png[..4], Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47 }));
            if (string.Equals(Environment.GetEnvironmentVariable("WPF_INSPECTOR_CAPTURE_SAMPLE_SCREENSHOT"), "1", StringComparison.Ordinal))
            {
                var screenshotPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "assets", "sample-dashboard.png"));
                Directory.CreateDirectory(Path.GetDirectoryName(screenshotPath)!);
                await File.WriteAllBytesAsync(screenshotPath, png);
            }

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

    private static string BuildConfiguration => new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
        ?? throw new InvalidOperationException($"Could not determine build configuration from '{AppContext.BaseDirectory}'.");

    private static string Text(CallToolResult result)
    {
        Assert.That(result.Content[0], Is.TypeOf<TextContentBlock>());
        return ((TextContentBlock)result.Content[0]).Text;
    }

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
