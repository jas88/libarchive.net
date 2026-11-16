/*
 * <lzo/lzo1x.h> for libarchive's lzop filters, implemented on lzokay.
 * See lzoconf.h: this is not liblzo2, only the part of its API libarchive uses.
 */
#ifndef LZOKAY_SHIM_LZO_LZO1X_H
#define LZOKAY_SHIM_LZO_LZO1X_H

#include "lzoconf.h"

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Work memory a caller must pass to the compressors, in bytes.
 *
 * lzokay has a single compressor, so all three levels need the same amount: room
 * for its match-finder dictionary, which the compressors build in the caller's
 * work memory instead of allocating one of their own. lzo-shim.cpp fails to compile
 * if the dictionary of the pinned lzokay does not fit. These sizes differ from
 * liblzo2's, which is why the two must never be mixed.
 */
#define LZO1X_1_MEM_COMPRESS        ((lzo_uint32) 458752)
#define LZO1X_1_15_MEM_COMPRESS     ((lzo_uint32) 458752)
#define LZO1X_999_MEM_COMPRESS      ((lzo_uint32) 458752)

/*
 * Decompress one LZO1X block. On entry *dst_len is the capacity of dst; on return
 * it is the number of bytes written, whether or not the call succeeded. Safe on
 * corrupt and hostile input: nothing is read outside src or written outside dst.
 * wrkmem is not used and may be null.
 */
int lzo1x_decompress_safe(const unsigned char *src, lzo_uint src_len,
                          unsigned char *dst, lzo_uint *dst_len,
                          lzo_voidp wrkmem);

/*
 * Compress one LZO1X block of less than 2 GiB. As in liblzo2, no capacity is passed
 * for dst: it must have room for src_len + src_len / 16 + 64 + 3 bytes. The
 * compressors stop 12 bytes short of that and fail rather than write further.
 * wrkmem must point to at least LZO1X_*_MEM_COMPRESS bytes; it need not be
 * initialised.
 *
 * lzo1x_1_compress, lzo1x_1_15_compress and lzo1x_999_compress_level all run the
 * same lzokay compressor: the level is ignored, and so is a preset dictionary
 * (the output never refers to one, so it decompresses with or without it).
 */
int lzo1x_1_compress(const unsigned char *src, lzo_uint src_len,
                     unsigned char *dst, lzo_uint *dst_len,
                     lzo_voidp wrkmem);

int lzo1x_1_15_compress(const unsigned char *src, lzo_uint src_len,
                        unsigned char *dst, lzo_uint *dst_len,
                        lzo_voidp wrkmem);

int lzo1x_999_compress_level(const unsigned char *src, lzo_uint src_len,
                             unsigned char *dst, lzo_uint *dst_len,
                             lzo_voidp wrkmem,
                             const unsigned char *dict, lzo_uint dict_len,
                             void *cb, int compression_level);

#ifdef __cplusplus
}
#endif

#endif /* LZOKAY_SHIM_LZO_LZO1X_H */
