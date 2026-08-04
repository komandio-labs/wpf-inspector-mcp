using System.IO;

/// <summary>CoreCLR startup-hook entry point used when the MCP server launches a managed WPF application.</summary>
public static class StartupHook
{
    public static void Initialize()
    {
        var pipeName = Environment.GetEnvironmentVariable("WPF_INSPECTOR_PIPE");
        var secret = Environment.GetEnvironmentVariable("WPF_INSPECTOR_SECRET");
        if (string.IsNullOrWhiteSpace(pipeName) || string.IsNullOrWhiteSpace(secret)) return;
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), $"WpfInspector.Agent-{Environment.ProcessId}.log"), "Startup hook initialized." + Environment.NewLine); } catch { }
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher is not null) { WpfInspector.Agent.AgentEntryPoint.Initialize(pipeName, secret); return; }
                await Task.Delay(100).ConfigureAwait(false);
            }
        });
    }
}
