#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shobjidl.h>
#include <shlobj.h>
#include <shellapi.h>
#include <wrl/client.h>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <string>
#include <stdexcept>
#include <vector>
#include <functional>
using Microsoft::WRL::ComPtr;
static const CLSID CopyPathClsid = {0x62f68386, 0x2117, 0x4d94, {0x94, 0x05, 0xc9, 0xb7, 0x92, 0xbe, 0x8e, 0xeb}};
static void Check(HRESULT hr) { if (FAILED(hr)) { char text[32]; sprintf_s(text, "HRESULT 0x%08lX", static_cast<unsigned long>(hr)); throw std::runtime_error(text); } }
static ComPtr<IShellItemArray> Selection(const std::vector<std::wstring>& paths) {
    std::vector<PIDLIST_ABSOLUTE> ids;
    for (const auto& path : paths) { PIDLIST_ABSOLUTE id = nullptr; Check(SHParseDisplayName(path.c_str(), nullptr, &id, 0, nullptr)); ids.push_back(id); }
    ComPtr<IShellItemArray> result;
    const auto hr = SHCreateShellItemArrayFromIDLists(static_cast<UINT>(ids.size()), const_cast<PCIDLIST_ABSOLUTE*>(ids.data()), &result);
    for (auto id : ids) CoTaskMemFree(id);
    Check(hr); return result;
}
static std::wstring ClipboardText() {
    bool opened = false;
    for (int attempt = 0; attempt < 40; ++attempt) {
        if (OpenClipboard(nullptr)) { opened = true; break; }
        Sleep(25);
    }
    if (!opened) throw std::runtime_error("Cannot read clipboard after retrying");
    auto value = GetClipboardData(CF_UNICODETEXT);
    auto text = value ? static_cast<const wchar_t*>(GlobalLock(value)) : nullptr;
    std::wstring result = text ? text : L"";
    if (text) GlobalUnlock(value);
    CloseClipboard(); return result;
}
// Metadata may not be ready during Explorer's fast GetState call.
class PendingItem final : public IShellItem {
    LONG refs_ = 1;
    ComPtr<IShellItem> inner_;
public:
    explicit PendingItem(IShellItem* item) : inner_(item) {}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** value) override {
        if (!value) return E_POINTER;
        *value = nullptr;
        if (iid != IID_IUnknown && iid != IID_IShellItem) return E_NOINTERFACE;
        *value = static_cast<IShellItem*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&refs_); }
    ULONG STDMETHODCALLTYPE Release() override { const auto count = InterlockedDecrement(&refs_); if (!count) delete this; return count; }
    HRESULT STDMETHODCALLTYPE BindToHandler(IBindCtx* c, REFGUID h, REFIID i, void** v) override { return inner_->BindToHandler(c,h,i,v); }
    HRESULT STDMETHODCALLTYPE GetParent(IShellItem** v) override { return inner_->GetParent(v); }
    HRESULT STDMETHODCALLTYPE GetDisplayName(SIGDN n, PWSTR* v) override { return inner_->GetDisplayName(n,v); }
    HRESULT STDMETHODCALLTYPE GetAttributes(SFGAOF, SFGAOF*) override { return E_FAIL; }
    HRESULT STDMETHODCALLTYPE Compare(IShellItem* o, SICHINTF h, int* v) override { return inner_->Compare(o,h,v); }
};
class PendingSelection final : public IShellItemArray {
    LONG refs_ = 1;
    ComPtr<IShellItem> item_;
public:
    explicit PendingSelection(IShellItem* item) { if (item) item_.Attach(new PendingItem(item)); }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** value) override {
        if (!value) return E_POINTER; *value = nullptr;
        if (iid != IID_IUnknown && iid != IID_IShellItemArray) return E_NOINTERFACE;
        *value = static_cast<IShellItemArray*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&refs_); }
    ULONG STDMETHODCALLTYPE Release() override { const auto count = InterlockedDecrement(&refs_); if (!count) delete this; return count; }
    HRESULT STDMETHODCALLTYPE GetCount(DWORD* v) override { if (!v) return E_POINTER; *v = item_ ? 1 : 0; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetItemAt(DWORD index, IShellItem** v) override { if (!v) return E_POINTER; *v = nullptr; if (index || !item_) return E_INVALIDARG; return item_.CopyTo(v); }
    HRESULT STDMETHODCALLTYPE BindToHandler(IBindCtx*, REFGUID, REFIID, void**) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetPropertyStore(GETPROPERTYSTOREFLAGS, REFIID, void**) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetPropertyDescriptionList(REFPROPERTYKEY, REFIID, void**) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetAttributes(SIATTRIBFLAGS, SFGAOF, SFGAOF*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE EnumItems(IEnumShellItems**) override { return E_NOTIMPL; }
};
int wmain(int argc, wchar_t** argv) {
    if (argc < 3) return 2;
    Check(OleInitialize(nullptr));
    if (argc > 3 && (std::wstring(argv[3]) == L"--find-verb" || std::wstring(argv[3]) == L"--find-menu-title" || std::wstring(argv[3]) == L"--find-background-title")) {
        int result = 3;
        PIDLIST_ABSOLUTE id = nullptr;
        HMENU menu = CreatePopupMenu();
        try {
            Check(SHParseDisplayName(argv[1], nullptr, &id, 0, nullptr));
            ComPtr<IShellFolder> parent;
            PCUITEMID_CHILD child = nullptr;
            Check(SHBindToParent(id, IID_PPV_ARGS(&parent), &child));
            ComPtr<IContextMenu> context;
            const auto mode = std::wstring(argv[3]);
            if (mode == L"--find-background-title") {
                ComPtr<IShellItem> folder;
                Check(SHCreateItemFromParsingName(argv[1], nullptr, IID_PPV_ARGS(&folder)));
                ComPtr<IShellFolder> object;
                Check(folder->BindToHandler(nullptr, BHID_SFObject, IID_PPV_ARGS(&object)));
                Check(object->CreateViewObject(nullptr, IID_IContextMenu, reinterpret_cast<void**>(context.GetAddressOf())));
            } else Check(parent->GetUIObjectOf(nullptr, 1, &child, IID_IContextMenu, nullptr, reinterpret_cast<void**>(context.GetAddressOf())));
            const auto hr = context->QueryContextMenu(menu, 0, 1, 0x7fff, CMF_NORMAL);
            Check(hr);
            const auto count = static_cast<UINT>(HRESULT_CODE(hr));
            for (UINT command = 0; mode == L"--find-verb" && command < count; ++command) {
                wchar_t verb[512] = {};
                if (SUCCEEDED(context->GetCommandString(command, GCS_VERBW, nullptr, reinterpret_cast<LPSTR>(verb), 512)) && _wcsicmp(verb, argv[2]) == 0) { result = 0; break; }
            }
            if (mode != L"--find-verb") {
                ComPtr<IContextMenu2> messages; context.As(&messages);
                std::function<bool(HMENU, int)> find = [&](HMENU current, int depth) {
                    if (depth > 5) return false;
                    const int length = GetMenuItemCount(current);
                    for (int index = 0; index < length; ++index) {
                        wchar_t text[512] = {};
                        MENUITEMINFOW item{}; item.cbSize = sizeof(item); item.fMask = MIIM_STRING | MIIM_SUBMENU; item.dwTypeData = text; item.cch = 511;
                        if (!GetMenuItemInfoW(current, static_cast<UINT>(index), TRUE, &item)) continue;
                        std::wstring label; for (const auto letter : std::wstring(text)) if (letter != L'&') label += letter;
                        if (label == argv[2]) return true;
                        if (item.hSubMenu) {
                            if (messages) messages->HandleMenuMsg(WM_INITMENUPOPUP, reinterpret_cast<WPARAM>(item.hSubMenu), MAKELPARAM(index, FALSE));
                            if (find(item.hSubMenu, depth + 1)) return true;
                        }
                    }
                    return false;
                };
                if (find(menu, 0)) result = 0;
            }
        } catch (const std::exception& ex) { std::cerr << ex.what() << "\n"; result = 1; }
        if (menu) DestroyMenu(menu);
        CoTaskMemFree(id);
        OleUninitialize();
        return result;
    }
    ComPtr<IDataObject> before;
    Check(OleGetClipboard(&before));
    HMODULE dll = nullptr;
    int exitCode = 0;
    try {
        ComPtr<IExplorerCommand> command;
        if (argc > 3 && std::wstring(argv[3]) == L"--registered") {
            Check(CoCreateInstance(CopyPathClsid, nullptr, CLSCTX_LOCAL_SERVER, IID_PPV_ARGS(&command)));
        } else {
            dll = LoadLibraryW(argv[1]);
            if (!dll) throw std::runtime_error("Cannot load command DLL");
            auto getClass = reinterpret_cast<HRESULT(__stdcall*)(REFCLSID, REFIID, void**)>(GetProcAddress(dll, "DllGetClassObject"));
            if (!getClass) throw std::runtime_error("Missing COM factory");
            ComPtr<IClassFactory> factory;
            Check(getClass(CopyPathClsid, IID_PPV_ARGS(&factory)));
            Check(factory->CreateInstance(nullptr, IID_PPV_ARGS(&command)));
        }
        PWSTR title = nullptr;
        Check(command->GetTitle(nullptr, &title));
        const bool titleMatches = std::wstring(title) == L"复制文件路径";
        CoTaskMemFree(title);
        if (!titleMatches) throw std::runtime_error("Wrong title");
        EXPCMDFLAGS flags; Check(command->GetFlags(&flags));
        if (flags != ECF_DEFAULT) throw std::runtime_error("Command unexpectedly has submenus");
        EXPCMDSTATE state; Check(command->GetState(nullptr, FALSE, &state));
        if (state != ECS_ENABLED) throw std::runtime_error("Early Explorer probe hides command");
        ComPtr<IShellItemArray> empty; empty.Attach(new PendingSelection(nullptr));
        Check(command->GetState(empty.Get(), FALSE, &state));
        if (state != ECS_HIDDEN) throw std::runtime_error("Explicit empty selection visible");
        std::cout << "PASS early Explorer probe and explicit empty-selection distinction\n";
        const std::filesystem::path root(argv[2]);
        std::filesystem::create_directories(root / L"项目 材料, v1");
        const auto file = (root / L"会议 终稿, v1.txt").wstring();
        const auto folder = (root / L"项目 材料, v1").wstring();
        const auto special = (root / L"a & b $value `quote'.txt").wstring();
        std::ofstream(file) << "preserve sample file";
        std::ofstream(special) << "preserve special sample";
        for (const auto& path : {file, folder, special}) {
            auto selection = Selection({path});
            Check(command->GetState(selection.Get(), FALSE, &state));
            if (state != ECS_ENABLED) throw std::runtime_error("Command not enabled for file/folder");
            Check(command->Invoke(selection.Get(), nullptr));
            if (ClipboardText() != path) throw std::runtime_error("Copied path differs or contains quotes");
        }
        std::cout << "PASS Unicode files, folders, spaces, commas, and shell metacharacters without quotes\n";
        auto regular = Selection({file});
        ComPtr<IShellItem> regularItem; Check(regular->GetItemAt(0, &regularItem));
        ComPtr<IShellItemArray> pending; pending.Attach(new PendingSelection(regularItem.Get()));
        if (command->GetState(pending.Get(), FALSE, &state) != E_PENDING) throw std::runtime_error("Transient metadata failure hides the command");
        Check(command->GetState(pending.Get(), TRUE, &state));
        if (state != ECS_ENABLED) throw std::runtime_error("Deferred filesystem-path query does not enable command");
        std::cout << "PASS deferred metadata and filesystem-path fallback in Explorer\n";
        auto multiple = Selection({file, folder, special});
        Check(command->Invoke(multiple.Get(), nullptr));
        if (ClipboardText() != file + L"\r\n" + folder + L"\r\n" + special) throw std::runtime_error("Multi-selection order/line breaks differ");
        std::cout << "PASS multiple selections use one unquoted path per line\n";
        ComPtr<IObjectWithSelection> selectedObject; Check(command.As(&selectedObject));
        Check(selectedObject->SetSelection(multiple.Get()));
        Check(command->GetState(nullptr, FALSE, &state));
        if (state != ECS_ENABLED) throw std::runtime_error("Stored selection not enabled");
        Check(command->Invoke(nullptr, nullptr));
        if (ClipboardText() != file + L"\r\n" + folder + L"\r\n" + special) throw std::runtime_error("Stored selection copied incorrectly");
        if (SUCCEEDED(command->Invoke(empty.Get(), nullptr))) throw std::runtime_error("Explicit empty selection used stale items");
        ComPtr<IObjectWithSite> siteObject; Check(command.As(&siteObject));
        Check(siteObject->SetSite(nullptr));
        ComPtr<IShellItemArray> reset;
        if (SUCCEEDED(selectedObject->GetSelection(IID_PPV_ARGS(&reset)))) throw std::runtime_error("Site reset retains stale selection");
        auto virtualSelection = Selection({L"::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"});
        Check(command->GetState(virtualSelection.Get(), FALSE, &state));
        if (state != ECS_HIDDEN || SUCCEEDED(command->Invoke(virtualSelection.Get(), nullptr))) throw std::runtime_error("Virtual item accepted");
        std::cout << "PASS stored selections, explicit empty override, site reset and virtual-item rejection\n";
        const auto saved = ClipboardText();
        if (SUCCEEDED(command->Invoke(nullptr, nullptr)) || ClipboardText() != saved) throw std::runtime_error("Invalid selection changed clipboard");
        std::cout << "PASS invalid selection leaves clipboard intact\n";
        std::ifstream check(file); std::string content; std::getline(check, content);
        if (content != "preserve sample file") throw std::runtime_error("File content changed");
        std::cout << "PASS selected material stays in place and unchanged\n";
        if (argc > 3) std::cout << "PASS packaged COM activation in Windows surrogate\n";
    } catch (const std::exception& ex) { std::cerr << ex.what() << "\n"; exitCode = 1; }
    HRESULT restored = E_FAIL;
    for (int attempt = 0; attempt < 40; ++attempt) {
        restored = OleSetClipboard(before.Get());
        if (SUCCEEDED(restored)) { restored = OleFlushClipboard(); if (SUCCEEDED(restored)) break; }
        Sleep(25);
    }
    if (FAILED(restored)) { std::cerr << "Cannot restore previous clipboard: " << std::hex << restored << "\n"; exitCode = 1; }
    else std::cout << "PASS previous clipboard restored\n";
    before.Reset();
    if (dll) FreeLibrary(dll);
    OleUninitialize();
    return exitCode;
}
