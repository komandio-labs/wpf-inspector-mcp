using Xunit;

namespace WpfInspectorMcp.Tests;

public class McpServerTests
{
    [Fact]
    public void WindowTitleValidation_RejectsOversizedFilters() =>
        Assert.False(Win32Api.IsValidWindowTitleFilter(new string('a', 257)));
}
