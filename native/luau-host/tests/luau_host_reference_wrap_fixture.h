#pragma once

/* Test-only exports of the reference-wrap fixture, a build of luau_host.cpp
 * that tests can position the process reference-token allocator in. The
 * fixture exports the whole host ABI unchanged plus these two functions. It is
 * built only with LUAU_HOST_BUILD_TESTS, never installed, and never copied into
 * the package; the shipped host has neither function. */

#include "luau_host.h"

#ifdef __cplusplus
extern "C" {
#endif

/* The next reference creation tries next first and continues from there as
 * usual. next must lie in [1, INT32_MAX]; other values are ignored. */
LUAU_HOST_API void LUAU_HOST_CALL luau_host_test_reference_tokens_seek(int32_t next);

/* The number of live reference tokens in the process, across every root. */
LUAU_HOST_API uint64_t LUAU_HOST_CALL luau_host_test_reference_tokens_live(void);

#ifdef __cplusplus
}
#endif
