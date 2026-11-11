#pragma once

#include <cstddef>
#include <cstdint>
#include <limits>
#include <mutex>
#include <unordered_map>

namespace luau_host_internal
{
// The process-wide table of opaque managed reference tokens. A token is a
// positive int32 value naming one registry reference of one root; it is valid
// only on that root, from the insert that issued it until it is erased.
//
// Values are issued in ascending order from a cursor. After the maximum
// (INT32_MAX) the cursor continues from 1, and it passes over every value that
// is live. So until the range has been used once, which takes 2,147,483,647
// issues, no value is issued twice and a stale token is always rejected.
// After that, a value is issued at most once on each pass of the cursor, but
// there is no minimum delay between its release and its next issue: a
// reference that lived for most of a pass and is released just before the
// cursor reaches its value has that value issued again at once. A stale token
// held by mistake may then name another reference, on its own root only.
// Insertion fails only when every value is live at once, which needs about
// two billion references held together.
//
// One mutex serializes issue, lookup, and erasure, so a value is in the table
// at most once, and insert never returns a value that is live.
class ReferenceTokenTable
{
public:
    static constexpr uint32_t kMaximum = uint32_t(std::numeric_limits<int32_t>::max());

    // first and maximum are for tests; the host uses the defaults. Values lie
    // in [1, maximum], and the first issue tries first.
    explicit ReferenceTokenTable(uint32_t first = 1, uint32_t maximum = kMaximum) noexcept
        : maximum_(maximum == 0 || maximum > kMaximum ? kMaximum : maximum)
        , next_(clamp(first))
    {
    }

    ReferenceTokenTable(const ReferenceTokenTable&) = delete;
    ReferenceTokenTable& operator=(const ReferenceTokenTable&) = delete;

    // Issues the first value at or after the cursor that is not live, records
    // it for root and registryReference, and moves the cursor past it.
    // Returns 0, changing nothing, when every value is live. Throws
    // std::bad_alloc, changing nothing, when the record cannot be stored.
    int32_t insert(const void* root, int registryReference)
    {
        std::lock_guard<std::mutex> lock(mutex_);
        if (live_.size() >= maximum_)
            return 0;

        // Some value is free, so this ends within one pass of the range.
        uint32_t candidate = next_;
        for (;;)
        {
            if (live_.try_emplace(int32_t(candidate), Record{root, registryReference}).second)
            {
                next_ = successor(candidate);
                return int32_t(candidate);
            }

            candidate = successor(candidate);
        }
    }

    // The registry reference of a token that is live for root.
    bool find(const void* root, int32_t token, int* registryReference) const
    {
        std::lock_guard<std::mutex> lock(mutex_);
        const auto found = live_.find(token);
        if (found == live_.end() || found->second.root != root)
            return false;

        *registryReference = found->second.registryReference;
        return true;
    }

    // Erases a token only while it is still live for root and
    // registryReference.
    void erase(const void* root, int32_t token, int registryReference) noexcept
    {
        std::lock_guard<std::mutex> lock(mutex_);
        const auto found = live_.find(token);
        if (found != live_.end() && found->second.root == root &&
            found->second.registryReference == registryReference)
            live_.erase(found);
    }

    // Erases every token of root, which makes their values free to issue.
    void eraseroot(const void* root) noexcept
    {
        std::lock_guard<std::mutex> lock(mutex_);
        for (auto current = live_.begin(); current != live_.end();)
        {
            if (current->second.root == root)
                current = live_.erase(current);
            else
                ++current;
        }
    }

    // Moves the cursor, so the next insert tries next first. For tests.
    void seek(uint32_t next) noexcept
    {
        std::lock_guard<std::mutex> lock(mutex_);
        next_ = clamp(next);
    }

    size_t size() const noexcept
    {
        std::lock_guard<std::mutex> lock(mutex_);
        return live_.size();
    }

private:
    struct Record
    {
        const void* root;
        int registryReference;
    };

    uint32_t clamp(uint32_t value) const noexcept
    {
        return value == 0 || value > maximum_ ? 1 : value;
    }

    uint32_t successor(uint32_t value) const noexcept
    {
        return value >= maximum_ ? 1 : value + 1;
    }

    mutable std::mutex mutex_;
    std::unordered_map<int32_t, Record> live_;
    const uint32_t maximum_;
    uint32_t next_;
};
} // namespace luau_host_internal
