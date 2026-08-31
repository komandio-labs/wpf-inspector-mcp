using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace KomandioLabs.WpfInspector.Mcp;

internal static partial class Win32Api
{
    private const int SwRestore = 9;
    private const int SwShow = 5;
    private const uint MouseeventfLeftdown = 0x0002;
    private const uint MouseeventfLeftup = 0x0004;
    private const int DwmwaExtendedFrameBounds = 9;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(nint dpiFlag);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDPIAware();

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, nint dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PrintWindow(nint hwnd, nint hdcBlt, uint nFlags);

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    internal sealed record WindowInfo(int ProcessId, string ProcessName, string Title, string ClassName, long Handle, int X, int Y, int Width, int Height);

    internal static void EnsureDpiAwareness()
    {
        try
        {
            // Try DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 (-4)
            if (!SetProcessDpiAwarenessContext((nint)(-4)))
            {
                SetProcessDPIAware();
            }
        }
        catch { }
    }

    internal static bool GetWindowBounds(nint hWnd, out RECT rect)
    {
        try
        {
            if (DwmGetWindowAttribute(hWnd, DwmwaExtendedFrameBounds, out rect, Marshal.SizeOf<RECT>()) == 0)
            {
                var width = rect.Right - rect.Left;
                var height = rect.Bottom - rect.Top;
                if (width > 0 && height > 0)
                    return true;
            }
        }
        catch { }

        return GetWindowRect(hWnd, out rect);
    }

    internal static bool IsValidWindowTitleFilter(string? windowTitle) => windowTitle is null || windowTitle.Length <= 256;

    internal static string SerializeWindows(IReadOnlyList<WindowInfo> windows) => JsonSerializer.Serialize(windows, new JsonSerializerOptions { WriteIndented = true });

    internal static List<WindowInfo> GetVisibleWindowsForProcessId(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return GetVisibleWindows([processId], process.ProcessName);
        }
        catch (ArgumentException) { return []; }
    }

    private static List<WindowInfo> GetVisibleWindows(HashSet<int> processIds, string processName)
    {
        if (processIds.Count == 0)
            return [];

        EnsureDpiAwareness();
        var windows = new List<WindowInfo>();
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd) || !GetWindowBounds(hWnd, out var rect)) return true;
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width <= 50 || height <= 50) return true;

            GetWindowThreadProcessId(hWnd, out var pid);
            if (!processIds.Contains((int)pid)) return true;

            var title = ReadWindowText(hWnd);
            var className = ReadClassName(hWnd);
            windows.Add(new WindowInfo((int)pid, processName, title, className, hWnd, rect.Left, rect.Top, width, height));
            return true;
        }, 0);

        return windows.OrderBy(window => window.ProcessId).ThenBy(window => window.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static bool TryFindVisibleWindow(int processId, string? titleFilter, out WindowInfo window, out string error)
    {
        var windows = GetVisibleWindowsForProcessId(processId);
        if (!string.IsNullOrWhiteSpace(titleFilter))
            windows = windows.Where(item => item.Title.Contains(titleFilter, StringComparison.OrdinalIgnoreCase)).ToList();

        if (windows.Count == 0)
        {
            window = null!;
            error = $"No visible top-level window was found for managed process {processId}" +
                (string.IsNullOrWhiteSpace(titleFilter) ? "." : $" with title containing '{titleFilter}'.");
            return false;
        }

        if (windows.Count > 1)
        {
            window = null!;
            error = $"More than one visible window matched managed process {processId}. Supply windowTitle to select one: " +
                string.Join("; ", windows.Take(5).Select(item => $"'{item.Title}' (PID {item.ProcessId})"));
            return false;
        }

        window = windows[0];
        error = string.Empty;
        return true;
    }

    internal static byte[] CaptureWindowByHandle(nint hWnd)
    {
        EnsureDpiAwareness();
        ActivateWindow(hWnd, requireForeground: false);
        if (!GetWindowBounds(hWnd, out var rect)) throw new InvalidOperationException("Could not read the target window bounds.");

        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top);

        GetWindowRect(hWnd, out var windowRect);
        var fullWidth = Math.Max(width, windowRect.Right - windowRect.Left);
        var fullHeight = Math.Max(height, windowRect.Bottom - windowRect.Top);

        using var fullBitmap = new Bitmap(fullWidth, fullHeight, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(fullBitmap);
        var hdc = graphics.GetHdc();
        var printed = PrintWindow(hWnd, hdc, 2); // PW_RENDERFULLCONTENT
        if (!printed)
            printed = PrintWindow(hWnd, hdc, 0);
        graphics.ReleaseHdc(hdc);

        if (!printed)
        {
            try
            {
                graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
                printed = true;
            }
            catch { }
        }

        if (!printed) throw new InvalidOperationException("Windows could not render the target window for capture.");

        Bitmap finalBitmap;
        var disposeFinal = true;

        var offsetX = Math.Max(0, rect.Left - windowRect.Left);
        var offsetY = Math.Max(0, rect.Top - windowRect.Top);
        if ((offsetX > 0 || offsetY > 0 || width < fullWidth || height < fullHeight) && (offsetX + width <= fullWidth && offsetY + height <= fullHeight))
        {
            var cropped = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(cropped))
            {
                g.DrawImage(fullBitmap, new Rectangle(0, 0, width, height), new Rectangle(offsetX, offsetY, width, height), GraphicsUnit.Pixel);
            }
            finalBitmap = cropped;
        }
        else
        {
            finalBitmap = fullBitmap;
            disposeFinal = false;
        }

        try
        {
            using var stream = new MemoryStream();
            finalBitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
        finally
        {
            if (disposeFinal)
                finalBitmap.Dispose();
        }
    }

    internal static string ClickWindowPoint(nint hWnd, WindowInfo window, int relativeX, int relativeY)
    {
        ActivateWindow(hWnd, requireForeground: true);
        var screenX = checked(window.X + relativeX);
        var screenY = checked(window.Y + relativeY);
        if (!SetCursorPos(screenX, screenY)) throw new InvalidOperationException("Windows refused to move the cursor.");

        mouse_event(MouseeventfLeftdown, 0, 0, 0, 0);
        mouse_event(MouseeventfLeftup, 0, 0, 0, 0);
        return $"Clicked ({relativeX}, {relativeY}) in '{window.Title}' (PID {window.ProcessId}).";
    }

    private static void ActivateWindow(nint hWnd, bool requireForeground)
    {
        ShowWindow(hWnd, SwRestore);
        ShowWindow(hWnd, SwShow);
        if (!SetForegroundWindow(hWnd) && requireForeground) throw new InvalidOperationException("Windows refused to foreground the target window.");
        Thread.Sleep(200);
    }

    private static string ReadWindowText(nint hWnd)
    {
        var value = new StringBuilder(1024);
        GetWindowText(hWnd, value, value.Capacity);
        return value.ToString();
    }

    private static string ReadClassName(nint hWnd)
    {
        var value = new StringBuilder(1024);
        GetClassName(hWnd, value, value.Capacity);
        return value.ToString();
    }
}
