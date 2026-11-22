// The part of the liblzo2 API that libarchive's lzop filters use, implemented on
// lzokay (https://github.com/AxioDL/lzokay, MIT licence) so that no GPL code is
// linked into the native libraries. See include/lzo/lzoconf.h.
//
// This file and lzokay are built by build_lzo_shim in ../build-lzo-shim.sh into a
// liblzo2.a that the native libraries link statically. The Linux libraries are
// linked against their C library alone, with no C++ runtime, so nothing here may need
// one: no exceptions, no RTTI, no allocation, no static objects, and only header-only
// parts of the C++ standard library. The build verifies the result.

#include <lzo/lzo1x.h>

#include <cstddef>
#include <cstdint>
#include <new>

#include "lzokay.hpp"

namespace {

// lzokay's compressor keeps its match-finder state in a dictionary that its own
// Dict<> allocates with operator new, about 444 KiB per construction. liblzo2
// callers already hand every compress call a block of work memory, so this
// dictionary lives there instead and the shim never allocates.
class WorkMemoryDict final : public lzokay::DictBase {
public:
    static constexpr std::size_t kSize = sizeof(storage_type);
    static constexpr std::size_t kAlignment = alignof(storage_type);

    // The storage is plain arrays: starting its lifetime runs no code, and lzokay
    // initialises the parts it reads at the start of every compress call.
    explicit WorkMemoryDict(void* memory) noexcept { _storage = ::new (memory) storage_type; }
};

// The work memory sizes in lzo1x.h are literals because C callers need them. This
// is what keeps them honest if the pinned lzokay ever changes its dictionary. The
// alignment term covers callers whose work memory is not aligned for it.
static_assert((WorkMemoryDict::kAlignment & (WorkMemoryDict::kAlignment - 1)) == 0,
              "the dictionary alignment must be a power of two");
static_assert(WorkMemoryDict::kSize + WorkMemoryDict::kAlignment - 1 <= LZO1X_1_MEM_COMPRESS,
              "LZO1X_1_MEM_COMPRESS in lzo1x.h is too small for lzokay's dictionary");
static_assert(WorkMemoryDict::kSize + WorkMemoryDict::kAlignment - 1 <= LZO1X_1_15_MEM_COMPRESS,
              "LZO1X_1_15_MEM_COMPRESS in lzo1x.h is too small for lzokay's dictionary");
static_assert(WorkMemoryDict::kSize + WorkMemoryDict::kAlignment - 1 <= LZO1X_999_MEM_COMPRESS,
              "LZO1X_999_MEM_COMPRESS in lzo1x.h is too small for lzokay's dictionary");

// lzokay counts input in 32-bit integers, and the output bound below must not
// overflow where std::size_t is 32 bits wide.
constexpr std::size_t kMaxCompressInput = 0x7fffffff;

// liblzo2's API passes no output capacity. Its documented worst case is
// src_len + src_len / 16 + 64 + 3 bytes, so that is the most a caller can be assumed
// to have. libarchive's lzop writer reserves exactly that for a full block, but then
// writes its 12-byte block header in front of the first block's data, leaving 12
// bytes fewer. lzokay checks every write against the capacity it is given, so
// telling it the smaller figure turns a compressor that expanded its input that far
// into an error rather than a heap overflow. It cannot happen in practice: lzokay's
// worst case is about src_len / 22 bytes of expansion (a 19-byte literal run before
// every 3-byte match).
constexpr std::size_t kCallerHeaderBytes = 12;

constexpr std::size_t compress_capacity(std::size_t src_len) noexcept {
    return src_len + src_len / 16 + 64 + 3 - kCallerHeaderBytes;
}

// An empty LZO1X stream is the end-of-stream marker on its own.
constexpr unsigned char kEndOfStream[] = {0x11, 0x00, 0x00};

int to_lzo_error(lzokay::EResult result) noexcept {
    switch (result) {
    case lzokay::EResult::Success:
        return LZO_E_OK;
    case lzokay::EResult::InputOverrun:
        return LZO_E_INPUT_OVERRUN;
    case lzokay::EResult::OutputOverrun:
        return LZO_E_OUTPUT_OVERRUN;
    case lzokay::EResult::LookbehindOverrun:
        return LZO_E_LOOKBEHIND_OVERRUN;
    case lzokay::EResult::InputNotConsumed:
        return LZO_E_INPUT_NOT_CONSUMED;
    case lzokay::EResult::Error:
        break;
    }
    return LZO_E_ERROR;
}

int compress(const unsigned char* src, lzo_uint src_len, unsigned char* dst, lzo_uint* dst_len,
             lzo_voidp wrkmem) noexcept {
    if (dst_len == nullptr) {
        return LZO_E_INVALID_ARGUMENT;
    }
    *dst_len = 0;
    if (dst == nullptr || wrkmem == nullptr || (src == nullptr && src_len != 0) ||
        src_len > kMaxCompressInput) {
        return LZO_E_INVALID_ARGUMENT;
    }

    // lzokay encodes empty input as a stream that no LZO1X decompressor accepts, its
    // own included, so write what liblzo2 writes.
    if (src_len == 0) {
        for (std::size_t i = 0; i < sizeof(kEndOfStream); ++i) {
            dst[i] = kEndOfStream[i];
        }
        *dst_len = sizeof(kEndOfStream);
        return LZO_E_OK;
    }

    const std::uintptr_t mask = WorkMemoryDict::kAlignment - 1;
    const std::uintptr_t aligned = (reinterpret_cast<std::uintptr_t>(wrkmem) + mask) & ~mask;
    WorkMemoryDict dict(reinterpret_cast<void*>(aligned));

    std::size_t written = 0;
    const lzokay::EResult result =
        lzokay::compress(src, src_len, dst, compress_capacity(src_len), written, dict);
    *dst_len = written;
    return to_lzo_error(result);
}

} // namespace

extern "C" {

int lzo_init(void) {
    return LZO_E_OK;
}

unsigned lzo_version(void) {
    return LZO_VERSION;
}

const char* lzo_version_string(void) {
    return LZO_VERSION_STRING;
}

lzo_uint32 lzo_adler32(lzo_uint32 adler, const unsigned char* buf, lzo_uint len) {
    // Largest number of bytes that can be summed before the 32-bit sums must be
    // reduced, and the largest prime below 65536.
    constexpr lzo_uint kMaxRun = 5552;
    constexpr std::uint32_t kBase = 65521;

    if (buf == nullptr) {
        return 1;
    }

    std::uint32_t low = adler & 0xffff;
    std::uint32_t high = (adler >> 16) & 0xffff;
    while (len > 0) {
        lzo_uint run = len < kMaxRun ? len : kMaxRun;
        len -= run;
        while (run-- > 0) {
            low += *buf++;
            high += low;
        }
        low %= kBase;
        high %= kBase;
    }
    return (high << 16) | low;
}

int lzo1x_decompress_safe(const unsigned char* src, lzo_uint src_len, unsigned char* dst,
                          lzo_uint* dst_len, lzo_voidp /* wrkmem */) {
    if (dst_len == nullptr) {
        return LZO_E_INVALID_ARGUMENT;
    }
    const std::size_t capacity = *dst_len;
    *dst_len = 0;
    if ((src == nullptr && src_len != 0) || (dst == nullptr && capacity != 0)) {
        return LZO_E_INVALID_ARGUMENT;
    }

    // lzokay does pointer arithmetic on the output even when there is no room in it.
    unsigned char no_output = 0;
    if (dst == nullptr) {
        dst = &no_output;
    }

    // lzokay.cpp is built with lzokay-bounds-checks.patch applied; without it this
    // call reads and, on 32-bit targets, writes out of bounds on hostile input.
    std::size_t written = 0;
    const lzokay::EResult result = lzokay::decompress(src, src_len, dst, capacity, written);
    *dst_len = written;
    return to_lzo_error(result);
}

int lzo1x_1_compress(const unsigned char* src, lzo_uint src_len, unsigned char* dst,
                     lzo_uint* dst_len, lzo_voidp wrkmem) {
    return compress(src, src_len, dst, dst_len, wrkmem);
}

int lzo1x_1_15_compress(const unsigned char* src, lzo_uint src_len, unsigned char* dst,
                        lzo_uint* dst_len, lzo_voidp wrkmem) {
    return compress(src, src_len, dst, dst_len, wrkmem);
}

int lzo1x_999_compress_level(const unsigned char* src, lzo_uint src_len, unsigned char* dst,
                             lzo_uint* dst_len, lzo_voidp wrkmem, const unsigned char* /* dict */,
                             lzo_uint /* dict_len */, void* /* cb */, int /* compression_level */) {
    return compress(src, src_len, dst, dst_len, wrkmem);
}

} // extern "C"
