/*
 * <lzo/lzoconf.h> for libarchive's lzop filters, implemented on lzokay.
 *
 * This is not liblzo2. It declares only the part of the liblzo2 API that libarchive
 * uses, so that libarchive's --with-lzo2 configure checks find it and its lzop
 * filters build unmodified. The functions are defined in lzo-shim.cpp on top of
 * lzokay (https://github.com/AxioDL/lzokay, MIT licence).
 *
 * Never mix these headers with the real liblzo2, or the real liblzo2 headers with
 * this library: the work memory sizes in lzo1x.h are lzokay's, not liblzo2's.
 */
#ifndef LZOKAY_SHIM_LZO_LZOCONF_H
#define LZOKAY_SHIM_LZO_LZOCONF_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/*
 * The liblzo2 release whose API this subset follows. libarchive refuses to write
 * lzop data with a library older than 0x0940 and stores this number in the header
 * of every lzop file it writes.
 */
#define LZO_VERSION         0x20a0

/* Reported by archive_version_details() as "liblzo2/<this string>". */
#define LZO_VERSION_STRING  "lzokay-shim"

typedef size_t   lzo_uint;
typedef uint32_t lzo_uint32;
typedef void    *lzo_voidp;

/* Error codes, with the values liblzo2 gives them. */
#define LZO_E_OK                    0
#define LZO_E_ERROR                 (-1)
#define LZO_E_OUT_OF_MEMORY         (-2)    /* never returned: nothing here allocates */
#define LZO_E_INPUT_OVERRUN         (-4)
#define LZO_E_OUTPUT_OVERRUN        (-5)
#define LZO_E_LOOKBEHIND_OVERRUN    (-6)
#define LZO_E_INPUT_NOT_CONSUMED    (-8)
#define LZO_E_INVALID_ARGUMENT      (-10)

/* Always succeeds: there is no global state to set up. */
int lzo_init(void);

unsigned lzo_version(void);
const char *lzo_version_string(void);

/*
 * Adler-32 as in liblzo2 and zlib: pass 1 (or the result of a call with a null
 * buffer) as the initial value, then the running checksum.
 */
lzo_uint32 lzo_adler32(lzo_uint32 adler, const unsigned char *buf, lzo_uint len);

#ifdef __cplusplus
}
#endif

#endif /* LZOKAY_SHIM_LZO_LZOCONF_H */
