#include <windows.h>
#include <userenv.h>
#include <wtsapi32.h>
#include <atomic>
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
constexpr wchar_t kPipeBase[] = L"\\\\.\\pipe\\SirK.MeshCentral.Workspace";
constexpr wchar_t kVersion[] = L"0.7.0";
constexpr wchar_t kWindowClass[] = L"SirK.Workspace.TestWindow";
HDESK gWorkerDesktop = nullptr;
std::atomic<DWORD> gTestWindowThreadId{0};
std::atomic<HWND> gTestWindow{nullptr};

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
           << L" [PID=" << GetCurrentProcessId() << L"] " << message << L'\n';
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
    if (desktop != nullptr && GetUserObjectInformationW(desktop, UOI_NAME, value, sizeof(value), &needed)) return value;
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
            if (character < 0x20) escaped << "\\u" << std::hex << std::setw(4) << std::setfill('0') << static_cast<int>(character) << std::dec;
            else escaped << static_cast<char>(character);
        }
    }
    return escaped.str();
}

bool IsLocalSystem() {
    SID_IDENTIFIER_AUTHORITY ntAuthority = SECURITY_NT_AUTHORITY;
    PSID systemSid = nullptr;
    if (!AllocateAndInitializeSid(&ntAuthority, 1, SECURITY_LOCAL_SYSTEM_RID, 0, 0, 0, 0, 0, 0, 0, &systemSid)) return false;
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
        if (sessions[index].State == WTSActive && SessionHasUser(sessions[index].SessionId)) { selected = sessions[index].SessionId; break; }
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

std::wstring NormalizeSlot(std::wstring slot) {
    if (slot == L"user" || slot == L"admin1" || slot == L"admin2") return slot;
    return L"user";
}

std::wstring PipeName(const std::wstring& slot) { return std::wstring(kPipeBase) + L"." + NormalizeSlot(slot); }

std::wstring DesktopNameForSlot(const std::wstring& slot) {
    if (slot == L"admin1") return L"SirK-Admin-1";
    if (slot == L"admin2") return L"SirK-Admin-2";
    return L"default";
}

bool SetupWorkerDesktop(const std::wstring& slot) {
    if (slot == L"user") return true;
    const std::wstring desktopName = DesktopNameForSlot(slot);
    gWorkerDesktop = CreateDesktopW(
        desktopName.c_str(), nullptr, nullptr, 0,
        DESKTOP_CREATEWINDOW | DESKTOP_ENUMERATE | DESKTOP_HOOKCONTROL |
        DESKTOP_READOBJECTS | DESKTOP_WRITEOBJECTS | DESKTOP_SWITCHDESKTOP,
        nullptr);
    if (gWorkerDesktop == nullptr) {
        Log(L"CreateDesktop failed for " + desktopName + L": " + std::to_wstring(GetLastError()));
        return false;
    }
    if (!SetThreadDesktop(gWorkerDesktop)) {
        Log(L"SetThreadDesktop failed for " + desktopName + L": " + std::to_wstring(GetLastError()));
        CloseDesktop(gWorkerDesktop);
        gWorkerDesktop = nullptr;
        return false;
    }
    Log(L"Isolated desktop ready: winsta0\\" + desktopName);
    return true;
}

LRESULT CALLBACK TestWindowProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam) {
    if (message == WM_CLOSE) { DestroyWindow(window); return 0; }
    if (message == WM_DESTROY) { PostQuitMessage(0); return 0; }
    return DefWindowProcW(window, message, wParam, lParam);
}

void TestWindowThread(std::wstring slot) {
    const std::wstring desktopName = DesktopNameForSlot(slot);
    HDESK desktop = OpenDesktopW(desktopName.c_str(), 0, FALSE,
        DESKTOP_CREATEWINDOW | DESKTOP_READOBJECTS | DESKTOP_WRITEOBJECTS);
    if (desktop == nullptr || !SetThreadDesktop(desktop)) {
        Log(L"Test window desktop attach failed for " + desktopName + L": " + std::to_wstring(GetLastError()));
        if (desktop != nullptr) CloseDesktop(desktop);
        return;
    }

    WNDCLASSEXW windowClass{};
    windowClass.cbSize = sizeof(windowClass);
    windowClass.lpfnWndProc = TestWindowProc;
    windowClass.hInstance = GetModuleHandleW(nullptr);
    windowClass.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    windowClass.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
    windowClass.lpszClassName = kWindowClass;
    RegisterClassExW(&windowClass);

    const std::wstring title = L"SirK Workspace Test - " + desktopName;
    HWND window = CreateWindowExW(0, kWindowClass, title.c_str(), WS_OVERLAPPEDWINDOW,
        120, 120, 900, 600, nullptr, nullptr, windowClass.hInstance, nullptr);
    if (window == nullptr) {
        Log(L"CreateWindowEx failed on " + desktopName + L": " + std::to_wstring(GetLastError()));
        CloseDesktop(desktop);
        return;
    }

    gTestWindow.store(window);
    gTestWindowThreadId.store(GetCurrentThreadId());
    ShowWindow(window, SW_SHOW);
    UpdateWindow(window);
    Log(L"Hidden desktop test window created. Desktop=" + desktopName + L", HWND=" + std::to_wstring(reinterpret_cast<uintptr_t>(window)));

    MSG message{};
    while (GetMessageW(&message, nullptr, 0, 0) > 0) {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
    gTestWindow.store(nullptr);
    gTestWindowThreadId.store(0);
    CloseDesktop(desktop);
}

void StartTestWindow(const std::wstring& slot) {
    if (slot == L"user") return;
    std::thread(TestWindowThread, slot).detach();
    for (int i = 0; i < 50 && gTestWindow.load() == nullptr; ++i) std::this_thread::sleep_for(std::chrono::milliseconds(100));
}

bool RelaunchInInteractiveSession(DWORD sessionId, const std::wstring& slot) {
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
            Log(L"DuplicateTokenEx failed: " + std::to_wstring(GetLastError())); break;
        }
        if (!CreateEnvironmentBlock(&environment, primaryToken, FALSE)) environment = nullptr;
        const std::wstring executable = CurrentExecutable();
        if (executable.empty()) break;
        std::wstring commandLine = L"\"" + executable + L"\" --worker --slot " + NormalizeSlot(slot);
        const DWORD flags = CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW;
        success = CreateProcessAsUserW(primaryToken, executable.c_str(), commandLine.data(), nullptr, nullptr, FALSE, flags,
            environment, std::filesystem::path(executable).parent_path().c_str(), &startupInfo, &processInfo) != FALSE;
        if (!success) {
            Log(L"CreateProcessAsUser failed: " + std::to_wstring(GetLastError()) + L". Trying CreateProcessWithTokenW.");
            success = CreateProcessWithTokenW(primaryToken, LOGON_WITH_PROFILE, executable.c_str(), commandLine.data(), flags,
                environment, std::filesystem::path(executable).parent_path().c_str(), &startupInfo, &processInfo) != FALSE;
        }
        if (success) Log(L"Interactive worker started. Slot=" + slot + L", Session=" + std::to_wstring(sessionId) + L", PID=" + std::to_wstring(processInfo.dwProcessId));
    } while (false);
    if (processInfo.hThread != nullptr) CloseHandle(processInfo.hThread);
    if (processInfo.hProcess != nullptr) CloseHandle(processInfo.hProcess);
    if (environment != nullptr) DestroyEnvironmentBlock(environment);
    if (primaryToken != nullptr) CloseHandle(primaryToken);
    CloseHandle(userToken);
    return success;
}

std::string Heartbeat(std::chrono::steady_clock::time_point started, const std::wstring& slot) {
    DWORD sessionId = 0;
    ProcessIdToSessionId(GetCurrentProcessId(), &sessionId);
    const auto uptime = std::chrono::duration_cast<std::chrono::seconds>(std::chrono::steady_clock::now() - started).count();
    const int monitorCount = GetSystemMetrics(SM_CMONITORS);
    const int primaryWidth = GetSystemMetrics(SM_CXSCREEN);
    const int primaryHeight = GetSystemMetrics(SM_CYSCREEN);
    const int virtualWidth = GetSystemMetrics(SM_CXVIRTUALSCREEN);
    const int virtualHeight = GetSystemMetrics(SM_CYVIRTUALSCREEN);
    const bool isolated = slot != L"user";
    const HWND testWindow = gTestWindow.load();
    const bool testWindowReady = testWindow != nullptr && IsWindow(testWindow);
    const std::wstring testWindowTitle = testWindowReady ? (L"SirK Workspace Test - " + DesktopNameForSlot(slot)) : L"";
    std::ostringstream json;
    json << "{\"type\":\"heartbeat\",\"version\":\"0.7.0\",\"pid\":" << GetCurrentProcessId()
         << ",\"sessionId\":" << sessionId
         << ",\"slot\":\"" << JsonEscape(Utf8(slot))
         << "\",\"workspaceType\":\"" << (isolated ? "admin" : "user")
         << "\",\"isolatedDesktop\":" << (isolated ? "true" : "false")
         << ",\"user\":\"" << JsonEscape(Utf8(CurrentUser()))
         << "\",\"desktop\":\"" << JsonEscape(Utf8(CurrentDesktop()))
         << "\",\"uptimeSeconds\":" << uptime
         << ",\"monitorCount\":" << monitorCount
         << ",\"primaryWidth\":" << primaryWidth
         << ",\"primaryHeight\":" << primaryHeight
         << ",\"virtualWidth\":" << virtualWidth
         << ",\"virtualHeight\":" << virtualHeight
         << ",\"testWindowReady\":" << (testWindowReady ? "true" : "false")
         << ",\"testWindowThreadId\":" << gTestWindowThreadId.load()
         << ",\"testWindowTitle\":\"" << JsonEscape(Utf8(testWindowTitle)) << "\"}\n";
    return json.str();
}

int RunServer(const std::wstring& slot) {
    if (!SetupWorkerDesktop(slot)) return 5;
    StartTestWindow(slot);
    const auto started = std::chrono::steady_clock::now();
    const std::wstring pipeName = PipeName(slot);
    Log(L"WorkspaceHost worker started. Version " + std::wstring(kVersion) + L", Slot=" + slot + L", User=" + CurrentUser() + L", Desktop=" + CurrentDesktop());
    while (true) {
        HANDLE pipe = CreateNamedPipeW(pipeName.c_str(), PIPE_ACCESS_OUTBOUND,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT, 1, 64 * 1024, 64 * 1024, 0, nullptr);
        if (pipe == INVALID_HANDLE_VALUE) { Log(L"CreateNamedPipe failed: " + std::to_wstring(GetLastError())); return 2; }
        const BOOL connected = ConnectNamedPipe(pipe, nullptr) || GetLastError() == ERROR_PIPE_CONNECTED;
        if (!connected) { CloseHandle(pipe); std::this_thread::sleep_for(std::chrono::seconds(2)); continue; }
        while (true) {
            const std::string payload = Heartbeat(started, slot);
            DWORD written = 0;
            if (!WriteFile(pipe, payload.data(), static_cast<DWORD>(payload.size()), &written, nullptr)) break;
            FlushFileBuffers(pipe);
            std::this_thread::sleep_for(std::chrono::seconds(5));
        }
        DisconnectNamedPipe(pipe);
        CloseHandle(pipe);
    }
}
}

int wmain(int argc, wchar_t* argv[]) {
    if (argc > 1 && std::wstring_view(argv[1]) == L"--version") { std::wcout << kVersion << L'\n'; return 0; }
    try {
        bool workerMode = false;
        std::wstring slot = L"user";
        for (int index = 1; index < argc; ++index) {
            const std::wstring_view arg(argv[index]);
            if (arg == L"--worker") workerMode = true;
            else if (arg == L"--slot" && index + 1 < argc) slot = NormalizeSlot(argv[++index]);
        }
        if (!workerMode && IsLocalSystem()) {
            const DWORD sessionId = FindInteractiveSession();
            if (sessionId == 0xFFFFFFFF) { Log(L"No active interactive user session found."); return 3; }
            return RelaunchInInteractiveSession(sessionId, slot) ? 0 : 4;
        }
        return RunServer(slot);
    } catch (const std::exception& ex) {
        Log(L"Unhandled exception: " + Wide(ex.what())); return 1;
    } catch (...) {
        Log(L"Unhandled unknown exception"); return 1;
    }
}