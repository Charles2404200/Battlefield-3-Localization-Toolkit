#include <windows.h>
#include <unknwn.h>
#include <MinHook.h>

#include <algorithm>
#include <array>
#include <cstdint>
#include <cwctype>
#include <cstring>
#include <string>
#include <unordered_map>
#include <vector>

namespace {

struct FontPayload {
    const wchar_t* archive;
    const wchar_t* file;
    std::uint64_t offset;
    DWORD packedSize;
    std::uint64_t decodedSize;
    std::vector<std::uint8_t> bytes;
};

std::array<FontPayload, 3> g_fonts{{
    {L"cas_04.cas", L"eulafontcollection_en_fontlib.cas-payload", 1044791866ULL, 28727, 56359, {}},
    {L"cas_07.cas", L"storefontcollection_en_fontlib.cas-payload", 662123013ULL, 28845, 56363, {}},
    {L"cas_08.cas", L"fontcollection_en_fontlib.cas-payload", 949660069ULL, 62403, 115603, {}}
}};

using ReadFileFn = BOOL(WINAPI*)(HANDLE, LPVOID, DWORD, LPDWORD, LPOVERLAPPED);
using GetOverlappedResultFn = BOOL(WINAPI*)(HANDLE, LPOVERLAPPED, LPDWORD, BOOL);
using MultiByteToWideCharFn = int(WINAPI*)(UINT, DWORD, LPCCH, int, LPWSTR, int);
ReadFileFn g_originalReadFile = nullptr;
GetOverlappedResultFn g_originalGetOverlappedResult = nullptr;
MultiByteToWideCharFn g_originalMultiByteToWideChar = nullptr;
HMODULE g_realDinput8 = nullptr;
INIT_ONCE g_realOnce = INIT_ONCE_STATIC_INIT;
INIT_ONCE g_hookOnce = INIT_ONCE_STATIC_INIT;
std::wstring g_gameRoot;
LONG g_textProbeCount = 0;
struct PendingRead {
    HANDLE handle;
    LPVOID buffer;
    FontPayload* font;
};
SRWLOCK g_pendingLock = SRWLOCK_INIT;
std::unordered_map<LPOVERLAPPED, PendingRead> g_pending;

struct ArchiveIdentity {
    DWORD volume;
    DWORD indexHigh;
    DWORD indexLow;
    DWORD sizeHigh;
    DWORD sizeLow;
};
struct ArchiveCacheEntry {
    ArchiveIdentity identity;
    int fontIndex;
};
SRWLOCK g_archiveLock = SRWLOCK_INIT;
std::unordered_map<HANDLE, ArchiveCacheEntry> g_archiveCache;
using CloseHandleFn = BOOL(WINAPI*)(HANDLE);
CloseHandleFn g_originalCloseHandle = nullptr;

std::wstring Lower(std::wstring text) {
    for (auto& ch : text) ch = static_cast<wchar_t>(std::towlower(ch));
    return text;
}

std::wstring GameRoot() {
    wchar_t path[MAX_PATH]{};
    const DWORD length = GetModuleFileNameW(nullptr, path, MAX_PATH);
    if (length == 0 || length >= MAX_PATH) return {};
    std::wstring result(path, length);
    const auto separator = result.find_last_of(L"\\/");
    return separator == std::wstring::npos ? std::wstring{} : result.substr(0, separator);
}

void Log(const wchar_t* message) {
    (void)message; // Logging disabled; keep hook behavior unchanged.
}

std::uint32_t BigEndian32(const std::uint8_t* p) {
    return (static_cast<std::uint32_t>(p[0]) << 24) |
        (static_cast<std::uint32_t>(p[1]) << 16) |
        (static_cast<std::uint32_t>(p[2]) << 8) | p[3];
}

bool ValidCasPayload(const FontPayload& font) {
    if (font.bytes.size() != font.packedSize) return false;
    std::size_t at = 0;
    std::uint64_t decoded = 0;
    while (at < font.bytes.size()) {
        if (font.bytes.size() - at < 10) return false;
        const auto blockSize = BigEndian32(font.bytes.data() + at);
        const auto compressedSize = BigEndian32(font.bytes.data() + at + 4);
        at += 8;
        if (blockSize == 0 || blockSize > 65536 || compressedSize < 2 ||
            compressedSize > font.bytes.size() - at || font.bytes[at] != 0x78) return false;
        decoded += blockSize;
        at += compressedSize;
    }
    return at == font.bytes.size() && decoded == font.decodedSize;
}

bool LoadPayload(FontPayload& font) {
    const std::wstring path = g_gameRoot + L"\\fonts\\" + font.file;
    HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ,
        nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    LARGE_INTEGER size{};
    bool okay = GetFileSizeEx(file, &size) && size.QuadPart == font.packedSize;
    if (okay) {
        font.bytes.resize(font.packedSize);
        DWORD read = 0;
        okay = ReadFile(file, font.bytes.data(), font.packedSize, &read, nullptr) && read == font.packedSize;
    }
    CloseHandle(file);
    if (!okay || !ValidCasPayload(font)) {
        font.bytes.clear();
        return false;
    }
    return true;
}

int FontIndexForRead(DWORD count) {
    for (int i = 0; i < static_cast<int>(g_fonts.size()); ++i)
        if (g_fonts[i].packedSize == count && !g_fonts[i].bytes.empty()) return i;
    return -1;
}

// Validate the engine's buffer pointer on every read. Cached pointers are never
// dereferenced after their read completes; the game may recycle those buffers.
bool WritableRange(void* buffer, std::size_t size) {
    if (!buffer || size == 0) return false;
    const auto start = reinterpret_cast<std::uintptr_t>(buffer);
    if (start > UINTPTR_MAX - size) return false;
    const auto end = start + size;
    for (auto cursor = start; cursor < end;) {
        MEMORY_BASIC_INFORMATION page{};
        if (!VirtualQuery(reinterpret_cast<void*>(cursor), &page, sizeof(page)) ||
            page.State != MEM_COMMIT || (page.Protect & (PAGE_GUARD | PAGE_NOACCESS))) return false;
        const DWORD protect = page.Protect & 0xff;
        if (protect != PAGE_READWRITE && protect != PAGE_WRITECOPY &&
            protect != PAGE_EXECUTE_READWRITE && protect != PAGE_EXECUTE_WRITECOPY) return false;
        const auto next = reinterpret_cast<std::uintptr_t>(page.BaseAddress) + page.RegionSize;
        if (next <= cursor) return false;
        cursor = next;
    }
    return true;
}

bool SafeReplace(void* buffer, const std::uint8_t* source, std::size_t size) {
    if (!WritableRange(buffer, size)) return false;
    __try {
        std::memcpy(buffer, source, size);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool SafeReadOverlapped(const OVERLAPPED* source, OVERLAPPED* target) {
    __try {
        *target = *source;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

ArchiveIdentity Identity(const BY_HANDLE_FILE_INFORMATION& info) {
    return {info.dwVolumeSerialNumber, info.nFileIndexHigh, info.nFileIndexLow,
        info.nFileSizeHigh, info.nFileSizeLow};
}

bool SameIdentity(const ArchiveIdentity& a, const ArchiveIdentity& b) {
    return std::memcmp(&a, &b, sizeof(a)) == 0;
}

bool IsExpectedArchive(HANDLE handle, int fontIndex) {
    BY_HANDLE_FILE_INFORMATION info{};
    if (!GetFileInformationByHandle(handle, &info)) return false;
    const ArchiveIdentity identity = Identity(info);
    AcquireSRWLockShared(&g_archiveLock);
    const auto it = g_archiveCache.find(handle);
    const bool cached = it != g_archiveCache.end() && SameIdentity(it->second.identity, identity);
    const bool matches = cached && it->second.fontIndex == fontIndex;
    ReleaseSRWLockShared(&g_archiveLock);
    if (cached) return matches;

    wchar_t path[1024]{};
    const DWORD length = GetFinalPathNameByHandleW(handle, path, 1024, FILE_NAME_NORMALIZED);
    if (length == 0 || length >= 1024) return false;
    std::wstring actual(path, length);
    if (actual.compare(0, 4, L"\\\\?\\") == 0) actual.erase(0, 4);
    for (auto& ch : actual) if (ch == L'/') ch = L'\\';
    int resolved = -1;
    actual = Lower(actual);
    for (int i = 0; i < static_cast<int>(g_fonts.size()); ++i) {
        const std::wstring expected = Lower(g_gameRoot + L"\\Data\\" + g_fonts[i].archive);
        if (actual == expected) { resolved = i; break; }
    }
    AcquireSRWLockExclusive(&g_archiveLock);
    g_archiveCache[handle] = {identity, resolved};
    ReleaseSRWLockExclusive(&g_archiveLock);
    return resolved == fontIndex;
}

BOOL WINAPI HookedCloseHandle(HANDLE handle) {
    AcquireSRWLockExclusive(&g_archiveLock);
    g_archiveCache.erase(handle);
    ReleaseSRWLockExclusive(&g_archiveLock);
    AcquireSRWLockExclusive(&g_pendingLock);
    for (auto it = g_pending.begin(); it != g_pending.end();) {
        if (it->second.handle == handle) it = g_pending.erase(it);
        else ++it;
    }
    ReleaseSRWLockExclusive(&g_pendingLock);
    return g_originalCloseHandle(handle);
}

BOOL WINAPI HookedReadFile(HANDLE handle, LPVOID buffer, DWORD count,
    LPDWORD bytesRead, LPOVERLAPPED overlapped) {
    // A reused OVERLAPPED address must not retain a pointer to an old buffer.
    if (overlapped) {
        AcquireSRWLockExclusive(&g_pendingLock);
        g_pending.erase(overlapped);
        ReleaseSRWLockExclusive(&g_pendingLock);
    }
    const int index = FontIndexForRead(count);
    FontPayload* font = nullptr;
    if (index >= 0 && buffer) {
        std::uint64_t offset = UINT64_MAX;
        if (overlapped) {
            OVERLAPPED snapshot{};
            if (SafeReadOverlapped(overlapped, &snapshot))
                offset = (static_cast<std::uint64_t>(snapshot.OffsetHigh) << 32) | snapshot.Offset;
        } else {
            LARGE_INTEGER zero{}, current{};
            if (SetFilePointerEx(handle, zero, &current, FILE_CURRENT))
                offset = static_cast<std::uint64_t>(current.QuadPart);
        }
        if (offset == g_fonts[index].offset && IsExpectedArchive(handle, index))
            font = &g_fonts[index];
    }

    const BOOL success = g_originalReadFile(handle, buffer, count, bytesRead, overlapped);
    const DWORD originalError = GetLastError();
    if (success && font && (!bytesRead || *bytesRead == count) &&
        SafeReplace(buffer, font->bytes.data(), font->bytes.size())) {
        wchar_t message[160]{};
        swprintf_s(message, L"FONT REPLACE %s offset=%llu bytes=%lu",
            font->archive, font->offset, font->packedSize);
        Log(message);
    } else if (!success && originalError == ERROR_IO_PENDING && font && overlapped) {
        AcquireSRWLockExclusive(&g_pendingLock);
        g_pending[overlapped] = {handle, buffer, font};
        ReleaseSRWLockExclusive(&g_pendingLock);
    }
    SetLastError(originalError);
    return success;
}

BOOL WINAPI HookedGetOverlappedResult(HANDLE handle, LPOVERLAPPED overlapped,
    LPDWORD transferred, BOOL wait) {
    const BOOL success = g_originalGetOverlappedResult(handle, overlapped, transferred, wait);
    const DWORD originalError = GetLastError();
    PendingRead pending{};
    bool found = false;
    AcquireSRWLockExclusive(&g_pendingLock);
    const auto it = g_pending.find(overlapped);
    if (it != g_pending.end() && it->second.handle == handle) {
        if (success) {
            pending = it->second;
            found = true;
        }
        if (success || originalError != ERROR_IO_INCOMPLETE) g_pending.erase(it);
    }
    ReleaseSRWLockExclusive(&g_pendingLock);
    if (found && pending.buffer && pending.font &&
        transferred && *transferred == pending.font->packedSize &&
        SafeReplace(pending.buffer, pending.font->bytes.data(), pending.font->bytes.size())) {
        wchar_t message[160]{};
        swprintf_s(message, L"FONT REPLACE async %s offset=%llu bytes=%lu",
            pending.font->archive, pending.font->offset, pending.font->packedSize);
        Log(message);
    }
    SetLastError(originalError);
    return success;
}

int WINAPI HookedMultiByteToWideChar(UINT codePage, DWORD flags, LPCCH source,
    int sourceLength, LPWSTR target, int targetLength) {
    const int converted = g_originalMultiByteToWideChar(
        codePage, flags, source, sourceLength, target, targetLength);
    const DWORD originalError = GetLastError();
    if (source && sourceLength != 0 && g_textProbeCount < 60) {
        const int limit = sourceLength < 0 ? 96 : std::min(sourceLength, 96);
        int length = 0;
        bool interesting = false;
        while (length < limit && (sourceLength >= 0 || source[length] != 0)) {
            const auto ch = static_cast<unsigned char>(source[length]);
            if (ch == 0xF4 || ch == 0xF1 || ch == 0xF2 || ch == 0xF3) interesting = true;
            ++length;
        }
        if (interesting && InterlockedIncrement(&g_textProbeCount) <= 60) {
            wchar_t header[120]{};
            swprintf_s(header, L"TEXT PROBE cp=%u flags=%lu in=%d out=%d bytes=",
                codePage, flags, sourceLength, converted);
            std::wstring message(header);
            for (int i = 0; i < length && i < 48; ++i) {
                wchar_t item[5]{};
                swprintf_s(item, L"%02X", static_cast<unsigned char>(source[i]));
                message += item;
            }
            if (target && converted > 0) {
                message += L" chars=";
                for (int i = 0; i < converted && i < 24; ++i) {
                    wchar_t item[6]{};
                    swprintf_s(item, L"%04X", static_cast<unsigned>(target[i]));
                    message += item;
                }
            }
            Log(message.c_str());
        }
    }
    SetLastError(originalError);
    return converted;
}

BOOL CALLBACK LoadRealDinput8(PINIT_ONCE, PVOID, PVOID*) {
    wchar_t systemDirectory[MAX_PATH]{};
    const UINT length = GetSystemDirectoryW(systemDirectory, MAX_PATH);
    if (length == 0 || length >= MAX_PATH) return TRUE;
    const std::wstring path = std::wstring(systemDirectory, length) + L"\\dinput8.dll";
    g_realDinput8 = LoadLibraryW(path.c_str());
    return TRUE;
}

FARPROC RealExport(const char* name) {
    InitOnceExecuteOnce(&g_realOnce, LoadRealDinput8, nullptr, nullptr);
    return g_realDinput8 ? GetProcAddress(g_realDinput8, name) : nullptr;
}

BOOL CALLBACK InstallFontHook(PINIT_ONCE, PVOID, PVOID*) {
    g_gameRoot = GameRoot();
    if (g_gameRoot.empty()) return TRUE;
    int loaded = 0;
    for (auto& font : g_fonts) {
        if (LoadPayload(font)) ++loaded;
        else {
            std::wstring message = L"SKIP missing or invalid payload: ";
            message += font.file;
            Log(message.c_str());
        }
    }
    if (loaded == 0) return TRUE;
    if (MH_Initialize() != MH_OK) {
        Log(L"ERROR MH_Initialize");
        return TRUE;
    }
    HMODULE kernel = GetModuleHandleW(L"kernel32.dll");
    void* readFile = kernel ? reinterpret_cast<void*>(GetProcAddress(kernel, "ReadFile")) : nullptr;
    void* overlappedResult = kernel ? reinterpret_cast<void*>(GetProcAddress(kernel, "GetOverlappedResult")) : nullptr;
    void* closeHandle = kernel ? reinterpret_cast<void*>(GetProcAddress(kernel, "CloseHandle")) : nullptr;
    const std::wstring probeMarker = g_gameRoot + L"\\fonts\\probe_text.flag";
    const bool probeText = GetFileAttributesW(probeMarker.c_str()) != INVALID_FILE_ATTRIBUTES;
    void* converter = probeText && kernel
        ? reinterpret_cast<void*>(GetProcAddress(kernel, "MultiByteToWideChar")) : nullptr;
    if (!readFile || !overlappedResult || !closeHandle ||
        MH_CreateHook(readFile, HookedReadFile,
            reinterpret_cast<void**>(&g_originalReadFile)) != MH_OK ||
        MH_CreateHook(overlappedResult, HookedGetOverlappedResult,
            reinterpret_cast<void**>(&g_originalGetOverlappedResult)) != MH_OK ||
        MH_CreateHook(closeHandle, HookedCloseHandle,
            reinterpret_cast<void**>(&g_originalCloseHandle)) != MH_OK ||
        (probeText && (!converter || MH_CreateHook(converter, HookedMultiByteToWideChar,
            reinterpret_cast<void**>(&g_originalMultiByteToWideChar)) != MH_OK)) ||
        MH_QueueEnableHook(readFile) != MH_OK ||
        MH_QueueEnableHook(overlappedResult) != MH_OK ||
        MH_QueueEnableHook(closeHandle) != MH_OK ||
        (probeText && MH_QueueEnableHook(converter) != MH_OK) ||
        MH_ApplyQueued() != MH_OK) {
        Log(L"ERROR install ReadFile/GetOverlappedResult/CloseHandle hook");
        return TRUE;
    }
    Log(probeText ? L"HOOK ACTIVE x86; validated buffer and handle cache; text probe enabled"
                  : L"HOOK ACTIVE x86; validated buffer and handle cache");
    return TRUE;
}

void EnsureFontHook() {
    InitOnceExecuteOnce(&g_hookOnce, InstallFontHook, nullptr, nullptr);
}

} // namespace

extern "C" HRESULT WINAPI DirectInput8Create(HINSTANCE instance, DWORD version,
    REFIID iid, LPVOID* output, IUnknown* outer) {
    EnsureFontHook();
    using Fn = HRESULT(WINAPI*)(HINSTANCE, DWORD, REFIID, LPVOID*, IUnknown*);
    auto real = reinterpret_cast<Fn>(RealExport("DirectInput8Create"));
    return real ? real(instance, version, iid, output, outer) : E_FAIL;
}

extern "C" HRESULT WINAPI DllCanUnloadNow() {
    EnsureFontHook();
    using Fn = HRESULT(WINAPI*)();
    auto real = reinterpret_cast<Fn>(RealExport("DllCanUnloadNow"));
    return real ? real() : S_FALSE;
}

extern "C" HRESULT WINAPI DllGetClassObject(REFCLSID clsid, REFIID iid, LPVOID* output) {
    EnsureFontHook();
    using Fn = HRESULT(WINAPI*)(REFCLSID, REFIID, LPVOID*);
    auto real = reinterpret_cast<Fn>(RealExport("DllGetClassObject"));
    return real ? real(clsid, iid, output) : CLASS_E_CLASSNOTAVAILABLE;
}

extern "C" HRESULT WINAPI DllRegisterServer() {
    using Fn = HRESULT(WINAPI*)();
    auto real = reinterpret_cast<Fn>(RealExport("DllRegisterServer"));
    return real ? real() : E_FAIL;
}

extern "C" HRESULT WINAPI DllUnregisterServer() {
    using Fn = HRESULT(WINAPI*)();
    auto real = reinterpret_cast<Fn>(RealExport("DllUnregisterServer"));
    return real ? real() : E_FAIL;
}

extern "C" const void* WINAPI GetdfDIJoystick() {
    EnsureFontHook();
    using Fn = const void*(WINAPI*)();
    auto real = reinterpret_cast<Fn>(RealExport("GetdfDIJoystick"));
    return real ? real() : nullptr;
}

BOOL WINAPI DllMain(HINSTANCE self, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(self);
    return TRUE;
}
