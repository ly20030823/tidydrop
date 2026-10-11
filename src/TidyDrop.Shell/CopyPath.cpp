#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shobjidl.h>
#include <shlwapi.h>
#include <new>
#include <string>

// One command, with no child commands, so Explorer can show a single top-level row.
static const CLSID CopyPathClsid = {0x62f68386, 0x2117, 0x4d94, {0x94, 0x05, 0xc9, 0xb7, 0x92, 0xbe, 0x8e, 0xeb}};
static LONG objects = 0;
static HMODULE module = nullptr;

static HRESULT CopySelection(IShellItemArray* items) noexcept
{
    if (!items) return E_INVALIDARG;
    try {
        DWORD count = 0;
        HRESULT hr = items->GetCount(&count);
        if (FAILED(hr)) return hr;
        if (!count) return E_INVALIDARG;
        std::wstring text;
        for (DWORD index = 0; index < count; ++index) {
            IShellItem* item = nullptr;
            hr = items->GetItemAt(index, &item);
            if (FAILED(hr)) return hr;
            PWSTR path = nullptr;
            hr = item->GetDisplayName(SIGDN_FILESYSPATH, &path);
            item->Release();
            if (FAILED(hr)) { CoTaskMemFree(path); return hr; } // Leave the clipboard intact if any selection is virtual.
            try {
                if (index) text += L"\r\n";
                text += path;
            } catch (...) { CoTaskMemFree(path); throw; }
            CoTaskMemFree(path);
        }
        const SIZE_T bytes = (text.size() + 1) * sizeof(wchar_t);
        HGLOBAL buffer = GlobalAlloc(GMEM_MOVEABLE, bytes);
        if (!buffer) return E_OUTOFMEMORY;
        void* destination = GlobalLock(buffer);
        if (!destination) { GlobalFree(buffer); return E_OUTOFMEMORY; }
        memcpy(destination, text.c_str(), bytes);
        GlobalUnlock(buffer);
        // EmptyClipboard requires a real owner window for SetClipboardData.
        HWND owner = CreateWindowExW(0, L"STATIC", L"TidyDrop.CopyPath", 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, module, nullptr);
        if (!owner) { GlobalFree(buffer); return HRESULT_FROM_WIN32(GetLastError()); }
        bool opened = false;
        for (int attempt = 0; attempt < 6; ++attempt) {
            if (OpenClipboard(owner)) { opened = true; break; }
            if (attempt < 5) Sleep(25);
        }
        if (!opened) { GlobalFree(buffer); DestroyWindow(owner); return HRESULT_FROM_WIN32(ERROR_BUSY); }
        hr = S_OK;
        if (!EmptyClipboard() || !SetClipboardData(CF_UNICODETEXT, buffer)) {
            hr = HRESULT_FROM_WIN32(GetLastError());
            if (SUCCEEDED(hr)) hr = E_FAIL;
            GlobalFree(buffer);
        }
        CloseClipboard();
        DestroyWindow(owner);
        return hr;
    } catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
    catch (...) { return E_FAIL; }
}

class CopyPath final : public IExplorerCommand {
    LONG references = 1;
public:
    CopyPath() { InterlockedIncrement(&objects); }
    ~CopyPath() { InterlockedDecrement(&objects); }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** value) override {
        if (!value) return E_POINTER;
        *value = nullptr;
        if (iid != __uuidof(IUnknown) && iid != __uuidof(IExplorerCommand)) return E_NOINTERFACE;
        *value = static_cast<IExplorerCommand*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&references); }
    ULONG STDMETHODCALLTYPE Release() override { const auto left = InterlockedDecrement(&references); if (!left) delete this; return left; }
    HRESULT STDMETHODCALLTYPE GetTitle(IShellItemArray*, PWSTR* title) override { return title ? SHStrDupW(L"复制文件路径", title) : E_POINTER; }
    HRESULT STDMETHODCALLTYPE GetIcon(IShellItemArray*, PWSTR* icon) override {
        if (!icon) return E_POINTER;
        *icon = nullptr;
        wchar_t path[32768];
        const DWORD length = GetModuleFileNameW(module, path, ARRAYSIZE(path));
        if (!length || length == ARRAYSIZE(path)) return E_FAIL;
        try { return SHStrDupW((std::wstring(path, length) + L",-101").c_str(), icon); }
        catch (...) { return E_OUTOFMEMORY; }
    }
    HRESULT STDMETHODCALLTYPE GetToolTip(IShellItemArray*, PWSTR* tip) override { return tip ? SHStrDupW(L"复制完整路径，不带引号；多选时每行一个", tip) : E_POINTER; }
    HRESULT STDMETHODCALLTYPE GetCanonicalName(GUID* name) override { if (!name) return E_POINTER; *name = CopyPathClsid; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetState(IShellItemArray* items, BOOL okToBeSlow, EXPCMDSTATE* state) override {
        if (!state) return E_POINTER;
        *state = ECS_HIDDEN;
        DWORD count = 0;
        if (!items || FAILED(items->GetCount(&count)) || !count) return S_OK;
        IShellItem* first = nullptr;
        if (FAILED(items->GetItemAt(0, &first))) return S_OK;
        SFGAOF attributes = 0;
        const HRESULT hr = first->GetAttributes(SFGAO_FILESYSTEM, &attributes);
        if (SUCCEEDED(hr)) {
            if (attributes & SFGAO_FILESYSTEM) *state = ECS_ENABLED;
        } else if (!okToBeSlow) {
            // Explorer can initially supply an item whose attributes aren't ready.
            // Ask for the deferred query instead of permanently hiding the command.
            first->Release();
            return E_PENDING;
        } else {
            PWSTR path = nullptr;
            if (SUCCEEDED(first->GetDisplayName(SIGDN_FILESYSPATH, &path))) {
                *state = ECS_ENABLED;
                CoTaskMemFree(path);
            }
        }
        first->Release();
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE Invoke(IShellItemArray* items, IBindCtx*) override { return CopySelection(items); }
    HRESULT STDMETHODCALLTYPE GetFlags(EXPCMDFLAGS* flags) override { if (!flags) return E_POINTER; *flags = ECF_DEFAULT; return S_OK; }
    HRESULT STDMETHODCALLTYPE EnumSubCommands(IEnumExplorerCommand** commands) override { if (!commands) return E_POINTER; *commands = nullptr; return E_NOTIMPL; }
};

class Factory final : public IClassFactory {
    LONG references = 1;
public:
    Factory() { InterlockedIncrement(&objects); }
    ~Factory() { InterlockedDecrement(&objects); }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** value) override {
        if (!value) return E_POINTER;
        *value = nullptr;
        if (iid != __uuidof(IUnknown) && iid != __uuidof(IClassFactory)) return E_NOINTERFACE;
        *value = static_cast<IClassFactory*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&references); }
    ULONG STDMETHODCALLTYPE Release() override { const auto left = InterlockedDecrement(&references); if (!left) delete this; return left; }
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID iid, void** value) override {
        if (!value) return E_POINTER;
        *value = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        auto command = new (std::nothrow) CopyPath();
        if (!command) return E_OUTOFMEMORY;
        const HRESULT hr = command->QueryInterface(iid, value);
        command->Release(); return hr;
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override { if (lock) InterlockedIncrement(&objects); else InterlockedDecrement(&objects); return S_OK; }
};

extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID clsid, REFIID iid, void** value) {
    if (!value) return E_POINTER;
    *value = nullptr;
    if (clsid != CopyPathClsid) return CLASS_E_CLASSNOTAVAILABLE;
    auto factory = new (std::nothrow) Factory();
    if (!factory) return E_OUTOFMEMORY;
    const HRESULT hr = factory->QueryInterface(iid, value);
    factory->Release(); return hr;
}
extern "C" HRESULT __stdcall DllCanUnloadNow() { return objects == 0 ? S_OK : S_FALSE; }
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) { module = instance; DisableThreadLibraryCalls(instance); }
    return TRUE;
}
