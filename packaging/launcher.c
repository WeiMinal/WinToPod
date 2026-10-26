// SPDX-License-Identifier: GPL-3.0-or-later
#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif
#include <windows.h>
#include <shellapi.h>
#include <stdio.h>
#include <wchar.h>

static int fail(const wchar_t *message, DWORD error) {
    wchar_t text[2048];
    _snwprintf(text, 2048, L"%ls\n\nWindows error: %lu\n\nPlease extract the complete ZIP and keep the app folder.\nStartup diagnostics: %%LOCALAPPDATA%%\\WinToPod", message, (unsigned long)error);
    text[2047] = 0;
    if (!GetEnvironmentVariableW(L"WINTOPOD_NONINTERACTIVE", NULL, 0))
        MessageBoxW(NULL, text, L"WinToPod", MB_OK | MB_ICONERROR);
    return 1;
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR arguments, int show) {
    (void)instance; (void)previous; (void)show;
    wchar_t executable[32768], directory[32768], eventName[128];
    DWORD length = GetModuleFileNameW(NULL, directory, 32768);
    if (!length || length >= 32768) return fail(L"Cannot locate WinToPod.", GetLastError());
    wchar_t *slash = wcsrchr(directory, L'\\');
    if (!slash) return fail(L"Invalid application location.", ERROR_PATH_NOT_FOUND);
    *slash = 0;
    if (wcslen(directory) + 20 >= 32768) return fail(L"Application path is too long.", ERROR_FILENAME_EXCED_RANGE);
    _snwprintf(executable, 32768, L"%ls\\app\\WinToPod.exe", directory);
    if (GetFileAttributesW(executable) == INVALID_FILE_ATTRIBUTES)
        return fail(L"The app folder is missing or incomplete.", ERROR_FILE_NOT_FOUND);
    _snwprintf(eventName, 128, L"Local\\WinToPod.Startup.%lu.%llu", (unsigned long)GetCurrentProcessId(), (unsigned long long)GetTickCount64());
    HANDLE ready = CreateEventW(NULL, TRUE, FALSE, eventName);
    if (!ready) return fail(L"Cannot create the startup signal.", GetLastError());
    size_t capacity = wcslen(executable) + wcslen(arguments) + wcslen(eventName) + 32;
    if (capacity > 32767) { CloseHandle(ready); return fail(L"Command line is too long.", ERROR_BAD_ARGUMENTS); }
    wchar_t *command = HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, capacity * sizeof(wchar_t));
    if (!command) { CloseHandle(ready); return fail(L"Insufficient memory.", ERROR_NOT_ENOUGH_MEMORY); }
    // Original argument text is forwarded unchanged, including quoted Unicode paths.
    _snwprintf(command, capacity, L"\"%ls\" %ls --ready-event \"%ls\"", executable, arguments, eventName);
    STARTUPINFOW startup = {0}; startup.cb = sizeof(startup);
    PROCESS_INFORMATION child = {0};
    BOOL started = CreateProcessW(executable, command, NULL, NULL, FALSE, 0, NULL, directory, &startup, &child);
    DWORD error = started ? ERROR_SUCCESS : GetLastError();
    HeapFree(GetProcessHeap(), 0, command);
    if (!started) { CloseHandle(ready); return fail(L"WinToPod could not start.", error); }
    CloseHandle(child.hThread);
    int argc = 0; LPWSTR *argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    BOOL selfCheck = FALSE;
    if (argv) { for (int i = 1; i < argc; i++) if (!wcscmp(argv[i], L"--self-check")) selfCheck = TRUE; LocalFree(argv); }
    HANDLE handles[] = {ready, child.hProcess};
    DWORD waited = selfCheck ? WaitForSingleObject(child.hProcess, 90000) : WaitForMultipleObjects(2, handles, FALSE, 60000);
    DWORD exitCode = 0; int result = 0;
    if (waited == WAIT_TIMEOUT) result = fail(L"WinToPod has not completed startup. Check startup diagnostics before opening it again.", WAIT_TIMEOUT);
    else if (waited == WAIT_FAILED) result = fail(L"Cannot observe application startup.", GetLastError());
    else if (selfCheck || waited == WAIT_OBJECT_0 + 1) {
        if (!GetExitCodeProcess(child.hProcess, &exitCode)) result = fail(L"Cannot read application status.", GetLastError());
        else if (exitCode) result = fail(L"WinToPod exited with a startup error.", exitCode);
    }
    CloseHandle(child.hProcess); CloseHandle(ready);
    return result;
}
