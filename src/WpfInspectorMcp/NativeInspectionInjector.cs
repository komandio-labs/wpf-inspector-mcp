using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace WpfInspectorMcp;

/// <summary>Loads the trusted, server-shipped native bridge and waits for its authenticated agent setup result.</summary>
internal static class NativeInspectionInjector
{
    private const int Magic = 0x57495049, Version = 1, Capacity = 64 * 1024, RequestOffset = 32, ResultOffset = 32 * 1024;
    private const uint ProcessCreateThread = 0x0002, ProcessQueryInformation = 0x0400, ProcessVmOperation = 0x0008, ProcessVmRead = 0x0010, ProcessVmWrite = 0x0020;
    private const uint MemCommit = 0x1000, MemReserve = 0x2000, MemRelease = 0x8000, PageReadWrite = 0x04, WaitObject0 = 0, WaitTimeout = 0x102, DontResolveDllReferences = 1;

    internal static void Attach(Process process, string agentPath, string runtimeConfigPath, string pipeName, string secret)
    {
        ValidateTarget(process);
        var nativePath = Path.Combine(AppContext.BaseDirectory, "WpfInspector.NativeInjector.x64.dll");
        if (!File.Exists(nativePath)) throw new FileNotFoundException("The native inspector injector is not present beside the MCP server.", nativePath);
        var mapName = $"Local\\WpfInspector_Inject_{process.Id}";
        var eventName = $"Local\\WpfInspector_Inject_Result_{process.Id}";
        using var map = MemoryMappedFile.CreateOrOpen(mapName, Capacity, MemoryMappedFileAccess.ReadWrite);
        using var view = map.CreateViewAccessor(0, Capacity, MemoryMappedFileAccess.ReadWrite);
        using var completed = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        var sessionJson = JsonSerializer.Serialize(new { PipeName = pipeName, Secret = secret });
        var request = Encoding.Unicode.GetBytes(string.Concat(agentPath, '\0', "WpfInspector.Agent.AgentEntryPoint", '\0', "InitializeFromInjectionArgument", '\0', sessionJson, '\0'));
        view.Write(0, Magic); view.Write(4, Version); view.Write(8, request.Length); view.WriteArray(RequestOffset, request, 0, request.Length); view.Flush();
        var handle = OpenProcess(ProcessCreateThread | ProcessQueryInformation | ProcessVmOperation | ProcessVmRead | ProcessVmWrite, false, process.Id);
        if (handle == 0) throw Win32("Could not open the target WPF process. It must be owned by this user and not elevated.");
        try
        {
            var remoteModule = LoadLibrary(process, handle, nativePath);
            var entry = remoteModule + GetExportRva(nativePath, "WpfInspectorInject");
            var thread = CreateRemoteThread(handle, 0, 0, entry, 0, 0, out _);
            if (thread == 0) throw Win32("Could not begin inspection-agent injection.");
            try { Wait(thread, TimeSpan.FromSeconds(20), "Inspection-agent injection timed out."); }
            finally { CloseHandle(thread); }
            if (!completed.WaitOne(TimeSpan.FromSeconds(2))) throw new TimeoutException("The native injector did not report a result.");
            var code = view.ReadInt32(12); var length = view.ReadInt32(16);
            var bytes = new byte[Math.Clamp(length, 0, Capacity - ResultOffset)]; view.ReadArray(ResultOffset, bytes, 0, bytes.Length);
            var message = Encoding.Unicode.GetString(bytes).TrimEnd('\0');
            if (code != 0) throw new InvalidOperationException($"Native inspector injection failed ({code}): {message ?? "unknown error"}");
        }
        finally { CloseHandle(handle); }
    }

    internal static void ValidateTarget(Process process)
    {
        if (!Environment.Is64BitProcess || !Environment.Is64BitOperatingSystem) throw new PlatformNotSupportedException("The inspector injector requires 64-bit Windows.");
        if (process.HasExited) throw new InvalidOperationException("The target process exited before inspection could attach.");
        process.Refresh();
        var hasCoreClr = process.Modules.Cast<ProcessModule>().Any(m => string.Equals(m.ModuleName, "coreclr.dll", StringComparison.OrdinalIgnoreCase));
        var hasWpf = process.Modules.Cast<ProcessModule>().Any(m => string.Equals(m.ModuleName, "PresentationFramework.dll", StringComparison.OrdinalIgnoreCase));
        if (!hasCoreClr || !hasWpf) throw new InvalidOperationException("The target is not a running CoreCLR WPF process.");
    }

    private static nint LoadLibrary(Process process, nint handle, string path)
    {
        var bytes = Encoding.Unicode.GetBytes(path + '\0');
        var remote = VirtualAllocEx(handle, 0, (nuint)bytes.Length, MemCommit | MemReserve, PageReadWrite);
        if (remote == 0) throw Win32("Could not allocate memory in the target process.");
        try
        {
            if (!WriteProcessMemory(handle, remote, bytes, (nuint)bytes.Length, out var written) || written != (nuint)bytes.Length) throw Win32("Could not write the injector path into the target process.");
            var kernel = GetModuleHandleW("kernel32.dll"); var local = GetProcAddress(kernel, "LoadLibraryW"); var remoteKernel = FindModule(process, "kernel32.dll");
            var thread = CreateRemoteThread(handle, 0, 0, remoteKernel + (local - kernel), remote, 0, out _);
            if (thread == 0) throw Win32("Could not load the native inspection injector.");
            try { Wait(thread, TimeSpan.FromSeconds(10), "Loading the native inspection injector timed out."); }
            finally { CloseHandle(thread); }
            return WaitModule(process, path);
        }
        finally { VirtualFreeEx(handle, remote, 0, MemRelease); }
    }
    private static nint WaitModule(Process process, string path)
    {
        var full = Path.GetFullPath(path); var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline) { process.Refresh(); foreach (ProcessModule m in process.Modules) if (string.Equals(Path.GetFullPath(m.FileName), full, StringComparison.OrdinalIgnoreCase)) return m.BaseAddress; Thread.Sleep(50); }
        throw new InvalidOperationException("The target did not load the native inspection injector.");
    }
    private static nint FindModule(Process process, string name) { process.Refresh(); foreach (ProcessModule m in process.Modules) if (string.Equals(m.ModuleName, name, StringComparison.OrdinalIgnoreCase)) return m.BaseAddress; throw new InvalidOperationException($"Target has not loaded {name}."); }
    private static nint GetExportRva(string path, string name) { var module = LoadLibraryExW(path, 0, DontResolveDllReferences); if (module == 0) throw Win32("Could not inspect injector exports."); try { var export = GetProcAddress(module, name); if (export == 0) throw Win32("The injector export is missing."); return export - module; } finally { FreeLibrary(module); } }
    private static void Wait(nint thread, TimeSpan timeout, string message) { var deadline = DateTime.UtcNow + timeout; while (DateTime.UtcNow < deadline) { var wait = WaitForSingleObject(thread, 100); if (wait == WaitObject0) return; if (wait != WaitTimeout) throw Win32("Waiting for the target process failed."); } throw new TimeoutException(message); }
    private static Win32Exception Win32(string message) => new(Marshal.GetLastWin32Error(), message);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint VirtualAllocEx(nint process, nint address, nuint size, uint type, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFreeEx(nint process, nint address, nuint size, uint type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(nint process, nint address, byte[] bytes, nuint size, out nuint written);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint CreateRemoteThread(nint process, nint attributes, nuint stack, nint start, nint parameter, uint flags, out uint id);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern nint GetProcAddress(nint module, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint LoadLibraryExW(string file, nint hFile, uint flags);
    [DllImport("kernel32.dll")] private static extern bool FreeLibrary(nint module);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
