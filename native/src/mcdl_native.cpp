/*
 * mcdl_native.cpp - implementation of the mcdnative.dll C ABI.
 *
 * Dependency-free Windows implementation:
 *   - SHA-1 and CRC-32 through CNG (bcrypt.dll)
 *   - DEFLATE through ntdll!RtlDecompressBufferEx
 *   - ZIP container parsing by hand (EOCD + central directory + local header)
 *   - Win32 Unicode file/path APIs
 *
 * See include/mcdl_native.h for the ABI contract. No C++ exception is allowed
 * to escape an exported function; every entry point has a catch-all.
 */

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX

#include <windows.h>
#include <bcrypt.h>

#include "mcdl_native.h"

#include <algorithm>
#include <array>
#include <cctype>
#include <climits>
#include <cstdint>
#include <cstring>
#include <cwchar>
#include <cwctype>
#include <new>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#pragma comment(lib, "bcrypt.lib")
#pragma comment(lib, "ntdll.lib")

#ifndef BCRYPT_CRC32_ALGORITHM
// Not present in every Windows SDK / not available on every Windows version.
#define BCRYPT_CRC32_ALGORITHM L"CRC32"
#endif

extern "C" {
LONG __stdcall RtlGetCompressionWorkSpaceSize(USHORT compression_format_and_engine,
                                              ULONG* compress_buffer_workspace_size,
                                              ULONG* compress_fragment_workspace_size);
LONG __stdcall RtlDecompressBufferEx(USHORT compression_format,
                                     PUCHAR uncompressed_buffer,
                                     ULONG uncompressed_buffer_size,
                                     PUCHAR compressed_buffer,
                                     ULONG compressed_buffer_size,
                                     ULONG* final_uncompressed_size,
                                     PVOID workspace);
}

namespace {

constexpr std::size_t kStreamBufferSize = 1024u * 1024u;  // 1 MiB, as specified
constexpr std::uint32_t kMaxZipEntrySize = 0xFFFFFFFFu;   // ULONG limit of the ntdll API
constexpr USHORT kCompressionFormatDeflate = 0x0006;    // COMPRESSION_FORMAT_DEFLATE
constexpr std::uint32_t kCrc32CheckValue = 0xCBF43926u;   // CRC-32("123456789")

constexpr std::uint32_t kEocdSignature = 0x06054B50u;
constexpr std::uint32_t kCentralSignature = 0x02014B50u;
constexpr std::uint32_t kLocalSignature = 0x04034B50u;
constexpr std::uint16_t kZip64Marker16 = 0xFFFFu;
constexpr std::uint32_t kZip64Marker32 = 0xFFFFFFFFu;

// ---------------------------------------------------------------------------
// Small RAII wrappers
// ---------------------------------------------------------------------------

class WinHandle {
 public:
  WinHandle() = default;
  explicit WinHandle(HANDLE handle) : handle_(handle) {}
  ~WinHandle() { Reset(); }

  WinHandle(const WinHandle&) = delete;
  WinHandle& operator=(const WinHandle&) = delete;

  WinHandle(WinHandle&& other) noexcept : handle_(other.handle_) {
    other.handle_ = INVALID_HANDLE_VALUE;
  }

  WinHandle& operator=(WinHandle&& other) noexcept {
    if (this != &other) {
      Reset();
      handle_ = other.handle_;
      other.handle_ = INVALID_HANDLE_VALUE;
    }
    return *this;
  }

  void Reset() {
    if (handle_ != INVALID_HANDLE_VALUE) {
      CloseHandle(handle_);
      handle_ = INVALID_HANDLE_VALUE;
    }
  }

  HANDLE get() const { return handle_; }
  bool valid() const { return handle_ != INVALID_HANDLE_VALUE; }

 private:
  HANDLE handle_ = INVALID_HANDLE_VALUE;
};

class BCryptAlgorithm {
 public:
  BCryptAlgorithm() = default;
  ~BCryptAlgorithm() {
    if (handle_ != nullptr) {
      BCryptCloseAlgorithmProvider(handle_, 0);
    }
  }

  BCryptAlgorithm(const BCryptAlgorithm&) = delete;
  BCryptAlgorithm& operator=(const BCryptAlgorithm&) = delete;

  int Open(LPCWSTR algorithm) {
    const NTSTATUS status = BCryptOpenAlgorithmProvider(&handle_, algorithm, nullptr, 0);
    if (status < 0) {
      handle_ = nullptr;
      return MCDL_ERR_INTERNAL;
    }
    return MCDL_OK;
  }

  BCRYPT_ALG_HANDLE get() const { return handle_; }

 private:
  BCRYPT_ALG_HANDLE handle_ = nullptr;
};

class BCryptHash {
 public:
  BCryptHash() = default;
  ~BCryptHash() {
    if (handle_ != nullptr) {
      BCryptDestroyHash(handle_);
    }
  }

  BCryptHash(const BCryptHash&) = delete;
  BCryptHash& operator=(const BCryptHash&) = delete;

  int Create(BCRYPT_ALG_HANDLE algorithm) {
    DWORD object_length = 0;
    DWORD written = 0;
    NTSTATUS status = BCryptGetProperty(algorithm, BCRYPT_OBJECT_LENGTH,
                                        reinterpret_cast<PUCHAR>(&object_length),
                                        sizeof(object_length), &written, 0);
    if (status < 0 || object_length == 0) {
      return MCDL_ERR_INTERNAL;
    }
    object_.assign(object_length, 0);
    status = BCryptCreateHash(algorithm, &handle_, object_.data(),
                              static_cast<ULONG>(object_.size()), nullptr, 0, 0);
    if (status < 0) {
      handle_ = nullptr;
      return MCDL_ERR_INTERNAL;
    }
    return MCDL_OK;
  }

  int Update(const void* data, std::size_t length) {
    const auto* cursor = static_cast<const unsigned char*>(data);
    while (length > 0) {
      const ULONG chunk = static_cast<ULONG>(std::min<std::size_t>(length, kStreamBufferSize));
      if (BCryptHashData(handle_, const_cast<PUCHAR>(cursor), chunk, 0) < 0) {
        return MCDL_ERR_INTERNAL;
      }
      cursor += chunk;
      length -= chunk;
    }
    return MCDL_OK;
  }

  int Finish(void* digest, ULONG digest_length) {
    if (BCryptFinishHash(handle_, static_cast<PUCHAR>(digest), digest_length, 0) < 0) {
      return MCDL_ERR_INTERNAL;
    }
    return MCDL_OK;
  }

 private:
  BCRYPT_HASH_HANDLE handle_ = nullptr;
  std::vector<unsigned char> object_;
};

int MapWin32Error(DWORD error);

// Sequential reader over a Win32 file. One handle per exported call, so no
// cross-thread state is shared.
class FileReader {
 public:
  FileReader() = default;

  int Open(const wchar_t* path) {
    file_ = WinHandle(CreateFileW(path, GENERIC_READ,
                                  FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                                  nullptr, OPEN_EXISTING,
                                  FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN, nullptr));
    if (!file_.valid()) {
      return MapWin32Error(GetLastError());
    }
    LARGE_INTEGER size{};
    if (!GetFileSizeEx(file_.get(), &size) || size.QuadPart < 0) {
      return MapWin32Error(GetLastError());
    }
    size_ = static_cast<std::uint64_t>(size.QuadPart);
    return MCDL_OK;
  }

  std::uint64_t size() const { return size_; }

  int ReadSome(void* buffer, std::size_t capacity, std::size_t& bytes_read) {
    bytes_read = 0;
    if (capacity == 0) {
      return MCDL_OK;
    }
    const DWORD request = static_cast<DWORD>(std::min<std::size_t>(capacity, 1u << 30));
    DWORD got = 0;
    if (!ReadFile(file_.get(), buffer, request, &got, nullptr)) {
      return MapWin32Error(GetLastError());
    }
    bytes_read = got;
    return MCDL_OK;
  }

  int ReadAt(std::uint64_t offset, void* buffer, std::size_t length) {
    LARGE_INTEGER position{};
    position.QuadPart = static_cast<LONGLONG>(offset);
    if (!SetFilePointerEx(file_.get(), position, nullptr, FILE_BEGIN)) {
      return MapWin32Error(GetLastError());
    }
    auto* cursor = static_cast<unsigned char*>(buffer);
    std::size_t remaining = length;
    while (remaining > 0) {
      const DWORD request = static_cast<DWORD>(std::min<std::size_t>(remaining, 1u << 30));
      DWORD got = 0;
      if (!ReadFile(file_.get(), cursor, request, &got, nullptr)) {
        return MapWin32Error(GetLastError());
      }
      if (got == 0) {
        return MCDL_ERR_FORMAT;  // unexpected end of file
      }
      cursor += got;
      remaining -= got;
    }
    return MCDL_OK;
  }

 private:
  WinHandle file_;
  std::uint64_t size_ = 0;
};

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

int MapWin32Error(DWORD error) {
  switch (error) {
    case ERROR_FILE_NOT_FOUND:
    case ERROR_PATH_NOT_FOUND:
    case ERROR_INVALID_DRIVE:
    case ERROR_BAD_NETPATH:
    case ERROR_BAD_NET_NAME:
      return MCDL_ERR_NOTFOUND;
    case ERROR_NOT_ENOUGH_MEMORY:
    case ERROR_OUTOFMEMORY:
      return MCDL_ERR_MEMORY;
    case ERROR_INVALID_PARAMETER:
    case ERROR_INVALID_NAME:
    case ERROR_BAD_PATHNAME:
      return MCDL_ERR_ARG;
    case ERROR_ACCESS_DENIED:
    case ERROR_SHARING_VIOLATION:
    case ERROR_LOCK_VIOLATION:
    case ERROR_WRITE_PROTECT:
    case ERROR_DISK_FULL:
    case ERROR_HANDLE_DISK_FULL:
    default:
      return MCDL_ERR_IO;
  }
}

void ToHexLower(const unsigned char* data, std::size_t length, char* output) {
  static constexpr char kHexDigits[] = "0123456789abcdef";
  for (std::size_t i = 0; i < length; ++i) {
    output[i * 2] = kHexDigits[(data[i] >> 4) & 0x0Fu];
    output[i * 2 + 1] = kHexDigits[data[i] & 0x0Fu];
  }
  output[length * 2] = '\0';
}

int QueryHashLength(BCRYPT_ALG_HANDLE algorithm, ULONG& length) {
  DWORD value = 0;
  DWORD written = 0;
  if (BCryptGetProperty(algorithm, BCRYPT_HASH_LENGTH,
                        reinterpret_cast<PUCHAR>(&value), sizeof(value), &written, 0) < 0) {
    return MCDL_ERR_INTERNAL;
  }
  length = value;
  return MCDL_OK;
}

int HashFileChunks(const wchar_t* path, BCRYPT_ALG_HANDLE algorithm, ULONG digest_length,
                   unsigned char* digest) {
  FileReader reader;
  int rc = reader.Open(path);
  if (rc != MCDL_OK) {
    return rc;
  }

  BCryptHash hash;
  rc = hash.Create(algorithm);
  if (rc != MCDL_OK) {
    return rc;
  }

  std::vector<unsigned char> buffer(kStreamBufferSize);
  for (;;) {
    std::size_t read = 0;
    rc = reader.ReadSome(buffer.data(), buffer.size(), read);
    if (rc != MCDL_OK) {
      return rc;
    }
    if (read == 0) {
      break;
    }
    rc = hash.Update(buffer.data(), read);
    if (rc != MCDL_OK) {
      return rc;
    }
  }
  return hash.Finish(digest, digest_length);
}

int Sha1FileImpl(const wchar_t* path, char* out_hex) {
  BCryptAlgorithm algorithm;
  int rc = algorithm.Open(BCRYPT_SHA1_ALGORITHM);
  if (rc != MCDL_OK) {
    return rc;
  }
  ULONG digest_length = 0;
  rc = QueryHashLength(algorithm.get(), digest_length);
  if (rc != MCDL_OK || digest_length != 20) {
    return MCDL_ERR_INTERNAL;
  }
  unsigned char digest[20] = {};
  rc = HashFileChunks(path, algorithm.get(), digest_length, digest);
  if (rc != MCDL_OK) {
    return rc;
  }
  ToHexLower(digest, digest_length, out_hex);
  return MCDL_OK;
}

int Sha1BytesImpl(const void* data, std::size_t length, char* out_hex) {
  BCryptAlgorithm algorithm;
  int rc = algorithm.Open(BCRYPT_SHA1_ALGORITHM);
  if (rc != MCDL_OK) {
    return rc;
  }
  ULONG digest_length = 0;
  rc = QueryHashLength(algorithm.get(), digest_length);
  if (rc != MCDL_OK || digest_length != 20) {
    return MCDL_ERR_INTERNAL;
  }

  BCryptHash hash;
  rc = hash.Create(algorithm.get());
  if (rc != MCDL_OK) {
    return rc;
  }

  const auto* cursor = static_cast<const unsigned char*>(data);
  std::size_t offset = 0;
  while (offset < length) {
    const std::size_t chunk = std::min<std::size_t>(length - offset, kStreamBufferSize);
    rc = hash.Update(cursor + offset, chunk);
    if (rc != MCDL_OK) {
      return rc;
    }
    offset += chunk;
  }

  unsigned char digest[20] = {};
  rc = hash.Finish(digest, digest_length);
  if (rc != MCDL_OK) {
    return rc;
  }
  ToHexLower(digest, digest_length, out_hex);
  return MCDL_OK;
}

// ---------------------------------------------------------------------------
// CRC-32
// ---------------------------------------------------------------------------
//
// CNG exposes BCRYPT_CRC32_ALGORITHM only on recent Windows builds; Windows 10
// (incl. 21H2) returns STATUS_NOT_FOUND when the provider is opened. We therefore
// use CNG when it is available and fall back to a byte-table implementation with
// the same IEEE 802.3 polynomial as zlib. Both paths produce identical values.

constexpr std::array<std::uint32_t, 256> MakeCrc32Table() {
  std::array<std::uint32_t, 256> table{};
  for (std::uint32_t i = 0; i < 256; ++i) {
    std::uint32_t value = i;
    for (int bit = 0; bit < 8; ++bit) {
      value = (value & 1u) ? (0xEDB88320u ^ (value >> 1)) : (value >> 1);
    }
    table[i] = value;
  }
  return table;
}

std::uint32_t Crc32Update(std::uint32_t state, const unsigned char* data, std::size_t length) {
  static constexpr auto table = MakeCrc32Table();
  for (std::size_t i = 0; i < length; ++i) {
    state = table[(state ^ data[i]) & 0xFFu] ^ (state >> 8);
  }
  return state;
}

int Crc32FileViaTable(const wchar_t* path, unsigned int* out_crc) {
  FileReader reader;
  int rc = reader.Open(path);
  if (rc != MCDL_OK) {
    return rc;
  }
  std::vector<unsigned char> buffer(kStreamBufferSize);
  std::uint32_t state = 0xFFFFFFFFu;
  for (;;) {
    std::size_t read = 0;
    rc = reader.ReadSome(buffer.data(), buffer.size(), read);
    if (rc != MCDL_OK) {
      return rc;
    }
    if (read == 0) {
      break;
    }
    state = Crc32Update(state, buffer.data(), read);
  }
  *out_crc = state ^ 0xFFFFFFFFu;
  return MCDL_OK;
}

// Some CNG providers return the CRC-32 digest in big-endian byte order, others in
// little-endian. Calibrate against the well-known check value instead of guessing.
int Crc32CalibrateByteOrder(BCRYPT_ALG_HANDLE algorithm, bool& big_endian) {
  BCryptHash hash;
  int rc = hash.Create(algorithm);
  if (rc != MCDL_OK) {
    return rc;
  }
  constexpr unsigned char kCheck[] = {'1', '2', '3', '4', '5', '6', '7', '8', '9'};
  rc = hash.Update(kCheck, sizeof(kCheck));
  if (rc != MCDL_OK) {
    return rc;
  }
  unsigned char digest[4] = {};
  rc = hash.Finish(digest, 4);
  if (rc != MCDL_OK) {
    return rc;
  }
  const std::uint32_t be = (static_cast<std::uint32_t>(digest[0]) << 24) |
                           (static_cast<std::uint32_t>(digest[1]) << 16) |
                           (static_cast<std::uint32_t>(digest[2]) << 8) |
                           static_cast<std::uint32_t>(digest[3]);
  const std::uint32_t le = (static_cast<std::uint32_t>(digest[3]) << 24) |
                           (static_cast<std::uint32_t>(digest[2]) << 16) |
                           (static_cast<std::uint32_t>(digest[1]) << 8) |
                           static_cast<std::uint32_t>(digest[0]);
  if (be == kCrc32CheckValue) {
    big_endian = true;
    return MCDL_OK;
  }
  if (le == kCrc32CheckValue) {
    big_endian = false;
    return MCDL_OK;
  }
  return MCDL_ERR_INTERNAL;
}

int Crc32FileViaCng(const wchar_t* path, unsigned int* out_crc) {
  BCryptAlgorithm algorithm;
  int rc = algorithm.Open(BCRYPT_CRC32_ALGORITHM);
  if (rc != MCDL_OK) {
    return rc;
  }
  ULONG digest_length = 0;
  rc = QueryHashLength(algorithm.get(), digest_length);
  if (rc != MCDL_OK || digest_length != 4) {
    return MCDL_ERR_INTERNAL;
  }

  bool big_endian = true;
  rc = Crc32CalibrateByteOrder(algorithm.get(), big_endian);
  if (rc != MCDL_OK) {
    return rc;
  }

  unsigned char digest[4] = {};
  rc = HashFileChunks(path, algorithm.get(), digest_length, digest);
  if (rc != MCDL_OK) {
    return rc;
  }
  if (big_endian) {
    *out_crc = (static_cast<std::uint32_t>(digest[0]) << 24) |
               (static_cast<std::uint32_t>(digest[1]) << 16) |
               (static_cast<std::uint32_t>(digest[2]) << 8) |
               static_cast<std::uint32_t>(digest[3]);
  } else {
    *out_crc = (static_cast<std::uint32_t>(digest[3]) << 24) |
               (static_cast<std::uint32_t>(digest[2]) << 16) |
               (static_cast<std::uint32_t>(digest[1]) << 8) |
               static_cast<std::uint32_t>(digest[0]);
  }
  return MCDL_OK;
}

int Crc32FileImpl(const wchar_t* path, unsigned int* out_crc) {
  const int cng_result = Crc32FileViaCng(path, out_crc);
  if (cng_result == MCDL_OK) {
    return MCDL_OK;
  }
  if (cng_result != MCDL_ERR_INTERNAL) {
    return cng_result;  // real file error; do not retry
  }
  return Crc32FileViaTable(path, out_crc);
}

// ---------------------------------------------------------------------------
// DEFLATE (RFC 1951) fallback
// ---------------------------------------------------------------------------
//
// ntdll's RtlDecompressBufferEx supports COMPRESSION_FORMAT_DEFLATE only on
// recent Windows builds; Windows 10 (incl. 21H2) returns
// STATUS_COMPRESSION_NOT_SUPPORTED (0xC000025F). Keep the ntdll path as the
// primary implementation and fall back to this self-contained decoder, so the
// DLL stays dependency-free and works on every supported Windows version.

constexpr std::uint16_t kLengthBase[29] = {
    3,   4,   5,   6,   7,   8,   9,   10,  11,  13,  15,  17,  19,  23,  27,
    31,  35,  43,  51,  59,  67,  83,  99,  115, 131, 163, 195, 227, 258};
constexpr std::uint8_t kLengthExtra[29] = {
    0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2,
    2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0};
constexpr std::uint16_t kDistanceBase[30] = {
    1,     2,     3,     4,     5,     7,     9,     13,    17,    25,
    33,    49,    65,    97,    129,   193,   257,   385,   513,   769,
    1025,  1537,  2049,  3073,  4097,  6145,  8193,  12289, 16385, 24577};
constexpr std::uint8_t kDistanceExtra[30] = {
    0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6,
    6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13};
constexpr std::uint8_t kCodeLengthOrder[19] = {16, 17, 18, 0, 8,  7, 9,  6, 10, 5,
                                               11, 4,  12, 3, 13, 2, 14, 1, 15};

class DeflateBitReader {
 public:
  DeflateBitReader(const unsigned char* data, std::size_t size)
      : data_(data), size_(size) {}

  bool ReadBits(int count, std::uint32_t& value) {
    if (count < 0 || count > 24) {
      return false;
    }
    while (bit_count_ < count) {
      if (position_ >= size_) {
        return false;
      }
      bits_ |= static_cast<std::uint32_t>(data_[position_++]) << bit_count_;
      bit_count_ += 8;
    }
    value = bits_ & ((1u << count) - 1u);
    bits_ >>= count;
    bit_count_ -= count;
    return true;
  }

  void AlignToByte() {
    const int drop = bit_count_ & 7;
    bits_ >>= drop;
    bit_count_ -= drop;
  }

  bool ReadBytes(unsigned char* destination, std::size_t count) {
    std::size_t copied = 0;
    while (copied < count && bit_count_ >= 8) {
      destination[copied++] = static_cast<unsigned char>(bits_ & 0xFFu);
      bits_ >>= 8;
      bit_count_ -= 8;
    }
    const std::size_t remaining = count - copied;
    if (remaining > size_ - position_) {
      return false;
    }
    if (remaining > 0) {
      std::memcpy(destination + copied, data_ + position_, remaining);
      position_ += remaining;
    }
    return true;
  }

 private:
  const unsigned char* data_;
  std::size_t size_;
  std::size_t position_ = 0;
  std::uint32_t bits_ = 0;
  int bit_count_ = 0;
};

struct DeflateHuffman {
  std::array<std::uint16_t, 16> counts{};
  std::vector<std::uint16_t> symbols;

  bool Build(const std::uint8_t* lengths, std::size_t count) {
    counts.fill(0);
    for (std::size_t i = 0; i < count; ++i) {
      if (lengths[i] > 15) {
        return false;
      }
      ++counts[lengths[i]];
    }

    std::size_t total = 0;
    for (int length = 1; length <= 15; ++length) {
      total += counts[length];
    }
    symbols.assign(total, 0);
    if (total == 0) {
      return true;
    }

    int left = 1;
    for (int length = 1; length <= 15; ++length) {
      left <<= 1;
      left -= static_cast<int>(counts[length]);
      if (left < 0) {
        return false;  // over-subscribed code set
      }
    }

    std::array<std::size_t, 16> offsets{};
    offsets[1] = 0;
    for (int length = 2; length <= 15; ++length) {
      offsets[length] = offsets[length - 1] + counts[length - 1];
    }
    for (std::size_t symbol = 0; symbol < count; ++symbol) {
      const std::uint8_t length = lengths[symbol];
      if (length != 0) {
        symbols[offsets[length]++] = static_cast<std::uint16_t>(symbol);
      }
    }
    return true;
  }

  bool Decode(DeflateBitReader& reader, int& symbol) const {
    int code = 0;
    int first = 0;
    std::size_t index = 0;
    for (int length = 1; length <= 15; ++length) {
      std::uint32_t bit = 0;
      if (!reader.ReadBits(1, bit)) {
        return false;
      }
      code |= static_cast<int>(bit);
      const int count = static_cast<int>(counts[length]);
      if (code - count < first) {
        const std::size_t offset = index + static_cast<std::size_t>(code - first);
        if (offset >= symbols.size()) {
          return false;
        }
        symbol = symbols[offset];
        return true;
      }
      index += static_cast<std::size_t>(count);
      first = (first + count) << 1;
      code <<= 1;
    }
    return false;
  }
};

bool BuildFixedTrees(DeflateHuffman& literal, DeflateHuffman& distance) {
  std::array<std::uint8_t, 288> literal_lengths{};
  for (int i = 0; i <= 143; ++i) {
    literal_lengths[i] = 8;
  }
  for (int i = 144; i <= 255; ++i) {
    literal_lengths[i] = 9;
  }
  for (int i = 256; i <= 279; ++i) {
    literal_lengths[i] = 7;
  }
  for (int i = 280; i <= 287; ++i) {
    literal_lengths[i] = 8;
  }
  std::array<std::uint8_t, 32> distance_lengths{};
  distance_lengths.fill(5);
  return literal.Build(literal_lengths.data(), literal_lengths.size()) &&
         distance.Build(distance_lengths.data(), distance_lengths.size());
}

bool ReadDynamicTrees(DeflateBitReader& reader, DeflateHuffman& literal,
                      DeflateHuffman& distance) {
  std::uint32_t literal_bits = 0;
  std::uint32_t distance_bits = 0;
  std::uint32_t code_length_bits = 0;
  if (!reader.ReadBits(5, literal_bits) || !reader.ReadBits(5, distance_bits) ||
      !reader.ReadBits(4, code_length_bits)) {
    return false;
  }
  const std::size_t literal_count = 257 + literal_bits;
  const std::size_t distance_count = 1 + distance_bits;
  const std::size_t code_length_count = 4 + code_length_bits;

  std::array<std::uint8_t, 19> code_lengths{};
  for (std::size_t i = 0; i < code_length_count; ++i) {
    std::uint32_t value = 0;
    if (!reader.ReadBits(3, value)) {
      return false;
    }
    code_lengths[kCodeLengthOrder[i]] = static_cast<std::uint8_t>(value);
  }

  DeflateHuffman code_length_tree;
  if (!code_length_tree.Build(code_lengths.data(), code_lengths.size())) {
    return false;
  }

  std::vector<std::uint8_t> lengths(literal_count + distance_count, 0);
  std::size_t index = 0;
  while (index < lengths.size()) {
    int symbol = 0;
    if (!code_length_tree.Decode(reader, symbol)) {
      return false;
    }
    if (symbol >= 0 && symbol <= 15) {
      lengths[index++] = static_cast<std::uint8_t>(symbol);
      continue;
    }

    std::size_t repeat = 0;
    std::uint8_t value = 0;
    if (symbol == 16) {
      if (index == 0) {
        return false;
      }
      value = lengths[index - 1];
      std::uint32_t extra = 0;
      if (!reader.ReadBits(2, extra)) {
        return false;
      }
      repeat = 3 + extra;
    } else if (symbol == 17) {
      std::uint32_t extra = 0;
      if (!reader.ReadBits(3, extra)) {
        return false;
      }
      repeat = 3 + extra;
    } else if (symbol == 18) {
      std::uint32_t extra = 0;
      if (!reader.ReadBits(7, extra)) {
        return false;
      }
      repeat = 11 + extra;
    } else {
      return false;
    }
    if (index + repeat > lengths.size()) {
      return false;
    }
    for (std::size_t i = 0; i < repeat; ++i) {
      lengths[index++] = value;
    }
  }

  return literal.Build(lengths.data(), literal_count) &&
         distance.Build(lengths.data() + literal_count, distance_count);
}

bool InflateCodes(DeflateBitReader& reader, const DeflateHuffman& literal,
                  const DeflateHuffman& distance, unsigned char* output,
                  std::size_t output_size, std::size_t& position) {
  for (;;) {
    int symbol = 0;
    if (!literal.Decode(reader, symbol)) {
      return false;
    }
    if (symbol < 256) {
      if (position >= output_size) {
        return false;
      }
      output[position++] = static_cast<unsigned char>(symbol);
      continue;
    }
    if (symbol == 256) {
      return true;  // end of block
    }

    const int length_code = symbol - 257;
    if (length_code < 0 || length_code >= 29) {
      return false;
    }
    std::uint32_t extra = 0;
    if (kLengthExtra[length_code] != 0 &&
        !reader.ReadBits(kLengthExtra[length_code], extra)) {
      return false;
    }
    const std::size_t length =
        static_cast<std::size_t>(kLengthBase[length_code]) + static_cast<std::size_t>(extra);

    int distance_symbol = 0;
    if (!distance.Decode(reader, distance_symbol)) {
      return false;
    }
    if (distance_symbol < 0 || distance_symbol >= 30) {
      return false;
    }
    extra = 0;
    if (kDistanceExtra[distance_symbol] != 0 &&
        !reader.ReadBits(kDistanceExtra[distance_symbol], extra)) {
      return false;
    }
    const std::size_t distance_value =
        static_cast<std::size_t>(kDistanceBase[distance_symbol]) + static_cast<std::size_t>(extra);
    if (distance_value == 0 || distance_value > position) {
      return false;
    }
    if (length > output_size - position) {
      return false;
    }
    for (std::size_t i = 0; i < length; ++i) {
      output[position] = output[position - distance_value];
      ++position;
    }
  }
}

bool InflateStoredBlock(DeflateBitReader& reader, unsigned char* output,
                        std::size_t output_size, std::size_t& position) {
  reader.AlignToByte();
  unsigned char header[4] = {};
  if (!reader.ReadBytes(header, sizeof(header))) {
    return false;
  }
  const std::size_t length = static_cast<std::size_t>(header[0]) |
                             (static_cast<std::size_t>(header[1]) << 8);
  const std::size_t complement = static_cast<std::size_t>(header[2]) |
                                 (static_cast<std::size_t>(header[3]) << 8);
  if ((length ^ 0xFFFFu) != complement) {
    return false;
  }
  if (position > output_size || length > output_size - position) {
    return false;
  }
  if (length > 0 && !reader.ReadBytes(output + position, length)) {
    return false;
  }
  position += length;
  return true;
}

bool InflateRaw(const unsigned char* data, std::size_t size, unsigned char* output,
                std::size_t output_size, std::size_t& produced) {
  produced = 0;
  DeflateBitReader reader(data, size);
  std::size_t position = 0;
  bool last_block = false;
  while (!last_block) {
    std::uint32_t final_block = 0;
    std::uint32_t block_type = 0;
    if (!reader.ReadBits(1, final_block) || !reader.ReadBits(2, block_type)) {
      return false;
    }
    last_block = final_block != 0;

    if (block_type == 0) {
      if (!InflateStoredBlock(reader, output, output_size, position)) {
        return false;
      }
    } else if (block_type == 1) {
      DeflateHuffman literal;
      DeflateHuffman distance;
      if (!BuildFixedTrees(literal, distance) ||
          !InflateCodes(reader, literal, distance, output, output_size, position)) {
        return false;
      }
    } else if (block_type == 2) {
      DeflateHuffman literal;
      DeflateHuffman distance;
      if (!ReadDynamicTrees(reader, literal, distance) ||
          !InflateCodes(reader, literal, distance, output, output_size, position)) {
        return false;
      }
    } else {
      return false;
    }
  }
  produced = position;
  return true;
}

int DecompressWithNtdll(const std::vector<unsigned char>& compressed,
                        std::vector<unsigned char>& out) {
  ULONG buffer_workspace = 0;
  ULONG fragment_workspace = 0;
  LONG status = RtlGetCompressionWorkSpaceSize(kCompressionFormatDeflate, &buffer_workspace,
                                               &fragment_workspace);
  if (status < 0) {
    return MCDL_ERR_INTERNAL;
  }
  std::vector<unsigned char> workspace(fragment_workspace == 0 ? 1 : fragment_workspace);
  ULONG final_size = 0;
  status = RtlDecompressBufferEx(kCompressionFormatDeflate, out.data(),
                                 static_cast<ULONG>(out.size()),
                                 const_cast<PUCHAR>(compressed.data()),
                                 static_cast<ULONG>(compressed.size()), &final_size,
                                 workspace.data());
  if (status < 0) {
    return MCDL_ERR_FORMAT;
  }
  if (static_cast<std::size_t>(final_size) != out.size()) {
    return MCDL_ERR_FORMAT;
  }
  return MCDL_OK;
}

int DecompressDeflate(const std::vector<unsigned char>& compressed,
                      std::vector<unsigned char>& out) {
  if (DecompressWithNtdll(compressed, out) == MCDL_OK) {
    return MCDL_OK;
  }
  std::size_t produced = 0;
  if (!InflateRaw(compressed.data(), compressed.size(), out.data(), out.size(), produced) ||
      produced != out.size()) {
    return MCDL_ERR_FORMAT;
  }
  return MCDL_OK;
}

// ---------------------------------------------------------------------------
// ZIP parsing
// ---------------------------------------------------------------------------

struct ZipEntry {
  std::string name;
  std::uint16_t flags = 0;
  std::uint16_t method = 0;
  std::uint64_t compressed_size = 0;
  std::uint64_t uncompressed_size = 0;
  std::uint32_t local_offset = 0;
};

struct ZipDirectory {
  std::vector<ZipEntry> entries;
};

std::uint16_t ReadU16(const unsigned char* p) {
  return static_cast<std::uint16_t>(static_cast<std::uint16_t>(p[0]) |
                                    (static_cast<std::uint16_t>(p[1]) << 8));
}

std::uint32_t ReadU32(const unsigned char* p) {
  return static_cast<std::uint32_t>(p[0]) |
         (static_cast<std::uint32_t>(p[1]) << 8) |
         (static_cast<std::uint32_t>(p[2]) << 16) |
         (static_cast<std::uint32_t>(p[3]) << 24);
}

int LoadZipDirectory(FileReader& reader, ZipDirectory& directory) {
  const std::uint64_t file_size = reader.size();
  if (file_size < 22) {
    return MCDL_ERR_FORMAT;
  }

  const std::size_t tail_length = static_cast<std::size_t>(
      std::min<std::uint64_t>(file_size, 22u + 65535u));
  std::vector<unsigned char> tail(tail_length);
  int rc = reader.ReadAt(file_size - tail_length, tail.data(), tail.size());
  if (rc != MCDL_OK) {
    return rc;
  }

  // Find the EOCD, preferring a record whose comment length exactly consumes the
  // remainder of the file; fall back to the last structurally plausible record.
  std::uint64_t eocd_offset = 0;
  bool found = false;
  for (std::size_t i = tail_length - 22 + 1; i-- > 0;) {
    if (ReadU32(&tail[i]) != kEocdSignature) {
      continue;
    }
    const std::uint16_t comment_length = ReadU16(&tail[i + 20]);
    const std::uint64_t absolute = file_size - tail_length + i;
    if (absolute + 22u + comment_length == file_size) {
      eocd_offset = absolute;
      found = true;
      break;
    }
  }
  if (!found) {
    for (std::size_t i = tail_length - 22 + 1; i-- > 0;) {
      if (ReadU32(&tail[i]) != kEocdSignature) {
        continue;
      }
      const std::uint16_t comment_length = ReadU16(&tail[i + 20]);
      const std::uint64_t absolute = file_size - tail_length + i;
      if (absolute + 22u + comment_length <= file_size) {
        eocd_offset = absolute;
        found = true;
        break;
      }
    }
  }
  if (!found) {
    return MCDL_ERR_FORMAT;
  }

  unsigned char eocd[22] = {};
  rc = reader.ReadAt(eocd_offset, eocd, sizeof(eocd));
  if (rc != MCDL_OK) {
    return rc;
  }

  const std::uint16_t disk_number = ReadU16(&eocd[4]);
  const std::uint16_t central_disk = ReadU16(&eocd[6]);
  const std::uint16_t entries_this_disk = ReadU16(&eocd[8]);
  const std::uint16_t total_entries = ReadU16(&eocd[10]);
  const std::uint32_t central_size = ReadU32(&eocd[12]);
  const std::uint32_t central_offset = ReadU32(&eocd[16]);

  // ZIP64 is out of scope: reject it explicitly rather than mis-parsing.
  if (total_entries == kZip64Marker16 || entries_this_disk == kZip64Marker16 ||
      central_size == kZip64Marker32 || central_offset == kZip64Marker32) {
    return MCDL_ERR_FORMAT;
  }
  if (disk_number != 0 || central_disk != 0 || entries_this_disk != total_entries) {
    return MCDL_ERR_FORMAT;
  }
  if (static_cast<std::uint64_t>(central_offset) + central_size > file_size) {
    return MCDL_ERR_FORMAT;
  }
  if (total_entries == 0) {
    return MCDL_OK;
  }
  if (central_size == 0) {
    return MCDL_ERR_FORMAT;
  }

  std::vector<unsigned char> central(central_size);
  rc = reader.ReadAt(central_offset, central.data(), central.size());
  if (rc != MCDL_OK) {
    return rc;
  }

  std::size_t position = 0;
  directory.entries.reserve(total_entries);
  for (std::uint16_t index = 0; index < total_entries; ++index) {
    if (position + 46 > central.size()) {
      return MCDL_ERR_FORMAT;
    }
    if (ReadU32(&central[position]) != kCentralSignature) {
      return MCDL_ERR_FORMAT;
    }
    const std::uint16_t flags = ReadU16(&central[position + 8]);
    const std::uint16_t method = ReadU16(&central[position + 10]);
    const std::uint32_t compressed_size = ReadU32(&central[position + 20]);
    const std::uint32_t uncompressed_size = ReadU32(&central[position + 24]);
    const std::uint16_t name_length = ReadU16(&central[position + 28]);
    const std::uint16_t extra_length = ReadU16(&central[position + 30]);
    const std::uint16_t comment_length = ReadU16(&central[position + 32]);
    const std::uint16_t disk_start = ReadU16(&central[position + 34]);
    const std::uint32_t local_offset = ReadU32(&central[position + 42]);

    const std::size_t record_length = 46u + static_cast<std::size_t>(name_length) +
                                      static_cast<std::size_t>(extra_length) +
                                      static_cast<std::size_t>(comment_length);
    if (position + record_length > central.size()) {
      return MCDL_ERR_FORMAT;
    }
    if (name_length == 0) {
      return MCDL_ERR_FORMAT;
    }
    if (compressed_size == kZip64Marker32 || uncompressed_size == kZip64Marker32 ||
        local_offset == kZip64Marker32 || disk_start == kZip64Marker16) {
      return MCDL_ERR_FORMAT;
    }

    ZipEntry entry;
    entry.name.assign(reinterpret_cast<const char*>(&central[position + 46]), name_length);
    entry.flags = flags;
    entry.method = method;
    entry.compressed_size = compressed_size;
    entry.uncompressed_size = uncompressed_size;
    entry.local_offset = local_offset;
    directory.entries.push_back(std::move(entry));

    position += record_length;
  }
  return MCDL_OK;
}

const ZipEntry* FindEntry(const ZipDirectory& directory, const char* name) {
  for (const ZipEntry& entry : directory.entries) {
    if (entry.name == name) {
      return &entry;
    }
  }
  return nullptr;
}

int ReadEntryData(FileReader& reader, const ZipEntry& entry, std::vector<unsigned char>& out) {
  if ((entry.flags & 0x0001u) != 0) {
    return MCDL_ERR_FORMAT;  // encrypted
  }
  if (static_cast<std::uint64_t>(entry.local_offset) + 30u > reader.size()) {
    return MCDL_ERR_FORMAT;
  }

  unsigned char local_header[30] = {};
  int rc = reader.ReadAt(entry.local_offset, local_header, sizeof(local_header));
  if (rc != MCDL_OK) {
    return rc;
  }
  if (ReadU32(local_header) != kLocalSignature) {
    return MCDL_ERR_FORMAT;
  }
  const std::uint16_t name_length = ReadU16(&local_header[26]);
  const std::uint16_t extra_length = ReadU16(&local_header[28]);
  const std::uint64_t data_offset = static_cast<std::uint64_t>(entry.local_offset) + 30u +
                                    name_length + extra_length;
  if (data_offset > reader.size() || entry.compressed_size > reader.size() - data_offset) {
    return MCDL_ERR_FORMAT;
  }

  if (entry.method == 0) {  // stored
    if (entry.compressed_size != entry.uncompressed_size) {
      return MCDL_ERR_FORMAT;
    }
    if (entry.uncompressed_size > kMaxZipEntrySize) {
      return MCDL_ERR_FORMAT;
    }
    out.resize(static_cast<std::size_t>(entry.uncompressed_size));
    if (!out.empty()) {
      rc = reader.ReadAt(data_offset, out.data(), out.size());
      if (rc != MCDL_OK) {
        return rc;
      }
    }
    return MCDL_OK;
  }

  if (entry.method != 8) {  // only deflate is supported
    return MCDL_ERR_FORMAT;
  }
  if (entry.compressed_size > kMaxZipEntrySize || entry.uncompressed_size > kMaxZipEntrySize) {
    return MCDL_ERR_FORMAT;
  }

  out.resize(static_cast<std::size_t>(entry.uncompressed_size));
  if (out.empty()) {
    return MCDL_OK;  // empty entry; some writers emit a 2-byte empty deflate stream
  }
  if (entry.compressed_size == 0) {
    return MCDL_ERR_FORMAT;
  }

  std::vector<unsigned char> compressed(static_cast<std::size_t>(entry.compressed_size));
  rc = reader.ReadAt(data_offset, compressed.data(), compressed.size());
  if (rc != MCDL_OK) {
    return rc;
  }
  return DecompressDeflate(compressed, out);
}

bool StartsWithNoCaseAscii(const std::string& text, const std::string& prefix) {
  if (prefix.empty() || prefix.size() > text.size()) {
    return false;
  }
  for (std::size_t i = 0; i < prefix.size(); ++i) {
    unsigned char left = static_cast<unsigned char>(text[i]);
    unsigned char right = static_cast<unsigned char>(prefix[i]);
    if (left >= 'A' && left <= 'Z') {
      left = static_cast<unsigned char>(left - 'A' + 'a');
    }
    if (right >= 'A' && right <= 'Z') {
      right = static_cast<unsigned char>(right - 'A' + 'a');
    }
    if (left != right) {
      return false;
    }
  }
  return true;
}

// Rejects zip-slip names: absolute paths, drive letters, "..", ADS/colons and
// segments whose trailing dots/spaces Windows would silently strip.
bool IsSafeEntryName(const std::string& name) {
  if (name.empty() || name.find('\0') != std::string::npos) {
    return false;
  }
  if (name.front() == '/' || name.front() == '\\') {
    return false;
  }
  if (name.size() >= 2 && std::isalpha(static_cast<unsigned char>(name[0])) != 0 &&
      name[1] == ':') {
    return false;
  }

  std::size_t segment_start = 0;
  for (;;) {
    const std::size_t segment_end = name.find_first_of("/\\", segment_start);
    const std::size_t end = (segment_end == std::string::npos) ? name.size() : segment_end;
    const std::string_view segment(name.data() + segment_start, end - segment_start);
    if (segment == "..") {
      return false;
    }
    if (segment.find(':') != std::string_view::npos) {
      return false;
    }
    if (!segment.empty()) {
      const char last = segment.back();
      if (last == '.' || last == ' ') {
        return false;
      }
    }
    if (segment_end == std::string::npos) {
      break;
    }
    segment_start = segment_end + 1;
  }
  return true;
}

bool IsExcluded(const std::string& name, const char* const* prefixes, std::size_t count) {
  for (std::size_t i = 0; i < count; ++i) {
    if (StartsWithNoCaseAscii(name, prefixes[i])) {
      return true;
    }
  }
  return false;
}

int Utf8ToWide(const std::string& text, std::wstring& out) {
  if (text.empty()) {
    out.clear();
    return MCDL_OK;
  }
  if (text.size() > static_cast<std::size_t>(INT_MAX)) {
    return MCDL_ERR_FORMAT;
  }
  const int length = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(),
                                         static_cast<int>(text.size()), nullptr, 0);
  if (length <= 0) {
    return MCDL_ERR_FORMAT;
  }
  out.resize(static_cast<std::size_t>(length));
  const int written = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(),
                                          static_cast<int>(text.size()), out.data(), length);
  if (written != length) {
    out.clear();
    return MCDL_ERR_FORMAT;
  }
  return MCDL_OK;
}

std::wstring GetFullPath(const std::wstring& path) {
  if (path.empty()) {
    return path;
  }
  const DWORD required = GetFullPathNameW(path.c_str(), 0, nullptr, nullptr);
  if (required == 0) {
    return path;
  }
  std::wstring buffer(required, L'\0');
  const DWORD written = GetFullPathNameW(path.c_str(), required, buffer.data(), nullptr);
  if (written == 0 || written >= required) {
    return path;
  }
  buffer.resize(written);
  return buffer;
}

std::wstring ParentDirectory(const std::wstring& path) {
  const std::size_t slash = path.find_last_of(L"\\/");
  if (slash == std::wstring::npos) {
    return std::wstring();
  }
  if (slash == 0) {
    return path.substr(0, 1);
  }
  if (slash == 2 && path.size() >= 3 && path[1] == L':') {
    return path.substr(0, 3);  // "D:\file" -> "D:\"
  }
  return path.substr(0, slash);
}

int CreateDirectoryTree(std::wstring directory) {
  if (directory.empty()) {
    return MCDL_OK;
  }
  for (wchar_t& ch : directory) {
    if (ch == L'/') {
      ch = L'\\';
    }
  }
  while (directory.size() > 3 && directory.back() == L'\\') {
    directory.pop_back();
  }

  DWORD attributes = GetFileAttributesW(directory.c_str());
  if (attributes != INVALID_FILE_ATTRIBUTES) {
    return (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0 ? MCDL_OK : MCDL_ERR_IO;
  }

  std::size_t root_length = 0;
  if (directory.size() >= 3 && std::iswalpha(directory[0]) != 0 &&
      directory[1] == L':' && directory[2] == L'\\') {
    root_length = 3;
  } else if (directory.size() >= 2 && directory[0] == L'\\' && directory[1] == L'\\') {
    const std::size_t server = directory.find(L'\\', 2);
    if (server == std::wstring::npos) {
      root_length = directory.size();
    } else {
      const std::size_t share = directory.find(L'\\', server + 1);
      root_length = (share == std::wstring::npos) ? directory.size() : share + 1;
    }
  }

  for (std::size_t i = root_length; i <= directory.size(); ++i) {
    if (i != directory.size() && directory[i] != L'\\') {
      continue;
    }
    if (i > root_length) {
      const std::wstring prefix = directory.substr(0, i);
      if (!CreateDirectoryW(prefix.c_str(), nullptr)) {
        const DWORD error = GetLastError();
        if (error != ERROR_ALREADY_EXISTS) {
          const DWORD existing = GetFileAttributesW(prefix.c_str());
          if (existing == INVALID_FILE_ATTRIBUTES ||
              (existing & FILE_ATTRIBUTE_DIRECTORY) == 0) {
            return MapWin32Error(error);
          }
        }
      }
    }
  }

  attributes = GetFileAttributesW(directory.c_str());
  if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0) {
    return MCDL_ERR_IO;
  }
  return MCDL_OK;
}

int PrepareDestinationRoot(const std::wstring& destination, std::wstring& root) {
  if (destination.empty()) {
    return MCDL_ERR_ARG;
  }
  root = GetFullPath(destination);
  for (wchar_t& ch : root) {
    if (ch == L'/') {
      ch = L'\\';
    }
  }
  while (root.size() > 3 && root.back() == L'\\') {
    root.pop_back();
  }
  const int rc = CreateDirectoryTree(root);
  if (rc != MCDL_OK) {
    return rc;
  }
  if (!root.empty() && root.back() != L'\\') {
    root.push_back(L'\\');
  }
  return MCDL_OK;
}

int WriteFileAll(const std::wstring& path, const std::vector<unsigned char>& data) {
  WinHandle file(CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
                             FILE_ATTRIBUTE_NORMAL, nullptr));
  if (!file.valid()) {
    return MapWin32Error(GetLastError());
  }

  const unsigned char* cursor = data.data();
  std::size_t remaining = data.size();
  while (remaining > 0) {
    const DWORD request = static_cast<DWORD>(std::min<std::size_t>(remaining, 1u << 30));
    DWORD written = 0;
    if (!WriteFile(file.get(), cursor, request, &written, nullptr) || written != request) {
      const DWORD error = GetLastError();
      file.Reset();
      DeleteFileW(path.c_str());
      return MapWin32Error(error == ERROR_SUCCESS ? ERROR_WRITE_FAULT : error);
    }
    cursor += request;
    remaining -= request;
  }
  return MCDL_OK;
}

int ZipEntrySizeImpl(const wchar_t* zip_path, const char* entry_name_utf8,
                     unsigned long long* out_size) {
  FileReader reader;
  int rc = reader.Open(zip_path);
  if (rc != MCDL_OK) {
    return rc;
  }
  ZipDirectory directory;
  rc = LoadZipDirectory(reader, directory);
  if (rc != MCDL_OK) {
    return rc;
  }
  const ZipEntry* entry = FindEntry(directory, entry_name_utf8);
  if (entry == nullptr) {
    return 0;
  }
  *out_size = entry->uncompressed_size;
  return 1;
}

int ZipReadImpl(const wchar_t* zip_path, const char* entry_name_utf8, void* buffer,
                std::size_t buffer_size, std::size_t* out_written) {
  FileReader reader;
  int rc = reader.Open(zip_path);
  if (rc != MCDL_OK) {
    return rc;
  }
  ZipDirectory directory;
  rc = LoadZipDirectory(reader, directory);
  if (rc != MCDL_OK) {
    return rc;
  }
  const ZipEntry* entry = FindEntry(directory, entry_name_utf8);
  if (entry == nullptr) {
    return MCDL_ERR_NOTFOUND;
  }
  if (entry->uncompressed_size > buffer_size) {
    return MCDL_ERR_BUFFER;
  }

  std::vector<unsigned char> data;
  rc = ReadEntryData(reader, *entry, data);
  if (rc != MCDL_OK) {
    return rc;
  }
  if (data.size() > buffer_size) {
    return MCDL_ERR_BUFFER;
  }
  if (!data.empty()) {
    std::memcpy(buffer, data.data(), data.size());
  }
  *out_written = data.size();
  return MCDL_OK;
}

int ZipExtractImpl(const wchar_t* zip_path, const char* entry_name_utf8,
                   const wchar_t* dest_path) {
  FileReader reader;
  int rc = reader.Open(zip_path);
  if (rc != MCDL_OK) {
    return rc;
  }
  ZipDirectory directory;
  rc = LoadZipDirectory(reader, directory);
  if (rc != MCDL_OK) {
    return rc;
  }
  const ZipEntry* entry = FindEntry(directory, entry_name_utf8);
  if (entry == nullptr) {
    return MCDL_ERR_NOTFOUND;
  }
  if (!entry->name.empty() &&
      (entry->name.back() == '/' || entry->name.back() == '\\')) {
    return MCDL_ERR_FORMAT;
  }

  std::vector<unsigned char> data;
  rc = ReadEntryData(reader, *entry, data);
  if (rc != MCDL_OK) {
    return rc;
  }

  std::wstring full_path = GetFullPath(std::wstring(dest_path));
  const std::wstring parent = ParentDirectory(full_path);
  if (!parent.empty()) {
    rc = CreateDirectoryTree(parent);
    if (rc != MCDL_OK) {
      return rc;
    }
  }
  return WriteFileAll(full_path, data);
}

int ZipExtractAllImpl(const wchar_t* zip_path, const wchar_t* dest_dir,
                      const char* const* exclude_prefixes, std::size_t exclude_count) {
  FileReader reader;
  int rc = reader.Open(zip_path);
  if (rc != MCDL_OK) {
    return rc;
  }
  ZipDirectory directory;
  rc = LoadZipDirectory(reader, directory);
  if (rc != MCDL_OK) {
    return rc;
  }

  std::wstring root;
  rc = PrepareDestinationRoot(std::wstring(dest_dir), root);
  if (rc != MCDL_OK) {
    return rc;
  }

  int extracted = 0;
  for (const ZipEntry& entry : directory.entries) {
    if (!IsSafeEntryName(entry.name)) {
      return MCDL_ERR_FORMAT;
    }
    if (entry.name.back() == '/' || entry.name.back() == '\\') {
      continue;  // directory entry
    }
    if (IsExcluded(entry.name, exclude_prefixes, exclude_count)) {
      continue;
    }

    std::wstring relative;
    rc = Utf8ToWide(entry.name, relative);
    if (rc != MCDL_OK) {
      return rc;
    }
    for (wchar_t& ch : relative) {
      if (ch == L'/') {
        ch = L'\\';
      }
    }
    if (relative.empty()) {
      return MCDL_ERR_FORMAT;
    }

    const std::wstring target = root + relative;
    if (_wcsnicmp(target.c_str(), root.c_str(), root.size()) != 0) {
      return MCDL_ERR_FORMAT;
    }

    std::vector<unsigned char> data;
    rc = ReadEntryData(reader, entry, data);
    if (rc != MCDL_OK) {
      return rc;
    }

    const std::wstring parent = ParentDirectory(target);
    if (!parent.empty()) {
      rc = CreateDirectoryTree(parent);
      if (rc != MCDL_OK) {
        return rc;
      }
    }
    rc = WriteFileAll(target, data);
    if (rc != MCDL_OK) {
      return rc;
    }
    ++extracted;
  }
  return extracted;
}

int DiskFreeImpl(const wchar_t* path, unsigned long long* out_free_bytes) {
  ULARGE_INTEGER free_bytes{};
  if (!GetDiskFreeSpaceExW(path, nullptr, nullptr, &free_bytes)) {
    return MapWin32Error(GetLastError());
  }
  *out_free_bytes = free_bytes.QuadPart;
  return MCDL_OK;
}

}  // namespace

// ---------------------------------------------------------------------------
// Exported C ABI
// ---------------------------------------------------------------------------

extern "C" MCDL_EXPORT const char* MCDL_CALL mcdl_version(void) {
  try {
    return "mcdnative 1.0.0 (msvc)";
  } catch (...) {
    return "mcdnative";
  }
}

extern "C" MCDL_EXPORT int MCDL_CALL mcdl_sha1_file(const wchar_t* path, char out_hex[41]) {
  if (out_hex != nullptr) {
    out_hex[0] = '\0';
  }
  if (path == nullptr || out_hex == nullptr) {
    return MCDL_ERR_ARG;
  }
  try {
    return Sha1FileImpl(path, out_hex);
  } catch (const std::bad_alloc&) {
    out_hex[0] = '\0';
    return MCDL_ERR_MEMORY;
  } catch (...) {
    out_hex[0] = '\0';
    return MCDL_ERR_INTERNAL;
  }
}

extern "C" MCDL_EXPORT int MCDL_CALL mcdl_sha1_bytes(const void* data, size_t len,
                                                     char out_hex[41]) {
  if (out_hex != nullptr) {
    out_hex[0] = '\0';
  }
  if (out_hex == nullptr || (data == nullptr && len != 0)) {
    return MCDL_ERR_ARG;
  }
  try {
    return Sha1BytesImpl(data, len, out_hex);
  } catch (const std::bad_alloc&) {
    out_hex[0] = '\0';
    return MCDL_ERR_MEMORY;
  } catch (...) {
    out_hex[0] = '\0';
    return MCDL_ERR_INTERNAL;
  }
}

extern "C" MCDL_EXPORT int MCDL_CALL mcdl_crc32_file(const wchar_t* path,
                                                     unsigned int* out_crc) {
  if (out_crc != nullptr) {
    *out_crc = 0;
  }
  if (path == nullptr || out_crc == nullptr) {
    return MCDL_ERR_ARG;
  }
  try {
    return Crc32FileImpl(path, out_crc);
  } catch (const std::bad_alloc&) {
    *out_crc = 0;
    return MCDL_ERR_MEMORY;
  } catch (...) {
    *out_crc = 0;
    return MCDL_ERR_INTERNAL;
  }
}

extern "C" MCDL_EXPORT int MCDL_CALL mcdl_zip_entry_size(const wchar_t* zip_path,
                                                         const char* entry_name_utf8,
                                                         unsigned long long* out_size) {
  if (out_size != nullptr) {
    *out_size = 0;
  }
  if (zip_path == nullptr || entry_name_utf8 == nullptr || out_size == nullptr) {
    return MCDL_ERR_ARG;
  }
  try {
    return ZipEntrySizeImpl(zip_path, entry_name_utf8, out_size);
  } catch (const std::bad_alloc&) {
    *out_size = 0;
    return MCDL_ERR_MEMORY;
  } catch (...) {
    *out_size = 0;
    return MCDL_ERR_INTERNAL;
  }
}

extern "C" MCDL_EXPORT int MCDL_CALL mcdl_zip_read(const wchar_t* zip_path,
                                                   const char* entry_name_utf8,
                                                   void* buffer, size_t buffer_size,
                                                   size_t* out_written) {
  if (out_written != nullptr) {
    *out_written = 0;
  }
  if (zip_path == nullptr || entry_name_utf8 == nullptr || buffer == nullptr ||
      out_written == nullptr) {
    return MCDL_ERR_ARG;
  }
  try {
    return ZipReadImpl(zip_path, entry_name_utf8, buffer, buffer_size, out_written);
  } catch (const std::bad_alloc&) {
    *out_written = 0;
    return MCDL_ERR_MEMORY;
  } catch (...) {
    *out_written = 0;
    return MCDL_ERR_INTERNAL;
  }
}

extern "C" MCDL_EXPORT int MCDL_CALL mcdl_zip_extract(const wchar_t* zip_path,
                                                      const char* entry_name_utf8,
                                                      const wchar_t* dest_path) {
  if (zip_path == nullptr || entry_name_utf8 == nullptr || dest_path == nullptr ||
      dest_path[0] == L'\0') {
    return MCDL_ERR_ARG;
  }
  try {
    return ZipExtractImpl(zip_path, entry_name_utf8, dest_path);
  } catch (const std::bad_alloc&) {
    return MCDL_ERR_MEMORY;
  } catch (...) {
    return MCDL_ERR_INTERNAL;
  }
}

extern "C" MCDL_EXPORT int MCDL_CALL mcdl_zip_extract_all(
    const wchar_t* zip_path, const wchar_t* dest_dir, const char* const* exclude_prefixes,
    size_t exclude_count) {
  if (zip_path == nullptr || dest_dir == nullptr || dest_dir[0] == L'\0') {
    return MCDL_ERR_ARG;
  }
  if (exclude_count > 0 && exclude_prefixes == nullptr) {
    return MCDL_ERR_ARG;
  }
  for (size_t i = 0; i < exclude_count; ++i) {
    if (exclude_prefixes[i] == nullptr) {
      return MCDL_ERR_ARG;
    }
  }
  try {
    return ZipExtractAllImpl(zip_path, dest_dir, exclude_prefixes, exclude_count);
  } catch (const std::bad_alloc&) {
    return MCDL_ERR_MEMORY;
  } catch (...) {
    return MCDL_ERR_INTERNAL;
  }
}

extern "C" MCDL_EXPORT int MCDL_CALL mcdl_disk_free(const wchar_t* path,
                                                    unsigned long long* out_free_bytes) {
  if (out_free_bytes != nullptr) {
    *out_free_bytes = 0;
  }
  if (path == nullptr || path[0] == L'\0' || out_free_bytes == nullptr) {
    return MCDL_ERR_ARG;
  }
  try {
    return DiskFreeImpl(path, out_free_bytes);
  } catch (const std::bad_alloc&) {
    *out_free_bytes = 0;
    return MCDL_ERR_MEMORY;
  } catch (...) {
    *out_free_bytes = 0;
    return MCDL_ERR_INTERNAL;
  }
}
