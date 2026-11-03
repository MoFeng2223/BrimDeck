#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <msctf.h>
#include <cstdint>

// A targeted, short-lived call-window hook executes TSF on the input window's own thread.
// It never observes keyboard input or text. The private message only carries a random mapping atom.
namespace {
constexpr DWORD Magic = 0x4244494d;
constexpr GUID KeyboardCategory = {0x34745c63,0xb2f0,0x4784,{0x8b,0x67,0x5e,0x12,0xc8,0x70,0x1a,0x31}};
constexpr CLSID ProfilesClass = {0x33c53a50,0xf456,0x4884,{0xb0,0x49,0x85,0xfd,0x64,0x3e,0xcf,0xed}};
bool IsForeground(uint64_t window) {
    auto hwnd = reinterpret_cast<HWND>(window);
    return hwnd && GetAncestor(hwnd, GA_ROOT) == GetForegroundWindow();
}
struct Identity { DWORD type, language; GUID clsid, profile; uint64_t layout; };
struct Request {
    DWORD magic, version;
    uint64_t window;
    DWORD thread, command;
    uint64_t deadline;
    Identity desired, actual;
    HRESULT result;
    LONG completed;
};
static_assert(sizeof(Request) == 136);

HRESULT Execute(Request& request) {
    HRESULT initialized = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(initialized) && initialized != RPC_E_CHANGED_MODE) return initialized;
    ITfInputProcessorProfileMgr* manager = nullptr;
    HRESULT result = CoCreateInstance(ProfilesClass, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&manager));
    if (SUCCEEDED(result)) {
        if (request.command == 1) {
            // ChangeCurrentLanguage and ActivateProfile affect only this target thread.
            ITfInputProcessorProfiles* profiles = nullptr;
            result = manager->QueryInterface(IID_PPV_ARGS(&profiles));
            if (SUCCEEDED(result)) {
                LANGID current = 0;
                result = profiles->GetCurrentLanguage(&current);
                if (SUCCEEDED(result) && current != request.desired.language)
                    result = profiles->ChangeCurrentLanguage(static_cast<LANGID>(request.desired.language));
                profiles->Release();
            }
            if (SUCCEEDED(result) && GetTickCount64() <= request.deadline
                && IsForeground(request.window))
                result = manager->ActivateProfile(request.desired.type, static_cast<LANGID>(request.desired.language),
                    request.desired.clsid, request.desired.profile,
                    request.desired.type == TF_PROFILETYPE_INPUTPROCESSOR ? nullptr : reinterpret_cast<HKL>(request.desired.layout), 0);
            else if (SUCCEEDED(result)) result = HRESULT_FROM_WIN32(ERROR_CANCELLED);
        }
        if (SUCCEEDED(result)) {
            TF_INPUTPROCESSORPROFILE active{};
            HRESULT observed = manager->GetActiveProfile(KeyboardCategory, &active);
            if (observed == S_OK) request.actual = {active.dwProfileType, active.langid, active.clsid, active.guidProfile,
                reinterpret_cast<uint64_t>(active.hkl)};
            else result = observed == S_FALSE ? HRESULT_FROM_WIN32(ERROR_NOT_FOUND) : observed;
        }
        manager->Release();
    }
    if (SUCCEEDED(initialized)) CoUninitialize();
    return result;
}
}

extern "C" __declspec(dllexport) LRESULT CALLBACK BrimDeckImeHook(int code, WPARAM wparam, LPARAM lparam) {
    static const UINT message = RegisterWindowMessageW(L"BrimDeck.InputMethods.Request.v1");
    if (code >= 0 && lparam) {
        auto* call = reinterpret_cast<const CWPSTRUCT*>(lparam);
        if (call->message == message && call->lParam == Magic) {
            wchar_t name[128]{};
            if (GlobalGetAtomNameW(static_cast<ATOM>(call->wParam), name, 128)
                && wcsncmp(name, L"Local\\BrimDeck.Ime.", 19) == 0) {
                HANDLE mapping = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, name);
                if (mapping) {
                    auto* request = static_cast<Request*>(MapViewOfFile(mapping, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(Request)));
                    if (request) {
                        if (request->magic == Magic && request->version == 1 && request->completed == 0 && request->command <= 1
                            && request->window == reinterpret_cast<uint64_t>(call->hwnd)
                            && IsForeground(request->window)
                            && request->thread == GetCurrentThreadId() && GetTickCount64() <= request->deadline) {
                            request->result = Execute(*request);
                            InterlockedExchange(&request->completed, 1);
                        }
                        UnmapViewOfFile(request);
                    }
                    CloseHandle(mapping);
                }
            }
        }
    }
    return CallNextHookEx(nullptr, code, wparam, lparam);
}
