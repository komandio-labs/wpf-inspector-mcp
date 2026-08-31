using System.Windows;
using System.Text.Json;

namespace KomandioLabs.WpfInspector.Agent;

/// <summary>Entry point loaded into a modern CoreCLR WPF process by the native bootstrapper.</summary>
public static class AgentEntryPoint
{
    private static readonly object Gate = new();
    private static InspectionAgent? agent;

    public static void Initialize(string pipeName, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        lock (Gate)
        {
            if (agent is not null) return;
            agent = new InspectionAgent(Application.Current?.Dispatcher
                ?? throw new InvalidOperationException("The target process does not have a WPF application dispatcher."), pipeName, secret);
            agent.Start();
        }
    }

    public static void InitializeInjectedSession(string sessionJson)
    {
        var session = JsonSerializer.Deserialize<InjectedSession>(sessionJson) ?? throw new InvalidOperationException("Invalid injected inspection session.");
        Initialize(session.PipeName, session.Secret);
    }

    public static int InitializeFromInjectionArgument(string sessionJson)
    {
        try { InitializeInjectedSession(sessionJson); return 0; }
        catch { return 1; }
    }

    private sealed record InjectedSession(string PipeName, string Secret);
}
