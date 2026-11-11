#include <windows.h>
#include <winioctl.h>

#include <array>
#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>

int wmain(int argc, wchar_t** argv) {
    if (argc != 2) {
        std::fwprintf(stderr, L"Usage: bf3_font_smoke.exe PAYLOAD_DIRECTORY\n");
        return 2;
    }
    wchar_t path[MAX_PATH]{};
    const DWORD length = GetModuleFileNameW(nullptr, path, MAX_PATH);
    if (length == 0 || length >= MAX_PATH) return 3;
    std::wstring root(path, length);
    root.resize(root.find_last_of(L"\\/"));
    const std::wstring fonts = root + L"\\fonts";
    const std::wstring data = root + L"\\Data";
    CreateDirectoryW(fonts.c_str(), nullptr);
    CreateDirectoryW(data.c_str(), nullptr);
    for (const wchar_t* name : {
             L"fontcollection_en_fontlib.cas-payload",
             L"eulafontcollection_en_fontlib.cas-payload",
             L"storefontcollection_en_fontlib.cas-payload" }) {
        const std::wstring source = std::wstring(argv[1]) + L"\\" + name;
        const std::wstring target = fonts + L"\\" + name;
        if (!CopyFileW(source.c_str(), target.c_str(), FALSE)) return 4;
    }
    const std::wstring probeMarker = fonts + L"\\probe_text.flag";
    HANDLE marker = CreateFileW(probeMarker.c_str(), GENERIC_WRITE, FILE_SHARE_READ,
        nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (marker == INVALID_HANDLE_VALUE) return 16;
    CloseHandle(marker);
    const std::wstring archive = data + L"\\cas_08.cas";
    HANDLE file = CreateFileW(archive.c_str(), GENERIC_READ | GENERIC_WRITE,
        FILE_SHARE_READ, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return 5;
    DWORD ignored = 0;
    if (!DeviceIoControl(file, FSCTL_SET_SPARSE, nullptr, 0, nullptr, 0, &ignored, nullptr)) return 6;
    LARGE_INTEGER offset{};
    offset.QuadPart = 949660069LL;
    if (!SetFilePointerEx(file, offset, nullptr, FILE_BEGIN)) return 7;
    std::vector<std::uint8_t> zeros(62403, 0);
    DWORD written = 0;
    if (!WriteFile(file, zeros.data(), static_cast<DWORD>(zeros.size()), &written, nullptr) || written != zeros.size()) return 8;
    CloseHandle(file);

    const std::wstring dllPath = root + L"\\dinput8.dll";
    HMODULE proxy = LoadLibraryW(dllPath.c_str());
    if (!proxy) return 9;
    for (const char* exportName : {"DirectInput8Create", "DllCanUnloadNow", "DllGetClassObject",
             "DllRegisterServer", "DllUnregisterServer", "GetdfDIJoystick"})
        if (!GetProcAddress(proxy, exportName)) return 10;
    using CanUnloadFn = HRESULT(WINAPI*)();
    auto canUnload = reinterpret_cast<CanUnloadFn>(GetProcAddress(proxy, "DllCanUnloadNow"));
    canUnload(); // Initializes the hook and calls the system dinput8.dll.
    const char sample[] = {'C', 'H', 'I', static_cast<char>(0xF4), 'N', ' ',
        'D', static_cast<char>(0xF1), 'C', 'H', 0};
    wchar_t converted[32]{};
    if (!MultiByteToWideChar(CP_ACP, 0, sample, -1, converted, 32)) return 17;

    file = CreateFileW(archive.c_str(), GENERIC_READ, FILE_SHARE_READ,
        nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return 11;
    if (!SetFilePointerEx(file, offset, nullptr, FILE_BEGIN)) return 12;
    std::vector<std::uint8_t> actual(62403);
    DWORD read = 0;
    const BOOL success = ReadFile(file, actual.data(), static_cast<DWORD>(actual.size()), &read, nullptr);
    const std::wstring payloadPath = fonts + L"\\fontcollection_en_fontlib.cas-payload";
    HANDLE payload = CreateFileW(payloadPath.c_str(), GENERIC_READ, FILE_SHARE_READ,
        nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (payload == INVALID_HANDLE_VALUE) return 13;
    std::vector<std::uint8_t> expected(62403);
    DWORD expectedRead = 0;
    const BOOL expectedSuccess = ReadFile(payload, expected.data(), static_cast<DWORD>(expected.size()), &expectedRead, nullptr);
    CloseHandle(payload);
    const bool identical = success && read == actual.size() && expectedSuccess &&
        expectedRead == expected.size() && actual == expected;
    // A cached archive handle must still check the exact offset on every read.
    LARGE_INTEGER wrongOffset{};
    wrongOffset.QuadPart = offset.QuadPart - 1;
    if (!SetFilePointerEx(file, wrongOffset, nullptr, FILE_BEGIN)) return 18;
    std::vector<std::uint8_t> wrongActual(62403, 0xAA);
    DWORD wrongRead = 0;
    const BOOL wrongSuccess = ReadFile(file, wrongActual.data(),
        static_cast<DWORD>(wrongActual.size()), &wrongRead, nullptr);
    const bool wrongOffsetUntouched = wrongSuccess && wrongRead == wrongActual.size() &&
        wrongActual != expected;
    CloseHandle(file);
    file = CreateFileW(archive.c_str(), GENERIC_READ, FILE_SHARE_READ,
        nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OVERLAPPED, nullptr);
    if (file == INVALID_HANDLE_VALUE) return 15;
    OVERLAPPED overlapped{};
    overlapped.Offset = static_cast<DWORD>(offset.QuadPart);
    overlapped.OffsetHigh = static_cast<DWORD>(offset.QuadPart >> 32);
    overlapped.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    std::vector<std::uint8_t> asyncActual(62403);
    DWORD asyncRead = 0;
    const BOOL immediate = ReadFile(file, asyncActual.data(),
        static_cast<DWORD>(asyncActual.size()), &asyncRead, &overlapped);
    bool asyncOkay = immediate != FALSE;
    if (!immediate && GetLastError() == ERROR_IO_PENDING)
        asyncOkay = GetOverlappedResult(file, &overlapped, &asyncRead, TRUE) != FALSE;
    CloseHandle(overlapped.hEvent);
    CloseHandle(file);
    DeleteFileW(archive.c_str());
    DeleteFileW(probeMarker.c_str());
    const bool asyncIdentical = asyncOkay && asyncRead == asyncActual.size() && asyncActual == expected;
    std::printf("x86 proxy loaded; synchronous=%s; overlapped=%s; wrong offset=%s (immediate=%s)\n",
        identical ? "PASS" : "FAIL", asyncIdentical ? "PASS" : "FAIL",
        wrongOffsetUntouched ? "PASS" : "FAIL", immediate ? "yes" : "no");
    return identical && asyncIdentical && wrongOffsetUntouched ? 0 : 14;
}
