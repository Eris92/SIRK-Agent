#include <windows.h>
#include <userenv.h>
#include <wtsapi32.h>
#include <chrono>
#include <ctime>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <sstream>
#include <string>
#include <string_view>
#include <thread>
#include <vector>

namespace {
constexpr wchar_t kPipeName[] = L"\\\\.\\pipe\\SirK.MeshCentral.Workspace";
constexpr wchar_t kVersion[] = L"0.4.0";

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

std::string JsonEscape(const std::string& value) {
    std::ostringstream escaped;
    for (const unsigned char character : value) {
        switch (character) {
        case '"': escaped << "\\\""; break;
        case '\\': escaped << "\\\\"; break;
        case '\b': escaped << "\\b"; break;
        case '\f': escaped << "\\f"; break;
        case '\n': escaped << "\\n"; break;
        case '\r': escaped << "\\r"; break;
        case '\t': escaped << "\\t"; break;
        default:
            if (character < 0x20) {
                escaped << "\\u" << std::hex << std::setw(4) << std::setfill('0') << static_cast<int>(character) << std::dec;
            } else {
                escaped << static_cast<char>(character);
            }
        }
    }
    return escaped.str();
}

bool IsLocalSystem() {
    SID_IDENTIFIER_AUTHORITY ntAuthority = SECURITY_NT_AUTHORITY;
    PSID systemSid = nullptr;
    if (!AllocateAndInitializeSid(&ntAuthority, 1, SECURITY_LOCAL_SYSTEM_RID, 0, 0, 0, 0, 0, 0, 0, &systemSid)) {
        return false;
    }
    BOOL member = FALSE;
    const BOOL checked = CheckTokenMembership(nullptr, systemSid, &member);
    FreeSid(systemSid);
    return checked && member;
}

bool SessionHasUser(DWORD sessionId) {
    LPWSTR value = nullptr;
    DWORD bytes = 0;
    const BOOL ok = WTSQuerySessionInformationW(WTS_CURRENT_SERVER_HANDLE, sessionId, WTSUserName, &value, &bytes);
    const bool hasUser = ok && value != nullptr && bytes > sizeof(wchar_t) && value[0] != L'\0';
    if (value != nullptr) WTSFreeMemory(value);
    return hasUser;
}

DWORD FindInteractiveSession() {
    const DWORD consoleSession = WTSGetActiveConsoleSessionId();
    if (consoleSession != 0xFFFFFFFF && SessionHasUser(consoleSession)) return consoleSession;

    PWTS_SESSION_INFOW sessions = nullptr;
    DWORD count = 0;
    if (!WTSEnumerateSessionsW(WTS_CURRENT_SERVER_HANDLE, 0, 1, &sessions, &count)) return 0xFFFFFFFF;

    DWORD selected = 0xFFFFFFFF;
    for (DWORD index = 0; index < count; ++index) {
        if (sessions[index].State == WTSActive && SessionHasUser(sessions[index].SessionId)) {
            selected = sessions[index].SessionId;
            break;
        }
    }
    WTSFreeMemory(sessions);
    return selected;
}

std::wstring CurrentExecutable() {
    std::vector<wchar_t> buffer(32768);
    const DWORD length = GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
    if (length == 0 || length >= buffer.size()) return {};
    return std::wstring(buffer.data(), length);
}

bool RelaunchInInteractiveSession(DWORD sessionId) {
    HANDLE userToken = nullptr;
    HANDLE primaryToken = nullptr;
    LPVOID environment = nullptr;
    PROCESS_INFORMATION processInfo{};
    STARTUPINFOW startupInfo{};
    startupInfo.cb = sizeof(startupInfo);
    startupInfo.lpDesktop = const_cast<LPWSTR>(L"winsta0\\default");

    if (!WTSQueryUserToken(sessionId, &userToken)) {
        Log(L"WTSQueryUserToken failed for session " + std::to_wstring(sessionId) + L": " + std::to_wstring(GetLastError()));
        return false;
    }

    bool success = false;
    do {
        if (!DuplicateTokenEx(userToken, TOKEN_ALL_ACCESS, nullptr, SecurityImpersonation, TokenPrimary, &primaryToken)) {
            Log(L"DuplicateTokenEx failed: " + std::to_wstring(GetLastError()));
            break;
        }

        if (!CreateEnvironmentBlock(&environment, primaryToken, FALSE)) {
            Log(L"CreateEnvironmentBlock failed: " + std::to_wstring(GetLastError()));
            environment = nullptr;
        }

        const std::wstring executable = CurrentExecutable();
        if (executable.empty()) {
            Log(L"GetModuleFileName failed: " + std::to_wstring(GetLastError()));
            break;
        }

        std::wstring commandLine = L"\"" + executable + L"\" --worker";
        const DWORD flags = CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW;
        success = CreateProcessAsUserW(
            primaryToken,
            executable.c_str(),
            commandLine.data(),
            nullptr,
            nullptr,
            FALSE,
            flags,
            environment,
            std::filesystem::path(executable).parent_path().c_str(),
            &startupInfo,
            &processInfo) != FALSE;

        if (!success) {
            const DWORD firstError = GetLastError();
            Log(L"CreateProcessAsUser failed: " + std::to_wstring(firstError) + L". Trying CreateProcessWithTokenW.");
            success = CreateProcessWithTokenW(
                primaryToken,
                LOGON_WITH_PROFILE,
                executable.c_str(),
                commandLine.data(),
                flags,
                environment,
                std::filesystem::path(executable).parent_path().c_str(),
                &startupInfo,
                &processInfo) != FALSE;
            if (!success) Log(L"CreateProcessWithTokenW failed: " + std::to_wstring(GetLastError()));
        }

        if (success) {
            Log(L"Interactive worker started. Session=" + std::to_wstring(sessionId) + L", PID=" + std::to_wstring(processInfo.dwProcessId));
        }
    } while (false);

    if (processInfo.hThread != nullptr) CloseHandle(processInfo.hThread);
    if (processInfo.hProcess != nullptr) CloseHandle(processInfo.hProcess);
    if (environment != nullptr) DestroyEnvironmentBlock(environment);
    if (primaryToken != nullptr) CloseHandle(primaryToken);
    CloseHandle(userToken);
    return success;
}

std::string Heartbeat(std::chrono::steady_clock::time_point started) {
    DWORD sessionId = 0;
    ProcessIdToSessionId(GetCurrentProcessId(), &sessionId);
    const auto uptime = std::chrono::duration_cast<std::chrono::seconds>(std::chrono::steady_clock::now() - started).count();
    const int monitorCount = GetSystemMetrics(SM_CMONITORS);
    const int primaryWidth = GetSystemMetrics(SM_CXSCREEN);
    const int primaryHeight = GetSystemMetrics(SM_CYSCREEN);
    const int virtualWidth = GetSystemMetrics(SM_CXVIRTUALSCREEN);
    const int virtualHeight = GetSystemMetrics(SM_CYVIRTUALSCREEN);

    std::ostringstream json;
    json << "{\"type\":\"heartbeat\",\"version\":\"0.4.0\",\"pid\":" << GetCurrentProcessId()
         << ",\"sessionId\":" << sessionId
         << ",\"user\":\"" << JsonEscape(Utf8(CurrentUser()))
         << "\",\"desktop\":\"" << JsonEscape(Utf8(CurrentDesktop()))
         << "\",\"uptimeSeconds\":" << uptime
         << ",\"monitorCount\":" << monitorCount
         << ",\"primaryWidth\":" << primaryWidth
         << ",\"primaryHeight\":" << primaryHeight
         << ",\"virtualWidth\":" << virtualWidth
         << ",\"virtualHeight\":" << virtualHeight
         << "}\n";
    return json.str();
}

int RunServer() {
    const auto started = std::chrono::steady_clock::now();
    Log(L"WorkspaceHost worker started. Version " + std::wstring(kVersion) + L", User=" + CurrentUser());

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
        const bool workerMode = argc > 1 && std::wstring_view(argv[1]) == L"--worker";
        if (!workerMode && IsLocalSystem()) {
            const DWORD sessionId = FindInteractiveSession();
            if (sessionId == 0xFFFFFFFF) {
                Log(L"No active interactive user session found.");
                return 3;
            }
            return RelaunchInInteractiveSession(sessionId) ? 0 : 4;
        }
        return RunServer();
    } catch (const std::exception& ex) {
        Log(L"Unhandled exception: " + Wide(ex.what()));
        return 1;
    } catch (...) {
        Log(L"Unhandled unknown exception");
        return 1;
    }
}
