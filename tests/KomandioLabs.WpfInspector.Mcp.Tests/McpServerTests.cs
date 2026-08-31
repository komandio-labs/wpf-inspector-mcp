using System.Diagnostics;
using NUnit.Framework;

namespace KomandioLabs.WpfInspector.Mcp.Tests;

[NonParallelizable]
public class McpServerTests
{
    [Test]
    public void WindowTitleValidation_RejectsOversizedFilters() =>
        Assert.False(Win32Api.IsValidWindowTitleFilter(new string('a', 257)));

    [Test]
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

            Assert.That(info.Environment.Keys, Has.None.EqualTo("DOTNET_STARTUP_HOOKS").IgnoreCase);
            Assert.That(info.Environment.Keys, Has.None.EqualTo("WPF_INSPECTOR_PIPE").IgnoreCase);
            Assert.That(info.Environment.Keys, Has.None.EqualTo("WPF_INSPECTOR_SECRET").IgnoreCase);
            Assert.That(info.Environment["WINDIR"], Is.EqualTo(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", originalHook);
            Environment.SetEnvironmentVariable("WPF_INSPECTOR_PIPE", originalPipe);
            Environment.SetEnvironmentVariable("WPF_INSPECTOR_SECRET", originalSecret);
        }
    }

    [Test]
    public void DpiAwarenessAndWindowBounds_ExecuteSafely()
    {
        Win32Api.EnsureDpiAwareness();
        var result = Win32Api.GetWindowBounds(nint.Zero, out var rect);
        Assert.False(result);
    }
}
