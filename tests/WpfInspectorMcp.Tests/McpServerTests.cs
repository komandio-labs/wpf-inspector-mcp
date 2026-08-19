using System.Diagnostics;
using Xunit;

namespace WpfInspectorMcp.Tests;

[Collection("ServerTests")]
public class McpServerTests
{
    [Fact]
    public void WindowTitleValidation_RejectsOversizedFilters() =>
        Assert.False(Win32Api.IsValidWindowTitleFilter(new string('a', 257)));

    [Fact]
    public void ManagedLaunch_DoesNotPassLegacyStartupHookEnvironment()
    {
        var originalHook = Environment.GetEnvironmentVariable("DOTNET_STARTUP_HOOKS");
        var originalPipe = Environment.GetEnvironmentVariable("WPF_INSPECTOR_PIPE");
        var originalSecret = Environment.GetEnvironmentVariable("WPF_INSPECTOR_SECRET");
        try
        {
            Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", "stale-hook.dll");
            Environment.SetEnvironmentVariable("WPF_INSPECTOR_PIPE", "stale-pipe");
            Environment.SetEnvironmentVariable("WPF_INSPECTOR_SECRET", "stale-secret");

            var info = InspectorTools.CreateTargetStartInfo("C:\\test\\target.exe", null, "C:\\test");

            Assert.DoesNotContain("DOTNET_STARTUP_HOOKS", info.Environment.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("WPF_INSPECTOR_PIPE", info.Environment.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("WPF_INSPECTOR_SECRET", info.Environment.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.Windows), info.Environment["WINDIR"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", originalHook);
            Environment.SetEnvironmentVariable("WPF_INSPECTOR_PIPE", originalPipe);
            Environment.SetEnvironmentVariable("WPF_INSPECTOR_SECRET", originalSecret);
        }
    }

    [Fact]
    public void DpiAwarenessAndWindowBounds_ExecuteSafely()
    {
        Win32Api.EnsureDpiAwareness();
        var result = Win32Api.GetWindowBounds(nint.Zero, out var rect);
        Assert.False(result);
    }
}
