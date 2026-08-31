using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NUnit.Framework;

namespace KomandioLabs.WpfInspector.Mcp.Tests;

[NonParallelizable]
public sealed class ScrollInteractionTests
{
    [Test]
    public async Task McpServer_ScrollsAScrollViewerSemantically()
    {
        var samplePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "KomandioLabs.WpfInspector.Sample", "bin", BuildConfiguration, "net8.0-windows", "KomandioLabs.WpfInspector.Sample.exe"));
        var serverPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "KomandioLabs.WpfInspector.Mcp", "bin", BuildConfiguration, "net10.0-windows", "wpfinspectmcp.exe"));
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions { Name = "scroll-test", Command = serverPath }));

        var processId = 0;
        try
        {
            var start = await client.CallToolAsync("start_wpf_inspection", new Dictionary<string, object?> { ["executablePath"] = samplePath });
            Assert.False(start.IsError is true, Text(start));
            processId = JsonNode.Parse(Text(start))!["processId"]!.GetValue<int>();

            var openSettings = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "NavSettingsBtn", ["action"] = "invoke" });
            Assert.False(openSettings.IsError is true, Text(openSettings));

            var scroll = await client.CallToolAsync("interact_with_wpf_element", new Dictionary<string, object?> { ["processId"] = processId, ["automationId"] = "SettingsScrollViewer", ["action"] = "scroll", ["value"] = "bottom" });
            Assert.False(scroll.IsError is true, Text(scroll));
            await Task.Delay(125);
            var details = await client.CallToolAsync("get_wpf_element_details", new Dictionary<string, object?> { ["processId"] = processId, ["nodeId"] = JsonNode.Parse(Text(scroll))!["nodeId"]!.GetValue<string>() });
            Assert.False(details.IsError is true, Text(details));
            var state = JsonNode.Parse(Text(details))!["state"]!;
            Assert.True(state["scrollableHeight"]!.GetValue<double>() > 0, Text(details));
            Assert.That(state["verticalOffset"]!.GetValue<double>(), Is.EqualTo(state["scrollableHeight"]!.GetValue<double>()));
        }
        finally
        {
            if (processId != 0)
            {
                var end = await client.CallToolAsync("end_wpf_inspection", new Dictionary<string, object?> { ["processId"] = processId });
                Assert.False(end.IsError is true, Text(end));
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
}
