#include "luau_host.h"
#include "reference_tokens.h"
#include "tracked_allocation.h"

#include <algorithm>
#include <array>
#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <exception>
#include <iostream>
#include <limits>
#include <mutex>
#include <stdexcept>
#include <string>
#include <string_view>
#include <thread>
#include <utility>
#include <vector>

namespace
{
class TestFailure final : public std::runtime_error
{
public:
    TestFailure(const char* expression, const char* file, int line)
        : std::runtime_error(
              std::string(file) + ":" + std::to_string(line) + ": requirement failed: " + expression)
    {
    }
};

#define REQUIRE(expression)                                                                                              \
    do                                                                                                                   \
    {                                                                                                                    \
        if (!(expression))                                                                                               \
            throw TestFailure(#expression, __FILE__, __LINE__);                                                         \
    } while (false)

const uint8_t* bytes(const char* value)
{
    return reinterpret_cast<const uint8_t*>(value);
}

struct Root
{
    luau_host_state* state = nullptr;

    Root() = default;
    Root(const Root&) = delete;
    Root& operator=(const Root&) = delete;

    Root(Root&& other) noexcept
        : state(std::exchange(other.state, nullptr))
    {
    }

    Root& operator=(Root&& other) noexcept
    {
        if (this != &other)
        {
            close();
            state = std::exchange(other.state, nullptr);
        }
        return *this;
    }

    ~Root()
    {
        close();
    }

    void close()
    {
        if (state)
        {
            luau_host_state_close(state);
            state = nullptr;
        }
    }
};

Root create_root(const luau_host_state_options* options = nullptr)
{
    Root root;
    luau_host_memory_info failure = {};
    failure.struct_size = sizeof(failure);
    REQUIRE(luau_host_state_create(options, &root.state, &failure) == LUAU_HOST_STATUS_OK);
    REQUIRE(root.state != nullptr);
    return root;
}

struct CompiledBuffer
{
    luau_host_buffer value = {};

    CompiledBuffer() = default;
    CompiledBuffer(const CompiledBuffer&) = delete;
    CompiledBuffer& operator=(const CompiledBuffer&) = delete;

    CompiledBuffer(CompiledBuffer&& other) noexcept
        : value(std::exchange(other.value, {}))
    {
    }

    ~CompiledBuffer()
    {
        luau_host_buffer_free(&value);
    }
};

CompiledBuffer compile_source(const std::string& source)
{
    CompiledBuffer buffer;
    REQUIRE(
        luau_host_compile(bytes(source.data()), static_cast<uint64_t>(source.size()), nullptr, &buffer.value) ==
        LUAU_HOST_STATUS_OK);
    REQUIRE(buffer.value.data != nullptr);
    REQUIRE(buffer.value.size > 0);
    return buffer;
}

struct CompileCase
{
    std::string source;
    luau_host_compile_options options = {};
    bool useOptions = false;
    bool expectDiagnostic = false;
};

struct CompileBaseline
{
    std::vector<uint8_t> bytes;
    uint64_t hash = 0;
};

uint64_t hash_bytes(const uint8_t* data, size_t size)
{
    uint64_t value = UINT64_C(14695981039346656037);
    for (size_t index = 0; index < size; ++index)
        value = (value ^ data[index]) * UINT64_C(1099511628211);
    return value;
}

CompileBaseline compile_baseline(const CompileCase& compileCase)
{
    CompiledBuffer buffer;
    const luau_host_compile_options* options = compileCase.useOptions ? &compileCase.options : nullptr;
    REQUIRE(
        luau_host_compile(
            bytes(compileCase.source.data()),
            static_cast<uint64_t>(compileCase.source.size()),
            options,
            &buffer.value) == LUAU_HOST_STATUS_OK);
    REQUIRE(buffer.value.data != nullptr);
    REQUIRE(buffer.value.size > 0);
    REQUIRE(buffer.value.size <= uint64_t(std::numeric_limits<size_t>::max()));

    const size_t size = static_cast<size_t>(buffer.value.size);
    REQUIRE((buffer.value.data[0] == 0) == compileCase.expectDiagnostic);
    return {
        std::vector<uint8_t>(buffer.value.data, buffer.value.data + size),
        hash_bytes(buffer.value.data, size),
    };
}

class ThreadStartGate
{
public:
    void arrive_and_wait()
    {
        std::unique_lock<std::mutex> lock(mutex);
        ++ready;
        changed.notify_all();
        changed.wait(lock, [this] { return released; });
    }

    void release_when_ready(size_t expected)
    {
        std::unique_lock<std::mutex> lock(mutex);
        changed.wait(lock, [this, expected] { return ready == expected; });
        released = true;
        lock.unlock();
        changed.notify_all();
    }

private:
    std::mutex mutex;
    std::condition_variable changed;
    size_t ready = 0;
    bool released = false;
};

template<size_t ThreadCount, typename Worker>
void run_threads_together(Worker&& worker)
{
    ThreadStartGate gate;
    std::array<std::exception_ptr, ThreadCount> failures = {};
    std::array<std::thread, ThreadCount> threads;

    for (size_t index = 0; index < ThreadCount; ++index)
    {
        threads[index] = std::thread([&gate, &failures, &worker, index] {
            gate.arrive_and_wait();
            try
            {
                worker(index);
            }
            catch (...)
            {
                failures[index] = std::current_exception();
            }
        });
    }

    gate.release_when_ready(ThreadCount);
    for (std::thread& thread : threads)
        thread.join();

    for (const std::exception_ptr& failure : failures)
    {
        if (failure)
            std::rethrow_exception(failure);
    }
}

luau_host_status compile_and_load(luau_host_state* state, const std::string& source, const char* chunkName)
{
    CompiledBuffer bytecode = compile_source(source);
    luau_host_status loadStatus = LUAU_HOST_STATUS_INVALID_ARGUMENT;
    REQUIRE(
        luau_host_load(
            state,
            bytes(chunkName),
            bytecode.value.data,
            bytecode.value.size,
            0,
            &loadStatus) == LUAU_HOST_STATUS_OK);
    return loadStatus;
}

std::string string_at(luau_host_state* state, int32_t index)
{
    uint64_t length = 0;
    const uint8_t* value = luau_host_to_string_view(state, index, &length);
    REQUIRE(value != nullptr);
    return std::string(reinterpret_cast<const char*>(value), static_cast<size_t>(length));
}

luau_host_abi_info query_abi()
{
    luau_host_abi_info info = {};
    REQUIRE(luau_host_get_abi_info(static_cast<uint32_t>(sizeof(info)), &info) == LUAU_HOST_STATUS_OK);
    return info;
}

void test_abi_query()
{
    REQUIRE(luau_host_get_abi_info(static_cast<uint32_t>(sizeof(luau_host_abi_info)), nullptr) == LUAU_HOST_STATUS_INVALID_ARGUMENT);

    const luau_host_abi_info info = query_abi();
    REQUIRE(info.struct_size == sizeof(luau_host_abi_info));
    REQUIRE(info.magic == LUAU_HOST_ABI_MAGIC);
    REQUIRE(info.abi_major == LUAU_HOST_ABI_MAJOR);
    REQUIRE(info.abi_minor == LUAU_HOST_ABI_MINOR);
    REQUIRE(info.pointer_size == sizeof(void*));
    REQUIRE(info.size_t_size == sizeof(size_t));
    REQUIRE(info.little_endian == 1);
    REQUIRE(info.compile_options_size == sizeof(luau_host_compile_options));
    REQUIRE(info.callback_table_size == sizeof(luau_host_callback_table));
    REQUIRE(info.state_options_size == sizeof(luau_host_state_options));
    REQUIRE(info.memory_info_size == sizeof(luau_host_memory_info));
    REQUIRE(info.buffer_size == sizeof(luau_host_buffer));
    REQUIRE(info.upstream_revision_hash != 0);
    REQUIRE(info.host_build_fingerprint != 0);
    REQUIRE((info.feature_flags & LUAU_HOST_FEATURE_OPAQUE_REFERENCE_TOKENS) != 0);
    REQUIRE((info.feature_flags & LUAU_HOST_FEATURE_DIRECT_CALLBACK_IDENTITY) != 0);
    REQUIRE((info.feature_flags & LUAU_HOST_FEATURE_OBSERVATION_ONLY_GC_INTERRUPT) != 0);
    REQUIRE((info.feature_flags & LUAU_HOST_FEATURE_STACK_LIMIT) != 0);

    constexpr uint32_t fixedPrefixSize = static_cast<uint32_t>(offsetof(luau_host_abi_info, compile_options_size));
    alignas(luau_host_abi_info) std::array<uint8_t, sizeof(luau_host_abi_info) + 16> storage = {};

    storage.fill(0xa5);
    REQUIRE(
        luau_host_get_abi_info(fixedPrefixSize - 1, reinterpret_cast<luau_host_abi_info*>(storage.data())) ==
        LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(std::all_of(storage.begin() + fixedPrefixSize - 1, storage.end(), [](uint8_t value) { return value == 0xa5; }));

    storage.fill(0xa5);
    REQUIRE(
        luau_host_get_abi_info(fixedPrefixSize, reinterpret_cast<luau_host_abi_info*>(storage.data())) ==
        LUAU_HOST_STATUS_OK);
    REQUIRE(reinterpret_cast<const luau_host_abi_info*>(storage.data())->struct_size == sizeof(luau_host_abi_info));
    REQUIRE(std::all_of(storage.begin() + fixedPrefixSize, storage.end(), [](uint8_t value) { return value == 0xa5; }));

    storage.fill(0xa5);
    REQUIRE(
        luau_host_get_abi_info(static_cast<uint32_t>(storage.size()), reinterpret_cast<luau_host_abi_info*>(storage.data())) ==
        LUAU_HOST_STATUS_OK);
    REQUIRE(std::all_of(storage.begin() + sizeof(luau_host_abi_info), storage.end(), [](uint8_t value) { return value == 0xa5; }));
}

void test_compile_and_buffer_ownership()
{
    REQUIRE(luau_host_compile(nullptr, 0, nullptr, nullptr) == LUAU_HOST_STATUS_INVALID_ARGUMENT);

    // A nonempty output record remains caller-owned. Reject it without
    // clearing or freeing the pointer, even when the source itself is valid.
    luau_host_buffer output = {reinterpret_cast<uint8_t*>(static_cast<uintptr_t>(1)), 123};
    REQUIRE(
        luau_host_compile(bytes("return 1"), 8, nullptr, &output) ==
        LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(output.data == reinterpret_cast<uint8_t*>(static_cast<uintptr_t>(1)));
    REQUIRE(output.size == 123);

    output = {};
    REQUIRE(luau_host_compile(nullptr, 1, nullptr, &output) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(output.data == nullptr);
    REQUIRE(output.size == 0);

    luau_host_compile_options invalidOptions = {};
    invalidOptions.struct_size = sizeof(invalidOptions);
    invalidOptions.version = LUAU_HOST_COMPILE_OPTIONS_VERSION + 1;
    REQUIRE(luau_host_compile(bytes("return 1"), 8, &invalidOptions, &output) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(output.data == nullptr);
    REQUIRE(output.size == 0);

    REQUIRE(luau_host_compile(bytes("return 40 + 2"), 13, nullptr, &output) == LUAU_HOST_STATUS_OK);
    REQUIRE(output.data != nullptr);
    REQUIRE(output.size > 0);
    luau_host_buffer_free(&output);
    REQUIRE(output.data == nullptr);
    REQUIRE(output.size == 0);
    luau_host_buffer_free(&output);
    REQUIRE(output.data == nullptr);
    REQUIRE(output.size == 0);
    luau_host_buffer_free(nullptr);

    // The retired Rust bridge caught allocation/length failures around the
    // upstream compiler. The narrow host rejects an unrepresentable source
    // before dereferencing it; an initially empty output remains empty.
    uint8_t marker = 0;
    REQUIRE(
        luau_host_compile(&marker, std::numeric_limits<uint64_t>::max(), nullptr, &output) ==
        LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(output.data == nullptr);
    REQUIRE(output.size == 0);

    Root root = create_root();
    const luau_host_status compilerErrorStatus = compile_and_load(root.state, "return )", "@compiler-error");
    if (compilerErrorStatus != LUAU_HOST_STATUS_LUA_ERROR)
        std::cerr << "compiler error load status was " << compilerErrorStatus << '\n';
    REQUIRE(compilerErrorStatus == LUAU_HOST_STATUS_LUA_ERROR);
    REQUIRE(luau_host_stack_get_top(root.state) == 1);
    REQUIRE(!string_at(root.state, -1).empty());
}

void test_concurrent_compile_reentrancy_and_determinism()
{
    constexpr size_t threadCount = 4;
    constexpr size_t iterationsPerThread = 500;

    // Make the process's first compiler calls simultaneous. This catches any
    // unsynchronized lazy initialization that a serial baseline would hide.
    constexpr std::string_view coldSource = "local value = 21; return value * 2";
    std::array<CompiledBuffer, threadCount> coldOutputs;
    run_threads_together<threadCount>([&](size_t index) {
        REQUIRE(
            luau_host_compile(
                bytes(coldSource.data()),
                static_cast<uint64_t>(coldSource.size()),
                nullptr,
                &coldOutputs[index].value) == LUAU_HOST_STATUS_OK);
    });
    for (size_t first = 0; first < threadCount; ++first)
    {
        REQUIRE(coldOutputs[first].value.data != nullptr);
        for (size_t second = first + 1; second < threadCount; ++second)
        {
            REQUIRE(coldOutputs[first].value.size == coldOutputs[second].value.size);
            REQUIRE(
                std::equal(
                    coldOutputs[first].value.data,
                    coldOutputs[first].value.data + coldOutputs[first].value.size,
                    coldOutputs[second].value.data));
        }
    }

    luau_host_compile_options optimizedOptions = {};
    optimizedOptions.struct_size = sizeof(optimizedOptions);
    optimizedOptions.version = LUAU_HOST_COMPILE_OPTIONS_VERSION;
    optimizedOptions.optimization_level = 2;
    optimizedOptions.debug_level = 2;
    optimizedOptions.type_info_level = 1;
    optimizedOptions.coverage_level = 2;

    luau_host_compile_options minimalOptions = {};
    minimalOptions.struct_size = sizeof(minimalOptions);
    minimalOptions.version = LUAU_HOST_COMPILE_OPTIONS_VERSION;

    std::string largerSource(64 * 1024, ' ');
    largerSource += "return 73";

    const std::array<CompileCase, 5> compileCases = {{
        {"return 40 + 2", {}, false, false},
        {
            "local function sum(n: number): number\n"
            "    local value = 0\n"
            "    for index = 1, n do value += index end\n"
            "    return value\n"
            "end\n"
            "return sum(20)",
            optimizedOptions,
            true,
            false,
        },
        {"local value =\n", {}, false, true},
        {"function incomplete(\n", minimalOptions, true, true},
        {std::move(largerSource), minimalOptions, true, false},
    }};

    std::array<CompileBaseline, compileCases.size()> baselines;
    for (size_t index = 0; index < compileCases.size(); ++index)
        baselines[index] = compile_baseline(compileCases[index]);

    // Hold four near-default-source-limit compilation results live at once.
    // Each call receives a distinct source allocation and output record,
    // proving that ownership is independent while native compilation is
    // entered from multiple threads.
    std::string nearLimitSource(1024 * 1024 - 32, ' ');
    nearLimitSource += "return 101";
    const CompileCase nearLimitCase = {std::move(nearLimitSource), optimizedOptions, true, false};
    const CompileBaseline nearLimitBaseline = compile_baseline(nearLimitCase);
    std::array<std::string, threadCount> independentSources;
    std::array<CompiledBuffer, threadCount> independentOutputs;
    for (std::string& source : independentSources)
        source = nearLimitCase.source;
    for (size_t first = 0; first < threadCount; ++first)
    {
        for (size_t second = first + 1; second < threadCount; ++second)
            REQUIRE(independentSources[first].data() != independentSources[second].data());
    }

    run_threads_together<threadCount>([&](size_t index) {
        REQUIRE(
            luau_host_compile(
                bytes(independentSources[index].data()),
                static_cast<uint64_t>(independentSources[index].size()),
                &optimizedOptions,
                &independentOutputs[index].value) == LUAU_HOST_STATUS_OK);
    });

    for (size_t first = 0; first < threadCount; ++first)
    {
        const luau_host_buffer& output = independentOutputs[first].value;
        REQUIRE(output.data != nullptr);
        REQUIRE(output.size == nearLimitBaseline.bytes.size());
        REQUIRE(hash_bytes(output.data, static_cast<size_t>(output.size)) == nearLimitBaseline.hash);
        REQUIRE(std::equal(nearLimitBaseline.bytes.begin(), nearLimitBaseline.bytes.end(), output.data));
        for (size_t second = first + 1; second < threadCount; ++second)
            REQUIRE(output.data != independentOutputs[second].value.data);
    }

    // A whitespace-heavy source can approach the admission limit while
    // producing tiny bytecode. Separately retain a genuinely large bytecode
    // result so ownership/free behavior is exercised at the output limit's
    // meaningful scale too.
    std::string largeOutputSource = "return {\n";
    for (size_t index = 0; index < 32760; ++index)
    {
        largeOutputSource += "function() return 1 end,\n";
    }
    for (size_t index = 0; largeOutputSource.size() + 32 < 1024 * 1024; ++index)
    {
        largeOutputSource += "\"stage6-";
        largeOutputSource += std::to_string(index);
        largeOutputSource += "-xxxxxxxx\",\n";
    }
    largeOutputSource += "}\n";
    REQUIRE(largeOutputSource.size() >= 1024 * 1024 - 64);
    REQUIRE(largeOutputSource.size() < 1024 * 1024);
    const CompileCase largeOutputCase = {std::move(largeOutputSource), optimizedOptions, true, false};
    const CompileBaseline largeOutputBaseline = compile_baseline(largeOutputCase);
    REQUIRE(largeOutputBaseline.bytes.size() >= 2 * 1024 * 1024);

    // Exercise thousands of mixed valid, invalid, option-varied, and larger
    // inputs. Serial output is the oracle; hashes and exact bytes must match in
    // every parallel iteration, including deterministic compiler diagnostics.
    run_threads_together<threadCount>([&](size_t threadIndex) {
        for (size_t iteration = 0; iteration < iterationsPerThread; ++iteration)
        {
            const bool useNearLimitCase = iteration % 31 == 0;
            const size_t caseIndex = (threadIndex + iteration) % compileCases.size();
            const CompileCase& compileCase = useNearLimitCase
                ? nearLimitCase
                : compileCases[caseIndex];
            const CompileBaseline& baseline = useNearLimitCase
                ? nearLimitBaseline
                : baselines[caseIndex];
            const std::string independentSource = compileCase.source;
            CompiledBuffer output;
            const luau_host_compile_options* options = compileCase.useOptions ? &compileCase.options : nullptr;

            REQUIRE(
                luau_host_compile(
                    bytes(independentSource.data()),
                    static_cast<uint64_t>(independentSource.size()),
                    options,
                    &output.value) == LUAU_HOST_STATUS_OK);
            REQUIRE(output.value.data != nullptr);
            REQUIRE(output.value.size == baseline.bytes.size());
            REQUIRE(hash_bytes(output.value.data, static_cast<size_t>(output.value.size)) == baseline.hash);
            REQUIRE(std::equal(baseline.bytes.begin(), baseline.bytes.end(), output.value.data));
        }
    });
}

void test_root_and_thread_lifecycle()
{
    luau_host_state_close(nullptr);

    luau_host_state* invalidState = reinterpret_cast<luau_host_state*>(static_cast<uintptr_t>(1));
    luau_host_memory_info failure = {};
    failure.struct_size = sizeof(failure);
    REQUIRE(luau_host_state_create(nullptr, nullptr, &failure) == LUAU_HOST_STATUS_INVALID_ARGUMENT);

    luau_host_state_options invalidOptions = {};
    invalidOptions.struct_size = sizeof(invalidOptions);
    invalidOptions.version = LUAU_HOST_STATE_OPTIONS_VERSION + 1;
    REQUIRE(luau_host_state_create(&invalidOptions, &invalidState, &failure) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(invalidState == nullptr);

    Root root = create_root();
    REQUIRE(luau_host_main_thread(root.state) == root.state);
    REQUIRE(luau_host_thread_status(root.state) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_get_top(root.state) == 0);

    luau_host_state* child = nullptr;
    REQUIRE(luau_host_thread_create(root.state, nullptr) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(luau_host_thread_create(root.state, &child) == LUAU_HOST_STATUS_OK);
    REQUIRE(child != nullptr);
    REQUIRE(child != root.state);
    REQUIRE(luau_host_main_thread(child) == root.state);
    REQUIRE(luau_host_thread_status(child) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_get_top(root.state) == 1);
    REQUIRE(luau_host_to_thread(root.state, -1) == child);
    REQUIRE(luau_host_thread_reset(child) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_thread_status(child) == LUAU_HOST_STATUS_OK);
}

void test_execution_error_containment_and_reuse()
{
    Root root = create_root();

    REQUIRE(compile_and_load(root.state, "return 6 * 7", "@success") == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_pcall(root.state, 0, 1, 0) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_get_top(root.state) == 1);
    int32_t isNumber = 0;
    REQUIRE(luau_host_to_number(root.state, -1, &isNumber) == 42.0);
    REQUIRE(isNumber != 0);
    REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);

    REQUIRE(compile_and_load(root.state, "local value = nil; return value.missing", "@ordinary-error") == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_pcall(root.state, 0, 1, 0) == LUAU_HOST_STATUS_LUA_ERROR);
    REQUIRE(luau_host_stack_get_top(root.state) == 1);
    REQUIRE(!string_at(root.state, -1).empty());
    REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);

    REQUIRE(compile_and_load(root.state, "return 7", "@reused") == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_pcall(root.state, 0, 1, 0) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_get_top(root.state) == 1);
    REQUIRE(luau_host_to_number(root.state, -1, &isNumber) == 7.0);
    REQUIRE(isNumber != 0);
}

void test_stack_tables_and_references()
{
    const luau_host_abi_info abi = query_abi();
    Root root = create_root();

    REQUIRE(luau_host_push_integer(root.state, 1) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_push_integer(root.state, 2) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_push_integer(root.state, 3) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_abs_index(root.state, -1) == 3);
    REQUIRE(luau_host_stack_insert(root.state, 1) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_remove(root.state, 2) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_replace(root.state, 1) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_get_top(root.state) == 1);
    int32_t isInteger = 0;
    REQUIRE(luau_host_to_integer64(root.state, 1, &isInteger) == 2);
    REQUIRE(isInteger != 0);
    REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);

    REQUIRE(luau_host_table_create(root.state, 0, 2) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_type(root.state, 1) == abi.type_table);
    REQUIRE(luau_host_push_string(root.state, bytes("answer"), 6) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_push_integer(root.state, 42) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_table_raw_set(root.state, 1) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_get_top(root.state) == 1);

    REQUIRE(luau_host_push_string(root.state, bytes("answer"), 6) == LUAU_HOST_STATUS_OK);
    int32_t valueType = abi.type_nil;
    REQUIRE(luau_host_table_raw_get(root.state, 1, &valueType) == LUAU_HOST_STATUS_OK);
    REQUIRE(valueType == abi.type_integer);
    REQUIRE(luau_host_to_integer64(root.state, -1, &isInteger) == 42);
    REQUIRE(isInteger != 0);

    int32_t reference = 0;
    REQUIRE(luau_host_reference_create(root.state, -1, &reference) == LUAU_HOST_STATUS_OK);
    REQUIRE(reference > 0);
    REQUIRE(luau_host_stack_set_top(root.state, 1) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_reference_push(root.state, reference, &valueType) == LUAU_HOST_STATUS_OK);
    REQUIRE(valueType == abi.type_integer);
    REQUIRE(luau_host_to_integer64(root.state, -1, &isInteger) == 42);
    REQUIRE(isInteger != 0);
    REQUIRE(luau_host_reference_release(root.state, reference) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_set_top(root.state, 1) == LUAU_HOST_STATUS_OK);

    REQUIRE(luau_host_table_clone(root.state, 1) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_to_pointer(root.state, 1) != luau_host_to_pointer(root.state, 2));
    REQUIRE(luau_host_stack_set_top(root.state, 1) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_table_clear(root.state, 1) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_push_string(root.state, bytes("answer"), 6) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_table_raw_get(root.state, 1, &valueType) == LUAU_HOST_STATUS_OK);
    REQUIRE(valueType == abi.type_nil);

    // Preserve lua_next's exact stack contract through the narrow host: an
    // iteration hit replaces the key with key+value, and exhaustion consumes
    // the retained key without disturbing the table.
    REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_table_create(root.state, 1, 0) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_push_integer(root.state, 1) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_push_integer(root.state, 42) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_table_raw_set(root.state, 1) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_push_nil(root.state) == LUAU_HOST_STATUS_OK);

    int32_t hasNext = -1;
    REQUIRE(luau_host_table_next(root.state, 1, &hasNext) == LUAU_HOST_STATUS_OK);
    REQUIRE(hasNext == 1);
    REQUIRE(luau_host_stack_get_top(root.state) == 3);
    REQUIRE(luau_host_to_integer64(root.state, -2, &isInteger) == 1);
    REQUIRE(isInteger != 0);
    REQUIRE(luau_host_to_integer64(root.state, -1, &isInteger) == 42);
    REQUIRE(isInteger != 0);

    REQUIRE(luau_host_stack_set_top(root.state, 2) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_table_next(root.state, 1, &hasNext) == LUAU_HOST_STATUS_OK);
    REQUIRE(hasNext == 0);
    REQUIRE(luau_host_stack_get_top(root.state) == 1);
}

void test_numeric_conversion_boundaries()
{
    Root root = create_root();

    auto rejectSigned = [&](double value) {
        REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_push_number(root.state, value) == LUAU_HOST_STATUS_OK);
        int32_t isInteger = 123;
        REQUIRE(luau_host_to_integer32(root.state, -1, &isInteger) == 0);
        REQUIRE(isInteger == 0);
    };
    auto rejectUnsigned = [&](double value) {
        REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_push_number(root.state, value) == LUAU_HOST_STATUS_OK);
        int32_t isInteger = 123;
        REQUIRE(luau_host_to_unsigned32(root.state, -1, &isInteger) == 0);
        REQUIRE(isInteger == 0);
    };
    auto acceptSigned = [&](double value, int32_t expected) {
        REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_push_number(root.state, value) == LUAU_HOST_STATUS_OK);
        int32_t isInteger = 0;
        REQUIRE(luau_host_to_integer32(root.state, -1, &isInteger) == expected);
        REQUIRE(isInteger != 0);
    };
    auto acceptUnsigned = [&](double value, uint32_t expected) {
        REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_push_number(root.state, value) == LUAU_HOST_STATUS_OK);
        int32_t isInteger = 0;
        REQUIRE(luau_host_to_unsigned32(root.state, -1, &isInteger) == expected);
        REQUIRE(isInteger != 0);
    };

    const double notANumber = std::numeric_limits<double>::quiet_NaN();
    const double positiveInfinity = std::numeric_limits<double>::infinity();
    const double negativeInfinity = -std::numeric_limits<double>::infinity();
    const double huge = std::numeric_limits<double>::max();
    for (const double invalid : {notANumber, positiveInfinity, negativeInfinity, huge, -huge, 2147483648.0, -2147483649.0})
        rejectSigned(invalid);
    for (const double invalid : {notANumber, positiveInfinity, negativeInfinity, huge, -huge, -1.0, 4294967296.0})
        rejectUnsigned(invalid);

    acceptSigned(42.75, 42);
    acceptSigned(-42.75, -42);
    acceptSigned(2147483647.0, std::numeric_limits<int32_t>::max());
    acceptSigned(-2147483648.0, std::numeric_limits<int32_t>::min());
    acceptUnsigned(42.75, 42);
    acceptUnsigned(4294967295.0, std::numeric_limits<uint32_t>::max());
    REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);
}

void test_invalid_observer_and_stack_boundaries()
{
    Root root = create_root();
    Root otherRoot = create_root();
    constexpr int32_t noType = -1;
    constexpr std::array<int32_t, 5> invalidIndices = {
        0,
        1,
        -1,
        std::numeric_limits<int32_t>::min(),
        std::numeric_limits<int32_t>::max(),
    };

    REQUIRE(luau_host_stack_get_top(root.state) == 0);
    for (const int32_t index : invalidIndices)
    {
        REQUIRE(luau_host_stack_abs_index(root.state, index) == 0);
        REQUIRE(luau_host_type(root.state, index) == noType);
        REQUIRE(luau_host_object_length(root.state, index) == 0);
        REQUIRE(luau_host_to_boolean(root.state, index) == 0);

        int32_t isNumber = 123;
        REQUIRE(luau_host_to_number(root.state, index, &isNumber) == 0.0);
        REQUIRE(isNumber == 0);
        int32_t isInteger = 123;
        REQUIRE(luau_host_to_integer32(root.state, index, &isInteger) == 0);
        REQUIRE(isInteger == 0);
        isInteger = 123;
        REQUIRE(luau_host_to_unsigned32(root.state, index, &isInteger) == 0);
        REQUIRE(isInteger == 0);
        isInteger = 123;
        REQUIRE(luau_host_to_integer64(root.state, index, &isInteger) == 0);
        REQUIRE(isInteger == 0);

        REQUIRE(luau_host_to_vector(root.state, index) == nullptr);
        uint64_t length = 123;
        REQUIRE(luau_host_to_string_view(root.state, index, &length) == nullptr);
        REQUIRE(length == 0);
        REQUIRE(luau_host_to_light_userdata(root.state, index) == nullptr);
        REQUIRE(luau_host_to_userdata(root.state, index) == nullptr);
        REQUIRE(luau_host_to_thread(root.state, index) == nullptr);
        length = 123;
        REQUIRE(luau_host_to_buffer(root.state, index, &length) == nullptr);
        REQUIRE(length == 0);
        REQUIRE(luau_host_to_pointer(root.state, index) == nullptr);
        REQUIRE(luau_host_is_global(root.state, index) == 0);

        const uint8_t* stringOutput = reinterpret_cast<const uint8_t*>(static_cast<uintptr_t>(1));
        length = 123;
        REQUIRE(luau_host_to_string(root.state, index, &stringOutput, &length) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
        REQUIRE(stringOutput == nullptr);
        REQUIRE(length == 0);
        stringOutput = reinterpret_cast<const uint8_t*>(static_cast<uintptr_t>(1));
        length = 123;
        REQUIRE(luau_host_to_display_string(root.state, index, &stringOutput, &length) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
        REQUIRE(stringOutput == nullptr);
        REQUIRE(length == 0);

        int32_t reference = 123;
        REQUIRE(luau_host_reference_create(root.state, index, &reference) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
        REQUIRE(reference <= 0);
        REQUIRE(luau_host_push_value(root.state, index) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
        REQUIRE(luau_host_stack_insert(root.state, index) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
        REQUIRE(luau_host_stack_remove(root.state, index) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
        REQUIRE(luau_host_stack_replace(root.state, index) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
        int32_t tableResult = 123;
        REQUIRE(luau_host_table_get(root.state, index, &tableResult) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
        REQUIRE(luau_host_table_set(root.state, index) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
        REQUIRE(luau_host_table_raw_get(root.state, index, &tableResult) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
        REQUIRE(luau_host_table_raw_set(root.state, index) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
        REQUIRE(luau_host_table_next(root.state, index, &tableResult) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
        REQUIRE(luau_host_table_clear(root.state, index) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
        REQUIRE(luau_host_table_clone(root.state, index) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
        REQUIRE(luau_host_metatable_get(root.state, index, &tableResult) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
        REQUIRE(luau_host_metatable_set(root.state, index, &tableResult) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
        REQUIRE(luau_host_table_set_readonly(root.state, index, 1) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
        REQUIRE(luau_host_stack_get_top(root.state) == 0);
    }

    REQUIRE(luau_host_type_name(root.state, std::numeric_limits<int32_t>::min()) == nullptr);
    REQUIRE(luau_host_type_name(root.state, std::numeric_limits<int32_t>::max()) == nullptr);
    REQUIRE(luau_host_type_name(nullptr, 0) == nullptr);
    REQUIRE(luau_host_callback_registration_id(root.state) == 0);

    REQUIRE(luau_host_stack_set_top(root.state, -2) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(luau_host_stack_set_top(root.state, std::numeric_limits<int32_t>::min()) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(luau_host_stack_check(root.state, -1, nullptr) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(luau_host_stack_move(root.state, root.state, -1) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(luau_host_stack_move(root.state, root.state, 1) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(luau_host_stack_move(root.state, otherRoot.state, 0) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(luau_host_stack_get_top(root.state) == 0);
    REQUIRE(luau_host_stack_get_top(otherRoot.state) == 0);
}

void test_invalid_table_reference_and_load_boundaries()
{
    Root root = create_root();

    // Empty bytecode used to be replaced with a one-byte sentinel before
    // entering upstream's unchecked loader. Reject both null and non-null
    // empty spans without touching the stack or load-status output.
    luau_host_status emptyLoadStatus = LUAU_HOST_STATUS_CANCELED;
    const int32_t emptyLoadTop = luau_host_stack_get_top(root.state);
    REQUIRE(
        luau_host_load(
            root.state,
            bytes("@empty-bytecode"),
            nullptr,
            0,
            0,
            &emptyLoadStatus) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
    const uint8_t emptyBytecodeMarker = 0;
    REQUIRE(
        luau_host_load(
            root.state,
            bytes("@empty-bytecode"),
            &emptyBytecodeMarker,
            0,
            0,
            &emptyLoadStatus) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(emptyLoadStatus == LUAU_HOST_STATUS_CANCELED);
    REQUIRE(luau_host_stack_get_top(root.state) == emptyLoadTop);

    // The raw table APIs are assertion-prone upstream and therefore reject an
    // ordinary non-table target before consuming keys or values.
    REQUIRE(luau_host_push_integer(root.state, 7) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_push_string(root.state, bytes("key"), 3) == LUAU_HOST_STATUS_OK);
    int32_t output = 123;
    REQUIRE(luau_host_table_raw_get(root.state, 1, &output) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(luau_host_stack_get_top(root.state) == 2);
    REQUIRE(luau_host_push_integer(root.state, 9) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_table_raw_set(root.state, 1) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(luau_host_stack_get_top(root.state) == 3);
    REQUIRE(luau_host_stack_set_top(root.state, 1) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_push_nil(root.state) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_table_next(root.state, 1, &output) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(luau_host_stack_get_top(root.state) == 2);
    REQUIRE(luau_host_stack_set_top(root.state, 1) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_table_clear(root.state, 1) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(luau_host_table_clone(root.state, 1) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(luau_host_table_set_readonly(root.state, 1, 1) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(luau_host_stack_get_top(root.state) == 1);

    REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_table_create(root.state, 0, 0) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_push_integer(root.state, 10) == LUAU_HOST_STATUS_OK);
    output = 123;
    REQUIRE(luau_host_metatable_set(root.state, 1, &output) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(luau_host_stack_get_top(root.state) == 2);

    REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_push_integer(root.state, 42) == LUAU_HOST_STATUS_OK);
    int32_t firstReference = 0;
    REQUIRE(luau_host_reference_create(root.state, 1, &firstReference) == LUAU_HOST_STATUS_OK);
    REQUIRE(firstReference > 0);
    REQUIRE(luau_host_reference_release(root.state, firstReference) == LUAU_HOST_STATUS_OK);
    const int32_t nullOutputTop = luau_host_stack_get_top(root.state);
    REQUIRE(luau_host_reference_create(root.state, 1, nullptr) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(luau_host_stack_get_top(root.state) == nullOutputTop);

    int32_t reference = 0;
    REQUIRE(luau_host_reference_create(root.state, 1, &reference) == LUAU_HOST_STATUS_OK);
    REQUIRE(reference > firstReference);
    REQUIRE(luau_host_reference_push(root.state, firstReference, &output) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
    const int32_t referenceTop = luau_host_stack_get_top(root.state);
    REQUIRE(luau_host_reference_release(root.state, reference) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_get_top(root.state) == referenceTop);
    REQUIRE(luau_host_reference_release(root.state, reference) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(luau_host_reference_push(root.state, reference, &output) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(luau_host_reference_release(root.state, std::numeric_limits<int32_t>::max()) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(luau_host_reference_push(root.state, std::numeric_limits<int32_t>::max(), &output) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(luau_host_reference_push(root.state, 0, &output) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(luau_host_reference_release(root.state, 0) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_reference_release(root.state, -1) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_get_top(root.state) == referenceTop);

    // A load environment must be zero or an in-range table. An invalid
    // environment is rejected before the bytecode or value stack is touched.
    CompiledBuffer bytecode = compile_source("return 1");
    luau_host_status loadStatus = LUAU_HOST_STATUS_CANCELED;
    const int32_t loadTop = luau_host_stack_get_top(root.state);
    REQUIRE(
        luau_host_load(
            root.state,
            bytes("@invalid-environment"),
            bytecode.value.data,
            bytecode.value.size,
            1,
            &loadStatus) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(luau_host_stack_get_top(root.state) == loadTop);
    REQUIRE(
        luau_host_load(
            root.state,
            bytes("@missing-environment"),
            bytecode.value.data,
            bytecode.value.size,
            loadTop + 1,
            &loadStatus) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(luau_host_stack_get_top(root.state) == loadTop);
}

void test_reference_token_staleness_churn_and_exhaustion()
{
    Root root = create_root();
    Root otherRoot = create_root();
    REQUIRE(luau_host_push_integer(root.state, 42) == LUAU_HOST_STATUS_OK);

    int32_t first = 0;
    REQUIRE(luau_host_reference_create(root.state, 1, &first) == LUAU_HOST_STATUS_OK);
    REQUIRE(first > 0);

    int32_t type = -1;
    REQUIRE(luau_host_reference_push(otherRoot.state, first, &type) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(luau_host_reference_release(otherRoot.state, first) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(luau_host_reference_release(root.state, first) == LUAU_HOST_STATUS_OK);

    int32_t previous = first;
    constexpr int32_t churnCount = 100'000;
    for (int32_t iteration = 0; iteration < churnCount; ++iteration)
    {
        int32_t token = 0;
        REQUIRE(luau_host_reference_create(root.state, 1, &token) == LUAU_HOST_STATUS_OK);
        REQUIRE(token > previous);
        REQUIRE(luau_host_reference_push(root.state, token, &type) == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_stack_set_top(root.state, -2) == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_reference_release(root.state, token) == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_reference_push(root.state, previous, &type) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
        previous = token;
    }

    luau_host_internal::MonotonicReferenceTokenAllocator nearExhaustion(
        uint64_t(std::numeric_limits<int32_t>::max()) - 1);
    REQUIRE(nearExhaustion.allocate() == std::numeric_limits<int32_t>::max() - 1);
    REQUIRE(nearExhaustion.allocate() == std::numeric_limits<int32_t>::max());
    REQUIRE(nearExhaustion.allocate() == 0);
    REQUIRE(nearExhaustion.allocate() == 0);
}

void test_invalid_execution_boundaries()
{
    Root root = create_root();
    Root otherRoot = create_root();

    REQUIRE(luau_host_collect(nullptr) == LUAU_HOST_STATUS_INVALID_ARGUMENT);

    REQUIRE(luau_host_pcall(root.state, 0, 0, 0) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(luau_host_pcall(root.state, -1, 0, 0) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(luau_host_pcall(root.state, 0, -2, 0) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(luau_host_stack_get_top(root.state) == 0);

    REQUIRE(compile_and_load(root.state, "return 1", "@invalid-pcall") == LUAU_HOST_STATUS_OK);
    const int32_t pcallTop = luau_host_stack_get_top(root.state);
    REQUIRE(luau_host_pcall(root.state, 0, 0, 2) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(
        luau_host_pcall(root.state, 0, 0, std::numeric_limits<int32_t>::min()) ==
        LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(luau_host_stack_get_top(root.state) == pcallTop);
    REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);

    luau_host_state* child = nullptr;
    REQUIRE(luau_host_thread_create(root.state, &child) == LUAU_HOST_STATUS_OK);
    REQUIRE(child != nullptr);
    REQUIRE(luau_host_stack_get_top(child) == 0);
    REQUIRE(luau_host_resume(child, root.state, -1) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(luau_host_resume(child, root.state, 1) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(luau_host_resume(child, otherRoot.state, 0) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(luau_host_resume_error(child, root.state) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(luau_host_stack_get_top(child) == 0);

    REQUIRE(luau_host_push_string(child, bytes("error"), 5) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_resume_error(child, otherRoot.state) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(luau_host_resume(child, root.state, 2) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
    REQUIRE(luau_host_stack_get_top(child) == 1);
    REQUIRE(luau_host_stack_set_top(child, 0) == LUAU_HOST_STATUS_OK);

    // Yield is only meaningful from an active, yieldable managed callback.
    // Invalid calls return the neutral callback sentinel without mutating the
    // state or triggering Luau's non-yieldable-call assertion.
    REQUIRE(luau_host_yield(root.state, -1) == 0);
    REQUIRE(luau_host_yield(root.state, 0) == 0);
    REQUIRE(luau_host_yield(root.state, 1) == 0);
    REQUIRE(luau_host_yield(child, 0) == 0);
    REQUIRE(luau_host_thread_status(root.state) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_thread_status(child) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_get_top(root.state) == 1);
    REQUIRE(luau_host_stack_get_top(child) == 0);
    REQUIRE(luau_host_thread_reset(child) == LUAU_HOST_STATUS_OK);

    // ABI 2 exposes only the product's full-collection operation; arbitrary
    // collector tuning is no longer reachable through the host boundary.
    REQUIRE(luau_host_collect(root.state) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_get_top(root.state) == 1);
}

void test_allocator_quota_and_recovery()
{
    constexpr uint64_t memoryLimit = UINT64_C(1024) * 1024;
    luau_host_state_options options = {};
    options.struct_size = sizeof(options);
    options.version = LUAU_HOST_STATE_OPTIONS_VERSION;
    options.flags = LUAU_HOST_STATE_OPTION_TRACK_MEMORY;
    options.memory_limit_bytes = memoryLimit;

    Root root = create_root(&options);
    luau_host_memory_info before = {};
    before.struct_size = sizeof(before);
    REQUIRE(luau_host_memory_get(root.state, &before) == LUAU_HOST_STATUS_OK);
    REQUIRE(before.struct_size == sizeof(before));
    REQUIRE(before.tracked == 1);
    REQUIRE(before.limit_bytes == memoryLimit);
    REQUIRE(before.current_bytes > 0);
    REQUIRE(before.peak_bytes >= before.current_bytes);
    REQUIRE(before.failure == LUAU_HOST_ALLOCATOR_FAILURE_NONE);

    constexpr uint32_t memoryInfoFixedPrefixSize =
        static_cast<uint32_t>(offsetof(luau_host_memory_info, current_bytes));
    // Keep a complete C++ object while advertising only the ABI prefix; the
    // canary still proves that the host honors the caller-declared size.
    luau_host_memory_info shortInfo = {};
    std::memset(&shortInfo, 0xa5, sizeof(shortInfo));
    shortInfo.struct_size = memoryInfoFixedPrefixSize;
    const auto* shortBytes = reinterpret_cast<const uint8_t*>(&shortInfo);
    for (int query = 0; query < 2; ++query)
    {
        REQUIRE(luau_host_memory_get(root.state, &shortInfo) == LUAU_HOST_STATUS_OK);
        REQUIRE(shortInfo.struct_size == memoryInfoFixedPrefixSize);
        REQUIRE(shortBytes[memoryInfoFixedPrefixSize] == 0xa5);
    }

    const std::vector<uint8_t> largeString(
        static_cast<size_t>(memoryLimit) + 1,
        static_cast<uint8_t>('x'));
    const int32_t originalTop = luau_host_stack_get_top(root.state);
    REQUIRE(
        luau_host_push_string(root.state, largeString.data(), static_cast<uint64_t>(largeString.size())) ==
        LUAU_HOST_STATUS_MEMORY_QUOTA);
    REQUIRE(luau_host_stack_get_top(root.state) == originalTop + 1);

    luau_host_memory_info failed = {};
    failed.struct_size = sizeof(failed);
    REQUIRE(luau_host_memory_get(root.state, &failed) == LUAU_HOST_STATUS_OK);
    REQUIRE(failed.failure == LUAU_HOST_ALLOCATOR_FAILURE_QUOTA);
    REQUIRE(failed.last_attempted_bytes > failed.limit_bytes);
    REQUIRE(failed.current_bytes <= failed.limit_bytes);

    REQUIRE(luau_host_stack_set_top(root.state, originalTop) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_memory_reset_failure(root.state) == LUAU_HOST_STATUS_OK);
    REQUIRE(
        luau_host_push_string(root.state, bytes("ok"), 2) ==
        LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_get_top(root.state) == originalTop + 1);

    luau_host_memory_info recovered = {};
    recovered.struct_size = sizeof(recovered);
    REQUIRE(luau_host_memory_get(root.state, &recovered) == LUAU_HOST_STATUS_OK);
    REQUIRE(recovered.failure == LUAU_HOST_ALLOCATOR_FAILURE_NONE);
    REQUIRE(recovered.last_attempted_bytes == 0);
    REQUIRE(recovered.current_bytes <= recovered.limit_bytes);

    // Port the retired bridge's protected buffer-allocation coverage to the
    // host-owned allocator contract.  Failure leaves one contained error;
    // clearing it and resetting telemetry makes the same root reusable.
    REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);
    void* nativeBuffer = reinterpret_cast<void*>(static_cast<uintptr_t>(1));
    REQUIRE(
        luau_host_buffer_create(root.state, UINT64_C(2) * 1024 * 1024, &nativeBuffer) ==
        LUAU_HOST_STATUS_MEMORY_QUOTA);
    REQUIRE(nativeBuffer == nullptr);
    REQUIRE(luau_host_stack_get_top(root.state) == 1);

    REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_memory_reset_failure(root.state) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_buffer_create(root.state, 16, &nativeBuffer) == LUAU_HOST_STATUS_OK);
    REQUIRE(nativeBuffer != nullptr);
    REQUIRE(luau_host_stack_get_top(root.state) == 1);
    REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);

    REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);

    luau_host_state_options tiny = options;
    tiny.memory_limit_bytes = 1;
    luau_host_state* rejected = reinterpret_cast<luau_host_state*>(static_cast<uintptr_t>(1));
    luau_host_memory_info createFailure = {};
    createFailure.struct_size = sizeof(createFailure);
    REQUIRE(luau_host_state_create(&tiny, &rejected, &createFailure) == LUAU_HOST_STATUS_MEMORY_QUOTA);
    REQUIRE(rejected == nullptr);
    REQUIRE(createFailure.failure == LUAU_HOST_ALLOCATOR_FAILURE_QUOTA);
    REQUIRE(createFailure.limit_bytes == 1);
    REQUIRE(createFailure.last_attempted_bytes > createFailure.limit_bytes);
}

void* fail_reallocate(void*, size_t)
{
    return nullptr;
}

void test_failed_shrink_retains_allocator_charge()
{
    using luau_host_internal::freetrackedallocation;
    using luau_host_internal::resizetrackedallocation;
    using luau_host_internal::trackedallocationsize;

    constexpr size_t originalSize = 4096;
    const luau_host_internal::TrackedAllocationResizeResult allocated =
        resizetrackedallocation(nullptr, originalSize);
    REQUIRE(!allocated.failed);
    REQUIRE(allocated.block != nullptr);
    REQUIRE(allocated.retainedSize == originalSize);
    REQUIRE(trackedallocationsize(allocated.block) == originalSize);
    REQUIRE(reinterpret_cast<uintptr_t>(allocated.block) % alignof(std::max_align_t) == 0);

    std::memset(allocated.block, 0x5a, originalSize);
    const luau_host_internal::TrackedAllocationResizeResult retained =
        resizetrackedallocation(allocated.block, 64, fail_reallocate);
    REQUIRE(!retained.failed);
    REQUIRE(retained.block == allocated.block);
    REQUIRE(retained.retainedSize == originalSize);
    REQUIRE(trackedallocationsize(retained.block) == originalSize);

    const luau_host_internal::TrackedAllocationResizeResult failedGrowth =
        resizetrackedallocation(retained.block, originalSize + 1, fail_reallocate);
    REQUIRE(failedGrowth.failed);
    REQUIRE(failedGrowth.block == nullptr);
    REQUIRE(failedGrowth.retainedSize == originalSize);
    REQUIRE(trackedallocationsize(retained.block) == originalSize);

    freetrackedallocation(retained.block);
}

int callbackInvocations = 0;
luau_host_status callbackPushStatus = LUAU_HOST_STATUS_INVALID_ARGUMENT;
int destructorInvocations = 0;
int destructorPayload = 0;
int userdataPayloadDestructions = 0;
int maximumOwnerDestructions = 0;
void* expectedUserdataDestructorPointer = nullptr;
int userdataDestructorPhase = 0;
std::array<int, 4> userdataDestructorPhaseCounts = {};
int userdataDestructorPointerMismatches = 0;
int interruptPolls = 0;
int alternateInterruptPolls = 0;
int nonYieldableInterruptPolls = 0;
int executionInterruptKinds = 0;
int gcInterruptKinds = 0;
int invalidInterruptKinds = 0;
int invalidCallbackReturn = 0;
bool invalidCallbackReturnsTopPlusOne = false;

int32_t LUAU_HOST_CALL managed_callback(luau_host_state* state)
{
    const uint64_t registrationId = luau_host_callback_registration_id(state);
    if (registrationId != 0)
        ++callbackInvocations;
    callbackPushStatus = luau_host_push_integer(state, static_cast<int64_t>(registrationId));
    return callbackPushStatus == LUAU_HOST_STATUS_OK ? 1 : 0;
}

void LUAU_HOST_CALL userdata_destructor(void* userdata)
{
    ++destructorInvocations;
    if (userdata)
    {
        destructorPayload = *static_cast<int*>(userdata);
        if (destructorPayload == 1234)
            ++userdataPayloadDestructions;
        if (destructorPayload == 254)
            ++maximumOwnerDestructions;
    }
}

void LUAU_HOST_CALL recording_userdata_destructor(void* userdata)
{
    if (userdata != expectedUserdataDestructorPointer)
        ++userdataDestructorPointerMismatches;
    if (userdataDestructorPhase >= 0 &&
        static_cast<size_t>(userdataDestructorPhase) < userdataDestructorPhaseCounts.size())
        ++userdataDestructorPhaseCounts[static_cast<size_t>(userdataDestructorPhase)];
}

void record_interrupt_kind(luau_host_interrupt_kind kind)
{
    if (kind == LUAU_HOST_INTERRUPT_EXECUTION)
        ++executionInterruptKinds;
    else if (kind == LUAU_HOST_INTERRUPT_GC)
        ++gcInterruptKinds;
    else
        ++invalidInterruptKinds;
}

void reset_interrupt_kind_counts()
{
    executionInterruptKinds = 0;
    gcInterruptKinds = 0;
    invalidInterruptKinds = 0;
}

int32_t LUAU_HOST_CALL interrupt_poll(luau_host_state*, luau_host_interrupt_kind kind)
{
    record_interrupt_kind(kind);
    ++interruptPolls;
    return 1;
}

int32_t LUAU_HOST_CALL alternate_interrupt_poll(luau_host_state*, luau_host_interrupt_kind kind)
{
    record_interrupt_kind(kind);
    ++alternateInterruptPolls;
    return 1;
}

int32_t LUAU_HOST_CALL continue_interrupt_poll(luau_host_state*, luau_host_interrupt_kind kind)
{
    record_interrupt_kind(kind);
    return 0;
}

int32_t LUAU_HOST_CALL gc_observation_poll(luau_host_state*, luau_host_interrupt_kind kind)
{
    record_interrupt_kind(kind);
    return kind == LUAU_HOST_INTERRUPT_GC ? 1 : 0;
}

int32_t LUAU_HOST_CALL nonyieldable_interrupt_poll(
    luau_host_state* state,
    luau_host_interrupt_kind kind)
{
    record_interrupt_kind(kind);
    if (kind != LUAU_HOST_INTERRUPT_EXECUTION || luau_host_is_yieldable(state) != 0)
        return 0;

    ++nonYieldableInterruptPolls;
    return 1;
}

int32_t LUAU_HOST_CALL invalid_return_callback(luau_host_state* state)
{
    return invalidCallbackReturnsTopPlusOne
        ? luau_host_stack_get_top(state) + 1
        : invalidCallbackReturn;
}

int32_t LUAU_HOST_CALL failure_return_callback(luau_host_state* state)
{
    constexpr char failure[] = "managed callback failure token";
    callbackPushStatus = luau_host_push_string(
        state,
        bytes(failure),
        static_cast<uint64_t>(sizeof(failure) - 1));
    return callbackPushStatus == LUAU_HOST_STATUS_OK ? LUAU_HOST_CALLBACK_ERROR : 0;
}

luau_host_callback_table callback_table()
{
    luau_host_callback_table callbacks = {};
    callbacks.struct_size = sizeof(callbacks);
    callbacks.version = LUAU_HOST_CALLBACK_TABLE_VERSION;
    callbacks.registration_id = 1;
    return callbacks;
}

void test_callback_and_destructor_lifetime()
{
    callbackInvocations = 0;
    callbackPushStatus = LUAU_HOST_STATUS_INVALID_ARGUMENT;
    destructorInvocations = 0;
    destructorPayload = 0;
    userdataPayloadDestructions = 0;
    maximumOwnerDestructions = 0;

    Root root = create_root();
    int callbackValue = 99;
    int callbackOwnerValue = 4321;

    {
        luau_host_callback_table callbacks = callback_table();
        callbacks.registration_id = 0;
        callbacks.managed_function = managed_callback;
        REQUIRE(
            luau_host_push_callback(root.state, &callbacks, nullptr, 0, 0, nullptr, nullptr) ==
            LUAU_HOST_STATUS_INVALID_ARGUMENT);
        REQUIRE(luau_host_stack_get_top(root.state) == 0);
    }

    {
        luau_host_callback_table callbacks = callback_table();
        callbacks.registration_id = static_cast<uint64_t>(callbackValue);
        callbacks.userdata = &callbackOwnerValue;
        callbacks.userdata_destructor = userdata_destructor;
        callbacks.managed_function = managed_callback;
        int32_t ownerTransferred = 0;
        int32_t errorObject = 0;
        REQUIRE(luau_host_push_callback(root.state, &callbacks, bytes("conformance_callback"), 20, 0, &ownerTransferred, &errorObject) == LUAU_HOST_STATUS_OK);
        REQUIRE(ownerTransferred == 1);
        REQUIRE(errorObject == 0);
    }

    REQUIRE(luau_host_pcall(root.state, 0, 1, 0) == LUAU_HOST_STATUS_OK);
    REQUIRE(callbackInvocations == 1);
    REQUIRE(callbackPushStatus == LUAU_HOST_STATUS_OK);
    int32_t isInteger = 0;
    REQUIRE(luau_host_to_integer64(root.state, -1, &isInteger) == callbackValue);
    REQUIRE(isInteger != 0);
    REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_collect(root.state) == LUAU_HOST_STATUS_OK);
    REQUIRE(destructorInvocations == 1);
    REQUIRE(destructorPayload == callbackOwnerValue);

    destructorInvocations = 0;
    destructorPayload = 0;

    {
        luau_host_callback_table callbacks = callback_table();
        callbacks.userdata_destructor = userdata_destructor;
        void* payload = nullptr;
        REQUIRE(luau_host_userdata_create_with_destructor(root.state, sizeof(int), &callbacks, &payload) == LUAU_HOST_STATUS_OK);
        REQUIRE(payload != nullptr);
        *static_cast<int*>(payload) = 1234;
    }

    for (int index = 0; index < 255; ++index)
        REQUIRE(luau_host_push_nil(root.state) == LUAU_HOST_STATUS_OK);
    {
        luau_host_callback_table callbacks = callback_table();
        callbacks.managed_function = managed_callback;
        int32_t ownerTransferred = 123;
        int32_t errorObject = 123;
        REQUIRE(
            luau_host_push_callback(
                root.state,
                &callbacks,
                bytes("x"),
                std::numeric_limits<uint64_t>::max(),
                255,
                &ownerTransferred,
                &errorObject) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
        REQUIRE(ownerTransferred == 0);
        REQUIRE(errorObject == 0);
        REQUIRE(luau_host_stack_get_top(root.state) == 256);
    }
    REQUIRE(luau_host_stack_set_top(root.state, 1) == LUAU_HOST_STATUS_OK);

    int rejectedMaximumUpvalueOwner = 255;
    for (int index = 0; index < 255; ++index)
        REQUIRE(luau_host_push_nil(root.state) == LUAU_HOST_STATUS_OK);
    {
        luau_host_callback_table callbacks = callback_table();
        callbacks.userdata = &rejectedMaximumUpvalueOwner;
        callbacks.userdata_destructor = userdata_destructor;
        callbacks.managed_function = managed_callback;
        int32_t ownerTransferred = 123;
        int32_t errorObject = 123;
        REQUIRE(
            luau_host_push_callback(
                root.state,
                &callbacks,
                bytes("owned_maximum_upvalues"),
                22,
                255,
                &ownerTransferred,
                &errorObject) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
        REQUIRE(ownerTransferred == 0);
        REQUIRE(errorObject == 0);
        REQUIRE(destructorInvocations == 0);
        REQUIRE(luau_host_stack_get_top(root.state) == 256);
    }
    REQUIRE(luau_host_stack_set_top(root.state, 1) == LUAU_HOST_STATUS_OK);

    for (int index = 0; index < 255; ++index)
        REQUIRE(luau_host_push_nil(root.state) == LUAU_HOST_STATUS_OK);
    {
        luau_host_callback_table callbacks = callback_table();
        callbacks.managed_function = managed_callback;
        int32_t ownerTransferred = 123;
        int32_t errorObject = 123;
        REQUIRE(
            luau_host_push_callback(
                root.state,
                &callbacks,
                nullptr,
                0,
                255,
                &ownerTransferred,
                &errorObject) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
        REQUIRE(ownerTransferred == 0);
        REQUIRE(errorObject == 0);
        REQUIRE(luau_host_stack_get_top(root.state) == 256);
    }
    REQUIRE(luau_host_stack_set_top(root.state, 1) == LUAU_HOST_STATUS_OK);

    int maximumUpvalueOwner = 254;
    for (int index = 0; index < 254; ++index)
        REQUIRE(luau_host_push_nil(root.state) == LUAU_HOST_STATUS_OK);
    {
        luau_host_callback_table callbacks = callback_table();
        callbacks.userdata = &maximumUpvalueOwner;
        callbacks.userdata_destructor = userdata_destructor;
        callbacks.managed_function = managed_callback;
        int32_t ownerTransferred = 0;
        int32_t errorObject = 0;
        REQUIRE(
            luau_host_push_callback(
                root.state,
                &callbacks,
                bytes("maximum_upvalues"),
                16,
                254,
                &ownerTransferred,
                &errorObject) == LUAU_HOST_STATUS_OK);
        REQUIRE(ownerTransferred == 1);
        REQUIRE(errorObject == 0);
    }
    REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);

    REQUIRE(destructorInvocations == 0);
    root.close();
    REQUIRE(destructorInvocations == 2);
    REQUIRE(userdataPayloadDestructions == 1);
    REQUIRE(maximumOwnerDestructions == 1);
}

luau_host_state* suspendedCallbackState = nullptr;

int32_t LUAU_HOST_CALL suspending_callback(luau_host_state* state)
{
    suspendedCallbackState = state;
    return luau_host_suspend(state);
}

void test_nested_callback_suspension()
{
    Root root = create_root();
    REQUIRE(luau_host_suspend(nullptr) == 0);
    REQUIRE(luau_host_suspend(root.state) == 0);
    REQUIRE(luau_host_resume_target(nullptr) == nullptr);
    REQUIRE(luau_host_resume_target(root.state) == root.state);
    int32_t count = 0;
    REQUIRE(luau_host_open_library(root.state, LUAU_HOST_LIBRARY_BASE, &count) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_open_library(root.state, LUAU_HOST_LIBRARY_COROUTINE, &count) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);
    auto callbacks = callback_table();
    callbacks.managed_function = suspending_callback;
    REQUIRE(luau_host_push_callback(root.state, &callbacks, nullptr, 0, 0, nullptr, nullptr) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_global_set(root.state, bytes("host")) == LUAU_HOST_STATUS_OK);

    for (int action = 0; action < 3; ++action)
    {
        suspendedCallbackState = nullptr;
        REQUIRE(compile_and_load(root.state,
            "return coroutine.wrap(function() "
            "local co = coroutine.create(function() return host(42) end) "
            "return coroutine.resume(co) end)()", "@nested-host-suspension") == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_resume(root.state, nullptr, 0) == LUAU_HOST_STATUS_BREAK);
        auto target = luau_host_resume_target(root.state);
        REQUIRE(target == suspendedCallbackState);
        REQUIRE(target != root.state);
        REQUIRE(luau_host_stack_get_top(target) == 1);
        int32_t isNumber = 0;
        REQUIRE(luau_host_to_number(target, 1, &isNumber) == 42.0);
        REQUIRE(isNumber != 0);
        // The waiting Lua frames must retain all intermediate coroutines.
        REQUIRE(luau_host_collect(root.state) == LUAU_HOST_STATUS_OK);
        if (action == 0)
        {
            REQUIRE(luau_host_push_number(target, 43) == LUAU_HOST_STATUS_OK);
            REQUIRE(luau_host_resume(root.state, nullptr, 1) == LUAU_HOST_STATUS_OK);
            REQUIRE(luau_host_stack_get_top(root.state) == 2);
            REQUIRE(luau_host_to_boolean(root.state, 1) != 0);
            REQUIRE(luau_host_to_number(root.state, 2, &isNumber) == 43.0);
        }
        else if (action == 1)
        {
            REQUIRE(luau_host_push_string(target, bytes("failure"), 7) == LUAU_HOST_STATUS_OK);
            REQUIRE(luau_host_resume_error(root.state, nullptr) == LUAU_HOST_STATUS_OK);
            REQUIRE(luau_host_stack_get_top(root.state) == 2);
            REQUIRE(luau_host_to_boolean(root.state, 1) == 0);
            REQUIRE(string_at(root.state, 2) == "failure");
        }
        else
        {
            REQUIRE(luau_host_thread_reset(root.state) == LUAU_HOST_STATUS_OK);
            REQUIRE(luau_host_stack_get_top(target) == 0);
            REQUIRE(luau_host_thread_status(target) == LUAU_HOST_STATUS_OK);
        }
        REQUIRE(luau_host_resume_target(root.state) == root.state);
        REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);
    }
}

void test_rejected_coroutine_suspension_does_not_retain_child()
{
    const std::array<const char*, 4> bodies = {{
        "local proxy = setmetatable({}, {__index = function() return coroutine.resume(co) end}); return proxy.value",
        "table.sort({2, 1}, function(a, b) coroutine.resume(co); return a < b end)",
        "return string.gsub('x', '.', function() return coroutine.resume(co) end)",
        "return coroutine.resume(co)",
    }};
    for (size_t index = 0; index < bodies.size(); ++index)
    {
        Root root = create_root();
        for (auto library : {LUAU_HOST_LIBRARY_BASE, LUAU_HOST_LIBRARY_COROUTINE, LUAU_HOST_LIBRARY_TABLE, LUAU_HOST_LIBRARY_STRING})
        {
            int32_t count = 0;
            REQUIRE(luau_host_open_library(root.state, library, &count) == LUAU_HOST_STATUS_OK);
        }
        REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);
        auto callbacks = callback_table();
        callbacks.managed_function = suspending_callback;
        REQUIRE(luau_host_push_callback(root.state, &callbacks, nullptr, 0, 0, nullptr, nullptr) == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_global_set(root.state, bytes("host")) == LUAU_HOST_STATUS_OK);
        const std::string source =
            "local co = coroutine.create(function() return host(42) end); return pcall(function() " +
            std::string(bodies[index]) + " end)";
        REQUIRE(compile_and_load(root.state, source, "@rejected-coroutine-suspension") == LUAU_HOST_STATUS_OK);
        // The last case exercises the same protected entry used by require.
        const auto status = index == bodies.size() - 1
            ? luau_host_pcall(root.state, 0, 2, 0)
            : luau_host_resume(root.state, nullptr, 0);
        REQUIRE(status == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_to_boolean(root.state, 1) == 0);
        REQUIRE(string_at(root.state, 2).find("attempt to break across") != std::string::npos);
        // Check the link before collecting the now-unreachable child: a stale
        // raw pointer must never survive a failed parent break.
        REQUIRE(luau_host_resume_target(root.state) == root.state);
        REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_collect(root.state) == LUAU_HOST_STATUS_OK);
        REQUIRE(compile_and_load(root.state, "local a, b = ...; return a + b", "@after-rejected-suspension") == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_push_number(root.state, 1) == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_push_number(root.state, 2) == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_resume(root.state, nullptr, 2) == LUAU_HOST_STATUS_OK);
        int32_t isNumber = 0;
        REQUIRE(luau_host_to_number(root.state, -1, &isNumber) == 3.0);
        REQUIRE(isNumber != 0);
    }
}

void test_callback_return_validation_and_recovery()
{
    Root root = create_root();

    auto pushInvalidCallback = [](luau_host_state* state) {
        luau_host_callback_table callbacks = callback_table();
        callbacks.managed_function = invalid_return_callback;
        int32_t ownerTransferred = 123;
        int32_t errorObject = 123;
        REQUIRE(
            luau_host_push_callback(
                state,
                &callbacks,
                nullptr,
                0,
                0,
                &ownerTransferred,
                &errorObject) == LUAU_HOST_STATUS_OK);
        REQUIRE(ownerTransferred == 0);
        REQUIRE(errorObject == 0);
    };

    invalidCallbackReturnsTopPlusOne = true;
    pushInvalidCallback(root.state);
    REQUIRE(luau_host_pcall(root.state, 0, 0, 0) == LUAU_HOST_STATUS_LUA_ERROR);
    REQUIRE(luau_host_stack_get_top(root.state) == 1);
    REQUIRE(!string_at(root.state, -1).empty());
    REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);

    invalidCallbackReturnsTopPlusOne = false;
    invalidCallbackReturn = std::numeric_limits<int32_t>::max();
    pushInvalidCallback(root.state);
    REQUIRE(luau_host_pcall(root.state, 0, 0, 0) == LUAU_HOST_STATUS_LUA_ERROR);
    REQUIRE(luau_host_stack_get_top(root.state) == 1);
    REQUIRE(!string_at(root.state, -1).empty());
    REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);

    // -2 is the explicit immediate-failure sentinel. The callback leaves its
    // failure value on top of a non-empty frame; that exact top value becomes
    // the Luau error rather than the host's generic invalid-return token.
    callbackPushStatus = LUAU_HOST_STATUS_INVALID_ARGUMENT;
    {
        luau_host_callback_table callbacks = callback_table();
        callbacks.managed_function = failure_return_callback;
        int32_t ownerTransferred = 123;
        int32_t errorObject = 123;
        REQUIRE(
            luau_host_push_callback(
                root.state,
                &callbacks,
                nullptr,
                0,
                0,
                &ownerTransferred,
                &errorObject) == LUAU_HOST_STATUS_OK);
        REQUIRE(ownerTransferred == 0);
        REQUIRE(errorObject == 0);
    }
    REQUIRE(luau_host_pcall(root.state, 0, 0, 0) == LUAU_HOST_STATUS_LUA_ERROR);
    REQUIRE(callbackPushStatus == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_get_top(root.state) == 1);
    REQUIRE(string_at(root.state, -1) == "managed callback failure token");
    REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);

    luau_host_state* child = nullptr;
    REQUIRE(luau_host_thread_create(root.state, &child) == LUAU_HOST_STATUS_OK);
    REQUIRE(child != nullptr);

    for (const int32_t invalidResult : {-3, -2, -1})
    {
        invalidCallbackReturn = invalidResult;
        pushInvalidCallback(child);
        REQUIRE(luau_host_resume(child, root.state, 0) == LUAU_HOST_STATUS_LUA_ERROR);
        REQUIRE(luau_host_thread_status(child) == LUAU_HOST_STATUS_LUA_ERROR);
        REQUIRE(luau_host_stack_get_top(child) == 1);
        REQUIRE(!string_at(child, -1).empty());
        REQUIRE(luau_host_thread_reset(child) == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_thread_status(child) == LUAU_HOST_STATUS_OK);
    }

    REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);
    REQUIRE(compile_and_load(root.state, "return 7", "@callback-return-recovery") == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_pcall(root.state, 0, 1, 0) == LUAU_HOST_STATUS_OK);
    int32_t isNumber = 0;
    REQUIRE(luau_host_to_number(root.state, -1, &isNumber) == 7.0);
    REQUIRE(isNumber != 0);
}

void test_userdata_wrapper_visibility_and_destructor_lifetime()
{
    userdataDestructorPhaseCounts.fill(0);
    userdataDestructorPointerMismatches = 0;
    expectedUserdataDestructorPointer = nullptr;
    userdataDestructorPhase = 0;

    Root root = create_root();
    luau_host_callback_table callbacks = callback_table();
    callbacks.userdata_destructor = recording_userdata_destructor;

    void* sized = nullptr;
    REQUIRE(
        luau_host_userdata_create_with_destructor(root.state, 8, &callbacks, &sized) ==
        LUAU_HOST_STATUS_OK);
    REQUIRE(sized != nullptr);
    REQUIRE(reinterpret_cast<uintptr_t>(sized) % alignof(std::max_align_t) == 0);
    REQUIRE(luau_host_object_length(root.state, -1) == 8);
    REQUIRE(luau_host_to_userdata(root.state, -1) == sized);
    REQUIRE(luau_host_to_pointer(root.state, -1) == sized);
    std::memset(sized, 0x5a, 8);

    expectedUserdataDestructorPointer = sized;
    userdataDestructorPhase = 1;
    REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_collect(root.state) == LUAU_HOST_STATUS_OK);
    REQUIRE(userdataDestructorPhaseCounts[1] == 1);
    REQUIRE(userdataDestructorPointerMismatches == 0);

    void* zeroCollected = nullptr;
    REQUIRE(
        luau_host_userdata_create_with_destructor(root.state, 0, &callbacks, &zeroCollected) ==
        LUAU_HOST_STATUS_OK);
    REQUIRE(zeroCollected != nullptr);
    REQUIRE(luau_host_object_length(root.state, -1) == 0);
    REQUIRE(luau_host_to_userdata(root.state, -1) == zeroCollected);
    REQUIRE(luau_host_to_pointer(root.state, -1) == zeroCollected);

    expectedUserdataDestructorPointer = zeroCollected;
    userdataDestructorPhase = 2;
    REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_collect(root.state) == LUAU_HOST_STATUS_OK);
    REQUIRE(userdataDestructorPhaseCounts[2] == 1);
    REQUIRE(userdataDestructorPointerMismatches == 0);

    // Ordinary tagged userdata can contain the wrapper's private magic bytes
    // without being shifted: the reserved destructor tag is also required.
    constexpr uint64_t mimickedPrivateMagic = UINT64_C(0x6c75617575646174);
    void* ordinary = nullptr;
    REQUIRE(luau_host_userdata_create(root.state, 32, 0, &ordinary) == LUAU_HOST_STATUS_OK);
    REQUIRE(ordinary != nullptr);
    std::memcpy(ordinary, &mimickedPrivateMagic, sizeof(mimickedPrivateMagic));
    REQUIRE(luau_host_object_length(root.state, -1) == 32);
    REQUIRE(luau_host_to_userdata(root.state, -1) == ordinary);
    REQUIRE(luau_host_to_pointer(root.state, -1) == ordinary);
    REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);

    int lightPayload = 42;
    REQUIRE(luau_host_push_light_userdata(root.state, &lightPayload, 0) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_to_light_userdata(root.state, -1) == &lightPayload);
    REQUIRE(luau_host_to_userdata(root.state, -1) == &lightPayload);
    REQUIRE(luau_host_to_pointer(root.state, -1) == &lightPayload);
    REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);

    void* zeroClosed = nullptr;
    REQUIRE(
        luau_host_userdata_create_with_destructor(root.state, 0, &callbacks, &zeroClosed) ==
        LUAU_HOST_STATUS_OK);
    REQUIRE(zeroClosed != nullptr);
    REQUIRE(luau_host_object_length(root.state, -1) == 0);
    REQUIRE(luau_host_to_userdata(root.state, -1) == zeroClosed);
    REQUIRE(luau_host_to_pointer(root.state, -1) == zeroClosed);

    expectedUserdataDestructorPointer = zeroClosed;
    userdataDestructorPhase = 3;
    root.close();
    REQUIRE(userdataDestructorPhaseCounts[3] == 1);
    REQUIRE(userdataDestructorPointerMismatches == 0);
    REQUIRE(
        userdataDestructorPhaseCounts[1] +
            userdataDestructorPhaseCounts[2] +
            userdataDestructorPhaseCounts[3] ==
        3);
}

void test_gc_interrupt_is_observation_only()
{
    reset_interrupt_kind_counts();
    Root root = create_root();
    REQUIRE(
        compile_and_load(
            root.state,
            "local retained = {} "
            "for i = 1, 200000 do retained[i] = {i, i + 1} end "
            "return #retained",
            "@gc-observation-only") == LUAU_HOST_STATUS_OK);

    luau_host_callback_table callbacks = callback_table();
    callbacks.interrupt_poll = gc_observation_poll;
    REQUIRE(luau_host_interrupt_install(root.state, &callbacks) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_pcall(root.state, 0, 1, 0) == LUAU_HOST_STATUS_OK);
    luau_host_interrupt_uninstall(root.state);

    REQUIRE(gcInterruptKinds > 0);
    REQUIRE(invalidInterruptKinds == 0);
    REQUIRE(luau_host_thread_status(root.state) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_get_top(root.state) == 1);
}

void test_interrupt_yield_and_recovery()
{
    interruptPolls = 0;
    reset_interrupt_kind_counts();
    Root root = create_root();
    luau_host_state* child = nullptr;
    REQUIRE(luau_host_thread_create(root.state, &child) == LUAU_HOST_STATUS_OK);
    REQUIRE(child != nullptr);
    REQUIRE(compile_and_load(child, "while true do end", "@interrupt-yield") == LUAU_HOST_STATUS_OK);

    luau_host_callback_table callbacks = callback_table();
    callbacks.interrupt_poll = interrupt_poll;
    REQUIRE(luau_host_interrupt_install(child, &callbacks) == LUAU_HOST_STATUS_OK);
    const luau_host_status result = luau_host_resume(child, root.state, 0);
    luau_host_interrupt_uninstall(child);

    REQUIRE(result == LUAU_HOST_STATUS_BREAK);
    REQUIRE(interruptPolls > 0);
    REQUIRE(invalidInterruptKinds == 0);
    REQUIRE(executionInterruptKinds + gcInterruptKinds == interruptPolls);
    REQUIRE(luau_host_thread_status(child) == LUAU_HOST_STATUS_BREAK);
    REQUIRE(luau_host_thread_reset(child) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_thread_status(child) == LUAU_HOST_STATUS_OK);

    // Installing the same poll twice must not double-count the state.  A
    // single uninstall must permit a different function pointer, matching the
    // Unity domain-reload lifecycle exercised by the retired bridge test.
    REQUIRE(luau_host_interrupt_install(child, &callbacks) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_interrupt_install(child, &callbacks) == LUAU_HOST_STATUS_OK);
    luau_host_interrupt_uninstall(child);

    luau_host_callback_table continueCallbacks = callback_table();
    continueCallbacks.interrupt_poll = continue_interrupt_poll;
    REQUIRE(luau_host_interrupt_install(child, &continueCallbacks) == LUAU_HOST_STATUS_OK);
    luau_host_interrupt_uninstall(child);
    REQUIRE(luau_host_interrupt_install(child, &callbacks) == LUAU_HOST_STATUS_OK);
    luau_host_interrupt_uninstall(child);
}

void test_nonyieldable_interrupt_hard_unwind_and_recovery()
{
    nonYieldableInterruptPolls = 0;
    reset_interrupt_kind_counts();
    Root root = create_root();
    int32_t resultCount = 0;
    REQUIRE(
        luau_host_open_library(root.state, LUAU_HOST_LIBRARY_STRING, &resultCount) ==
        LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);

    luau_host_state* child = nullptr;
    REQUIRE(luau_host_thread_create(root.state, &child) == LUAU_HOST_STATUS_OK);
    REQUIRE(child != nullptr);
    REQUIRE(
        compile_and_load(
            child,
            "local haystack = string.rep('x', 100); "
            "local pattern = string.rep('x?', 100) .. string.rep('x', 100); "
            "return string.find(haystack, pattern)",
            "@nonyieldable-interrupt") == LUAU_HOST_STATUS_OK);

    luau_host_callback_table callbacks = callback_table();
    callbacks.interrupt_poll = nonyieldable_interrupt_poll;
    REQUIRE(luau_host_interrupt_install(child, &callbacks) == LUAU_HOST_STATUS_OK);
    const luau_host_status result = luau_host_resume(child, root.state, 0);
    luau_host_interrupt_uninstall(child);

    REQUIRE(result == LUAU_HOST_STATUS_CANCELED);
    REQUIRE(nonYieldableInterruptPolls > 0);
    REQUIRE(invalidInterruptKinds == 0);
    REQUIRE(executionInterruptKinds + gcInterruptKinds >= nonYieldableInterruptPolls);
    REQUIRE(luau_host_thread_status(child) == LUAU_HOST_STATUS_CANCELED);
    REQUIRE(luau_host_thread_status(child) == LUAU_HOST_STATUS_CANCELED);
    REQUIRE(luau_host_memory_reset_failure(root.state) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_thread_reset(child) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_thread_status(child) == LUAU_HOST_STATUS_OK);
}

void test_multi_root_interrupt_uninstall_preserves_other_root()
{
    interruptPolls = 0;
    alternateInterruptPolls = 0;
    reset_interrupt_kind_counts();
    Root firstRoot = create_root();
    Root secondRoot = create_root();
    luau_host_state* firstChild = nullptr;
    luau_host_state* secondChild = nullptr;
    REQUIRE(luau_host_thread_create(firstRoot.state, &firstChild) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_thread_create(secondRoot.state, &secondChild) == LUAU_HOST_STATUS_OK);
    REQUIRE(compile_and_load(firstChild, "while true do end", "@first-root-interrupt") == LUAU_HOST_STATUS_OK);
    REQUIRE(compile_and_load(secondChild, "while true do end", "@second-root-interrupt") == LUAU_HOST_STATUS_OK);

    luau_host_callback_table firstCallbacks = callback_table();
    firstCallbacks.interrupt_poll = interrupt_poll;
    luau_host_callback_table secondCallbacks = callback_table();
    secondCallbacks.interrupt_poll = alternate_interrupt_poll;
    REQUIRE(luau_host_interrupt_install(firstChild, &firstCallbacks) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_interrupt_install(secondChild, &secondCallbacks) == LUAU_HOST_STATUS_OK);

    // Independent roots own independent poll pointers. Removing one must not
    // disturb the other's installation.
    luau_host_interrupt_uninstall(firstChild);
    REQUIRE(luau_host_resume(secondChild, secondRoot.state, 0) == LUAU_HOST_STATUS_BREAK);
    REQUIRE(interruptPolls == 0);
    REQUIRE(alternateInterruptPolls > 0);
    REQUIRE(invalidInterruptKinds == 0);
    REQUIRE(executionInterruptKinds + gcInterruptKinds == alternateInterruptPolls);
    REQUIRE(luau_host_thread_reset(secondChild) == LUAU_HOST_STATUS_OK);
    luau_host_interrupt_uninstall(secondChild);

    // Reinstalling a different poll on either root remains legal.
    luau_host_callback_table continueCallbacks = callback_table();
    continueCallbacks.interrupt_poll = continue_interrupt_poll;
    REQUIRE(luau_host_interrupt_install(firstChild, &continueCallbacks) == LUAU_HOST_STATUS_OK);
    luau_host_interrupt_uninstall(firstChild);
}

// One host-visible frame holds at most this many values (LUAI_MAXCSTACK).
constexpr int32_t kFrameSlotLimit = LUAU_HOST_FRAME_SLOT_LIMIT;

// Each refused call is repeated well past Luau's five-slot EXTRA_STACK reserve.
// Before the host reserved error-object slots, every refusal appended one
// unchecked error object, so this loop wrote past the stack allocation.
constexpr int kRefusalRepetitions = 32;

// 8003 values: the VM can build this frame, but it exceeds the frame limit.
constexpr const char* kOverfullReturn = "local t = table.create(7999, 1) return 1, 2, 3, 4, table.unpack(t)";

void require_stack_limit(luau_host_state* state, luau_host_status status, int32_t expectedTop)
{
    REQUIRE(status == LUAU_HOST_STATUS_STACK_LIMIT);
    REQUIRE(luau_host_stack_get_top(state) == expectedTop);
}

// Every protected operation that consumes nothing, or that grows the frame,
// must refuse a full frame without touching it.
void require_full_frame_refusals(luau_host_state* state, int32_t tableIndex, int32_t reference, const CompiledBuffer& bytecode)
{
    const int32_t top = luau_host_stack_get_top(state);
    luau_host_callback_table callbacks = callback_table();
    callbacks.managed_function = managed_callback;
    luau_host_callback_table destructorCallbacks = callback_table();
    destructorCallbacks.userdata_destructor = userdata_destructor;

    for (int attempt = 0; attempt < kRefusalRepetitions; ++attempt)
    {
        require_stack_limit(state, luau_host_push_nil(state), top);
        require_stack_limit(state, luau_host_push_boolean(state, 1), top);
        require_stack_limit(state, luau_host_push_integer(state, 1), top);
        require_stack_limit(state, luau_host_push_number(state, 1.0), top);
        require_stack_limit(state, luau_host_push_vector(state, 1.0f, 2.0f, 3.0f), top);
        require_stack_limit(state, luau_host_push_string(state, bytes("x"), 1), top);
        require_stack_limit(state, luau_host_push_light_userdata(state, nullptr, 0), top);
        require_stack_limit(state, luau_host_push_value(state, tableIndex), top);
        int32_t isMainThread = 0;
        require_stack_limit(state, luau_host_push_thread(state, &isMainThread), top);

        int32_t ownerTransferred = 123;
        int32_t errorObject = 123;
        require_stack_limit(
            state,
            luau_host_push_callback(state, &callbacks, nullptr, 0, 0, &ownerTransferred, &errorObject),
            top);
        REQUIRE(ownerTransferred == 0);
        REQUIRE(errorObject == 0);

        void* payload = reinterpret_cast<void*>(static_cast<uintptr_t>(1));
        require_stack_limit(state, luau_host_userdata_create(state, 8, 0, &payload), top);
        REQUIRE(payload == nullptr);
        payload = reinterpret_cast<void*>(static_cast<uintptr_t>(1));
        require_stack_limit(state, luau_host_userdata_create_with_destructor(state, 8, &destructorCallbacks, &payload), top);
        REQUIRE(payload == nullptr);
        payload = reinterpret_cast<void*>(static_cast<uintptr_t>(1));
        require_stack_limit(state, luau_host_buffer_create(state, 8, &payload), top);
        REQUIRE(payload == nullptr);

        int32_t result = 123;
        require_stack_limit(state, luau_host_table_create(state, 0, 0), top);
        require_stack_limit(state, luau_host_table_clone(state, tableIndex), top);
        require_stack_limit(state, luau_host_table_clear(state, tableIndex), top);
        require_stack_limit(state, luau_host_table_set_readonly(state, tableIndex, 0), top);
        require_stack_limit(state, luau_host_metatable_get(state, tableIndex, &result), top);
        require_stack_limit(state, luau_host_global_get(state, bytes("print"), &result), top);
        require_stack_limit(state, luau_host_global_push(state), top);

        int32_t newReference = 123;
        require_stack_limit(state, luau_host_reference_create(state, tableIndex, &newReference), top);
        REQUIRE(newReference <= 0);
        require_stack_limit(state, luau_host_reference_push(state, reference, &result), top);

        const uint8_t* text = bytes("unchanged");
        uint64_t length = 123;
        require_stack_limit(state, luau_host_to_string(state, tableIndex, &text, &length), top);
        REQUIRE(text == nullptr);
        REQUIRE(length == 0);
        require_stack_limit(state, luau_host_to_display_string(state, tableIndex, &text, &length), top);

        luau_host_status loadStatus = LUAU_HOST_STATUS_INVALID_ARGUMENT;
        require_stack_limit(
            state,
            luau_host_load(state, bytes("@full-frame"), bytecode.value.data, bytecode.value.size, 0, &loadStatus),
            top);
        REQUIRE(loadStatus == LUAU_HOST_STATUS_INVALID_ARGUMENT);

        luau_host_state* child = reinterpret_cast<luau_host_state*>(static_cast<uintptr_t>(1));
        require_stack_limit(state, luau_host_thread_create(state, &child), top);
        REQUIRE(child == nullptr);

        require_stack_limit(state, luau_host_collect(state), top);
        require_stack_limit(state, luau_host_open_library(state, LUAU_HOST_LIBRARY_MATH, &result), top);
        require_stack_limit(state, luau_host_sandbox_thread(state), top);

        // Neutral observers and preconditions stay neutral on a full frame.
        int32_t canGrow = 123;
        REQUIRE(luau_host_stack_check(state, 1, &canGrow) == LUAU_HOST_STATUS_OK);
        REQUIRE(canGrow == 0);
        REQUIRE(luau_host_stack_set_top(state, std::max(top, kFrameSlotLimit) + 1) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
        REQUIRE(luau_host_stack_get_top(state) == top);
    }
}

// Shrinking and rearranging never need a free slot, so a full frame can always
// be emptied. Leaves the frame at baseTop.
void require_full_frame_recovers(luau_host_state* state, int32_t baseTop)
{
    int32_t top = luau_host_stack_get_top(state);
    REQUIRE(top > baseTop + 4);
    REQUIRE(luau_host_stack_insert(state, baseTop + 1) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_get_top(state) == top);
    REQUIRE(luau_host_stack_replace(state, baseTop + 1) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_get_top(state) == --top);
    REQUIRE(luau_host_stack_remove(state, baseTop + 1) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_get_top(state) == --top);
    REQUIRE(luau_host_stack_set_top(state, -2) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_get_top(state) == --top);
    REQUIRE(luau_host_stack_set_top(state, top) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_get_top(state) == top);
    REQUIRE(luau_host_stack_set_top(state, baseTop) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_get_top(state) == baseTop);
}

void require_reusable(luau_host_state* state, const char* chunkName)
{
    const int32_t top = luau_host_stack_get_top(state);
    REQUIRE(compile_and_load(state, "return 42", chunkName) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_pcall(state, 0, 1, 0) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_get_top(state) == top + 1);
    int32_t isNumber = 0;
    REQUIRE(luau_host_to_number(state, -1, &isNumber) == 42.0);
    REQUIRE(isNumber != 0);
    REQUIRE(luau_host_stack_set_top(state, top) == LUAU_HOST_STATUS_OK);
}

luau_host_status fillingCallbackStatus = LUAU_HOST_STATUS_OK;
int32_t fillingCallbackTop = 0;
int32_t fillingCallbackTopAfterRefusals = 0;

int32_t LUAU_HOST_CALL filling_callback(luau_host_state* state)
{
    const int32_t entryTop = luau_host_stack_get_top(state);
    luau_host_status status = LUAU_HOST_STATUS_OK;
    while ((status = luau_host_push_integer(state, 1)) == LUAU_HOST_STATUS_OK)
    {
    }

    fillingCallbackStatus = status;
    fillingCallbackTop = luau_host_stack_get_top(state);
    for (int attempt = 0; attempt < kRefusalRepetitions; ++attempt)
        (void)luau_host_push_nil(state);
    fillingCallbackTopAfterRefusals = luau_host_stack_get_top(state);
    return fillingCallbackTop - entryTop;
}

void test_stack_slot_limit_refusals_and_recovery()
{
    const CompiledBuffer bytecode = compile_source("return 1");

    // A frame the host fills itself: exactly kFrameSlotLimit values fit.
    {
        Root root = create_root();
        luau_host_state* child = nullptr;
        REQUIRE(luau_host_thread_create(root.state, &child) == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_table_create(root.state, 0, 0) == LUAU_HOST_STATUS_OK);
        int32_t reference = 0;
        REQUIRE(luau_host_reference_create(root.state, 2, &reference) == LUAU_HOST_STATUS_OK);
        for (int32_t value = 3; value <= kFrameSlotLimit; ++value)
            REQUIRE(luau_host_push_integer(root.state, value) == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_stack_get_top(root.state) == kFrameSlotLimit);

        require_full_frame_refusals(root.state, 2, reference, bytecode);

        // Consuming operations need no new slot and still run on a full frame.
        REQUIRE(luau_host_global_set(root.state, bytes("filled")) == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_stack_get_top(root.state) == kFrameSlotLimit - 1);
        REQUIRE(luau_host_push_string(root.state, bytes("key"), 3) == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_table_raw_get(root.state, 2, nullptr) == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_stack_get_top(root.state) == kFrameSlotLimit);

        // Values can move out of a full frame; moving more in is rejected.
        REQUIRE(luau_host_stack_move(root.state, child, 1) == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_stack_get_top(root.state) == kFrameSlotLimit - 1);
        REQUIRE(luau_host_stack_move(child, root.state, 1) == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_stack_get_top(root.state) == kFrameSlotLimit);
        REQUIRE(luau_host_push_integer(child, 1) == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_stack_move(child, root.state, 1) == LUAU_HOST_STATUS_INVALID_ARGUMENT);
        REQUIRE(luau_host_stack_get_top(root.state) == kFrameSlotLimit);
        REQUIRE(luau_host_stack_get_top(child) == 1);

        require_full_frame_recovers(root.state, 2);
        require_reusable(root.state, "@frame-limit/host-filled");
        REQUIRE(luau_host_reference_release(root.state, reference) == LUAU_HOST_STATUS_OK);
    }

    // A frame Luau builds past the limit: every growing operation is refused,
    // the frame can be emptied, and both the root and a child stay reusable.
    {
        Root root = create_root();
        int32_t ignored = 0;
        REQUIRE(luau_host_open_library(root.state, LUAU_HOST_LIBRARY_BASE, &ignored) == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_open_library(root.state, LUAU_HOST_LIBRARY_TABLE, &ignored) == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);

        REQUIRE(luau_host_table_create(root.state, 0, 0) == LUAU_HOST_STATUS_OK);
        int32_t reference = 0;
        REQUIRE(luau_host_reference_create(root.state, 1, &reference) == LUAU_HOST_STATUS_OK);
        REQUIRE(compile_and_load(root.state, kOverfullReturn, "@frame-limit/root") == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_pcall(root.state, 0, LUAU_HOST_MULTIPLE_RESULTS, 0) == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_stack_get_top(root.state) == 1 + 8003);

        require_full_frame_refusals(root.state, 1, reference, bytecode);
        REQUIRE(luau_host_reference_release(root.state, reference) == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_stack_get_top(root.state) == 1 + 8003);
        require_full_frame_recovers(root.state, 0);
        require_reusable(root.state, "@frame-limit/root-recovered");

        luau_host_state* child = nullptr;
        REQUIRE(luau_host_thread_create(root.state, &child) == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_table_create(child, 0, 0) == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_reference_create(child, 1, &reference) == LUAU_HOST_STATUS_OK);
        REQUIRE(compile_and_load(child, kOverfullReturn, "@frame-limit/child") == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_resume(child, root.state, 0) == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_stack_get_top(child) == 1 + 8003);

        require_full_frame_refusals(child, 1, reference, bytecode);
        REQUIRE(luau_host_reference_release(child, reference) == LUAU_HOST_STATUS_OK);
        require_full_frame_recovers(child, 0);
        REQUIRE(luau_host_thread_reset(child) == LUAU_HOST_STATUS_OK);
        REQUIRE(compile_and_load(child, "return 7", "@frame-limit/child-reused") == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_resume(child, root.state, 0) == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_stack_get_top(child) == 1);
        int32_t isNumber = 0;
        REQUIRE(luau_host_to_number(child, -1, &isNumber) == 7.0);
        REQUIRE(isNumber != 0);
        require_reusable(root.state, "@frame-limit/root-after-child");
    }

    // A managed callback that keeps returning values is refused at the frame
    // limit without growing its frame, and can still return what fit.
    {
        Root root = create_root();
        luau_host_callback_table callbacks = callback_table();
        callbacks.managed_function = filling_callback;
        REQUIRE(luau_host_push_callback(root.state, &callbacks, nullptr, 0, 0, nullptr, nullptr) == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_pcall(root.state, 0, LUAU_HOST_MULTIPLE_RESULTS, 0) == LUAU_HOST_STATUS_OK);
        REQUIRE(fillingCallbackStatus == LUAU_HOST_STATUS_STACK_LIMIT);
        REQUIRE(fillingCallbackTop == kFrameSlotLimit);
        REQUIRE(fillingCallbackTopAfterRefusals == kFrameSlotLimit);
        REQUIRE(luau_host_stack_get_top(root.state) == kFrameSlotLimit);
        require_full_frame_recovers(root.state, 0);
        require_reusable(root.state, "@frame-limit/callback");
    }
}

// When the stack cannot grow, a failed operation still reports its allocation
// failure in the normal shape while a slot for the error object remains, and
// is refused without touching the stack once none does.
void test_stack_growth_failure_without_an_error_slot()
{
    luau_host_state_options options = {};
    options.struct_size = sizeof(options);
    options.version = LUAU_HOST_STATE_OPTIONS_VERSION;
    options.flags = LUAU_HOST_STATE_OPTION_TRACK_MEMORY;
    Root probe = create_root(&options);
    luau_host_memory_info baseline = {};
    baseline.struct_size = sizeof(baseline);
    REQUIRE(luau_host_memory_get(probe.state, &baseline) == LUAU_HOST_STATUS_OK);

    // Room for a few hundred stack slots, far below the frame limit.
    options.memory_limit_bytes = baseline.current_bytes + UINT64_C(16) * 1024;
    Root root = create_root(&options);

    luau_host_status status = LUAU_HOST_STATUS_OK;
    int32_t pushed = 0;
    while ((status = luau_host_push_integer(root.state, pushed)) == LUAU_HOST_STATUS_OK)
        ++pushed;

    REQUIRE(pushed < kFrameSlotLimit);
    REQUIRE(status == LUAU_HOST_STATUS_MEMORY_QUOTA);
    REQUIRE(luau_host_stack_get_top(root.state) == pushed + 1);
    REQUIRE(string_at(root.state, -1) == "not enough memory");

    // The unconsumed error object took the last physical slot. Nothing is left
    // for another error object, so every further attempt is refused.
    for (int attempt = 0; attempt < kRefusalRepetitions; ++attempt)
    {
        require_stack_limit(root.state, luau_host_push_nil(root.state), pushed + 1);
        int32_t reference = 123;
        require_stack_limit(root.state, luau_host_reference_create(root.state, 1, &reference), pushed + 1);
    }

    luau_host_memory_info failed = {};
    failed.struct_size = sizeof(failed);
    REQUIRE(luau_host_memory_get(root.state, &failed) == LUAU_HOST_STATUS_OK);
    REQUIRE(failed.failure == LUAU_HOST_ALLOCATOR_FAILURE_QUOTA);

    REQUIRE(luau_host_stack_set_top(root.state, -2) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_memory_reset_failure(root.state) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_push_integer(root.state, 1) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_get_top(root.state) == 1);
}

int32_t fullFrameFailureEntryTop = 0;
luau_host_status fullFrameFailureRefusal = LUAU_HOST_STATUS_OK;

int32_t LUAU_HOST_CALL full_frame_failure_callback(luau_host_state* state)
{
    // The arguments fill the frame, so nothing can be appended above them.
    fullFrameFailureEntryTop = luau_host_stack_get_top(state);
    fullFrameFailureRefusal = luau_host_push_nil(state);

    // A failed callback's arguments are dead. Dropping them always leaves room
    // for the failure value.
    if (luau_host_stack_set_top(state, 0) != LUAU_HOST_STATUS_OK)
        return 0;
    constexpr char failure[] = "full frame callback failure";
    callbackPushStatus = luau_host_push_string(
        state,
        bytes(failure),
        static_cast<uint64_t>(sizeof(failure) - 1));
    return callbackPushStatus == LUAU_HOST_STATUS_OK ? LUAU_HOST_CALLBACK_ERROR : 0;
}

// A callback whose arguments fill its frame can still raise a failure that a
// script pcall or xpcall catches.
void test_callback_failure_on_a_full_frame()
{
    Root root = create_root();
    int32_t ignored = 0;
    REQUIRE(luau_host_open_library(root.state, LUAU_HOST_LIBRARY_BASE, &ignored) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_open_library(root.state, LUAU_HOST_LIBRARY_TABLE, &ignored) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);

    luau_host_callback_table callbacks = callback_table();
    callbacks.managed_function = full_frame_failure_callback;
    REQUIRE(luau_host_push_callback(root.state, &callbacks, nullptr, 0, 0, nullptr, nullptr) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_global_set(root.state, bytes("fail")) == LUAU_HOST_STATUS_OK);

    const char* const caught[] = {
        "local ok, e = pcall(function() return fail(0, table.unpack(table.create(7999, 0))) end) return ok, e",
        "local ok, e = xpcall(function() return fail(0, table.unpack(table.create(7999, 0))) end, "
        "function(e) return e end) return ok, e",
    };
    for (const char* source : caught)
    {
        for (int attempt = 0; attempt < kRefusalRepetitions; ++attempt)
        {
            fullFrameFailureEntryTop = 0;
            fullFrameFailureRefusal = LUAU_HOST_STATUS_OK;
            callbackPushStatus = LUAU_HOST_STATUS_INVALID_ARGUMENT;
            REQUIRE(compile_and_load(root.state, source, "@frame-limit/callback-failure") == LUAU_HOST_STATUS_OK);
            REQUIRE(luau_host_pcall(root.state, 0, 2, 0) == LUAU_HOST_STATUS_OK);
            REQUIRE(fullFrameFailureEntryTop == kFrameSlotLimit);
            REQUIRE(fullFrameFailureRefusal == LUAU_HOST_STATUS_STACK_LIMIT);
            REQUIRE(callbackPushStatus == LUAU_HOST_STATUS_OK);
            REQUIRE(luau_host_stack_get_top(root.state) == 2);
            REQUIRE(luau_host_to_boolean(root.state, -2) == 0);
            REQUIRE(string_at(root.state, -1) == "full frame callback failure");
            REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);
        }
    }

    // Uncaught, the same failure value is the call's error.
    REQUIRE(
        compile_and_load(
            root.state,
            "return fail(0, table.unpack(table.create(7999, 0)))",
            "@frame-limit/callback-failure-uncaught") == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_pcall(root.state, 0, LUAU_HOST_MULTIPLE_RESULTS, 0) == LUAU_HOST_STATUS_LUA_ERROR);
    REQUIRE(luau_host_stack_get_top(root.state) == 1);
    REQUIRE(string_at(root.state, -1) == "full frame callback failure");
    REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);
    require_reusable(root.state, "@frame-limit/callback-failure-recovered");
}

int metamethodInvocations = 0;

int32_t LUAU_HOST_CALL counting_metamethod(luau_host_state* state)
{
    ++metamethodInvocations;
    constexpr char text[] = "metamethod result";
    return luau_host_push_string(state, bytes(text), static_cast<uint64_t>(sizeof(text) - 1)) == LUAU_HOST_STATUS_OK
        ? 1
        : 0;
}

enum class StackEndOperation
{
    TableGet,
    TableSet,
    DisplayString,
};

luau_host_state_options tracked_memory_options(uint64_t limitBytes)
{
    luau_host_state_options options = {};
    options.struct_size = sizeof(options);
    options.version = LUAU_HOST_STATE_OPTIONS_VERSION;
    options.flags = LUAU_HOST_STATE_OPTION_TRACK_MEMORY;
    options.memory_limit_bytes = limitBytes;
    return options;
}

uint64_t current_memory(luau_host_state* state)
{
    luau_host_memory_info info = {};
    info.struct_size = sizeof(info);
    REQUIRE(luau_host_memory_get(state, &info) == LUAU_HOST_STATUS_OK);
    return info.current_bytes;
}

// Leaves [1] a child thread and [2] a table whose __index, __newindex, and
// __tostring are host callbacks, plus a large buffer held only by a reference.
void prepare_stack_end_root(luau_host_state* root, luau_host_state** child, int32_t* ballastReference)
{
    constexpr uint64_t kBallastBytes = UINT64_C(256) * 1024;

    REQUIRE(luau_host_thread_create(root, child) == LUAU_HOST_STATUS_OK);

    void* ballast = nullptr;
    REQUIRE(luau_host_buffer_create(root, kBallastBytes, &ballast) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_reference_create(root, 2, ballastReference) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_set_top(root, 1) == LUAU_HOST_STATUS_OK);

    REQUIRE(luau_host_table_create(root, 0, 0) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_table_create(root, 0, 0) == LUAU_HOST_STATUS_OK);
    luau_host_callback_table callbacks = callback_table();
    callbacks.managed_function = counting_metamethod;
    for (const char* name : {"__index", "__newindex", "__tostring"})
    {
        REQUIRE(luau_host_push_string(root, bytes(name), std::strlen(name)) == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_push_callback(root, &callbacks, nullptr, 0, 0, nullptr, nullptr) == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_table_raw_set(root, 3) == LUAU_HOST_STATUS_OK);
    }
    int32_t result = 0;
    REQUIRE(luau_host_metatable_set(root, 2, &result) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_get_top(root) == 2);
}

// Operations that only consume inputs reserve no slot, yet a metamethod call
// writes its function and arguments just past the top before growing the
// stack. That is sound only because the top never passes the physical end of
// the stack and Luau keeps a five-slot reserve beyond it. Drive those calls
// with the top exactly at the physical end: first while the memory quota stops
// the stack from growing, then once it can grow again.
void require_metamethods_at_the_physical_stack_end(StackEndOperation operation)
{
    // Measure the setup on an unlimited root, then give the real root that
    // much memory plus room for a stack of at most a few thousand slots. Luau
    // allocates small objects in 16 KB pages, so the setup cannot be estimated.
    uint64_t setupBytes = 0;
    {
        const luau_host_state_options unlimited = tracked_memory_options(0);
        Root probe = create_root(&unlimited);
        luau_host_state* probeChild = nullptr;
        int32_t probeReference = 0;
        prepare_stack_end_root(probe.state, &probeChild, &probeReference);
        setupBytes = current_memory(probe.state);
    }

    const luau_host_state_options options = tracked_memory_options(setupBytes + UINT64_C(40) * 1024);
    Root root = create_root(&options);
    luau_host_state* child = nullptr;
    int32_t ballastReference = 0;
    prepare_stack_end_root(root.state, &child, &ballastReference);

    // Fill the frame until the stack cannot grow. The failed push appends its
    // error object into the last slot, leaving the top at the physical end.
    luau_host_status status = LUAU_HOST_STATUS_OK;
    int32_t pushed = 0;
    while ((status = luau_host_push_integer(root.state, pushed)) == LUAU_HOST_STATUS_OK)
        ++pushed;
    REQUIRE(status == LUAU_HOST_STATUS_MEMORY_QUOTA);
    REQUIRE(pushed > 8);
    const int32_t endTop = 2 + pushed + 1;
    REQUIRE(endTop < kFrameSlotLimit);
    REQUIRE(luau_host_stack_get_top(root.state) == endTop);
    REQUIRE(string_at(root.state, -1) == "not enough memory");

    metamethodInvocations = 0;
    for (int attempt = 0; attempt < kRefusalRepetitions; ++attempt)
    {
        // __index: the key is consumed, so the growth failure replaces it.
        int32_t type = 123;
        REQUIRE(luau_host_table_get(root.state, 2, &type) == LUAU_HOST_STATUS_MEMORY_QUOTA);
        REQUIRE(luau_host_stack_get_top(root.state) == endTop);
        REQUIRE(string_at(root.state, -1) == "not enough memory");

        // __newindex: key and value are consumed and one error replaces them.
        REQUIRE(luau_host_table_set(root.state, 2) == LUAU_HOST_STATUS_MEMORY_QUOTA);
        REQUIRE(luau_host_stack_get_top(root.state) == endTop - 1);
        REQUIRE(string_at(root.state, -1) == "not enough memory");

        // One slot is left, so a failed push still reports its error there and
        // puts the top back at the physical end.
        REQUIRE(luau_host_push_nil(root.state) == LUAU_HOST_STATUS_MEMORY_QUOTA);
        REQUIRE(luau_host_stack_get_top(root.state) == endTop);

        // Operations that need a new slot are refused without touching the stack.
        const uint8_t* text = nullptr;
        uint64_t length = 0;
        require_stack_limit(root.state, luau_host_to_display_string(root.state, 2, &text, &length), endTop);
        require_stack_limit(root.state, luau_host_global_get(root.state, bytes("print"), &type), endTop);
    }
    REQUIRE(metamethodInvocations == 0);

    // Free the ballast through the child, whose frame still has room.
    REQUIRE(luau_host_reference_release(root.state, ballastReference) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_collect(child) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_memory_reset_failure(root.state) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_get_top(root.state) == endTop);

    // The top is still at the physical end, and the metamethod call now runs.
    switch (operation)
    {
    case StackEndOperation::TableGet:
        REQUIRE(luau_host_table_get(root.state, 2, nullptr) == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_stack_get_top(root.state) == endTop);
        REQUIRE(string_at(root.state, -1) == "metamethod result");
        break;
    case StackEndOperation::TableSet:
        REQUIRE(luau_host_table_set(root.state, 2) == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_stack_get_top(root.state) == endTop - 2);
        break;
    case StackEndOperation::DisplayString:
    {
        const uint8_t* text = nullptr;
        uint64_t length = 0;
        REQUIRE(luau_host_to_display_string(root.state, 2, &text, &length) == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_stack_get_top(root.state) == endTop + 1);
        REQUIRE(string_at(root.state, -1) == "metamethod result");
        break;
    }
    }
    REQUIRE(metamethodInvocations == 1);

    REQUIRE(luau_host_stack_set_top(root.state, 1) == LUAU_HOST_STATUS_OK);
    require_reusable(root.state, "@stack-end/recovered");
}

void test_metamethods_at_the_physical_stack_end()
{
    require_metamethods_at_the_physical_stack_end(StackEndOperation::TableGet);
    require_metamethods_at_the_physical_stack_end(StackEndOperation::TableSet);
    require_metamethods_at_the_physical_stack_end(StackEndOperation::DisplayString);
}

// Leaves [1] the destination thread, with its initial small stack, and kMoved
// integers above it on a source stack sized for exactly that.
constexpr int32_t kMovedValues = 3000;

void prepare_move_source(luau_host_state* root, luau_host_state** child)
{
    REQUIRE(luau_host_thread_create(root, child) == LUAU_HOST_STATUS_OK);
    int32_t reserved = 0;
    REQUIRE(luau_host_stack_check(root, kMovedValues, &reserved) == LUAU_HOST_STATUS_OK);
    REQUIRE(reserved != 0);
    for (int32_t value = 0; value < kMovedValues; ++value)
        REQUIRE(luau_host_push_integer(root, value) == LUAU_HOST_STATUS_OK);
}

// A move whose destination cannot grow restores the source and reports the
// failure there; the destination is untouched.
void test_stack_move_when_the_destination_cannot_grow()
{
    // The destination needs about 48 KB of stack for the moved values. Measure
    // the setup, then leave only half of that available.
    uint64_t setupBytes = 0;
    {
        const luau_host_state_options unlimited = tracked_memory_options(0);
        Root probe = create_root(&unlimited);
        luau_host_state* probeChild = nullptr;
        prepare_move_source(probe.state, &probeChild);
        setupBytes = current_memory(probe.state);
    }

    const luau_host_state_options options = tracked_memory_options(setupBytes + UINT64_C(24) * 1024);
    Root root = create_root(&options);
    luau_host_state* child = nullptr;
    prepare_move_source(root.state, &child);
    const int32_t sourceTop = 1 + kMovedValues;

    for (int attempt = 0; attempt < kRefusalRepetitions; ++attempt)
    {
        REQUIRE(luau_host_stack_move(root.state, child, kMovedValues) == LUAU_HOST_STATUS_MEMORY_QUOTA);
        REQUIRE(luau_host_stack_get_top(root.state) == sourceTop + 1);
        REQUIRE(string_at(root.state, -1) == "not enough memory");
        REQUIRE(luau_host_stack_get_top(child) == 0);
        REQUIRE(luau_host_stack_set_top(root.state, -2) == LUAU_HOST_STATUS_OK);
    }

    // A move that fits the destination's existing stack needs no memory.
    REQUIRE(luau_host_memory_reset_failure(root.state) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_move(root.state, child, 8) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_get_top(root.state) == sourceTop - 8);
    REQUIRE(luau_host_stack_get_top(child) == 8);
    REQUIRE(luau_host_stack_move(child, root.state, 8) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_get_top(root.state) == sourceTop);
    REQUIRE(luau_host_stack_get_top(child) == 0);
}

// A coroutine can be resumed with a full frame of arguments, and one that
// yielded a full frame can be resumed with an error.
void test_coroutine_resume_at_the_frame_slot_limit()
{
    Root root = create_root();
    int32_t ignored = 0;
    REQUIRE(luau_host_open_library(root.state, LUAU_HOST_LIBRARY_BASE, &ignored) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_open_library(root.state, LUAU_HOST_LIBRARY_TABLE, &ignored) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_open_library(root.state, LUAU_HOST_LIBRARY_COROUTINE, &ignored) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);
    luau_host_state* child = nullptr;
    REQUIRE(luau_host_thread_create(root.state, &child) == LUAU_HOST_STATUS_OK);

    // The function and its arguments fill the frame exactly.
    REQUIRE(compile_and_load(child, "return select('#', ...)", "@frame-limit/resume-arguments") == LUAU_HOST_STATUS_OK);
    for (int32_t value = 1; value < kFrameSlotLimit; ++value)
        REQUIRE(luau_host_push_integer(child, value) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_get_top(child) == kFrameSlotLimit);
    require_stack_limit(child, luau_host_push_nil(child), kFrameSlotLimit);
    REQUIRE(luau_host_resume(child, root.state, kFrameSlotLimit - 1) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_get_top(child) == 1);
    int32_t isNumber = 0;
    REQUIRE(luau_host_to_number(child, -1, &isNumber) == double(kFrameSlotLimit - 1));
    REQUIRE(isNumber != 0);
    REQUIRE(luau_host_stack_set_top(child, 0) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_thread_reset(child) == LUAU_HOST_STATUS_OK);

    constexpr char failure[] = "resume failure";
    auto replaceTopWithFailure = [&]() {
        REQUIRE(luau_host_stack_get_top(child) == kFrameSlotLimit);
        for (int attempt = 0; attempt < kRefusalRepetitions; ++attempt)
            require_stack_limit(child, luau_host_push_nil(child), kFrameSlotLimit);
        REQUIRE(luau_host_stack_set_top(child, -2) == LUAU_HOST_STATUS_OK);
        REQUIRE(
            luau_host_push_string(child, bytes(failure), static_cast<uint64_t>(sizeof(failure) - 1)) ==
            LUAU_HOST_STATUS_OK);
    };

    // The error raised at a full-frame yield reaches the script's pcall.
    REQUIRE(
        compile_and_load(
            child,
            "local t = table.create(7999, 1) "
            "local ok, e = pcall(function() coroutine.yield(0, table.unpack(t)) end) return ok, e",
            "@frame-limit/resume-error-caught") == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_resume(child, root.state, 0) == LUAU_HOST_STATUS_YIELDED);
    replaceTopWithFailure();
    REQUIRE(luau_host_resume_error(child, root.state) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_get_top(child) == 2);
    REQUIRE(luau_host_to_boolean(child, -2) == 0);
    REQUIRE(string_at(child, -1) == failure);
    REQUIRE(luau_host_stack_set_top(child, 0) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_thread_reset(child) == LUAU_HOST_STATUS_OK);

    // Without a handler the coroutine ends with exactly that error.
    REQUIRE(
        compile_and_load(
            child,
            "local t = table.create(7999, 1) coroutine.yield(0, table.unpack(t))",
            "@frame-limit/resume-error-uncaught") == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_resume(child, root.state, 0) == LUAU_HOST_STATUS_YIELDED);
    replaceTopWithFailure();
    REQUIRE(luau_host_resume_error(child, root.state) == LUAU_HOST_STATUS_LUA_ERROR);
    REQUIRE(string_at(child, -1) == failure);
    REQUIRE(luau_host_thread_reset(child) == LUAU_HOST_STATUS_OK);

    REQUIRE(compile_and_load(child, "return 7", "@frame-limit/resume-reused") == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_resume(child, root.state, 0) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_to_number(child, -1, &isNumber) == 7.0);
    REQUIRE(isNumber != 0);
    require_reusable(root.state, "@frame-limit/resume-root");
}

using TestFunction = void (*)();

int run_test(const char* name, TestFunction test)
{
    try
    {
        test();
        std::cout << "[PASS] " << name << '\n';
        return 0;
    }
    catch (const std::exception& exception)
    {
        std::cerr << "[FAIL] " << name << ": " << exception.what() << '\n';
        return 1;
    }
    catch (...)
    {
        std::cerr << "[FAIL] " << name << ": unknown exception\n";
        return 1;
    }
}
} // namespace

int main()
{
    int failures = 0;
    failures += run_test("ABI query, truncation, and invalid arguments", test_abi_query);
    failures += run_test(
        "concurrent compiler reentrancy, ownership, and deterministic output",
        test_concurrent_compile_reentrancy_and_determinism);
    failures += run_test("compiler and host-buffer ownership", test_compile_and_buffer_ownership);
    failures += run_test("root and child-thread lifecycle", test_root_and_thread_lifecycle);
    failures += run_test("load, pcall, ordinary error containment, and reuse", test_execution_error_containment_and_reuse);
    failures += run_test("stack, table, and registry-reference operations", test_stack_tables_and_references);
    failures += run_test("numeric conversion finite-range validation", test_numeric_conversion_boundaries);
    failures += run_test("invalid observer and stack boundaries", test_invalid_observer_and_stack_boundaries);
    failures += run_test("invalid table, reference, and load boundaries", test_invalid_table_reference_and_load_boundaries);
    failures += run_test(
        "opaque reference token staleness, churn, and exhaustion",
        test_reference_token_staleness_churn_and_exhaustion);
    failures += run_test("invalid execution and yield boundaries", test_invalid_execution_boundaries);
    failures += run_test("tracked allocator quota telemetry and recovery", test_allocator_quota_and_recovery);
    failures += run_test("failed allocator shrink retains its full charge", test_failed_shrink_retains_allocator_charge);
    failures += run_test("managed callback and userdata-destructor lifetime", test_callback_and_destructor_lifetime);
    failures += run_test("managed callback return validation and recovery", test_callback_return_validation_and_recovery);
    failures += run_test("userdata wrapper visibility and destructor lifetime", test_userdata_wrapper_visibility_and_destructor_lifetime);
    failures += run_test("nested callback suspension", test_nested_callback_suspension);
    failures += run_test("rejected coroutine suspension", test_rejected_coroutine_suspension_does_not_retain_child);
    failures += run_test("GC interrupt notifications are observation-only", test_gc_interrupt_is_observation_only);
    failures += run_test("interrupt-driven coroutine yield and reset", test_interrupt_yield_and_recovery);
    failures += run_test(
        "non-yieldable interrupt hard unwind and reset",
        test_nonyieldable_interrupt_hard_unwind_and_recovery);
    failures += run_test(
        "multi-root interrupt uninstall ownership",
        test_multi_root_interrupt_uninstall_preserves_other_root);
    failures += run_test("stack slot limit refusals and recovery", test_stack_slot_limit_refusals_and_recovery);
    failures += run_test("stack growth failure without an error slot", test_stack_growth_failure_without_an_error_slot);
    failures += run_test("callback failure on a full frame", test_callback_failure_on_a_full_frame);
    failures += run_test("metamethods at the physical stack end", test_metamethods_at_the_physical_stack_end);
    failures += run_test("stack move when the destination cannot grow", test_stack_move_when_the_destination_cannot_grow);
    failures += run_test("coroutine resume at the frame slot limit", test_coroutine_resume_at_the_frame_slot_limit);

    if (failures != 0)
        std::cerr << failures << " conformance test group(s) failed\n";

    return failures == 0 ? 0 : 1;
}
