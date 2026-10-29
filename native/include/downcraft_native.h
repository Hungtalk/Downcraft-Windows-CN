/*
 * downcraft_native.h - C ABI exported by downcraft_native.dll (DownCraft native helper).
 *
 * The managed side (DownCraft.Core.Native.NativeInterop) P/Invokes these entry
 * points for the hot paths: SHA-1 verification of multi-hundred-megabyte jars,
 * CRC32 and ZIP entry extraction of `natives` archives. Every function has a
 * managed fallback, so the product still works when this DLL is absent.
 *
 * Conventions
 *  - Return value is 0 (DC_OK) on success, a negative DC_ERR_* code otherwise.
 *  - `out_hex` must point to at least 41 bytes; the implementation writes 40
 *    lowercase hex chars followed by '\0'.
 *  - Strings are UTF-16LE for paths (wchar_t on Windows) and UTF-8 for ZIP entry
 *    names, because entry names are UTF-8 by ZIP specification.
 *  - The DLL must not throw across the ABI boundary; all exceptions are caught
 *    and mapped to DC_ERR_* codes.
 */
#ifndef DC_NATIVE_H
#define DC_NATIVE_H

#include <stddef.h>

#if defined(_WIN32)
#  include <wchar.h>
#  define DC_EXPORT __declspec(dllexport)
#  define DC_CALL __cdecl
#else
#  define DC_EXPORT __attribute__((visibility("default")))
#  define DC_CALL
typedef unsigned short wchar_t_compat;
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define DC_OK           0
#define DC_ERR_ARG     -1
#define DC_ERR_IO      -2
#define DC_ERR_NOTFOUND -3
#define DC_ERR_FORMAT  -4
#define DC_ERR_MEMORY  -5
#define DC_ERR_BUFFER  -6
#define DC_ERR_INTERNAL -7

/* Returns a static UTF-8 version string such as "downcraft_native 1.0.0 (msvc)".
 * The pointer stays valid for the process lifetime. */
DC_EXPORT const char* DC_CALL dc_version(void);

/* SHA-1 of a file, read in 1 MiB chunks (no full-file buffering). */
DC_EXPORT int DC_CALL dc_sha1_file(const wchar_t* path, char out_hex[41]);

/* SHA-1 of an in-memory buffer. */
DC_EXPORT int DC_CALL dc_sha1_bytes(const void* data, size_t len, char out_hex[41]);

/* CRC-32 (IEEE 802.3, same polynomial as zlib) of a file. */
DC_EXPORT int DC_CALL dc_crc32_file(const wchar_t* path, unsigned int* out_crc);

/* Looks up an entry in the ZIP central directory.
 * On success writes the uncompressed size and returns 1; returns 0 when the
 * entry does not exist; returns a negative DC_ERR_* code on failure. */
DC_EXPORT int DC_CALL dc_zip_entry_size(const wchar_t* zip_path,
                                              const char* entry_name_utf8,
                                              unsigned long long* out_size);

/* Reads one ZIP entry into `buffer` (uncompressed). Returns bytes written.
 * `out_written` receives the byte count. Fails with DC_ERR_BUFFER when the
 * entry is larger than `buffer_size`. */
DC_EXPORT int DC_CALL dc_zip_read(const wchar_t* zip_path,
                                        const char* entry_name_utf8,
                                        void* buffer,
                                        size_t buffer_size,
                                        size_t* out_written);

/* Extracts a single ZIP entry to `dest_path`, creating parent directories. */
DC_EXPORT int DC_CALL dc_zip_extract(const wchar_t* zip_path,
                                           const char* entry_name_utf8,
                                           const wchar_t* dest_path);

/* Extracts every file entry into `dest_dir` using its stored name.
 * "META-INF/" and similar noise is skipped through `exclude_prefixes`
 * (array of `exclude_count` UTF-8 prefix strings, matched case-insensitively
 * against the entry name). Entry names containing "..", absolute paths and
 * drive letters are rejected with DC_ERR_FORMAT to prevent zip-slip.
 * Returns the number of files written (>= 0) or a negative DC_ERR_* code. */
DC_EXPORT int DC_CALL dc_zip_extract_all(const wchar_t* zip_path,
                                               const wchar_t* dest_dir,
                                               const char* const* exclude_prefixes,
                                               size_t exclude_count);

/* Free bytes available on the volume that hosts `path` (directory or file). */
DC_EXPORT int DC_CALL dc_disk_free(const wchar_t* path,
                                         unsigned long long* out_free_bytes);

#ifdef __cplusplus
}
#endif

#endif /* DC_NATIVE_H */
