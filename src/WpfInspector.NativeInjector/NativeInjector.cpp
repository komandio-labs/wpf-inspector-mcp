#include <windows.h>
#include <unknwn.h>
#include <stdio.h>

// CoreCLR exposes this compatibility entry point for tools that attach to an
// already-running runtime. It returns that runtime without creating a second
// hostfxr initialization context in the target process.
typedef HRESULT (STDAPICALLTYPE* GetCLRRuntimeHostFn)(REFIID, IUnknown**);
typedef HRESULT (STDMETHODCALLTYPE* ExecuteInDefaultAppDomainFn)(void*, LPCWSTR, LPCWSTR, LPCWSTR, LPCWSTR, DWORD*);

static const int Magic = 0x57495049, Capacity = 64 * 1024, RequestOffset = 32, ResultOffset = 32 * 1024;

extern "C" __declspec(dllexport) DWORD WINAPI WpfInspectorInject(void*)
{
    const DWORD pid = GetCurrentProcessId();
    wchar_t mapName[128], eventName[128];
    swprintf_s(mapName, L"Local\\WpfInspector_Inject_%lu", pid);
    swprintf_s(eventName, L"Local\\WpfInspector_Inject_Result_%lu", pid);
    int code = 100; const wchar_t* message = L"Unknown injector failure.";
    HANDLE map = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, mapName);
    if (map)
    {
        auto data = static_cast<unsigned char*>(MapViewOfFile(map, FILE_MAP_ALL_ACCESS, 0, 0, Capacity));
        if (data && *reinterpret_cast<int*>(data) == Magic)
        {
            int length = *reinterpret_cast<int*>(data + 8);
            auto request = reinterpret_cast<wchar_t*>(data + RequestOffset);
            if (length > 4 && length < ResultOffset - RequestOffset)
            {
                // Request is: absolute agent DLL path\0type name\0method name\0argument JSON\0
                wchar_t* assembly = request;
                wchar_t* type = assembly + wcslen(assembly) + 1;
                wchar_t* method = type + wcslen(type) + 1;
                wchar_t* argument = method + wcslen(method) + 1;
                HMODULE coreclr = GetModuleHandleW(L"coreclr.dll");
                auto getHost = coreclr ? reinterpret_cast<GetCLRRuntimeHostFn>(GetProcAddress(coreclr, "GetCLRRuntimeHost")) : nullptr;
                void* host = nullptr;
                if (!getHost) { code = 21; message = L"Target CoreCLR host was unavailable."; }
                else
                {
                    // IID_ICLRRuntimeHost. The call uses the target's existing
                    // CoreCLR context; no hostfxr initialization is created here.
                    const IID runtimeHostIid = { 0x90F1A06C, 0x7712, 0x4762, { 0x86, 0xB5, 0x7A, 0x5E, 0xBA, 0x6B, 0xDB, 0x02 } };
                    const auto hostResult = getHost(runtimeHostIid, reinterpret_cast<IUnknown**>(&host));
                    if (FAILED(hostResult) || !host) { code = 22; message = L"Could not obtain target CoreCLR runtime host."; }
                    else
                    {
                        auto vtable = *reinterpret_cast<void***>(host);
                        auto execute = reinterpret_cast<ExecuteInDefaultAppDomainFn>(vtable[11]);
                        DWORD managed = 0; HRESULT hr = execute(host, assembly, type, method, argument, &managed);
                        if (FAILED(hr)) { code = 23; message = L"CoreCLR rejected the inspection agent."; }
                        else if (managed != 0) { code = 24; message = L"Inspection agent returned a failure code."; }
                        else { code = 0; message = L"Inspection agent attached."; }
                        reinterpret_cast<IUnknown*>(host)->Release();
                    }
                }
            }
            else { code = 20; message = L"Invalid injector request."; }
            auto result = reinterpret_cast<wchar_t*>(data + ResultOffset);
            wcsncpy_s(result, (Capacity - ResultOffset) / sizeof(wchar_t), message, _TRUNCATE);
            *reinterpret_cast<int*>(data + 12) = code;
            *reinterpret_cast<int*>(data + 16) = static_cast<int>((wcslen(result) + 1) * sizeof(wchar_t));
            FlushViewOfFile(data, Capacity);
            UnmapViewOfFile(data);
        }
        CloseHandle(map);
    }
    HANDLE done = OpenEventW(EVENT_MODIFY_STATE, FALSE, eventName); if (done) { SetEvent(done); CloseHandle(done); }
    return code;
}
