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
int wmain(int argc, wchar_t** argv) {
    if (argc < 3) return 2;
    Check(OleInitialize(nullptr));
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
        if (state != ECS_HIDDEN) throw std::runtime_error("Command visible without a selection");
        std::cout << "PASS single command and empty-selection state\n";
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
        auto multiple = Selection({file, folder, special});
        Check(command->Invoke(multiple.Get(), nullptr));
        if (ClipboardText() != file + L"\r\n" + folder + L"\r\n" + special) throw std::runtime_error("Multi-selection order/line breaks differ");
        std::cout << "PASS multiple selections use one unquoted path per line\n";
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
