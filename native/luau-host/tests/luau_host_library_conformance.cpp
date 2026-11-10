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

    Root()
    {
        luau_host_memory_info failure = {};
        failure.struct_size = sizeof(failure);
        REQUIRE(luau_host_state_create(nullptr, &state, &failure) == LUAU_HOST_STATUS_OK);
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

void open_libraries(luau_host_state* state)
{
    for (luau_host_library library : kCorpusLibraries)
    {
        int32_t count = 0;
        REQUIRE(luau_host_open_library(state, library, &count) == LUAU_HOST_STATUS_OK);
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

// Runs a chunk that returns one string in a host root. An unthrottled poll
// that never stops is installed, so every safepoint the host versions reach
// goes through the whole interrupt path.
std::string run_in_host(const Bytecode& bytecode, const char* chunkName)
{
    Root root;
    open_libraries(root.state);
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
void require_same_records(const std::string& source, const char* chunkName, size_t minimumRecords)
{
    const Bytecode bytecode(source);
    const std::vector<std::string> host = lines_of(run_in_host(bytecode, chunkName));
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

std::string corpus(const char* body, int seed)
{
    return "math.randomseed(" + std::to_string(seed) + ")\n" + kCorpusPrelude + body + "\nreturn table.concat(out, \"\\n\")\n";
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
    else init = inits[rnd(1, #inits)] end
    local plain = ({true, true, true, false, nil, 1, "yes", 0})[rnd(1, 8)]
    local shape = rnd(1, 12)
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
)LUAU";

const char* kSplitCorpus = R"LUAU(
local function summarize(ok, t, ...)
    -- Pieces are consecutive spans, so their lengths, the next() order of
    -- the keys, and whether joining them gives back the subject identify the
    -- table completely.
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
    return ok, #t, table.concat(lengths, ","), table.concat(keys, ","), select("#", ...)
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
    else sep = ({",", 1, 0.5, "\0"})[rnd(1, 4)] end
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
        local pieces = split(hay, sep)
        record(label, type(sep) ~= "string" or sep == "" or concat(pieces, sep) == hay, #pieces)
    end
end

-- Pieces large enough to reach a safepoint between them.
local many = string.rep("a,", 40000)
record("split many", summarize(pcall(split, many, ",")))
record("split many empty", summarize(pcall(split, string.rep("ab", 20000), "")))
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
    for i = 1, n do t[i] = setmetatable({id = i, v = rnd(1, 20)}, ordered) end
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
    elseif cmpkind == 13 then cmp = 5
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
    local i, j
    local range = rnd(1, 10)
    if range <= 5 then i, j = nil, nil
    elseif range <= 8 then i, j = rnd(-3, n + 3), rnd(-3, n + 3)
    elseif range == 9 then i, j = 1.5, "2"
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
    local range = rnd(1, 10)
    if range <= 4 then i, j = nil, nil
    elseif range <= 8 then i, j = rnd(-#s - 2, #s + 2), rnd(-#s - 2, #s + 2)
    elseif range == 9 then i, j = 0, -1
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

void test_find_matches_upstream()
{
    for (int seed : {1, 2, 3})
        require_same_records(corpus(kFindCorpus, seed), "@find-corpus", 12000);
}

void test_split_matches_upstream()
{
    for (int seed : {4, 5})
        require_same_records(corpus(kSplitCorpus, seed), "@split-corpus", 1500);
}

void test_sort_matches_upstream()
{
    for (int seed : {6, 7})
        require_same_records(corpus(kSortCorpus, seed), "@sort-corpus", 1300);
}

void test_concat_matches_upstream()
{
    for (int seed : {8, 9})
        require_same_records(corpus(kConcatCorpus, seed), "@concat-corpus", 1500);
}

void test_utf8_len_matches_upstream()
{
    for (int seed : {10, 11})
        require_same_records(corpus(kUtf8Corpus, seed), "@utf8-corpus", 900);
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
    REQUIRE(run_in_host(bytecode, "@routes") == "true,true,true,true");
    REQUIRE(run_in_upstream(bytecode, "@routes") == "true,true,true,true");
}

// Long calls used by the interrupt tests. Each one sets `reached` only after
// its call returns, so a stop inside the call leaves it nil.
struct LongCall
{
    const char* name;
    const char* setup;
    const char* call;
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
    failures += run_test("long library calls reach the interrupt callback", test_long_calls_reach_the_interrupt_callback);
    failures += run_test("long library calls stop inside the call", test_long_calls_stop_inside_the_call);
    failures += run_test("long library calls obey throttled budgets", test_long_calls_obey_throttled_budgets);
    failures += run_test(
        "long library calls stop on a request from another thread",
        test_long_calls_stop_on_a_request_from_another_thread);

    if (failures != 0)
        std::cerr << failures << " library conformance test group(s) failed\n";
    return failures == 0 ? 0 : 1;
}
