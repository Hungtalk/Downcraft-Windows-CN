# mcdnative

Small, dependency-free x64 Windows helper DLL for McDownload. It exports the C ABI
declared in [`include/mcdl_native.h`](include/mcdl_native.h), which the managed
application consumes through
`DllImport("mcdnative.dll", CallingConvention = CallingConvention.Cdecl)`.

The native component is optional: every managed call site keeps a managed
fallback, so the product still works when `mcdnative.dll` is missing.

## Exports

| Export | Result | Notes |
| --- | --- | --- |
| `mcdl_version()` | `const char*` | Static banner, valid for the process lifetime. |
| `mcdl_sha1_file(path, out_hex)` | `MCDL_OK` / error | Streams the file in 1 MiB chunks; writes 40 lowercase hex chars + NUL. |
| `mcdl_sha1_bytes(data, len, out_hex)` | `MCDL_OK` / error | In-memory buffer; `data == NULL` is accepted only when `len == 0`. |
| `mcdl_crc32_file(path, out_crc)` | `MCDL_OK` / error | IEEE 802.3 CRC-32 (same polynomial as zlib). |
| `mcdl_zip_entry_size(zip, name, out_size)` | `1` found, `0` missing, negative error | Looks up the central directory. |
| `mcdl_zip_read(zip, name, buffer, size, out_written)` | `MCDL_OK` / error | Fails with `MCDL_ERR_BUFFER` when the entry is larger than `buffer`. |
| `mcdl_zip_extract(zip, name, dest_path)` | `MCDL_OK` / error | Creates parent directories, overwrites the destination file. |
| `mcdl_zip_extract_all(zip, dest_dir, excludes, count)` | file count / error | Skips directories and excluded prefixes; zip-slip names are rejected. |
| `mcdl_disk_free(path, out_free)` | `MCDL_OK` / error | `GetDiskFreeSpaceExW` available bytes for the volume. |

Error codes (negative): `MCDL_ERR_ARG` -1, `MCDL_ERR_IO` -2,
`MCDL_ERR_NOTFOUND` -3, `MCDL_ERR_FORMAT` -4, `MCDL_ERR_MEMORY` -5,
`MCDL_ERR_BUFFER` -6, `MCDL_ERR_INTERNAL` -7.

Conventions: paths are UTF-16 (`wchar_t`), ZIP entry names are UTF-8, `out_hex`
points to at least 41 bytes, and every output parameter is zeroed on failure.
Each exported function validates its pointers and catches all C++ exceptions, so
nothing crosses the ABI boundary.

## Build

Prerequisites: Windows x64, Visual Studio with the MSVC x64 toolset, CMake 3.20+
and (preferred) Ninja. CMake and Ninja from the WinGet MinGW package work fine
with MSVC.

```powershell
pwsh -File scripts\build-native.ps1 -Clean
# If the ExecutionPolicy blocks local scripts:
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-native.ps1 -Clean
```

Outputs:

```
native/build/out/mcdnative.dll
native/build/out/mcdl_native_test.exe
```

The script discovers Visual Studio with `vswhere.exe` (falling back to standard
install roots), initializes the x64 developer environment, configures CMake and
builds. It prints the DLL path and exits 0 on success, or prints a clear error and
exits 1.

Manual build:

```bat
call "%ProgramFiles%\Microsoft Visual Studio\18\Community\Common7\Tools\VsDevCmd.bat" -arch=x64 -host_arch=x64
cmake -S native -B native/build -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build native/build --config Release
```

The DLL and tests are built with the static CRT (`/MT`), C++20, `/W4`,
`/permissive-`, `/EHsc`, and therefore need no VC++ redistributable.

## Design

- **SHA-1**: CNG `bcrypt.dll` (`BCRYPT_SHA1_ALGORITHM`) with a reusable hash
  object and one 1 MiB buffer; files are streamed, never buffered whole.
- **CRC-32**: CNG `BCRYPT_CRC32_ALGORITHM` when the OS exposes it. Windows 10
  (including 21H2) returns `STATUS_NOT_FOUND` from
  `BCryptOpenAlgorithmProvider(L"CRC32")`, so a table-driven IEEE implementation
  is used there. Both paths were validated against `CRC32("123456789") =
  0xCBF43926`. The CNG path calibrates the digest byte order against that same
  check value.
- **ZIP**: hand-written EOCD scan, central-directory parse and local-header data
  offset calculation. Stored (method 0) and deflate (method 8) are supported.
  ZIP64 (`0xFFFF`/`0xFFFFFFFF` EOCD or entry fields) is rejected with
  `MCDL_ERR_FORMAT` instead of mis-parsing. Encrypted, multi-disk and other
  compression methods are rejected with `MCDL_ERR_FORMAT`.
- **DEFLATE**: ntdll `RtlGetCompressionWorkSpaceSize` +
  `RtlDecompressBufferEx(COMPRESSION_FORMAT_DEFLATE)` is the primary path.
  Windows 10's ntdll reports `STATUS_COMPRESSION_NOT_SUPPORTED (0xC000025F)` for
  that format, so a self-contained RFC 1951 inflate implementation is used as a
  fallback. No third-party code or libraries are involved.
- **Zip-slip protection** (`mcdl_zip_extract_all`): rejects absolute names,
  drive letters, colons (drive/ADS), `..` segments, and segments ending in dots
  or spaces that Windows would strip; skips directory entries; excluded prefixes
  are compared case-insensitively (ASCII) before extraction.
- **Paths**: Win32 Unicode APIs (`CreateFileW`, `ReadFile`, `WriteFile`,
  `CreateDirectoryW`, `GetDiskFreeSpaceExW`, `MultiByteToWideChar`,
  `WideCharToMultiByte`).
- **Runtime dependencies**: only `KERNEL32.dll`, `bcrypt.dll` and `ntdll.dll`
  (verified with `dumpbin /dependents`). No `VCRUNTIME140.dll` /
  `MSVCP140.dll`.

## Build-environment notes

- This repository's Visual Studio 2026 install ships `vcvars64.bat` without the
  `vcvarsall.bat` it calls. `scripts/build-native.ps1` detects that and falls
  back to `Common7\Tools\VsDevCmd.bat -arch=x64 -host_arch=x64`, which is the
  supported developer-environment entry point.
- The same install only has OneCore CRT libraries (`lib\onecore\x64\libcmt.lib`).
  When the desktop `lib\x64\libcmt.lib` is absent, the batch shim prepends the
  OneCore directory to `LIB` so `/MT` can link. This is derived from
  `VCToolsInstallDir`, not hard-coded.
- With a localized MSVC, Ninja may print the compiler's `showIncludes` lines
  because CMake cannot match the localized prefix. It is cosmetic; the build
  still succeeds.

## Limitations

- ZIP64 archives are not extracted (explicit `MCDL_ERR_FORMAT`).
- Only stored and deflate entries are supported; encrypted entries, multi-disk
  archives and other compression methods are rejected.
- Entry names must be valid UTF-8 (plain ASCII works everywhere). Non-UTF-8
  legacy code-page names are rejected.
- Paths longer than `MAX_PATH` are not handled with the `\\?\` prefix; natives
  archive paths are short.
- `mcdl_zip_read` requires a non-NULL buffer even for a zero-length entry.
- `mcdl_zip_extract_all` validates entry names before exclusion checks, so an
  archive containing a malicious entry fails even if that entry would have been
  skipped.
