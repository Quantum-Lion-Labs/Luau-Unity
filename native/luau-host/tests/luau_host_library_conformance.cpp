// Conformance of the host's versions of long-running library functions
// (string.find's plain search, string.split, table.sort, table.concat,
// utf8.len). The host installs them when it opens a library; see "Long
// library calls" in luau_host.cpp.
//
// The differential tests run one generated corpus twice: in a host root, which
// reaches the host versions, and in a plain upstream VM linked into this
// executable, which reaches upstream's functions. Both runs must produce the
// same text, result by result. The interrupt tests check that each host
// version reaches the interrupt callback while it works, and that a stop
// requested by a count limit, a deadline, or another thread ends the call.
// The library-open tests check that no script code can obtain upstream's
// version of a replaced function, however the library is opened.
//
// The same source also builds luau_host_library_coverage, which compiles the
// host into itself with LUAU_HOST_LIBRARY_COVERAGE: the host then counts how
// often its rarely taken paths run, and that build requires the corpus and
// the library-open tests to reach every one of them.

#include "luau_host.h"

#include "lua.h"
#include "lualib.h"

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <cstring>
#include <exception>
#include <iostream>
#include <mutex>
#include <stdexcept>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#if defined(LUAU_HOST_LIBRARY_COVERAGE)
// Defined in luau_host.cpp in this build only.
uint64_t luau_host_library_coverage_count(const char* point);
#endif

namespace
{
class TestFailure final : public std::runtime_error
{
public:
    TestFailure(const std::string& expression, const char* file, int line)
        : std::runtime_error(std::string(file) + ":" + std::to_string(line) + ": requirement failed: " + expression)
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

    explicit Root(uint64_t memoryLimit = 0)
    {
        luau_host_state_options options = {};
        options.struct_size = sizeof(options);
        options.version = LUAU_HOST_STATE_OPTIONS_VERSION;
        options.memory_limit_bytes = memoryLimit;
        luau_host_memory_info failure = {};
        failure.struct_size = sizeof(failure);
        REQUIRE(luau_host_state_create(memoryLimit != 0 ? &options : nullptr, &state, &failure) == LUAU_HOST_STATUS_OK);
        REQUIRE(state != nullptr);
    }

    Root(const Root&) = delete;
    Root& operator=(const Root&) = delete;

    ~Root()
    {
        luau_host_state_close(state);
    }
};

struct Bytecode
{
    luau_host_buffer value = {};

    explicit Bytecode(const std::string& source)
    {
        REQUIRE(
            luau_host_compile(bytes(source.data()), uint64_t(source.size()), nullptr, &value) == LUAU_HOST_STATUS_OK);
        REQUIRE(value.data != nullptr && value.size > 1);
        // A leading zero byte means the compiler reported a diagnostic.
        if (value.data[0] == 0)
            throw std::runtime_error("corpus failed to compile: " + std::string(reinterpret_cast<const char*>(value.data) + 1, size_t(value.size - 1)));
    }

    Bytecode(const Bytecode&) = delete;
    Bytecode& operator=(const Bytecode&) = delete;

    ~Bytecode()
    {
        luau_host_buffer_free(&value);
    }
};

const luau_host_library kCorpusLibraries[] = {
    LUAU_HOST_LIBRARY_BASE,
    LUAU_HOST_LIBRARY_MATH,
    LUAU_HOST_LIBRARY_STRING,
    LUAU_HOST_LIBRARY_TABLE,
    LUAU_HOST_LIBRARY_UTF8,
    LUAU_HOST_LIBRARY_COROUTINE,
};

luau_host_status open_library(luau_host_state* state, luau_host_library library)
{
    int32_t count = 0;
    const luau_host_status status = luau_host_open_library(state, library, &count);
    if (status == LUAU_HOST_STATUS_OK)
        REQUIRE(count == 1 && luau_host_stack_get_top(state) == 1);
    else
        REQUIRE(luau_host_stack_get_top(state) == 1);
    return status;
}

void open_libraries(luau_host_state* state)
{
    for (luau_host_library library : kCorpusLibraries)
    {
        REQUIRE(open_library(state, library) == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_stack_set_top(state, 0) == LUAU_HOST_STATUS_OK);
    }
}

luau_host_status load(luau_host_state* state, const Bytecode& bytecode, const char* chunkName)
{
    luau_host_status loadStatus = LUAU_HOST_STATUS_INVALID_ARGUMENT;
    REQUIRE(
        luau_host_load(state, bytes(chunkName), bytecode.value.data, bytecode.value.size, 0, &loadStatus) ==
        LUAU_HOST_STATUS_OK);
    return loadStatus;
}

luau_host_status run_source(luau_host_state* state, const std::string& source, const char* chunkName);

// Opens the libraries a second time, after script code has removed every
// replaced function, so that registration creates each key again, and given
// the string and table libraries metatables without __newindex, which must not
// stop the open. The corpus then runs against what the reopened libraries hold.
void reopen_libraries(luau_host_state* state)
{
    const luau_host_status status = run_source(state, R"LUAU(
        string.find, string.split, table.concat, table.sort, utf8.len = nil, nil, nil, nil, nil
        setmetatable(string, {__metatable = "locked"})
        setmetatable(table, {__index = {}})
    )LUAU", "@reopen");
    REQUIRE(status == LUAU_HOST_STATUS_OK);
    for (luau_host_library library : {LUAU_HOST_LIBRARY_STRING, LUAU_HOST_LIBRARY_TABLE, LUAU_HOST_LIBRARY_UTF8})
    {
        REQUIRE(open_library(state, library) == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_stack_set_top(state, 0) == LUAU_HOST_STATUS_OK);
    }
}

std::string string_at(luau_host_state* state, int32_t index)
{
    uint64_t length = 0;
    const uint8_t* value = luau_host_to_string_view(state, index, &length);
    REQUIRE(value != nullptr);
    return std::string(reinterpret_cast<const char*>(value), size_t(length));
}

int32_t LUAU_HOST_CALL continue_poll(luau_host_state*, luau_host_interrupt_kind)
{
    return 0;
}

luau_host_callback_table callback_table(luau_host_interrupt_poll poll)
{
    luau_host_callback_table callbacks = {};
    callbacks.struct_size = sizeof(callbacks);
    callbacks.version = LUAU_HOST_CALLBACK_TABLE_VERSION;
    callbacks.registration_id = 1;
    callbacks.interrupt_poll = poll;
    return callbacks;
}

// Runs a chunk that returns one string in a host root, whose libraries were
// opened once or, with reopen, twice. An unthrottled poll that never stops is
// installed, so every safepoint the host versions reach goes through the whole
// interrupt path.
std::string run_in_host(const Bytecode& bytecode, const char* chunkName, bool reopen)
{
    Root root;
    open_libraries(root.state);
    if (reopen)
        reopen_libraries(root.state);
    luau_host_callback_table callbacks = callback_table(continue_poll);
    REQUIRE(luau_host_interrupt_install(root.state, &callbacks) == LUAU_HOST_STATUS_OK);
    REQUIRE(load(root.state, bytecode, chunkName) == LUAU_HOST_STATUS_OK);
    const luau_host_status status = luau_host_pcall(root.state, 0, 1, 0);
    if (status != LUAU_HOST_STATUS_OK)
        throw std::runtime_error("host corpus run failed: " + string_at(root.state, -1));
    std::string result = string_at(root.state, -1);
    luau_host_interrupt_uninstall(root.state);
    return result;
}

// Runs the same chunk in a plain upstream VM with the same libraries.
std::string run_in_upstream(const Bytecode& bytecode, const char* chunkName)
{
    lua_State* L = luaL_newstate();
    REQUIRE(L != nullptr);
    luaopen_base(L);
    luaopen_math(L);
    luaopen_string(L);
    luaopen_table(L);
    luaopen_utf8(L);
    luaopen_coroutine(L);
    lua_settop(L, 0);
    REQUIRE(luau_load(L, chunkName, reinterpret_cast<const char*>(bytecode.value.data), size_t(bytecode.value.size), 0) == 0);
    const int status = lua_pcall(L, 0, 1, 0);
    size_t length = 0;
    const char* text = lua_tolstring(L, -1, &length);
    std::string result = text ? std::string(text, length) : std::string("<no string>");
    lua_close(L);
    if (status != 0)
        throw std::runtime_error("upstream corpus run failed: " + result);
    return result;
}

std::vector<std::string> lines_of(const std::string& text)
{
    std::vector<std::string> lines;
    size_t start = 0;
    while (start <= text.size())
    {
        const size_t end = text.find('\n', start);
        if (end == std::string::npos)
        {
            lines.push_back(text.substr(start));
            break;
        }
        lines.push_back(text.substr(start, end - start));
        start = end + 1;
    }
    return lines;
}

std::string shorten(const std::string& line)
{
    return line.size() <= 400 ? line : line.substr(0, 400) + "... (" + std::to_string(line.size()) + " bytes)";
}

// Runs the corpus in both VMs and requires identical records.
void require_same_records(const std::string& source, const char* chunkName, size_t minimumRecords, bool reopen)
{
    const Bytecode bytecode(source);
    const std::vector<std::string> host = lines_of(run_in_host(bytecode, chunkName, reopen));
    const std::vector<std::string> upstream = lines_of(run_in_upstream(bytecode, chunkName));
    for (size_t index = 0; index < host.size() && index < upstream.size(); ++index)
    {
        if (host[index] != upstream[index])
        {
            std::cerr << "record " << index << " differs\n  host:     " << shorten(host[index])
                      << "\n  upstream: " << shorten(upstream[index]) << '\n';
            REQUIRE(host[index] == upstream[index]);
        }
    }
    REQUIRE(host.size() == upstream.size());
    REQUIRE(host.size() >= minimumRecords);
}

// Shared by every corpus: deterministic generation and serialization. Each
// record is one line: a case label, then each value pcall returned.
const char* kCorpusPrelude = R"LUAU(
local rnd = math.random
local find, split, sort, concat, ulen = string.find, string.split, table.sort, table.concat, utf8.len
local out = {}

local function escape(s)
    return (string.gsub(string.gsub(s, "\\", "\\\\"), "\n", "\\n"))
end

local function serialize(v, nested)
    local t = type(v)
    if t == "string" then
        return "s" .. #v .. ":" .. v
    elseif t == "number" then
        if v ~= v then return "nan" end
        return "n" .. string.format("%.17g", v)
    elseif t == "nil" or t == "boolean" then
        return tostring(v)
    elseif t == "table" then
        if nested then return "table" end
        -- Every entry in next() order, which depends on the table's layout.
        local parts = {"t#" .. #v}
        local k, x = next(v)
        while k ~= nil do
            parts[#parts + 1] = serialize(k, true) .. "=" .. serialize(x, true)
            k, x = next(v, k)
        end
        return table.concat(parts, ",")
    end
    return t
end

local function record(label, ...)
    local n = select("#", ...)
    local parts = {label, tostring(n)}
    for i = 1, n do
        parts[#parts + 1] = serialize((select(i, ...)), false)
    end
    out[#out + 1] = escape(table.concat(parts, "|"))
end

local function randomstring(alphabet, len)
    local parts = table.create(len)
    local count = #alphabet
    for i = 1, len do
        local c = rnd(1, count)
        parts[i] = string.sub(alphabet, c, c)
    end
    return table.concat(parts)
end

local function mutate(s, alphabet, edits)
    for _ = 1, edits do
        if #s == 0 then return s end
        local p = rnd(1, #s)
        s = string.sub(s, 1, p - 1) .. randomstring(alphabet, 1) .. string.sub(s, p + 1)
    end
    return s
end

-- Subjects with the structure that makes plain search hard: periodic text,
-- long runs, repeated blocks, and plain random text.
local function subject(alphabet, len)
    local style = rnd(1, 4)
    local s
    if style == 1 then
        local unit = randomstring(alphabet, rnd(1, 8))
        s = string.sub(string.rep(unit, len // #unit + 1), 1, len)
    elseif style == 2 then
        local parts, total = {}, 0
        while total < len do
            local k = rnd(1, 60)
            parts[#parts + 1] = string.rep(randomstring(alphabet, 1), k)
            total += k
        end
        s = string.sub(table.concat(parts), 1, len)
    elseif style == 3 then
        local block = randomstring(alphabet, rnd(16, 200))
        s = string.sub(string.rep(block, len // #block + 1), 1, len)
    else
        local parts = {}
        for i = 1, len // 16 + 1 do parts[i] = randomstring(alphabet, 16) end
        s = string.sub(table.concat(parts), 1, len)
    end
    return mutate(s, alphabet, rnd(0, 8))
end

local alphabets = {"ab", "ab", "abc", "a\0", "xyzw", "\255\1a", "ab.%", "a(b%"}

-- Index arguments beyond the ordinary ones: fractions, the ends of int's
-- range, strings that convert or do not, and values of the wrong type. Under
-- outofrange, also NaN, infinities, numbers outside int's range, and the most
-- negative int, which upstream's table.concat decrements in int.
local oddindices = {2.5, -2.5, 0.5, -0.5, 2^31 - 1, -2^31 + 1, "3", " 4 ", "0x10", "1e1", "x", "", true, false, {}, print}
if outofrange then
    for _, v in {-2^31, 2^31, -2^31 - 1, 2^53, -2^53, 1e300, -1e300, 1 / 0, -1 / 0, 0 / 0} do
        oddindices[#oddindices + 1] = v
    end
end
local function oddindex()
    return oddindices[rnd(1, #oddindices)]
end
-- Separators and patterns of the wrong type, and numbers that convert.
local oddstrings = {7, 0.5, -0.0, 1e300, 0 / 0, true, false, {}, print}
local function oddstring()
    return oddstrings[rnd(1, #oddstrings)]
end

local function subjectlength()
    local band = rnd(1, 20)
    if band <= 13 then return rnd(4090, 4700) end
    if band <= 16 then return rnd(0, 300) end
    if band <= 19 then return rnd(4700, 14000) end
    return rnd(20000, 70000)
end

local function needle(hay, alphabet)
    local kind = rnd(1, 12)
    local len = #hay
    if kind <= 5 and len > 0 then
        local l = rnd(1, 4) == 1 and rnd(0, 800) or rnd(0, 40)
        local i = rnd(1, len)
        local s = string.sub(hay, i, i + l - 1)
        if rnd(1, 3) == 1 then s = mutate(s, alphabet, 1) end
        return s
    elseif kind <= 7 then
        return randomstring(alphabet, rnd(1, 12))
    elseif kind <= 9 then
        local s = string.rep(randomstring(alphabet, rnd(1, 5)), rnd(1, 80))
        if rnd(1, 2) == 1 then s ..= randomstring(alphabet, rnd(0, 3)) end
        return s
    elseif kind == 10 and len > 0 then
        return string.sub(hay, rnd(1, len))
    elseif kind == 11 then
        return hay .. randomstring(alphabet, rnd(0, 2))
    end
    return ""
end
)LUAU";

// Upstream converts an index argument to int with a plain cast
// (luai_num2int), which is undefined for NaN and for numbers outside int's
// range, and its table.concat subtracts one from a start index in int, which
// overflows for the most negative int. UBSan stops on both. Both
// implementations make the conversion in the same upstream function, and
// the host subtracts without overflow, so outside sanitized builds the corpus
// also passes such numbers, and the two must agree on what the platform makes
// of them.
#if defined(LUAU_HOST_TEST_SANITIZED)
constexpr bool kOutOfRangeIndices = false;
#else
constexpr bool kOutOfRangeIndices = true;
#endif

std::string corpus(const char* body, int seed)
{
    return "math.randomseed(" + std::to_string(seed) + ")\n" + "local outofrange = " +
        (kOutOfRangeIndices ? "true" : "false") + "\n" + kCorpusPrelude + body + "\nreturn table.concat(out, \"\\n\")\n";
}

const char* kFindCorpus = R"LUAU(
local pool = {}
for i = 1, 260 do
    local alphabet = alphabets[rnd(1, #alphabets)]
    pool[i] = {subject(alphabet, subjectlength()), alphabet}
end

local inits = {1, 0, -1, 2.5, "3", true}
for case = 1, 9000 do
    local entry = pool[rnd(1, #pool)]
    local hay, alphabet = entry[1], entry[2]
    local p = needle(hay, alphabet)
    local init
    local which = rnd(1, 10)
    if which <= 5 then init = nil
    elseif which <= 8 then init = rnd(-#hay - 3, #hay + 3)
    elseif which == 9 then init = ({#hay, #hay + 1, #hay + 2, -#hay, -#hay - 1})[rnd(1, 5)]
    elseif rnd(1, 2) == 1 then init = inits[rnd(1, #inits)]
    else init = oddindex() end
    local plain = ({true, true, true, false, nil, 1, "yes", 0})[rnd(1, 8)]
    local shape = rnd(1, 13)
    local label = "find " .. case
    if shape <= 4 then
        record(label, pcall(string.find, hay, p, init, plain))
    elseif shape <= 6 then
        record(label, pcall(function() return hay:find(p, init, plain) end))
    elseif shape <= 8 then
        record(label, pcall(find, hay, p, init))
    elseif shape == 9 then
        record(label, pcall(find, hay, p))
    elseif shape == 10 then
        -- Arguments that need coercion or are rejected.
        local a = ({tonumber(string.rep("7", rnd(1, 9))), hay, nil, {}})[rnd(1, 4)]
        local b = ({p, 77, nil, {}})[rnd(1, 4)]
        record(label, pcall(find, a, b, init, plain))
    elseif shape == 11 then
        record(label, pcall(string.find, hay))
    elseif shape == 12 then
        -- A pattern of the wrong type, or a number that converts.
        record(label, pcall(find, hay, oddstring(), init, plain))
    else
        record(label, pcall(find, hay, p, init, plain, "extra"))
    end
end

-- Periodic subjects and long, nearly periodic needles: upstream's search
-- compares most of the needle at many positions here, so the host's search
-- switches to the two-way algorithm, periodic and not.
for case = 1, 3000 do
    local alphabet = alphabets[rnd(1, 3)]
    local unit = randomstring(alphabet, rnd(1, 6))
    local hay = string.rep(unit, rnd(5000, 9000) // #unit)
    hay = mutate(hay, alphabet, rnd(0, 3))
    local p = string.rep(unit, rnd(60, 400))
    local edit = rnd(1, 4)
    if edit == 1 then p = mutate(p, alphabet, 1)
    elseif edit == 2 then p = string.sub(p, rnd(1, #unit)) .. randomstring(alphabet, rnd(1, 3))
    elseif edit == 3 then p = randomstring(alphabet, rnd(1, 3)) .. p end
    local init = rnd(1, 3) == 1 and rnd(1, #hay) or nil
    record("find periodic " .. case, pcall(find, hay, p, init, true))
end

-- Directed cases: classic two-way edges at sizes the host searches itself.
local big = string.rep("a", 5000)
local directed = {
    {big, string.rep("a", 4999) .. "b"},
    {big .. "b", string.rep("a", 4999) .. "b"},
    {big, big},
    {big, big .. "a"},
    {string.rep("ab", 3000), "ba"},
    {string.rep("ab", 3000), "abab" .. "c"},
    {string.rep("abc", 2000) .. "abd", "abcabd"},
    {string.rep("aab", 2000), "aabaabaab"},
    {string.rep("\0", 5000) .. "\1", "\0\0\1"},
    {string.rep("\255", 5000), "\255\255\254"},
    {string.rep("xyz", 2000), "zx"},
    {string.rep("a", 4500) .. "ba" .. string.rep("a", 400), "a" .. "b" .. string.rep("a", 300)},
}
for i, c in directed do
    record("find directed " .. i, pcall(find, c[1], c[2], 1, true))
    record("find directed tail " .. i, pcall(find, c[1], c[2], -#c[2] - 1, true))
    record("find directed nospecials " .. i, pcall(find, c[1], c[2]))
end

-- Long patterns, on short and long subjects: init is checked before the
-- pattern is classified, and a pattern longer than the subject can match
-- only an empty capture or anchor. A special character first, in the middle,
-- or last; a malformed pattern; and a pattern that is the subject.
local x4096, x4097 = string.rep("x", 4096), string.rep("x", 4097)
local long = string.rep("y", 4200)
local patterns = {
    long, "." .. long, long .. ".", string.sub(long, 1, 2100) .. "%" .. string.sub(long, 1, 2100), long .. "%",
    string.rep("x", 5000), string.rep("x", 4097), string.rep("x", 4096) .. "$", "^" .. string.rep("x", 4096),
    string.rep("x?", 2100), "(" .. long, string.rep("x", 4000) .. "y",
}
local subjects = {x4096, x4097, x4097 .. long, string.rep("xy", 2100), ""}
for i, p in patterns do
    for j, hay in subjects do
        -- false stands for an absent init.
        for k, init in {false, 1, 4098, 5000, -1, -10000} do
            for _, plain in {false, true} do
                local label = "find long pattern " .. i .. "," .. j .. "," .. k .. "," .. tostring(plain)
                if init then
                    record(label, pcall(find, hay, p, init, plain))
                elseif plain then
                    record(label, pcall(find, hay, p, nil, plain))
                else
                    record(label, pcall(find, hay, p))
                end
            end
        end
    end
end
)LUAU";

const char* kSplitCorpus = R"LUAU(
-- A returned table's keys in next() order, which depends on its layout, the
-- length of every piece, and the pieces joined: the lengths and the joined
-- text together give the contents of every piece.
local function summarize(ok, t, ...)
    if not ok or type(t) ~= "table" then return ok, t, ... end
    local lengths = table.create(#t)
    for i = 1, #t do lengths[i] = #t[i] end
    local keys, n = {}, 0
    local k = next(t)
    while k ~= nil do
        n += 1
        keys[n] = k
        k = next(t, k)
    end
    return ok, #t, table.concat(lengths, ","), table.concat(keys, ","), table.concat(t), select("#", ...)
end

for case = 1, 1500 do
    local alphabet = alphabets[rnd(1, #alphabets)]
    local hay = subject(alphabet, subjectlength())
    local which = rnd(1, 10)
    local sep
    if which <= 4 then sep = needle(hay, alphabet)
    elseif which <= 6 then sep = randomstring(alphabet, 1)
    elseif which == 7 then sep = ""
    elseif which == 8 then sep = nil
    elseif which == 9 then sep = randomstring(alphabet, rnd(2, 5))
    elseif rnd(1, 2) == 1 then sep = ({",", 1, 0.5, "\0"})[rnd(1, 4)]
    else sep = oddstring() end
    local shape = rnd(1, 8)
    local label = "split " .. case
    if shape <= 3 then
        record(label, summarize(pcall(string.split, hay, sep)))
    elseif shape <= 5 then
        record(label, summarize(pcall(function() return hay:split(sep) end)))
    elseif shape == 6 then
        record(label, summarize(pcall(split, hay)))
    elseif shape == 7 then
        local a = ({tonumber(string.rep("1", rnd(1, 9))), nil, {}, hay})[rnd(1, 4)]
        record(label, summarize(pcall(split, a, sep)))
    else
        local ok, pieces = pcall(split, hay, sep)
        if ok then
            record(label, type(sep) ~= "string" or sep == "" or concat(pieces, sep) == hay, #pieces)
        else
            record(label, ok, pieces)
        end
    end
end

-- Pieces large enough to reach a safepoint between them.
local many = string.rep("a,", 40000)
record("split many", summarize(pcall(split, many, ",")))
record("split many empty", summarize(pcall(split, string.rep("ab", 20000), "")))
-- Short subjects with long separators, which go to upstream's function, and
-- long subjects with separators longer than the subject.
local x4096, x5000 = string.rep("x", 4096), string.rep("x", 5000)
for i, c in {
    {x4096, string.rep("x", 6000)}, {x4096, x4096}, {x4096, string.rep("x", 4095)}, {x5000, x5000 .. "x"},
    {x5000, x5000}, {x5000, string.rep("x", 4999)}, {x5000, string.rep("xy", 3000)}, {"", x5000}, {x5000, ""},
} do
    record("split long separator " .. i, summarize(pcall(split, c[1], c[2])))
end
)LUAU";

const char* kSortCorpus = R"LUAU(
local nan = 0 / 0
local specials = {0, -0.0, 1 / 0, -1 / 0, nan, 1e308, -1e-308, 0.5}

local function numbers(n)
    local t = table.create(n)
    local range = ({3, 50, 1000000, 0})[rnd(1, 4)]
    for i = 1, n do
        if range == 0 then t[i] = rnd() * 1000 - 500
        elseif rnd(1, 40) == 1 then t[i] = specials[rnd(1, #specials)]
        else t[i] = rnd(1, range) end
    end
    return t
end

local function strings(n)
    local t = table.create(n)
    local prefix = string.rep("p", rnd(0, 3) * 20)
    local alphabet = alphabets[rnd(1, #alphabets)]
    for i = 1, n do t[i] = prefix .. randomstring(alphabet, rnd(0, 6)) end
    return t
end

local metamethodcalls = 0
local ordered = {__lt = function(a, b) metamethodcalls += 1 return a.v < b.v end}
-- A C function as __lt: rawequal is false for distinct objects, so every
-- object compares as equal to every other.
local cordered = {__lt = rawequal}

-- McIlroy's adversary ("A Killer Adversary for Quicksort", 1999) decides the
-- order lazily so that each partition is as unbalanced as possible, which
-- drives the introsort into its heap-sort fallback. With tied, frozen items
-- share values in groups, so the heap sort also compares equal items.
local function adversary(n, tied)
    local gas = n + 1
    local value = table.create(n, gas)
    local solid, candidate = 0, nil
    local function freeze(x)
        value[x] = tied and solid // 3 or solid
        solid += 1
    end
    local t = table.create(n)
    for i = 1, n do t[i] = i end
    return t, function(x, y)
        if value[x] == gas and value[y] == gas then
            if x == candidate then freeze(x) else freeze(y) end
        end
        if value[x] == gas then candidate = x
        elseif value[y] == gas then candidate = y end
        return value[x] < value[y]
    end
end
local function objects(n)
    local t = table.create(n)
    local mt = rnd(1, 4) == 1 and cordered or ordered
    for i = 1, n do t[i] = setmetatable({id = i, v = rnd(1, 20)}, mt) end
    return t
end

local function snapshot(t)
    local parts = table.create(#t)
    for i = 1, #t do
        local v = t[i]
        parts[i] = type(v) == "table" and ("#" .. tostring(rawget(v, "id"))) or serialize(v, true)
    end
    return table.concat(parts, ",")
end

for case = 1, 1300 do
    local band = rnd(1, 40)
    local n = band <= 26 and rnd(0, 60) or band <= 39 and rnd(60, 800) or rnd(3000, 12000)
    local kind = rnd(1, 12)
    local t, killer
    if kind <= 4 then t = numbers(n) elseif kind <= 7 then t = strings(n) elseif kind <= 9 then t = objects(n)
    elseif kind == 10 then
        t = numbers(n)
        if n > 0 then t[rnd(1, n)] = "mixed" end
    else
        t, killer = adversary(n, kind == 12)
    end

    local cmpkind = rnd(1, 14)
    local cmp
    local calls = 0
    local limit = rnd(1, 4 * n + 2)
    if cmpkind <= 5 then cmp = nil
    elseif cmpkind == 6 then cmp = function(a, b) return a > b end
    elseif cmpkind == 7 then cmp = function(a, b) return a <= b end
    elseif cmpkind == 8 then cmp = function() return rnd() < 0.5 end
    elseif cmpkind == 9 then cmp = function(a, b) calls += 1 if calls == limit then error("stop " .. calls) end return a < b end
    elseif cmpkind == 10 then cmp = function(a, b) calls += 1 if calls == limit then t[#t + 1] = a end return a < b end
    elseif cmpkind == 11 then cmp = rawequal
    elseif cmpkind == 12 then cmp = function(a, b) return calls end
    elseif cmpkind == 13 then cmp = ({5, "x", true, {}, setmetatable({}, {__call = function() return false end})})[rnd(1, 5)]
    else cmp = function(a, b) calls += 1 if calls == limit then coroutine.yield() end return a < b end end

    if kind ~= 10 and cmpkind > 5 and cmpkind ~= 11 and cmpkind ~= 13 and rnd(1, 2) == 1 then
        -- Comparisons of the objects' fields, through a Luau comparator.
        local inner = cmp
        if kind == 8 or kind == 9 then cmp = function(a, b) return inner(a.v, b.v) end end
    end
    if killer and cmpkind ~= 13 then cmp = killer end
    -- Count the calls of every Luau comparison, which pins the order in which
    -- the sort compares.
    local counted = 0
    if type(cmp) == "function" and cmp ~= rawequal then
        local inner = cmp
        cmp = function(a, b)
            counted += 1
            return inner(a, b)
        end
    end
    metamethodcalls = 0

    local shape = rnd(1, 20)
    local label = "sort " .. case .. " n=" .. n .. " kind=" .. kind .. " cmp=" .. cmpkind
    if shape == 1 then table.freeze(t) end
    local results
    if shape == 2 then
        results = {pcall(sort, "x")}
    elseif shape == 3 then
        results = {pcall(sort, t, nil)}
    elseif shape == 4 then
        results = {pcall(table.sort, t, cmp, "extra")}
    else
        results = {pcall(sort, t, cmp)}
    end
    record(label, results[1], results[2], snapshot(t), counted, metamethodcalls)
end
)LUAU";

const char* kConcatCorpus = R"LUAU(
local values = {"a", "bc", "", "\0", 1, 0.5, -0.0, 1e300, 2^53, 0 / 0, -1 / 0, 123456789}
for case = 1, 1500 do
    local n = rnd(1, 10) <= 8 and rnd(0, 40) or rnd(1000, 6000)
    local t = table.create(n)
    for i = 1, n do
        if rnd(1, 4) == 1 then t[i] = values[rnd(1, #values)]
        else t[i] = randomstring(alphabets[rnd(1, #alphabets)], rnd(0, 8)) end
    end
    local hole = rnd(1, 12)
    if hole == 1 and n > 0 then t[rnd(1, n)] = nil
    elseif hole == 2 and n > 0 then t[rnd(1, n)] = ({true, {}, print})[rnd(1, 3)]
    elseif hole == 3 then
        -- Entries in the hash part.
        local h = {}
        for i = n, 1, -1 do h[i] = t[i] end
        t = h
    end
    local sep = ({nil, "", ",", ", ", string.rep("-", 40), 7})[rnd(1, 6)]
    if rnd(1, 12) == 1 then sep = oddstring() end
    local i, j
    local range = rnd(1, 12)
    if range <= 5 then i, j = nil, nil
    elseif range <= 8 then i, j = rnd(-3, n + 3), rnd(-3, n + 3)
    elseif range == 9 then i, j = 1.5, "2"
    elseif range == 10 then i, j = oddindex(), nil
    elseif range == 11 then i, j = rnd(-3, n + 3), oddindex()
    else i, j = n + 1, n end
    local shape = rnd(1, 6)
    local label = "concat " .. case
    if shape <= 4 then
        record(label, pcall(concat, t, sep, i, j))
    elseif shape == 5 then
        record(label, pcall(table.concat, t, sep))
    else
        record(label, pcall(concat, ({t, "x", nil, 5})[rnd(1, 4)], sep))
    end
end
)LUAU";

const char* kUtf8Corpus = R"LUAU(
-- Lone continuations, overlong forms (including the longest ones, just
-- below each length's range), surrogates, values past U+10FFFF, five-byte
-- forms, and truncated sequences.
local invalid = {
    "\128", "\192\128", "\193\191", "\224\159\191", "\240\143\191\191", "\237\160\128", "\237\191\191",
    "\244\144\128\128", "\248\136\128\128\128", "\224\128", "\255", "\194",
}
local function text(len)
    local parts, total = {}, 0
    while total < len do
        local piece
        local which = rnd(1, 40)
        if which == 1 then piece = invalid[rnd(1, #invalid)]
        elseif which == 2 then piece = string.char(rnd(128, 255), rnd(0, 255), rnd(128, 191))
        elseif which <= 20 then piece = string.char(rnd(32, 126))
        elseif which <= 28 then piece = utf8.char(rnd(0x80, 0x7FF))
        elseif which <= 36 then
            local c = rnd(0x800, 0xFFFF)
            if c >= 0xD800 and c <= 0xDFFF then c = 0x41 end
            piece = utf8.char(c)
        else piece = utf8.char(rnd(0x10000, 0x10FFFF)) end
        parts[#parts + 1] = piece
        total += #piece
    end
    return table.concat(parts)
end

for case = 1, 900 do
    local len = rnd(1, 10) <= 7 and rnd(0, 200) or rnd(16000, 40000)
    local s = text(len)
    if rnd(1, 3) == 1 then
        -- Mostly valid text, with at most one invalid sequence somewhere.
        s = string.gsub(s, "[\128-\255]+", function(c) return utf8.len(c) and c or "" end)
        if rnd(1, 2) == 1 and #s > 0 then
            local p = rnd(1, #s)
            s = string.sub(s, 1, p - 1) .. invalid[rnd(1, #invalid)] .. string.sub(s, p)
        end
    end
    local i, j
    local range = rnd(1, 12)
    if range <= 4 then i, j = nil, nil
    elseif range <= 8 then i, j = rnd(-#s - 2, #s + 2), rnd(-#s - 2, #s + 2)
    elseif range == 9 then i, j = 0, -1
    elseif range == 10 then i, j = oddindex(), nil
    elseif range == 11 then i, j = rnd(-#s - 2, #s + 2), oddindex()
    else i, j = #s + 1, #s end
    local shape = rnd(1, 5)
    local label = "utf8.len " .. case
    if shape <= 3 then
        record(label, pcall(ulen, s, i, j))
    elseif shape == 4 then
        record(label, pcall(utf8.len, s))
    else
        record(label, pcall(ulen, ({s, 12345, nil, {}})[rnd(1, 4)], i))
    end
end
)LUAU";

// Each corpus runs with several seeds; the last runs against libraries opened
// a second time.
void test_find_matches_upstream()
{
    for (int seed : {1, 2, 3})
        require_same_records(corpus(kFindCorpus, seed), "@find-corpus", 12000, seed == 3);
}

void test_split_matches_upstream()
{
    for (int seed : {4, 5})
        require_same_records(corpus(kSplitCorpus, seed), "@split-corpus", 1500, seed == 5);
}

void test_sort_matches_upstream()
{
    for (int seed : {6, 7})
        require_same_records(corpus(kSortCorpus, seed), "@sort-corpus", 1300, seed == 7);
}

void test_concat_matches_upstream()
{
    for (int seed : {8, 9})
        require_same_records(corpus(kConcatCorpus, seed), "@concat-corpus", 1500, seed == 9);
}

void test_utf8_len_matches_upstream()
{
    for (int seed : {10, 11})
        require_same_records(corpus(kUtf8Corpus, seed), "@utf8-corpus", 900, seed == 11);
}

// Method calls on strings index the string table itself, so a reference
// saved after opening, the table, and the metatable all name one closure. The
// interrupt tests below show that the closure is the host's.
void test_every_route_names_one_function()
{
    const std::string source = R"LUAU(
        local saved, savedsplit = string.find, string.split
        local index = getmetatable("").__index
        return table.concat({
            tostring(index == string),
            tostring(("").find == saved),
            tostring(index.split == savedsplit),
            tostring(rawequal(string.find, saved)),
        }, ",")
    )LUAU";
    const Bytecode bytecode(source);
    REQUIRE(run_in_host(bytecode, "@routes", false) == "true,true,true,true");
    REQUIRE(run_in_host(bytecode, "@routes", true) == "true,true,true,true");
    REQUIRE(run_in_upstream(bytecode, "@routes") == "true,true,true,true");
}

// Long calls used by the interrupt tests. Each one sets `reached` only after
// its call returns, so a stop inside the call leaves it nil.
struct LongCall
{
    const char* name;
    const char* setup;
    const char* call;
    // Takes only milliseconds, though it reaches hundreds of safepoints, so
    // a deadline could pass after it returned.
    bool brief = false;
};

const LongCall kLongCalls[] = {
    // Upstream's search takes hours on these; the host's is linear but still
    // compares tens of millions of bytes.
    {"plain find", "hay = string.rep(string.rep('a', 9999) .. 'b', 3000) needle = string.rep('a', 10000) .. 'b'",
        "local r = string.find(hay, needle, 1, true) reached = true return r"},
    {"find without specials", "hay = string.rep(string.rep('a', 9999) .. 'b', 3000) needle = string.rep('a', 10000) .. 'b'",
        "local r = hay:find(needle) reached = true return r"},
    {"split", "hay = string.rep(string.rep('a', 9999) .. 'b', 3000) needle = string.rep('a', 10000) .. 'b'",
        "local r = string.split(hay, needle) reached = true return #r"},
    {"split into pieces", "hay = string.rep('a,', 1000000)", "local r = hay:split(',') reached = true return #r"},
    {"sort", "t = table.create(1000000) for i = 1, 1000000 do t[i] = (i * 7919) % 1000003 end",
        "table.sort(t) reached = true return #t"},
    // rawequal is false for every pair of distinct numbers, which a sort
    // treats like a table of equal values.
    {"sort with a C comparison", "t = table.create(300000) for i = 1, 300000 do t[i] = i end",
        "table.sort(t, rawequal) reached = true return #t"},
    {"concat", "t = table.create(1000000) for i = 1, 1000000 do t[i] = i + 0.5 end",
        "local r = table.concat(t, ',') reached = true return #r"},
    {"utf8.len", "s = string.rep('\\u{20AC}', 10000000)", "local r = utf8.len(s) reached = true return r"},
    // A 50 MB subject in which the needle's first byte comes only every
    // 128 KB, so the search spends its time in memchr between candidates.
    {"find with rare candidates", "hay = string.rep(string.rep('x', 131071) .. 'a', 400)",
        "local r = string.find(hay, 'ab', 1, true) reached = true return r", true},
    {"split with rare candidates", "hay = string.rep(string.rep('x', 131071) .. 'a', 400)",
        "local r = hay:split('ab') reached = true return #r", true},
    // A 30 MB pattern without special characters, classified before the
    // search finds that it is longer than the subject.
    {"find classifying a long pattern", "hay = string.rep('x', 5000) needle = string.rep('y', 30 * 1048576)",
        "local r = string.find(hay, needle) reached = true return r", true},
    {"find classifying a long pattern on a short subject", "hay = string.rep('x', 100) needle = string.rep('y', 30 * 1048576)",
        "local r = hay:find(needle) reached = true return r", true},
};

constexpr std::chrono::seconds kWaitTimeout(30);

struct StopProbe
{
    std::atomic<int> executionPolls = {0};
    // The poll stops execution once this many polls have happened.
    std::atomic<int> stopAt = {0};
    // While set, the next poll lets another thread know the call is running
    // and waits until that thread has requested a stop.
    std::atomic<bool> waiting = {false};
    std::atomic<bool> requested = {false};
    std::mutex mutex;
    std::condition_variable changed;
};

StopProbe* stopProbe = nullptr;

int32_t LUAU_HOST_CALL stop_poll(luau_host_state*, luau_host_interrupt_kind kind)
{
    if (kind != LUAU_HOST_INTERRUPT_EXECUTION || !stopProbe)
        return 0;
    const int ordinal = ++stopProbe->executionPolls;
    if (stopProbe->waiting)
    {
        std::unique_lock<std::mutex> lock(stopProbe->mutex);
        stopProbe->waiting = false;
        stopProbe->changed.notify_all();
        stopProbe->changed.wait_for(lock, kWaitTimeout, [] { return stopProbe->requested.load(); });
        return 0;
    }
    if (stopProbe->requested)
        return 1;
    return stopProbe->stopAt != 0 && ordinal >= stopProbe->stopAt ? 1 : 0;
}

luau_host_status run_source(luau_host_state* state, const std::string& source, const char* chunkName)
{
    const Bytecode bytecode(source);
    REQUIRE(load(state, bytecode, chunkName) == LUAU_HOST_STATUS_OK);
    return luau_host_pcall(state, 0, 0, 0);
}

bool reached(luau_host_state* state)
{
    int32_t type = 0;
    REQUIRE(luau_host_global_get(state, bytes("reached"), &type) == LUAU_HOST_STATUS_OK);
    const bool value = luau_host_to_boolean(state, -1) != 0;
    REQUIRE(luau_host_stack_set_top(state, 0) == LUAU_HOST_STATUS_OK);
    return value;
}

void prepare(luau_host_state* state, const LongCall& call)
{
    open_libraries(state);
    REQUIRE(run_source(state, call.setup, "@long-call-setup") == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_get_top(state) == 0);
}

// A stop leaves exactly its error object, the statement after the call did
// not run, and the root runs code again once the hard stop is cleared.
void require_stopped_inside_and_recovers(luau_host_state* state)
{
    REQUIRE(luau_host_stack_get_top(state) == 1);
    REQUIRE(luau_host_stack_set_top(state, 0) == LUAU_HOST_STATUS_OK);
    REQUIRE(!reached(state));
    REQUIRE(luau_host_memory_reset_failure(state) == LUAU_HOST_STATUS_OK);
    REQUIRE(run_source(state, "reached = 1 + 1 == 2", "@long-call-recovery") == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_get_top(state) == 0);
    REQUIRE(reached(state));
}

// The call reaches the interrupt callback many times while it works; the
// script itself has only a handful of safepoints.
void test_long_calls_reach_the_interrupt_callback()
{
    for (const LongCall& call : kLongCalls)
    {
        StopProbe probe;
        stopProbe = &probe;
        Root root;
        prepare(root.state, call);
        luau_host_callback_table callbacks = callback_table(stop_poll);
        REQUIRE(luau_host_interrupt_install(root.state, &callbacks) == LUAU_HOST_STATUS_OK);
        const luau_host_status status = run_source(root.state, call.call, "@long-call");
        luau_host_interrupt_uninstall(root.state);
        stopProbe = nullptr;
        if (status != LUAU_HOST_STATUS_OK || probe.executionPolls < 20)
            std::cerr << call.name << ": status " << status << ", " << probe.executionPolls << " polls\n";
        REQUIRE(status == LUAU_HOST_STATUS_OK);
        REQUIRE(reached(root.state));
        REQUIRE(probe.executionPolls >= 20);
    }
}

// A poll that stops execution inside the call ends it there, as a hard stop.
void test_long_calls_stop_inside_the_call()
{
    for (const LongCall& call : kLongCalls)
    {
        StopProbe probe;
        probe.stopAt = 10;
        stopProbe = &probe;
        Root root;
        prepare(root.state, call);
        luau_host_callback_table callbacks = callback_table(stop_poll);
        REQUIRE(luau_host_interrupt_install(root.state, &callbacks) == LUAU_HOST_STATUS_OK);
        const luau_host_status status = run_source(root.state, call.call, "@long-call-stop");
        luau_host_interrupt_uninstall(root.state);
        stopProbe = nullptr;
        if (status != LUAU_HOST_STATUS_CANCELED)
            std::cerr << call.name << ": status " << status << '\n';
        REQUIRE(status == LUAU_HOST_STATUS_CANCELED);
        REQUIRE(probe.executionPolls == 10);
        require_stopped_inside_and_recovers(root.state);
    }
}

// The throttled count limit stops the call at exactly limit + 1 safepoints,
// counting the ones the call reaches, and so does a deadline.
void test_long_calls_obey_throttled_budgets()
{
    using Clock = std::chrono::steady_clock;
    for (const LongCall& call : kLongCalls)
    {
        for (bool deadline : {false, true})
        {
            if (deadline && call.brief)
                continue;
            StopProbe probe;
            probe.stopAt = 1;
            stopProbe = &probe;
            Root root;
            prepare(root.state, call);
            luau_host_callback_table callbacks = callback_table(stop_poll);
            REQUIRE(luau_host_interrupt_install(root.state, &callbacks) == LUAU_HOST_STATUS_OK);
            const auto started = Clock::now();
            REQUIRE(
                luau_host_interrupt_configure(root.state, deadline ? 0 : 50, deadline ? 1000000 : 0) == LUAU_HOST_STATUS_OK);
            const luau_host_status status = run_source(root.state, call.call, "@long-call-budget");
            const auto elapsed = Clock::now() - started;
            const uint64_t count = luau_host_interrupt_count(root.state);
            luau_host_interrupt_uninstall(root.state);
            stopProbe = nullptr;
            if (status != LUAU_HOST_STATUS_CANCELED)
                std::cerr << call.name << (deadline ? " (deadline)" : " (count)") << ": status " << status << '\n';
            REQUIRE(status == LUAU_HOST_STATUS_CANCELED);
            REQUIRE(probe.executionPolls == 1);
            if (!deadline)
                REQUIRE(count == 51);
            // Generous for loaded runners; the call itself would finish.
            REQUIRE(elapsed < std::chrono::seconds(10));
            require_stopped_inside_and_recovers(root.state);
        }
    }
}

// A stop requested by another thread while the call runs ends it at the
// call's next safepoint. The poll waits inside the call until the request
// has been made, so the request always lands while the call is running.
void test_long_calls_stop_on_a_request_from_another_thread()
{
    for (const LongCall& call : kLongCalls)
    {
        StopProbe probe;
        stopProbe = &probe;
        Root root;
        prepare(root.state, call);
        luau_host_callback_table callbacks = callback_table(stop_poll);
        REQUIRE(luau_host_interrupt_install(root.state, &callbacks) == LUAU_HOST_STATUS_OK);
        // A count limit of 5 makes the throttle poll inside the call; that
        // poll lets the requester go, and the request stops the call.
        REQUIRE(luau_host_interrupt_configure(root.state, 5, 0) == LUAU_HOST_STATUS_OK);
        probe.waiting = true;
        bool signaled = false;
        std::thread requester([&probe, &signaled, state = root.state] {
            std::unique_lock<std::mutex> lock(probe.mutex);
            signaled = probe.changed.wait_for(lock, kWaitTimeout, [&probe] { return !probe.waiting.load(); });
            luau_host_interrupt_request(state);
            probe.requested = true;
            probe.changed.notify_all();
        });
        const luau_host_status status = run_source(root.state, call.call, "@long-call-request");
        requester.join();
        luau_host_interrupt_uninstall(root.state);
        stopProbe = nullptr;
        REQUIRE(signaled);
        REQUIRE(status == LUAU_HOST_STATUS_CANCELED);
        REQUIRE(probe.executionPolls == 2);
        require_stopped_inside_and_recovers(root.state);
    }
}

// Poll counts. With an unthrottled poll installed, every safepoint polls, so
// these counts are exact and depend on nothing but the inputs.
std::atomic<long> countedPolls = {0};

int32_t LUAU_HOST_CALL count_poll(luau_host_state*, luau_host_interrupt_kind kind)
{
    if (kind == LUAU_HOST_INTERRUPT_EXECUTION)
        ++countedPolls;
    return 0;
}

long polls_of(luau_host_state* state, const std::string& source)
{
    luau_host_callback_table callbacks = callback_table(count_poll);
    REQUIRE(luau_host_interrupt_install(state, &callbacks) == LUAU_HOST_STATUS_OK);
    countedPolls = 0;
    const luau_host_status status = run_source(state, source, "@polls");
    luau_host_interrupt_uninstall(state);
    if (status != LUAU_HOST_STATUS_OK)
        throw std::runtime_error("chunk failed: " + string_at(state, -1) + "\n" + source);
    REQUIRE(luau_host_stack_get_top(state) == 0);
    return countedPolls;
}

// The polls reached inside one call: those of `local r = <call>` beyond those
// of the same statement around a call that reaches none.
long polls_inside(luau_host_state* state, const std::string& call)
{
    return polls_of(state, "local r = " + call) - polls_of(state, "local r = rawequal(1, 1)");
}

void run_ok(luau_host_state* state, const std::string& source)
{
    const luau_host_status status = run_source(state, source, "@check");
    if (status != LUAU_HOST_STATUS_OK)
        throw std::runtime_error("chunk failed: " + string_at(state, -1) + "\n" + source);
    REQUIRE(luau_host_stack_get_top(state) == 0);
}

bool truthy(luau_host_state* state, const std::string& expression)
{
    const Bytecode bytecode("return " + expression);
    REQUIRE(load(state, bytecode, "@truthy") == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_pcall(state, 0, 1, 0) == LUAU_HOST_STATUS_OK);
    const bool value = luau_host_to_boolean(state, -1) != 0;
    REQUIRE(luau_host_stack_set_top(state, 0) == LUAU_HOST_STATUS_OK);
    return value;
}

// A candidate that matches is charged like one that does not: the bytes
// memchr scanned to reach it count before the search returns.
void test_matched_rare_candidates_are_charged()
{
    Root root;
    open_libraries(root.state);

    // 400 matches 128 KB apart. For each piece split scans 131,071 bytes and
    // then copies them, and each of the two is half a poll's worth of work, so
    // there is about one poll per piece. Without the charge for the scan there
    // is one for every two pieces.
    run_ok(root.state, "hay = string.rep(string.rep('x', 131071) .. 'ab', 400)");
    const long matched = polls_inside(root.state, "string.split(hay, 'ab')");
    if (matched < 380)
        std::cerr << "split: " << matched << " polls\n";
    REQUIRE(matched >= 380);

    // One candidate, 262,143 bytes in, which matches: a scan of that many
    // bytes plus the candidate is one poll's worth of work.
    for (const char* needle : {"ab", "abc"})
    {
        run_ok(root.state, std::string("one = string.rep('x', 262143) .. '") + needle + "'");
        const std::string quoted = std::string("'") + needle + "'";
        const long find = polls_inside(root.state, "string.find(one, " + quoted + ", 1, true)");
        if (find < 1)
            std::cerr << needle << ": find " << find << " polls\n";
        REQUIRE(find >= 1);
    }
}

// A needle whose first byte comes only every 128 KB of a 50 MB subject: the
// search spends its time in memchr between candidates, and must charge those
// bytes like the bytes of a scan that finds nothing.
void test_rare_candidates_are_charged()
{
    Root root;
    open_libraries(root.state);
    run_ok(root.state, "hay = string.rep(string.rep('x', 131071) .. 'a', 400)");
    const long absent = polls_inside(root.state, "string.find(hay, 'z', 1, true)");
    REQUIRE(absent >= 190);
    for (const char* needle : {"ab", "abc", "abcdefgh", "abcdefghijklmnopqrstuvwxyz012345", "abcdefghijklmnopqrstuvwxyz0123456"})
    {
        const std::string quoted = std::string("'") + needle + "'";
        const long find = polls_inside(root.state, "string.find(hay, " + quoted + ", 1, true)");
        const long split = polls_inside(root.state, "string.split(hay, " + quoted + ")");
        if (find < absent - 2 || split < absent - 2)
            std::cerr << needle << ": find " << find << ", split " << split << ", absent " << absent << " polls\n";
        REQUIRE(find >= absent - 2);
        REQUIRE(split >= absent - 2);
    }
}

// string.find checks its arguments and init before it classifies the pattern,
// classifies it once, and reaches safepoints while it does.
void test_find_classifies_after_init_and_once()
{
    Root root;
    open_libraries(root.state);
    run_ok(root.state, "hay = string.rep('x', 4097) short = string.rep('x', 100) needle = string.rep('y', 30 * 1048576)");
    // One poll per 16 KB of pattern examined.
    const long classification = long((30 * 1048576) / 16384);

    // init past the end of the subject: nil before any classification.
    REQUIRE(polls_inside(root.state, "string.find(hay, needle, 5000)") == 0);
    run_ok(root.state, "assert(select('#', string.find(hay, needle, 5000)) == 1 and string.find(hay, needle, 5000) == nil)");
    // No special character: one classification, then a plain search that
    // ends at once because the pattern is longer than the subject.
    for (const char* call : {"string.find(hay, needle)", "string.find(short, needle)", "string.find(hay, needle, -1)"})
    {
        const long polls = polls_inside(root.state, call);
        if (polls < classification || polls > classification + 2)
            std::cerr << call << ": " << polls << " polls, expected " << classification << '\n';
        REQUIRE(polls >= classification && polls <= classification + 2);
    }
    run_ok(root.state, "assert(string.find(hay, needle) == nil and string.find(short, needle) == nil)");
    // A plain search classifies nothing.
    REQUIRE(polls_inside(root.state, "string.find(hay, needle, 1, true)") == 0);
    // A special character at the end: the host's classification reaches the
    // same safepoints, then upstream's pattern matcher one per position.
    run_ok(root.state, "special = needle .. '.'");
    const long matched = polls_inside(root.state, "string.find(hay, special)");
    REQUIRE(matched >= classification + 4097);
    run_ok(root.state, "assert(string.find(hay, special) == nil)");
}

// The functions each library replaces, and calls that reach safepoints inside
// a host version but none inside upstream's.
struct ReplacedLibrary
{
    luau_host_library library;
    const char* name;
    std::vector<std::pair<const char*, const char*>> functions;
    // A function the library keeps, which must survive a refused open.
    const char* kept;
};

const ReplacedLibrary kReplacedLibraries[] = {
    {LUAU_HOST_LIBRARY_STRING, "string",
        {{"find", "string.find(probe, 'z', 1, true)"}, {"split", "string.split(probe, 'zz')"}}, "len"},
    {LUAU_HOST_LIBRARY_TABLE, "table",
        {{"concat", "table.concat(probetable)"}, {"sort", "table.sort({3, 1, 2}, rawequal)"}}, "insert"},
    {LUAU_HOST_LIBRARY_UTF8, "utf8", {{"len", "utf8.len(probe)"}}, "char"},
};

// The probes' inputs: a call over them reaches at least two safepoints in a
// host version. Uses string.rep and table.create.
const char* kProbeSetup = "probe = string.rep('x', 600000) probetable = table.create(4000, 'ab')";

void require_host_versions(luau_host_state* state, const ReplacedLibrary& library)
{
    for (const auto& function : library.functions)
    {
        const long polls = polls_inside(state, function.second);
        if (polls < 2)
            std::cerr << library.name << '.' << function.first << ": " << polls << " polls inside\n";
        REQUIRE(polls >= 2);
    }
}

std::string assignments(const ReplacedLibrary& library, const char* value)
{
    std::string source;
    for (const auto& function : library.functions)
        source += std::string(library.name) + "." + function.first + " = " + value + " ";
    return source;
}

void require_refused(luau_host_state* state, luau_host_library library, const std::string& message)
{
    const luau_host_status status = open_library(state, library);
    if (status != LUAU_HOST_STATUS_LUA_ERROR || string_at(state, -1) != message)
        std::cerr << "open: status " << status << ", " << (status == LUAU_HOST_STATUS_OK ? "" : string_at(state, -1)) << '\n';
    REQUIRE(status == LUAU_HOST_STATUS_LUA_ERROR);
    REQUIRE(string_at(state, -1) == message);
    REQUIRE(luau_host_stack_set_top(state, 0) == LUAU_HOST_STATUS_OK);
}

// The review's script: a __newindex on a library table opened before, which
// would capture each function that registration stores into the table.
void test_reopening_under_a_library_newindex_is_refused()
{
    for (const ReplacedLibrary& library : kReplacedLibraries)
    {
        Root root;
        open_libraries(root.state);
        const std::string lib = library.name;
        run_ok(root.state, "captured = {} " + assignments(library, "nil") +
            "kept = {__newindex = function(t, k, v) captured[#captured + 1] = k rawset(t, k, v) end} setmetatable(" + lib + ", kept)");
        require_refused(root.state, library.library,
            "cannot open the " + lib + " library while the library table has a __newindex metamethod");

        // Nothing was registered or captured, the library is as it was, and
        // the root runs code.
        std::string check = "assert(#captured == 0) assert(getmetatable(" + lib + ") == kept) assert(type(rawget(" + lib +
            ", '" + library.kept + "')) == 'function') ";
        for (const auto& function : library.functions)
            check += "assert(rawget(" + lib + ", '" + function.first + "') == nil) ";
        run_ok(root.state, check);

        // Without the metatable the library opens again, with the host's
        // versions, and nothing was captured.
        run_ok(root.state, "setmetatable(" + lib + ", nil)");
        REQUIRE(open_library(root.state, library.library) == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);
        run_ok(root.state, std::string("assert(#captured == 0) ") + kProbeSetup);
        require_host_versions(root.state, library);
    }
}

// A __newindex on the globals table before the first open, which would see
// the new library table stored there and could give it a metatable of its own.
void test_opening_under_a_globals_newindex_is_refused()
{
    for (const ReplacedLibrary& library : kReplacedLibraries)
    {
        Root root;
        REQUIRE(open_library(root.state, LUAU_HOST_LIBRARY_BASE) == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);
        const std::string lib = library.name;
        run_ok(root.state, R"LUAU(
            captured = {}
            setmetatable(_G, {__newindex = function(g, k, v)
                captured[#captured + 1] = k
                if type(v) == "table" then
                    setmetatable(v, {__newindex = function(t, name, f) captured[#captured + 1] = name rawset(t, name, f) end})
                end
                rawset(g, k, v)
            end})
        )LUAU");
        require_refused(root.state, library.library,
            "cannot open the " + lib + " library while the globals table has a __newindex metamethod");
        run_ok(root.state, "assert(#captured == 0) assert(rawget(_G, '" + lib + "') == nil)");
    }

    // luaopen_table also stores the global unpack after it registers concat
    // and sort. With unpack already there the store is raw and the open goes
    // ahead; without it, the open is refused.
    Root root;
    open_libraries(root.state);
    run_ok(root.state, std::string(kProbeSetup) + R"LUAU(
        captured = {}
        setmetatable(_G, {__newindex = function(g, k, v) captured[#captured + 1] = {k, rawget(table, "sort")} rawset(g, k, v) end})
    )LUAU");
    REQUIRE(open_library(root.state, LUAU_HOST_LIBRARY_TABLE) == LUAU_HOST_STATUS_OK);
    REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);
    run_ok(root.state, "assert(#captured == 0) rawset(_G, 'unpack', nil)");
    require_host_versions(root.state, kReplacedLibraries[1]);
    require_refused(root.state, LUAU_HOST_LIBRARY_TABLE, "cannot open the table library while the globals table has a __newindex metamethod");
    run_ok(root.state, "assert(#captured == 0) assert(rawget(_G, 'unpack') == nil)");
    require_host_versions(root.state, kReplacedLibraries[1]);
}

// Reopening without a __newindex: with no metatable, or with one that has
// other metamethods, the library opens and holds the host's versions, whether
// the functions were there or removed first.
void test_reopening_without_a_newindex_installs_the_host_versions()
{
    const char* metatables[] = {
        nullptr,
        "{}",
        "{__index = function() return nil end, __metatable = 'locked'}",
        "{__index = {find = 1, sort = 1, len = 1}, __call = print, __len = function() return 0 end}",
    };
    for (const ReplacedLibrary& library : kReplacedLibraries)
    {
        for (const char* metatable : metatables)
        {
            for (bool removed : {false, true})
            {
                Root root;
                open_libraries(root.state);
                const std::string lib = library.name;
                std::string setup = std::string(kProbeSetup) + " ";
                if (removed)
                    setup += assignments(library, "nil");
                if (metatable)
                    setup += "kept = " + std::string(metatable) + " setmetatable(" + lib + ", kept)";
                run_ok(root.state, setup);
                REQUIRE(open_library(root.state, library.library) == LUAU_HOST_STATUS_OK);
                REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);
                if (metatable)
                    run_ok(root.state, "assert(rawequal(getmetatable(" + lib + "), kept) or getmetatable(" + lib + ") == 'locked')");
                require_host_versions(root.state, library);
            }
        }
    }
}

// A sandboxed child thread's globals table proxies reads to the root's with an
// __index metamethod and has no __newindex, so a library opens there, into
// the root's library table when the root opened it, and into a new table in
// the child's globals otherwise.
void test_a_sandboxed_child_thread_opens_libraries()
{
    for (const ReplacedLibrary& library : kReplacedLibraries)
    {
        for (bool rootOpened : {false, true})
        {
            Root root;
            REQUIRE(open_library(root.state, LUAU_HOST_LIBRARY_BASE) == LUAU_HOST_STATUS_OK);
            REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);
            // The probes' setup needs string.rep and table.create.
            for (const ReplacedLibrary& other : kReplacedLibraries)
            {
                if (rootOpened || other.library != library.library)
                {
                    REQUIRE(open_library(root.state, other.library) == LUAU_HOST_STATUS_OK);
                    REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);
                }
            }

            luau_host_state* child = nullptr;
            REQUIRE(luau_host_thread_create(root.state, &child) == LUAU_HOST_STATUS_OK);
            REQUIRE(luau_host_sandbox_thread(child) == LUAU_HOST_STATUS_OK);
            REQUIRE(open_library(child, library.library) == LUAU_HOST_STATUS_OK);
            REQUIRE(luau_host_stack_set_top(child, 0) == LUAU_HOST_STATUS_OK);
            run_ok(child, std::string(kProbeSetup) + " assert(getmetatable(getfenv(1)) ~= nil)");
            require_host_versions(child, library);
            // The child's globals got the new table; the root's did not.
            REQUIRE(truthy(child, "rawget(getfenv(1), '" + std::string(library.name) + "') ~= nil") == !rootOpened);
            REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);
            REQUIRE(truthy(root.state, "rawget(_G, '" + std::string(library.name) + "') ~= nil") == rootOpened);
        }
    }
}

// An open that fails partway raises its error only after each name that
// registration wrote holds the host's version again: whatever the point of
// failure, a replaced name is then absent or the host's. Each attempt removes
// the names first, so a name present after a failed attempt was registered by
// it.
//
// Here memory runs out. Luau allocates small objects from pages per size
// class and asks the host's allocator only for a new page, so the open fails
// where its size class's page fills. The open allocates mostly C closures
// (48 bytes on 64-bit targets, the size of a string of 16 to 23 bytes). A
// pool of such strings first takes every free slot of that size, so that the
// last page of the size class is partly full; each attempt then takes a
// different number of further slots, up to more than a page holds, and fills
// all the remaining memory, so that the page fills at a different allocation
// in each attempt.
void test_an_open_that_runs_out_of_memory_leaves_no_upstream_function()
{
    constexpr uint64_t kLimit = 8 * 1024 * 1024;
    for (const ReplacedLibrary& library : kReplacedLibraries)
    {
        Root root(kLimit);
        open_libraries(root.state);
        run_ok(root.state, std::string(kProbeSetup) +
            " pool = table.create(4000) for i = 1, 4000 do pool[i] = string.format('p%018d', i) end");
        const Bytecode remove(assignments(library, "nil"));

        // What a large buffer, which gets an allocation of its own rather
        // than a slot in a page, costs beyond its payload.
        luau_host_memory_info memory = {};
        memory.struct_size = sizeof(memory);
        REQUIRE(luau_host_collect(root.state) == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_memory_get(root.state, &memory) == LUAU_HOST_STATUS_OK);
        const uint64_t before = memory.current_bytes;
        void* filler = nullptr;
        REQUIRE(luau_host_buffer_create(root.state, 100000, &filler) == LUAU_HOST_STATUS_OK);
        REQUIRE(luau_host_memory_get(root.state, &memory) == LUAU_HOST_STATUS_OK);
        const uint64_t overhead = memory.current_bytes - before - 100000;
        REQUIRE(overhead < 1024);
        REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);

        int failures = 0;
        int successes = 0;
        int registeredBeforeFailing = 0;
        // A small page holds fewer than 300 slots of this size.
        for (int taken = 0; taken < 330; ++taken)
        {
            REQUIRE(load(root.state, remove, "@remove") == LUAU_HOST_STATUS_OK);
            REQUIRE(luau_host_pcall(root.state, 0, 0, 0) == LUAU_HOST_STATUS_OK);
            run_ok(root.state, "keep = table.create(" + std::to_string(taken) + ") for i = 1, " + std::to_string(taken) +
                " do keep[i] = string.format('%019d', i) end");
            REQUIRE(luau_host_collect(root.state) == LUAU_HOST_STATUS_OK);
            REQUIRE(luau_host_memory_get(root.state, &memory) == LUAU_HOST_STATUS_OK);
            REQUIRE(memory.current_bytes + overhead < kLimit);
            REQUIRE(luau_host_buffer_create(root.state, kLimit - memory.current_bytes - overhead, &filler) == LUAU_HOST_STATUS_OK);
            REQUIRE(luau_host_memory_get(root.state, &memory) == LUAU_HOST_STATUS_OK);
            REQUIRE(memory.current_bytes == kLimit);

            int32_t count = 0;
            const luau_host_status status = luau_host_open_library(root.state, library.library, &count);
            REQUIRE(luau_host_stack_get_top(root.state) == 2);
            REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);
            REQUIRE(luau_host_memory_reset_failure(root.state) == LUAU_HOST_STATUS_OK);
            REQUIRE(luau_host_collect(root.state) == LUAU_HOST_STATUS_OK);
            run_ok(root.state, "keep = nil");
            REQUIRE(luau_host_collect(root.state) == LUAU_HOST_STATUS_OK);
            if (status == LUAU_HOST_STATUS_OK)
            {
                ++successes;
                require_host_versions(root.state, library);
                continue;
            }
            REQUIRE(status == LUAU_HOST_STATUS_MEMORY_QUOTA);
            ++failures;
            // Each name registration got to is the host's version.
            bool registered = false;
            for (const auto& function : library.functions)
            {
                if (!truthy(root.state, "rawget(" + std::string(library.name) + ", '" + function.first + "') ~= nil"))
                    continue;
                registered = true;
                const long polls = polls_inside(root.state, function.second);
                if (polls < 2)
                    std::cerr << library.name << '.' << function.first << " after a failed open (" << taken << " taken): " << polls << " polls inside\n";
                REQUIRE(polls >= 2);
            }
            registeredBeforeFailing += registered ? 1 : 0;
        }
        if (failures == 0 || successes == 0 || registeredBeforeFailing == 0)
            std::cerr << library.name << ": " << failures << " failures, " << successes << " successes, "
                      << registeredBeforeFailing << " after registering\n";
        REQUIRE(failures > 0 && successes > 0 && registeredBeforeFailing > 0);
    }
}

// Here luaopen_table fails after it has registered concat and sort, when it
// stores the global unpack into a read-only globals table. The table library
// is kept out of the sandbox's reach in an upvalue, so its table stays
// writable while the globals table is frozen.
void test_an_open_that_fails_on_read_only_globals_leaves_no_upstream_function()
{
    Root root;
    open_libraries(root.state);
    run_ok(root.state, std::string(kProbeSetup) + " local t = table gettable = function() return t end table = nil");
    REQUIRE(luau_host_sandbox_root(root.state) == LUAU_HOST_STATUS_OK);
    REQUIRE(open_library(root.state, LUAU_HOST_LIBRARY_TABLE) == LUAU_HOST_STATUS_LUA_ERROR);
    REQUIRE(string_at(root.state, -1).find("readonly") != std::string::npos);
    REQUIRE(luau_host_stack_set_top(root.state, 0) == LUAU_HOST_STATUS_OK);
    // The globals table is frozen, so the library table goes in a local.
    const long baseline = polls_of(root.state, "local t = gettable() local r = rawequal(1, 1)");
    REQUIRE(polls_of(root.state, "local t = gettable() local r = t.concat(probetable)") - baseline >= 2);
    REQUIRE(polls_of(root.state, "local t = gettable() local r = t.sort({3, 1, 2}, rawequal)") - baseline >= 2);
}

#if defined(LUAU_HOST_LIBRARY_COVERAGE)
// The corpus and the tests above reach every rarely taken path of the host
// versions: the two-way search in both its forms, its switch to scanning for
// the critical byte, and its continuation across string.split's pieces; the
// heap-sort fallback; and the library opens that are refused or repaired.
void test_the_corpus_covers_every_path()
{
    const char* points[] = {
        "twoWayPeriodic",
        "twoWayNonPeriodic",
        "twoWayCriticalSkip",
        "twoWayResumed",
        "heapSort",
        "findHandedToUpstream",
        "concatBufferGrowth",
        "splitRehash",
        "openRefused",
        "openRepairedAfterError",
    };
    bool covered = true;
    for (const char* point : points)
    {
        const uint64_t count = luau_host_library_coverage_count(point);
        std::cout << "  " << point << ": " << count << '\n';
        covered = covered && count != 0 && count != UINT64_MAX;
    }
    REQUIRE(covered);
}
#endif

template<typename Test>
int run_test(const char* name, Test test)
{
    try
    {
        const auto started = std::chrono::steady_clock::now();
        test();
        const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - started);
        std::cout << "[PASS] " << name << " (" << elapsed.count() << " ms)\n";
        return 0;
    }
    catch (const std::exception& exception)
    {
        std::cerr << "[FAIL] " << name << ": " << exception.what() << '\n';
        return 1;
    }
}
} // namespace

int main()
{
    int failures = 0;
    failures += run_test("every route names one function", test_every_route_names_one_function);
    failures += run_test("string.find matches upstream over a generated corpus", test_find_matches_upstream);
    failures += run_test("string.split matches upstream over a generated corpus", test_split_matches_upstream);
    failures += run_test("table.sort matches upstream over a generated corpus", test_sort_matches_upstream);
    failures += run_test("table.concat matches upstream over a generated corpus", test_concat_matches_upstream);
    failures += run_test("utf8.len matches upstream over a generated corpus", test_utf8_len_matches_upstream);
    failures += run_test("rare candidates are charged", test_rare_candidates_are_charged);
    failures += run_test("matched rare candidates are charged", test_matched_rare_candidates_are_charged);
    failures += run_test("string.find classifies after init and once", test_find_classifies_after_init_and_once);
    failures += run_test(
        "reopening under a library __newindex is refused", test_reopening_under_a_library_newindex_is_refused);
    failures += run_test(
        "opening under a globals __newindex is refused", test_opening_under_a_globals_newindex_is_refused);
    failures += run_test(
        "reopening without a __newindex installs the host versions",
        test_reopening_without_a_newindex_installs_the_host_versions);
    failures += run_test("a sandboxed child thread opens libraries", test_a_sandboxed_child_thread_opens_libraries);
    failures += run_test(
        "an open that runs out of memory leaves no upstream function",
        test_an_open_that_runs_out_of_memory_leaves_no_upstream_function);
    failures += run_test(
        "an open that fails on read-only globals leaves no upstream function",
        test_an_open_that_fails_on_read_only_globals_leaves_no_upstream_function);
    failures += run_test("long library calls reach the interrupt callback", test_long_calls_reach_the_interrupt_callback);
    failures += run_test("long library calls stop inside the call", test_long_calls_stop_inside_the_call);
    failures += run_test("long library calls obey throttled budgets", test_long_calls_obey_throttled_budgets);
    failures += run_test(
        "long library calls stop on a request from another thread",
        test_long_calls_stop_on_a_request_from_another_thread);
#if defined(LUAU_HOST_LIBRARY_COVERAGE)
    failures += run_test("the tests above cover every rarely taken path", test_the_corpus_covers_every_path);
#endif

    if (failures != 0)
        std::cerr << failures << " library conformance test group(s) failed\n";
    return failures == 0 ? 0 : 1;
}
