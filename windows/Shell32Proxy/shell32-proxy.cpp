#include <windows.h>
#include <shellapi.h>
#include <shlwapi.h>

extern "C" void* memcpy(void* destination, const void* source, size_t count)
{
    auto* output = static_cast<volatile unsigned char*>(destination);
    const auto* input = static_cast<const volatile unsigned char*>(source);
    for (size_t index = 0; index < count; ++index)
    {
        output[index] = input[index];
    }
    return destination;
}

extern "C" void* memset(void* destination, int value, size_t count)
{
    auto* output = static_cast<volatile unsigned char*>(destination);
    for (size_t index = 0; index < count; ++index)
    {
        output[index] = static_cast<unsigned char>(value);
    }
    return destination;
}

namespace
{
using FindExecutableWProc = HINSTANCE (WINAPI*)(LPCWSTR, LPCWSTR, LPWSTR);
using ShellExecuteExWProc = BOOL (WINAPI*)(SHELLEXECUTEINFOW*);

HMODULE GetRealShell32()
{
    HMODULE module = ::GetModuleHandleW(L"shell32real.dll");
    return module != nullptr ? module : ::LoadLibraryW(L"shell32real.dll");
}

FindExecutableWProc GetRealFindExecutableW()
{
    const HMODULE module = GetRealShell32();
    return module == nullptr
        ? nullptr
        : reinterpret_cast<FindExecutableWProc>(
            ::GetProcAddress(module, "FindExecutableW"));
}

ShellExecuteExWProc GetRealShellExecuteExW()
{
    const HMODULE module = GetRealShell32();
    return module == nullptr
        ? nullptr
        : reinterpret_cast<ShellExecuteExWProc>(
            ::GetProcAddress(module, "ShellExecuteExW"));
}

bool EqualsIgnoreCase(const wchar_t* left, const wchar_t* right)
{
    return ::CompareStringOrdinal(left, -1, right, -1, TRUE) == CSTR_EQUAL;
}

const wchar_t* FindExtension(const wchar_t* path)
{
    if (path == nullptr)
    {
        return nullptr;
    }

    const wchar_t* extension = nullptr;
    for (const wchar_t* cursor = path; *cursor != L'\0'; ++cursor)
    {
        if (*cursor == L'.')
        {
            extension = cursor;
        }
        else if (*cursor == L'\\' || *cursor == L'/')
        {
            extension = nullptr;
        }
    }
    return extension;
}

bool IsExecutable(const wchar_t* path)
{
    const wchar_t* extension = FindExtension(path);
    return extension != nullptr && EqualsIgnoreCase(extension, L".exe");
}

bool IsOpenVerb(const wchar_t* verb)
{
    return verb == nullptr || *verb == L'\0' || EqualsIgnoreCase(verb, L"open");
}
}

extern "C" HINSTANCE WINAPI HookFindExecutableW(
    LPCWSTR file,
    LPCWSTR directory,
    LPWSTR resultPath)
{
    const auto realFindExecutableW = GetRealFindExecutableW();
    const HINSTANCE shellResult = realFindExecutableW == nullptr
        ? reinterpret_cast<HINSTANCE>(SE_ERR_DLLNOTFOUND)
        : realFindExecutableW(file, directory, resultPath);
    if (reinterpret_cast<INT_PTR>(shellResult) > 32 || resultPath == nullptr)
    {
        return shellResult;
    }

    const wchar_t* extension = FindExtension(file);
    if (extension == nullptr)
    {
        return shellResult;
    }

    DWORD resultLength = MAX_PATH;
    const HRESULT associationResult = ::AssocQueryStringW(
        ASSOCF_NONE,
        ASSOCSTR_EXECUTABLE,
        extension,
        nullptr,
        resultPath,
        &resultLength);
    return SUCCEEDED(associationResult) && resultPath[0] != L'\0'
        ? reinterpret_cast<HINSTANCE>(33)
        : shellResult;
}

extern "C" BOOL WINAPI HookShellExecuteExW(SHELLEXECUTEINFOW* executeInfo)
{
    if (executeInfo == nullptr
        || !IsOpenVerb(executeInfo->lpVerb)
        || !IsExecutable(executeInfo->lpFile))
    {
        const auto realShellExecuteExW = GetRealShellExecuteExW();
        if (realShellExecuteExW == nullptr)
        {
            ::SetLastError(ERROR_MOD_NOT_FOUND);
            return FALSE;
        }
        return realShellExecuteExW(executeInfo);
    }

    const int executableLength = ::lstrlenW(executeInfo->lpFile);
    const int parameterLength =
        executeInfo->lpParameters == nullptr ? 0 : ::lstrlenW(executeInfo->lpParameters);
    const SIZE_T characterCount =
        static_cast<SIZE_T>(executableLength) + static_cast<SIZE_T>(parameterLength) + 5;
    auto* commandLine = static_cast<wchar_t*>(
        ::HeapAlloc(::GetProcessHeap(), HEAP_ZERO_MEMORY, characterCount * sizeof(wchar_t)));
    if (commandLine == nullptr)
    {
        ::SetLastError(ERROR_NOT_ENOUGH_MEMORY);
        return FALSE;
    }

    wchar_t* cursor = commandLine;
    *cursor++ = L'"';
    ::CopyMemory(cursor, executeInfo->lpFile, executableLength * sizeof(wchar_t));
    cursor += executableLength;
    *cursor++ = L'"';
    if (parameterLength != 0)
    {
        *cursor++ = L' ';
        ::CopyMemory(cursor, executeInfo->lpParameters, parameterLength * sizeof(wchar_t));
        cursor += parameterLength;
    }
    *cursor = L'\0';

    STARTUPINFOW startupInfo{};
    startupInfo.cb = sizeof(startupInfo);
    startupInfo.dwFlags = STARTF_USESHOWWINDOW;
    startupInfo.wShowWindow = static_cast<WORD>(executeInfo->nShow);

    PROCESS_INFORMATION processInfo{};
    const BOOL created = ::CreateProcessW(
        executeInfo->lpFile,
        commandLine,
        nullptr,
        nullptr,
        FALSE,
        CREATE_UNICODE_ENVIRONMENT,
        nullptr,
        executeInfo->lpDirectory,
        &startupInfo,
        &processInfo);
    const DWORD createProcessError = ::GetLastError();
    ::HeapFree(::GetProcessHeap(), 0, commandLine);

    if (!created)
    {
        ::SetLastError(createProcessError);
        return FALSE;
    }

    ::CloseHandle(processInfo.hThread);
    executeInfo->hInstApp = reinterpret_cast<HINSTANCE>(33);

    if ((executeInfo->fMask & SEE_MASK_NOCLOSEPROCESS) != 0)
    {
        executeInfo->hProcess = processInfo.hProcess;
    }
    else
    {
        ::CloseHandle(processInfo.hProcess);
        executeInfo->hProcess = nullptr;
    }

    return TRUE;
}
