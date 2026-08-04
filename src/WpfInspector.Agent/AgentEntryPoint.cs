using System.Windows;

namespace WpfInspector.Agent;

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
}
