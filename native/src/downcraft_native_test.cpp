/*
 * downcraft_native_test.cpp - self-test / diagnostic CLI for downcraft_native.dll.
 *
 * The DLL is loaded dynamically (LoadLibraryW + GetProcAddress) so that this
 * executable verifies exactly what the managed P/Invoke layer sees: the nine
 * exported names and their cdecl signatures.
 *
 * Usage:
 *   downcraft_native_test --sha1 <path>
 *   downcraft_native_test --sha1-bytes <path>
 *   downcraft_native_test --crc32 <path>
 *   downcraft_native_test --zip-size <zip> <entry>
 *   downcraft_native_test --zip-read <zip> <entry>
 *   downcraft_native_test --zip-extract <zip> <dest>
 *   downcraft_native_test --zip-extract-one <zip> <entry> <dest>
 *   downcraft_native_test --disk-free <path>
 *   downcraft_native_test --version
 */

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX

#include <windows.h>

#include "downcraft_native.h"

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <cwchar>
#include <string>
#include <vector>

#include <fcntl.h>
#include <io.h>

#pragma warning(disable : 4191)  // GetProcAddress FARPROC -> typed function pointer

namespace {

using VersionFn = const char*(__cdecl*)();
using Sha1FileFn = int(__cdecl*)(const wchar_t*, char*);
using Sha1BytesFn = int(__cdecl*)(const void*, size_t, char*);
using Crc32FileFn = int(__cdecl*)(const wchar_t*, unsigned int*);
using ZipEntrySizeFn = int(__cdecl*)(const wchar_t*, const char*, unsigned long long*);
using ZipReadFn = int(__cdecl*)(const wchar_t*, const char*, void*, size_t, size_t*);
using ZipExtractFn = int(__cdecl*)(const wchar_t*, const char*, const wchar_t*);
using ZipExtractAllFn = int(__cdecl*)(const wchar_t*, const wchar_t*, const char* const*, size_t);
using DiskFreeFn = int(__cdecl*)(const wchar_t*, unsigned long long*);

const char* ErrorName(int code) {
  switch (code) {
    case DC_OK:
      return "DC_OK";
    case DC_ERR_ARG:
      return "DC_ERR_ARG";
    case DC_ERR_IO:
      return "DC_ERR_IO";
    case DC_ERR_NOTFOUND:
      return "DC_ERR_NOTFOUND";
    case DC_ERR_FORMAT:
      return "DC_ERR_FORMAT";
    case DC_ERR_MEMORY:
      return "DC_ERR_MEMORY";
    case DC_ERR_BUFFER:
      return "DC_ERR_BUFFER";
    case DC_ERR_INTERNAL:
      return "DC_ERR_INTERNAL";
    default:
      return "DC_ERR_UNKNOWN";
  }
}

int ReportError(const char* operation, int code) {
  std::fprintf(stderr, "%s failed: %s (%d)\n", operation, ErrorName(code), code);
  return 1;
}

std::string ToUtf8(const std::wstring& text) {
  if (text.empty()) {
    return std::string();
  }
  const int length = WideCharToMultiByte(CP_UTF8, 0, text.data(),
                                         static_cast<int>(text.size()), nullptr, 0, nullptr,
                                         nullptr);
  if (length <= 0) {
    return std::string();
  }
  std::string result(static_cast<std::size_t>(length), '\0');
  WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), result.data(),
                      length, nullptr, nullptr);
  return result;
}

bool ReadWholeFile(const wchar_t* path, std::vector<unsigned char>& out,
                   std::wstring& error) {
  HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE |
                                 FILE_SHARE_DELETE,
                            nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
  if (file == INVALID_HANDLE_VALUE) {
    error = L"CreateFileW failed with Win32 error " + std::to_wstring(GetLastError());
    return false;
  }

  LARGE_INTEGER size{};
  if (!GetFileSizeEx(file, &size) || size.QuadPart < 0) {
    error = L"GetFileSizeEx failed with Win32 error " + std::to_wstring(GetLastError());
    CloseHandle(file);
    return false;
  }

  out.resize(static_cast<std::size_t>(size.QuadPart));
  std::size_t offset = 0;
  while (offset < out.size()) {
    const DWORD request =
        static_cast<DWORD>(std::min<std::size_t>(out.size() - offset, 1u << 20));
    DWORD got = 0;
    if (!ReadFile(file, out.data() + offset, request, &got, nullptr)) {
      error = L"ReadFile failed with Win32 error " + std::to_wstring(GetLastError());
      CloseHandle(file);
      return false;
    }
    if (got == 0) {
      break;
    }
    offset += got;
  }
  CloseHandle(file);

  if (offset != out.size()) {
    error = L"unexpected end of file";
    return false;
  }
  return true;
}

struct Api {
  HMODULE module = nullptr;
  VersionFn version = nullptr;
  Sha1FileFn sha1_file = nullptr;
  Sha1BytesFn sha1_bytes = nullptr;
  Crc32FileFn crc32_file = nullptr;
  ZipEntrySizeFn zip_entry_size = nullptr;
  ZipReadFn zip_read = nullptr;
  ZipExtractFn zip_extract = nullptr;
  ZipExtractAllFn zip_extract_all = nullptr;
  DiskFreeFn disk_free = nullptr;

  Api() = default;
  Api(const Api&) = delete;
  Api& operator=(const Api&) = delete;

  ~Api() {
    if (module != nullptr) {
      FreeLibrary(module);
    }
  }

  bool Load(std::string& error) {
    wchar_t module_path[32768] = {};
    const DWORD length =
        GetModuleFileNameW(nullptr, module_path,
                           static_cast<DWORD>(sizeof(module_path) / sizeof(module_path[0])));
    if (length == 0 || length >= sizeof(module_path) / sizeof(module_path[0])) {
      error = "GetModuleFileNameW failed";
      return false;
    }

    std::wstring directory(module_path, length);
    const std::size_t slash = directory.find_last_of(L"\\/");
    if (slash == std::wstring::npos) {
      error = "cannot determine executable directory";
      return false;
    }
    directory.resize(slash + 1);
    const std::wstring dll_path = directory + L"downcraft_native.dll";

    module = LoadLibraryW(dll_path.c_str());
    if (module == nullptr) {
      error = "LoadLibraryW(" + ToUtf8(dll_path) + ") failed with Win32 error " +
              std::to_string(GetLastError());
      return false;
    }

    std::string missing;
#define DC_RESOLVE(field, export_name)                                       \
  field = reinterpret_cast<decltype(field)>(GetProcAddress(module, export_name)); \
  if (field == nullptr) {                                                      \
    if (!missing.empty()) {                                                    \
      missing += ", ";                                                         \
    }                                                                          \
    missing += export_name;                                                    \
  }

    DC_RESOLVE(version, "dc_version")
    DC_RESOLVE(sha1_file, "dc_sha1_file")
    DC_RESOLVE(sha1_bytes, "dc_sha1_bytes")
    DC_RESOLVE(crc32_file, "dc_crc32_file")
    DC_RESOLVE(zip_entry_size, "dc_zip_entry_size")
    DC_RESOLVE(zip_read, "dc_zip_read")
    DC_RESOLVE(zip_extract, "dc_zip_extract")
    DC_RESOLVE(zip_extract_all, "dc_zip_extract_all")
    DC_RESOLVE(disk_free, "dc_disk_free")
#undef DC_RESOLVE

    if (!missing.empty()) {
      error = "missing export(s): " + missing;
      FreeLibrary(module);
      module = nullptr;
      return false;
    }
    return true;
  }
};

void PrintUsage() {
  std::fprintf(stderr,
               "usage:\n"
               "  downcraft_native_test --sha1 <path>\n"
               "  downcraft_native_test --sha1-bytes <path>\n"
               "  downcraft_native_test --crc32 <path>\n"
               "  downcraft_native_test --zip-size <zip> <entry>\n"
               "  downcraft_native_test --zip-read <zip> <entry>\n"
               "  downcraft_native_test --zip-extract <zip> <dest>\n"
               "  downcraft_native_test --zip-extract-one <zip> <entry> <dest>\n"
               "  downcraft_native_test --disk-free <path>\n"
               "  downcraft_native_test --version\n");
}

bool EntryNameToUtf8(const wchar_t* text, std::string& out) {
  const int length = static_cast<int>(wcslen(text));
  if (length <= 0) {
    return false;
  }
  const int needed = WideCharToMultiByte(CP_UTF8, 0, text, length, nullptr, 0, nullptr, nullptr);
  if (needed <= 0) {
    return false;
  }
  out.resize(static_cast<std::size_t>(needed));
  const int written =
      WideCharToMultiByte(CP_UTF8, 0, text, length, out.data(), needed, nullptr, nullptr);
  return written == needed;
}

}  // namespace

int wmain(int argc, wchar_t** argv) {
  if (argc < 2) {
    PrintUsage();
    return 1;
  }

  Api api;
  std::string load_error;
  if (!api.Load(load_error)) {
    std::fprintf(stderr, "%s\n", load_error.c_str());
    return 1;
  }

  const std::wstring mode(argv[1]);

  if (mode == L"--version") {
    const char* version = api.version();
    if (version == nullptr) {
      return ReportError("dc_version", DC_ERR_INTERNAL);
    }
    std::printf("%s\n", version);
    return 0;
  }

  if (mode == L"--sha1") {
    if (argc != 3) {
      PrintUsage();
      return 1;
    }
    char hex[41] = {};
    const int rc = api.sha1_file(argv[2], hex);
    if (rc != DC_OK) {
      return ReportError("dc_sha1_file", rc);
    }
    std::printf("sha1=%s\n", hex);
    return 0;
  }

  if (mode == L"--sha1-bytes") {
    if (argc != 3) {
      PrintUsage();
      return 1;
    }
    std::vector<unsigned char> content;
    std::wstring read_error;
    if (!ReadWholeFile(argv[2], content, read_error)) {
      std::fprintf(stderr, "read failed: %s\n", ToUtf8(read_error).c_str());
      return 1;
    }
    char hex[41] = {};
    const int rc =
        api.sha1_bytes(content.empty() ? nullptr : content.data(), content.size(), hex);
    if (rc != DC_OK) {
      return ReportError("dc_sha1_bytes", rc);
    }
    std::printf("sha1=%s\n", hex);
    return 0;
  }

  if (mode == L"--crc32") {
    if (argc != 3) {
      PrintUsage();
      return 1;
    }
    unsigned int crc = 0;
    const int rc = api.crc32_file(argv[2], &crc);
    if (rc != DC_OK) {
      return ReportError("dc_crc32_file", rc);
    }
    std::printf("crc32=%08x\n", crc);
    return 0;
  }

  if (mode == L"--zip-size") {
    if (argc != 4) {
      PrintUsage();
      return 1;
    }
    std::string entry;
    if (!EntryNameToUtf8(argv[3], entry)) {
      std::fprintf(stderr, "invalid entry name\n");
      return 1;
    }
    unsigned long long size = 0;
    const int found = api.zip_entry_size(argv[2], entry.c_str(), &size);
    if (found < 0) {
      return ReportError("dc_zip_entry_size", found);
    }
    if (found == 0) {
      std::fprintf(stderr, "entry not found: %s\n", entry.c_str());
      return 1;
    }
    std::printf("size=%llu\n", size);
    return 0;
  }

  if (mode == L"--zip-read") {
    if (argc != 4) {
      PrintUsage();
      return 1;
    }
    std::string entry;
    if (!EntryNameToUtf8(argv[3], entry)) {
      std::fprintf(stderr, "invalid entry name\n");
      return 1;
    }
    unsigned long long size = 0;
    const int found = api.zip_entry_size(argv[2], entry.c_str(), &size);
    if (found < 0) {
      return ReportError("dc_zip_entry_size", found);
    }
    if (found == 0) {
      std::fprintf(stderr, "entry not found: %s\n", entry.c_str());
      return 1;
    }
    std::vector<unsigned char> buffer(static_cast<std::size_t>(size) + 1, 0);
    std::size_t written = 0;
    const int rc =
        api.zip_read(argv[2], entry.c_str(), buffer.data(), buffer.size(), &written);
    if (rc != DC_OK) {
      return ReportError("dc_zip_read", rc);
    }
    _setmode(_fileno(stdout), _O_BINARY);
    if (written > 0) {
      std::fwrite(buffer.data(), 1, written, stdout);
    }
    std::fflush(stdout);
    return 0;
  }

  if (mode == L"--zip-extract") {
    if (argc != 4) {
      PrintUsage();
      return 1;
    }
    const char* excludes[] = {"META-INF/", "META-INF\\"};
    const int rc = api.zip_extract_all(argv[2], argv[3], excludes,
                                       sizeof(excludes) / sizeof(excludes[0]));
    if (rc < 0) {
      return ReportError("dc_zip_extract_all", rc);
    }
    std::printf("extracted=%d\n", rc);
    return 0;
  }

  if (mode == L"--zip-extract-one") {
    if (argc != 5) {
      PrintUsage();
      return 1;
    }
    std::string entry;
    if (!EntryNameToUtf8(argv[3], entry)) {
      std::fprintf(stderr, "invalid entry name\n");
      return 1;
    }
    const int rc = api.zip_extract(argv[2], entry.c_str(), argv[4]);
    if (rc != DC_OK) {
      return ReportError("dc_zip_extract", rc);
    }
    std::printf("extracted=1\n");
    return 0;
  }

  if (mode == L"--disk-free") {
    if (argc != 3) {
      PrintUsage();
      return 1;
    }
    unsigned long long free_bytes = 0;
    const int rc = api.disk_free(argv[2], &free_bytes);
    if (rc != DC_OK) {
      return ReportError("dc_disk_free", rc);
    }
    std::printf("free=%llu\n", free_bytes);
    return 0;
  }

  PrintUsage();
  return 1;
}
