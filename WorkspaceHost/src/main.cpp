#include <windows.h>
#include <chrono>
#include <ctime>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <iterator>
#include <sstream>
#include <string>
#include <string_view>
#include <thread>

namespace {
constexpr wchar_t kPipeName[] = L"\\\\.\\pipe\\SirK.MeshCentral.Workspace";
constexpr wchar_t kVersion[] = L"0.2.0";

std::filesystem::path LogPath() {
    wchar_t programData[MAX_PATH]{};
    const DWORD length = GetEnvironmentVariableW(L"ProgramData", programData, MAX_PATH);
    std::filesystem::path base = length > 0 ? programData : L"C:\\ProgramData";
    base /= L"SirK\\Workspace\\Logs";
    std::filesystem::create_directories(base);
    return base / L"workspace.log";
}

void Log(const std::wstring& message) {
    const auto now = std::chrono::system_clock::now();
    const std::time_t value = std::chrono::system_clock::to_time_t(now);
    std::tm local{};
    localtime_s(&local, &value);

    std::wofstream stream(LogPath(), std::ios::app);
    stream << std::put_time(&local, L"%Y-%m-%d %H:%M:%S")
           << L" [PID=" << GetCurrentProcessId() << L"] "
           << message << L'\n';
}

std::wstring CurrentUser() {
    wchar_t value[256]{};
    DWORD size = static_cast<DWORD>(std::size(value));
    return GetUserNameW(value, &size) ? value : L"unknown";
}

std::wstring CurrentDesktop() {
    HDESK desktop = GetThreadDesktop(GetCurrentThreadId());
    wchar_t value[256]{};
    DWORD needed = 0;
    if (desktop != nullptr && GetUserObjectInformationW(desktop, UOI_NAME, value, sizeof(value), &needed)) {
        return value;
    }
    return L"unknown";
}

std::string Utf8(const std::wstring& value) {
    if (value.empty()) return {};
    const int size = WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    std::string result(size, '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), result.data(), size, nullptr, nullptr);
    return result;
}

std::wstring Wide(const std::string& value) {
    if (value.empty()) return {};
    const int size = MultiByteToWideChar(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), nullptr, 0);
    std::wstring result(size, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), result.data(), size);
    return result;
}

std::string Heartbeat(std::chrono::steady_clock::time_point started) {
    DWORD sessionId = 0;
    ProcessIdToSessionId(GetCurrentProcessId(), &sessionId);
    const auto uptime = std::chrono::duration_cast<std::chrono::seconds>(std::chrono::steady_clock::now() - started).count();

    std::ostringstream json;
    json << "{\"type\":\"heartbeat\",\"version\":\"0.2.0\",\"pid\":" << GetCurrentProcessId()
         << ",\"sessionId\":" << sessionId
         << ",\"user\":\"" << Utf8(CurrentUser())
         << "\",\"desktop\":\"" << Utf8(CurrentDesktop())
         << "\",\"uptimeSeconds\":" << uptime << "}\n";
    return json.str();
}

int RunServer() {
    const auto started = std::chrono::steady_clock::now();
    Log(L"WorkspaceHost started. Version " + std::wstring(kVersion));

    while (true) {
        HANDLE pipe = CreateNamedPipeW(
            kPipeName,
            PIPE_ACCESS_OUTBOUND,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
            1,
            64 * 1024,
            64 * 1024,
            0,
            nullptr);

        if (pipe == INVALID_HANDLE_VALUE) {
            Log(L"CreateNamedPipe failed: " + std::to_wstring(GetLastError()));
            return 2;
        }

        Log(L"Waiting for pipe client");
        const BOOL connected = ConnectNamedPipe(pipe, nullptr) || GetLastError() == ERROR_PIPE_CONNECTED;
        if (!connected) {
            Log(L"ConnectNamedPipe failed: " + std::to_wstring(GetLastError()));
            CloseHandle(pipe);
            std::this_thread::sleep_for(std::chrono::seconds(2));
            continue;
        }

        Log(L"Pipe client connected");
        while (true) {
            const std::string payload = Heartbeat(started);
            DWORD written = 0;
            if (!WriteFile(pipe, payload.data(), static_cast<DWORD>(payload.size()), &written, nullptr)) {
                Log(L"Pipe client disconnected");
                break;
            }
            FlushFileBuffers(pipe);
            std::this_thread::sleep_for(std::chrono::seconds(5));
        }

        DisconnectNamedPipe(pipe);
        CloseHandle(pipe);
    }
}
}

int wmain(int argc, wchar_t* argv[]) {
    if (argc > 1 && std::wstring_view(argv[1]) == L"--version") {
        std::wcout << kVersion << L'\n';
        return 0;
    }

    try {
        return RunServer();
    } catch (const std::exception& ex) {
        Log(L"Unhandled exception: " + Wide(ex.what()));
        return 1;
    } catch (...) {
        Log(L"Unhandled unknown exception");
        return 1;
    }
}
