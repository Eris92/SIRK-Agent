#include "capture_dxgi.h"

#include <windows.h>
#include <filesystem>
#include <iostream>
#include <string>
#include <string_view>

int wmain(int argc, wchar_t* argv[]) {
    std::filesystem::path output;
    for (int index = 1; index < argc; ++index) {
        const std::wstring_view argument(argv[index]);
        if (argument == L"--output" && index + 1 < argc) output = argv[++index];
        else if (argument == L"--version") { std::wcout << L"0.1.0\n"; return 0; }
    }

    if (output.empty()) {
        std::wcerr << L"Missing --output <png-path>.\n";
        return 2;
    }

    workspace::CaptureResult result;
    std::wstring error;
    if (!workspace::CapturePrimaryOutputPng(output, result, error)) {
        std::wcerr << error << L'\n';
        return 3;
    }

    std::wcout << L"{\"ok\":true,\"width\":" << result.width
               << L",\"height\":" << result.height
               << L",\"backend\":\"DXGI Desktop Duplication\"}\n";
    return 0;
}
