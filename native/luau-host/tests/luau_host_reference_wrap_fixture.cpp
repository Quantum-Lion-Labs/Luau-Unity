// The reference-wrap fixture: the host, compiled from its own unchanged source,
// plus a seam that positions the process reference-token allocator. Tests use
// it to reach the end of the token range without creating two billion
// references first. Only test builds compile this file; see CMakeLists.txt.

#include "../src/luau_host.cpp"

#include "luau_host_reference_wrap_fixture.h"

#include <new>

extern "C" LUAU_HOST_API void LUAU_HOST_CALL luau_host_test_reference_tokens_seek(int32_t next)
{
    if (next <= 0)
        return;

    std::lock_guard<std::mutex> lock(referenceMutex);
    // The allocator is trivially destructible and holds only its cursor.
    new (&referenceTokenAllocator) luau_host_internal::MonotonicReferenceTokenAllocator(uint64_t(next));
}

extern "C" LUAU_HOST_API uint64_t LUAU_HOST_CALL luau_host_test_reference_tokens_live(void)
{
    std::lock_guard<std::mutex> lock(referenceMutex);
    return uint64_t(liveReferences.size());
}
