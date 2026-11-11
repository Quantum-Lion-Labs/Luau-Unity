cmake_minimum_required(VERSION 3.25)

project(luau_host LANGUAGES C CXX)

include(CTest)

option(LUAU_HOST_BUILD_TESTS "Build the native luau_host conformance executable" ON)
option(LUAU_HOST_ENABLE_SANITIZERS "Instrument the host and pinned Luau sources with ASan/UBSan" OFF)
option(LUAU_HOST_BUILD_FUZZERS "Build libFuzzer compiler and ABI harnesses" OFF)
option(LUAU_HOST_LINUX_SHIPPING "Build the self-contained, hardened Linux x64 shipping host" OFF)
set(LUAU_HOST_UPSTREAM_REVISION "2a3d8937c19c8fd9f7b72b6ec9068c62760e6588")
set(
    LUAU_HOST_BUILD_CONFIGURATION
    ""
    CACHE STRING
    "Build marker included in the ABI fingerprint; empty selects the active CMake configuration"
)

set(LUAU_HOST_ROOT "${CMAKE_CURRENT_SOURCE_DIR}")
get_filename_component(LUAU_REPOSITORY_ROOT "${LUAU_HOST_ROOT}/../.." REALPATH)
get_filename_component(
    LUAU_UPSTREAM_ROOT
    "${CMAKE_CURRENT_SOURCE_DIR}/../luau"
    REALPATH
)

if(NOT EXISTS "${LUAU_UPSTREAM_ROOT}/CMakeLists.txt")
    message(
        FATAL_ERROR
        "The pinned Luau submodule is unavailable at ${LUAU_UPSTREAM_ROOT}. "
        "Initialize submodules before configuring luau_host."
    )
endif()

# The ABI record must describe the sources that were actually compiled. A
# caller-provided cache value is not sufficient: without this check, a build
# from a different Luau checkout could advertise the approved revision and
# pass the managed handshake.
find_package(Git REQUIRED)
execute_process(
    COMMAND "${GIT_EXECUTABLE}" -C "${LUAU_UPSTREAM_ROOT}" rev-parse HEAD
    RESULT_VARIABLE _luau_host_git_result
    OUTPUT_VARIABLE _luau_host_actual_upstream_revision
    ERROR_VARIABLE _luau_host_git_error
    OUTPUT_STRIP_TRAILING_WHITESPACE
)
if(NOT _luau_host_git_result EQUAL 0)
    message(FATAL_ERROR "Unable to read the Luau submodule revision: ${_luau_host_git_error}")
endif()
if(NOT _luau_host_actual_upstream_revision STREQUAL LUAU_HOST_UPSTREAM_REVISION)
    message(
        FATAL_ERROR
        "Luau submodule revision mismatch: expected ${LUAU_HOST_UPSTREAM_REVISION}, "
        "actual ${_luau_host_actual_upstream_revision}"
    )
endif()

execute_process(
    COMMAND "${GIT_EXECUTABLE}" -C "${LUAU_UPSTREAM_ROOT}" status --porcelain --untracked-files=no
    RESULT_VARIABLE _luau_host_status_result
    OUTPUT_VARIABLE _luau_host_dirty_upstream
    ERROR_VARIABLE _luau_host_status_error
    OUTPUT_STRIP_TRAILING_WHITESPACE
)
if(NOT _luau_host_status_result EQUAL 0)
    message(FATAL_ERROR "Unable to inspect the Luau submodule worktree: ${_luau_host_status_error}")
endif()
if(NOT _luau_host_dirty_upstream STREQUAL "")
    message(
        FATAL_ERROR
        "The Luau submodule contains tracked local modifications. The host ABI may only be built from the exact approved commit.\n"
        "${_luau_host_dirty_upstream}"
    )
endif()

# Force an incremental build to reconfigure before it can compile changed
# provenance inputs using stale definitions. The submodule is detached in the
# supported checkout, so its Git HEAD file is also a direct revision trigger.
execute_process(
    COMMAND "${GIT_EXECUTABLE}" -C "${LUAU_UPSTREAM_ROOT}" rev-parse --git-path HEAD
    OUTPUT_VARIABLE _luau_host_git_head_path
    OUTPUT_STRIP_TRAILING_WHITESPACE
    COMMAND_ERROR_IS_FATAL ANY
)
if(NOT IS_ABSOLUTE "${_luau_host_git_head_path}")
    get_filename_component(
        _luau_host_git_head_path
        "${_luau_host_git_head_path}"
        ABSOLUTE
        BASE_DIR "${LUAU_UPSTREAM_ROOT}"
    )
endif()
file(
    GLOB_RECURSE
    _luau_host_upstream_provenance_inputs
    LIST_DIRECTORIES false
    "${LUAU_UPSTREAM_ROOT}/*.c"
    "${LUAU_UPSTREAM_ROOT}/*.cc"
    "${LUAU_UPSTREAM_ROOT}/*.cpp"
    "${LUAU_UPSTREAM_ROOT}/*.h"
    "${LUAU_UPSTREAM_ROOT}/*.hpp"
    "${LUAU_UPSTREAM_ROOT}/CMakeLists.txt"
)
set_property(
    DIRECTORY
    APPEND
    PROPERTY CMAKE_CONFIGURE_DEPENDS
        "${LUAU_HOST_ROOT}/include/luau_host.h"
        "${LUAU_HOST_ROOT}/src/luau_host.cpp"
        "${LUAU_HOST_ROOT}/src/reference_tokens.h"
        "${LUAU_HOST_ROOT}/src/tracked_allocation.h"
        "${LUAU_HOST_ROOT}/exports/luau_host.exports"
        "${LUAU_HOST_ROOT}/cmake/AuditCompatibility.cmake"
        "${LUAU_REPOSITORY_ROOT}/Luau.Unity/Runtime/Interop/NativeMethods.cs"
        "${LUAU_REPOSITORY_ROOT}/src/Luau/Internal/LuauNativeProtection.cs"
        "${_luau_host_git_head_path}"
        ${_luau_host_upstream_provenance_inputs}
)

# Hash ABI-relevant repository inputs rather than a hand-maintained label.
# Paths and timestamps are intentionally excluded so identical source trees
# produce the same compatibility fingerprint on every machine.
file(SHA256 "${LUAU_HOST_ROOT}/include/luau_host.h" _luau_host_header_hash)
file(SHA256 "${LUAU_HOST_ROOT}/src/luau_host.cpp" _luau_host_source_hash)
file(SHA256 "${LUAU_HOST_ROOT}/src/reference_tokens.h" _luau_host_reference_tokens_hash)
file(SHA256 "${LUAU_HOST_ROOT}/src/tracked_allocation.h" _luau_host_allocator_hash)
file(SHA256 "${LUAU_HOST_ROOT}/exports/luau_host.exports" _luau_host_exports_hash)
string(
    SHA256
    LUAU_HOST_BUILD_INPUT_FINGERPRINT
    "abi=2.0;upstream=${_luau_host_actual_upstream_revision};header=${_luau_host_header_hash};source=${_luau_host_source_hash};references=${_luau_host_reference_tokens_hash};allocator=${_luau_host_allocator_hash};exports=${_luau_host_exports_hash}"
)

# These values must be established before upstream creates its static targets.
# Hidden visibility on luau_host alone is not sufficient because the final
# shared library incorporates objects from the upstream archives.
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set(CMAKE_C_VISIBILITY_PRESET hidden)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)
set(CMAKE_VISIBILITY_INLINES_HIDDEN ON)

if(LUAU_HOST_ENABLE_SANITIZERS)
    if(NOT CMAKE_CXX_COMPILER_ID MATCHES "Clang|GNU" OR MSVC)
        message(FATAL_ERROR "LUAU_HOST_ENABLE_SANITIZERS requires Clang or GCC with ASan/UBSan support")
    endif()
    add_compile_options(-fsanitize=address,undefined -fno-omit-frame-pointer -fno-sanitize-recover=all)
    add_link_options(-fsanitize=address,undefined -fno-sanitize-recover=all)
endif()

if(LUAU_HOST_LINUX_SHIPPING)
    if(NOT CMAKE_SYSTEM_NAME STREQUAL "Linux" OR NOT CMAKE_CXX_COMPILER_ID STREQUAL "Clang")
        message(FATAL_ERROR "LUAU_HOST_LINUX_SHIPPING requires a Clang build targeting Linux")
    endif()
    if(LUAU_HOST_ENABLE_SANITIZERS OR NOT CMAKE_BUILD_TYPE STREQUAL "Release")
        message(FATAL_ERROR "LUAU_HOST_LINUX_SHIPPING requires an uninstrumented Release build")
    endif()
    # The shipped binary is stripped by the native-artifacts tooling; debug
    # information is retained only in the external symbol file. Hardening
    # applies to the upstream archives too, because they are linked into the
    # shared library. Mapping the checkout root and not recording compiler
    # command lines keeps build-machine paths out of debug information, so the
    # content-derived build ID below depends only on what was built.
    add_compile_options(
        -g
        -gno-record-gcc-switches
        -fstack-protector-strong
        -fstack-clash-protection
        "-ffile-prefix-map=${LUAU_REPOSITORY_ROOT}=."
    )
    add_compile_definitions(_FORTIFY_SOURCE=2)
endif()

if(MSVC)
    # Release packages remain stripped of symbols, while native CI retains a
    # full external PDB for crash/security investigation. /Brepro removes
    # timestamp/GUID variance from objects, and PDBALTPATH below prevents the
    # package DLL from embedding a build-machine absolute path.
    add_compile_options(/Zi /Brepro)

    # The Microsoft C and C++ runtimes are linked statically, so the plugin
    # imports only KERNEL32.dll. A Unity Windows player does not ship
    # MSVCP140.dll or VCRUNTIME140*.dll, so a plugin that needed them would fail
    # with DllNotFoundException on a machine without the Visual C++
    # redistributable. This is safe because no C++ object, allocation, or
    # exception crosses the C host ABI: every C++ exception and VM long jump is
    # contained inside this library, and the one buffer a caller receives is
    # released by luau_host_buffer_free in the same runtime that allocated it.
    # The preset selects the runtime; it must hold for every target, including
    # the upstream archives, which inherit it below. native-artifacts and
    # package-static reject any other import.
    set(LUAU_HOST_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
    if(NOT "${CMAKE_MSVC_RUNTIME_LIBRARY}" STREQUAL "${LUAU_HOST_MSVC_RUNTIME_LIBRARY}")
        message(FATAL_ERROR
            "luau_host requires CMAKE_MSVC_RUNTIME_LIBRARY=${LUAU_HOST_MSVC_RUNTIME_LIBRARY} "
            "(the static C and C++ runtimes); '${CMAKE_MSVC_RUNTIME_LIBRARY}' would make the "
            "plugin depend on Visual C++ runtime DLLs that Unity players do not contain. "
            "Configure with the windows-x64 preset.")
    endif()
    # An explicit runtime flag would override the selection above. It may
    # stand alone or inside a generator expression. The flag variables are
    # checked here; directory, target, and source options are checked once
    # every target exists, at the end of this file.
    set(LUAU_HOST_MSVC_RUNTIME_FLAG_PATTERN "(^|[ \t:;>])[/-]M[DT]d?([ \t;>,]|$)")
    set(_luau_host_flag_suffixes _DEBUG _RELEASE _RELWITHDEBINFO _MINSIZEREL)
    foreach(_luau_host_configuration IN LISTS CMAKE_CONFIGURATION_TYPES CMAKE_BUILD_TYPE)
        string(TOUPPER "_${_luau_host_configuration}" _luau_host_flag_suffix)
        list(APPEND _luau_host_flag_suffixes "${_luau_host_flag_suffix}")
    endforeach()
    list(REMOVE_DUPLICATES _luau_host_flag_suffixes)
    foreach(_luau_host_flags_variable IN ITEMS CMAKE_C_FLAGS CMAKE_CXX_FLAGS)
        set(_luau_host_flag_variables "${_luau_host_flags_variable}")
        foreach(_luau_host_flag_suffix IN LISTS _luau_host_flag_suffixes)
            list(APPEND _luau_host_flag_variables "${_luau_host_flags_variable}${_luau_host_flag_suffix}")
        endforeach()
        foreach(_luau_host_flag_variable IN LISTS _luau_host_flag_variables)
            if("${${_luau_host_flag_variable}}" MATCHES "${LUAU_HOST_MSVC_RUNTIME_FLAG_PATTERN}")
                message(FATAL_ERROR
                    "${_luau_host_flag_variable} selects a C runtime explicitly; luau_host selects "
                    "the static runtime through CMAKE_MSVC_RUNTIME_LIBRARY.")
            endif()
        endforeach()
    endforeach()
endif()

set(LUAU_BUILD_CLI OFF CACHE BOOL "" FORCE)
set(LUAU_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(LUAU_BUILD_WEB OFF CACHE BOOL "" FORCE)
set(LUAU_BUILD_SHARED OFF CACHE BOOL "" FORCE)
set(LUAU_EXTERN_C ON CACHE BOOL "" FORCE)
set(LUAU_WERROR OFF CACHE BOOL "" FORCE)

add_subdirectory(
    "${LUAU_UPSTREAM_ROOT}"
    "${CMAKE_CURRENT_BINARY_DIR}/upstream-luau"
    EXCLUDE_FROM_ALL
)

add_library(
    luau_host
    SHARED
    src/luau_host.cpp
    src/reference_tokens.h
    src/tracked_allocation.h
)

target_compile_features(luau_host PRIVATE cxx_std_17)
if(LUAU_HOST_BUILD_CONFIGURATION STREQUAL "")
    if(CMAKE_CONFIGURATION_TYPES)
        set(_luau_host_build_configuration "$<CONFIG>")
    elseif(NOT CMAKE_BUILD_TYPE STREQUAL "")
        set(_luau_host_build_configuration "${CMAKE_BUILD_TYPE}")
    else()
        set(_luau_host_build_configuration "unknown")
    endif()
else()
    set(_luau_host_build_configuration "${LUAU_HOST_BUILD_CONFIGURATION}")
endif()
target_compile_definitions(
    luau_host
    PRIVATE
        LUAU_HOST_BUILDING_LIBRARY=1
        "LUAU_HOST_UPSTREAM_REVISION=\"${_luau_host_actual_upstream_revision}\""
        "LUAU_HOST_BUILD_INPUT_FINGERPRINT=\"${LUAU_HOST_BUILD_INPUT_FINGERPRINT}\""
        "LUAU_HOST_BUILD_CONFIGURATION=\"${_luau_host_build_configuration}\""
)
target_include_directories(
    luau_host
    PUBLIC
        "$<BUILD_INTERFACE:${LUAU_HOST_ROOT}/include>"
        "$<INSTALL_INTERFACE:include>"
)
target_link_libraries(
    luau_host
    PRIVATE
        Luau.Compiler
        Luau.VM
        Luau.VM.Internals
)

if(ANDROID)
    # The NDK runtime choice is part of the reviewed toolchain contract. The
    # toolchain wrapper links libc++ statically, and --exclude-libs below keeps
    # it out of the dynamic symbol table, so the plugin depends only on Android
    # system libraries and loads whatever application entry point the player
    # uses. This is safe because no C++ object, allocation, or exception crosses
    # the C host ABI: every C++ exception and VM long jump is contained inside
    # this library. native-artifacts and package-static reject any other
    # dynamic dependency.
    if(NOT ANDROID_STL STREQUAL "c++_static")
        message(FATAL_ERROR
            "luau_host requires ANDROID_STL=c++_static; ${ANDROID_STL} would make the "
            "plugin depend on a C++ runtime that Unity players do not always contain")
    endif()

    # NDK lld otherwise hashes host-specific unstripped debug metadata into the
    # GNU build ID, even though the reviewed stripped binaries are identical.
    # Keep symbol correlation while making the ID a stable digest of the
    # source, target, toolchain, configuration, and reviewed shipping recipe.
    set(_luau_host_android_build_recipe "android-shipping-v1")
    string(
        SHA1
        _luau_host_android_build_id
        "recipe=${_luau_host_android_build_recipe};inputs=${LUAU_HOST_BUILD_INPUT_FINGERPRINT};abi=${ANDROID_ABI};platform=${ANDROID_PLATFORM};stl=${ANDROID_STL};ndk=${LUAU_HOST_APPROVED_ANDROID_NDK_REVISION};compiler=${CMAKE_CXX_COMPILER_ID}-${CMAKE_CXX_COMPILER_VERSION};configuration=${_luau_host_build_configuration}"
    )
    target_link_options(
        luau_host
        PRIVATE "LINKER:--build-id=0x${_luau_host_android_build_id}"
    )
endif()

if(LUAU_HOST_LINUX_SHIPPING)
    # The Linux plugin loads into Unity editors, players, and dedicated servers
    # on any distribution with glibc 2.35 or newer. Linking the C++ runtime
    # statically (and hiding it with --exclude-libs) leaves libc and libm as the
    # only dynamic dependencies, so the host never depends on the libstdc++
    # version of the process that loads it.
    target_link_options(
        luau_host
        PRIVATE
            -static-libstdc++
            -static-libgcc
            "LINKER:-z,relro"
            "LINKER:-z,now"
            "LINKER:-z,noexecstack"
    )

    # Derive the build ID from the linked output, so any change to sources,
    # flags, or the statically linked runtime produces a new ID and symbol
    # files can never be matched to the wrong binary.
    target_link_options(luau_host PRIVATE "LINKER:--build-id=sha1")
endif()

set_target_properties(
    luau_host
    PROPERTIES
        OUTPUT_NAME "luau_host"
        CXX_VISIBILITY_PRESET hidden
        VISIBILITY_INLINES_HIDDEN YES
        WINDOWS_EXPORT_ALL_SYMBOLS OFF
)

if(MSVC)
    target_compile_options(luau_host PRIVATE /W4 /WX /permissive- /EHsc)
    target_link_options(
        luau_host
        PRIVATE
            /DEBUG:FULL
            /Brepro
            "/PDBALTPATH:luau_host.pdb"
    )
    set_target_properties(luau_host PROPERTIES PDB_NAME "luau_host")
    # LINK treats an existing program PDB as both input and output, incrementing
    # its age and changing the PE reproducibility record on an otherwise
    # identical relink. Start every link with a fresh external PDB so clean and
    # incremental release builds converge while preserving exact DLL/PDB
    # identity for retained symbols.
    add_custom_command(
        TARGET luau_host
        PRE_LINK
        COMMAND "${CMAKE_COMMAND}" -E rm -f "$<TARGET_PDB_FILE:luau_host>"
        VERBATIM
    )
else()
    target_compile_options(luau_host PRIVATE -Wall -Wextra -Wpedantic -Werror)
    if(CMAKE_CXX_COMPILER_ID MATCHES "Clang")
        # The approved upstream VM internal headers use anonymous structs in
        # anonymous unions. Keep all other pedantic diagnostics fatal.
        target_compile_options(luau_host PRIVATE -Wno-nested-anon-types)
    endif()
endif()

# The version script is an additional ELF containment boundary. The exact
# checked-in allowlist is enforced after linking by luau_host_export_audit.
if(UNIX AND NOT APPLE)
    set(LUAU_HOST_ELF_VERSION_SCRIPT "${LUAU_HOST_ROOT}/exports/luau_host.map")
    target_link_options(
        luau_host
        PRIVATE
            "LINKER:--version-script=${LUAU_HOST_ELF_VERSION_SCRIPT}"
            "LINKER:--exclude-libs,ALL"
    )
    # Production ELF hosts must remain self-contained. Clang's shared-library
    # ASan mode deliberately leaves sanitizer hooks for the instrumented final
    # executable to provide, so --no-undefined is incompatible with that lane.
    if(NOT LUAU_HOST_ENABLE_SANITIZERS)
        target_link_options(luau_host PRIVATE "LINKER:--no-undefined")
    endif()
    set_property(
        TARGET luau_host
        APPEND
        PROPERTY LINK_DEPENDS "${LUAU_HOST_ELF_VERSION_SCRIPT}"
    )
endif()

set(LUAU_HOST_EXPORT_ALLOWLIST "${LUAU_HOST_ROOT}/exports/luau_host.exports")
set(LUAU_HOST_EXPORT_AUDIT_SCRIPT "${LUAU_HOST_ROOT}/cmake/AuditExports.cmake")
set(LUAU_HOST_COMPATIBILITY_AUDIT_SCRIPT "${LUAU_HOST_ROOT}/cmake/AuditCompatibility.cmake")
set(LUAU_HOST_MANAGED_INTEROP "${LUAU_REPOSITORY_ROOT}/Luau.Unity/Runtime/Interop/NativeMethods.cs")
set(LUAU_HOST_MANAGED_PROTECTION "${LUAU_REPOSITORY_ROOT}/src/Luau/Internal/LuauNativeProtection.cs")

execute_process(
    COMMAND
        "${CMAKE_COMMAND}"
        "-DHEADER=${LUAU_HOST_ROOT}/include/luau_host.h"
        "-DALLOWLIST=${LUAU_HOST_EXPORT_ALLOWLIST}"
        "-DINTEROP=${LUAU_HOST_MANAGED_INTEROP}"
        "-DMANAGED_PROTECTION=${LUAU_HOST_MANAGED_PROTECTION}"
        -P "${LUAU_HOST_COMPATIBILITY_AUDIT_SCRIPT}"
    RESULT_VARIABLE _luau_host_compatibility_result
    OUTPUT_VARIABLE _luau_host_compatibility_output
    ERROR_VARIABLE _luau_host_compatibility_error
)
if(NOT _luau_host_compatibility_result EQUAL 0)
    message(FATAL_ERROR "luau_host compatibility audit failed:\n${_luau_host_compatibility_output}${_luau_host_compatibility_error}")
endif()

if(MSVC)
    get_filename_component(LUAU_HOST_MSVC_TOOL_DIRECTORY "${CMAKE_LINKER}" DIRECTORY)
    find_program(
        LUAU_HOST_EXPORT_TOOL
        NAMES dumpbin.exe dumpbin
        HINTS "${LUAU_HOST_MSVC_TOOL_DIRECTORY}"
        NO_DEFAULT_PATH
        REQUIRED
    )
    set(LUAU_HOST_EXPORT_TOOL_KIND "MSVC")
elseif(UNIX AND NOT APPLE)
    set(LUAU_HOST_EXPORT_TOOL "${CMAKE_NM}")
    set(LUAU_HOST_EXPORT_TOOL_KIND "NM")
else()
    set(LUAU_HOST_EXPORT_TOOL "")
    set(LUAU_HOST_EXPORT_TOOL_KIND "UNSUPPORTED")
endif()

if(NOT LUAU_HOST_EXPORT_TOOL_KIND STREQUAL "UNSUPPORTED")
    add_custom_command(
        TARGET luau_host
        POST_BUILD
        COMMAND
            "${CMAKE_COMMAND}"
            "-DBINARY=$<TARGET_FILE:luau_host>"
            "-DALLOWLIST=${LUAU_HOST_EXPORT_ALLOWLIST}"
            "-DEXPORT_TOOL=${LUAU_HOST_EXPORT_TOOL}"
            "-DEXPORT_TOOL_KIND=${LUAU_HOST_EXPORT_TOOL_KIND}"
            -P "${LUAU_HOST_EXPORT_AUDIT_SCRIPT}"
        COMMAND
            "${CMAKE_COMMAND}"
            "-DHEADER=${LUAU_HOST_ROOT}/include/luau_host.h"
            "-DALLOWLIST=${LUAU_HOST_EXPORT_ALLOWLIST}"
            "-DINTEROP=${LUAU_HOST_MANAGED_INTEROP}"
            "-DMANAGED_PROTECTION=${LUAU_HOST_MANAGED_PROTECTION}"
            -P "${LUAU_HOST_COMPATIBILITY_AUDIT_SCRIPT}"
        COMMENT "Auditing luau_host exports and exact managed/native compatibility"
        VERBATIM
    )

    add_custom_target(
        luau_host_export_audit
        COMMAND
            "${CMAKE_COMMAND}"
            "-DBINARY=$<TARGET_FILE:luau_host>"
            "-DALLOWLIST=${LUAU_HOST_EXPORT_ALLOWLIST}"
            "-DEXPORT_TOOL=${LUAU_HOST_EXPORT_TOOL}"
            "-DEXPORT_TOOL_KIND=${LUAU_HOST_EXPORT_TOOL_KIND}"
            -P "${LUAU_HOST_EXPORT_AUDIT_SCRIPT}"
        DEPENDS luau_host
        COMMENT "Auditing luau_host exported symbols"
        VERBATIM
    )
endif()

add_custom_target(
    luau_host_compatibility_audit
    COMMAND
        "${CMAKE_COMMAND}"
        "-DHEADER=${LUAU_HOST_ROOT}/include/luau_host.h"
        "-DALLOWLIST=${LUAU_HOST_EXPORT_ALLOWLIST}"
        "-DINTEROP=${LUAU_HOST_MANAGED_INTEROP}"
        "-DMANAGED_PROTECTION=${LUAU_HOST_MANAGED_PROTECTION}"
        -P "${LUAU_HOST_COMPATIBILITY_AUDIT_SCRIPT}"
    DEPENDS luau_host
    COMMENT "Auditing exact luau_host managed/native compatibility"
    VERBATIM
)

# Compile the public contract as C11 independently of the C++ implementation.
# This is an object target by design: no platform executable or runtime is
# required, so cross-compiled Android builds exercise it as well.
add_library(
    luau_host_c_header_compile
    OBJECT
    tests/luau_host_header_compile.c
)
target_compile_features(luau_host_c_header_compile PRIVATE c_std_11)
target_include_directories(luau_host_c_header_compile PRIVATE "${LUAU_HOST_ROOT}/include")

if(MSVC)
    target_compile_options(luau_host_c_header_compile PRIVATE /W4 /WX)
else()
    target_compile_options(luau_host_c_header_compile PRIVATE -Wall -Wextra -Wpedantic -Werror)
endif()

if(LUAU_HOST_BUILD_TESTS)
    find_package(Threads REQUIRED)

    add_executable(
        luau_host_conformance
        tests/luau_host_conformance.cpp
    )
    target_compile_features(luau_host_conformance PRIVATE cxx_std_17)
    target_link_libraries(luau_host_conformance PRIVATE luau_host Threads::Threads)
    target_include_directories(luau_host_conformance PRIVATE "${LUAU_HOST_ROOT}/src")

    if(MSVC)
        target_compile_options(luau_host_conformance PRIVATE /W4 /WX /permissive- /EHsc)
    else()
        target_compile_options(luau_host_conformance PRIVATE -Wall -Wextra -Wpedantic -Werror)
    endif()

    add_test(NAME luau_host_conformance COMMAND luau_host_conformance)

    # The host replaces a few library functions with versions that reach the
    # interrupt callback while they work. This executable also links the
    # upstream VM, as a plain reference to compare them against; its copy is
    # separate from the one inside luau_host, whose symbols stay hidden.
    add_executable(
        luau_host_library_conformance
        tests/luau_host_library_conformance.cpp
    )
    target_compile_features(luau_host_library_conformance PRIVATE cxx_std_17)
    target_link_libraries(luau_host_library_conformance PRIVATE luau_host Luau.VM Threads::Threads)

    if(MSVC)
        target_compile_options(luau_host_library_conformance PRIVATE /W4 /WX /permissive- /EHsc)
    else()
        target_compile_options(luau_host_library_conformance PRIVATE -Wall -Wextra -Wpedantic -Werror)
    endif()

    add_test(NAME luau_host_library_conformance COMMAND luau_host_library_conformance)

    # The same tests with the host compiled into the executable and counters
    # on the rarely taken paths of the long library calls, which show that
    # the corpus reaches those paths. Only this build defines
    # LUAU_HOST_LIBRARY_COVERAGE; it shares one copy of the upstream VM with
    # the reference, which changes nothing the tests compare.
    add_executable(
        luau_host_library_coverage
        tests/luau_host_library_conformance.cpp
        src/luau_host.cpp
    )
    target_compile_features(luau_host_library_coverage PRIVATE cxx_std_17)
    target_compile_definitions(
        luau_host_library_coverage
        PRIVATE
            LUAU_HOST_LIBRARY_COVERAGE=1
            LUAU_HOST_BUILDING_LIBRARY=1
            "LUAU_HOST_UPSTREAM_REVISION=\"${_luau_host_actual_upstream_revision}\""
            "LUAU_HOST_BUILD_INPUT_FINGERPRINT=\"${LUAU_HOST_BUILD_INPUT_FINGERPRINT}\""
            "LUAU_HOST_BUILD_CONFIGURATION=\"${_luau_host_build_configuration}\""
    )
    target_include_directories(luau_host_library_coverage PRIVATE "${LUAU_HOST_ROOT}/include")
    target_link_libraries(
        luau_host_library_coverage
        PRIVATE
            Luau.Compiler
            Luau.VM
            Luau.VM.Internals
            Threads::Threads
    )

    if(MSVC)
        target_compile_options(luau_host_library_coverage PRIVATE /W4 /WX /permissive- /EHsc)
    else()
        target_compile_options(luau_host_library_coverage PRIVATE -Wall -Wextra -Wpedantic -Werror)
        if(CMAKE_CXX_COMPILER_ID MATCHES "Clang")
            target_compile_options(luau_host_library_coverage PRIVATE -Wno-nested-anon-types)
        endif()
    endif()

    if(LUAU_HOST_ENABLE_SANITIZERS)
        # The corpus then leaves out index arguments whose conversion to int
        # is undefined in upstream's own code.
        target_compile_definitions(luau_host_library_conformance PRIVATE LUAU_HOST_TEST_SANITIZED=1)
        target_compile_definitions(luau_host_library_coverage PRIVATE LUAU_HOST_TEST_SANITIZED=1)
    endif()

    add_test(NAME luau_host_library_coverage COMMAND luau_host_library_coverage)

    # The reference-wrap fixture is the host compiled from its own source with
    # two more exports that position the process reference-token allocator
    # (tests/luau_host_reference_wrap_fixture.h), so tests reach the end of
    # the token range without creating two billion references first. The
    # native tests below and the managed ReferenceTokenWrapTests load it. It
    # keeps the host's build identity, so the managed handshake accepts it;
    # it is never installed or copied into the package.
    if(NOT ANDROID)
        set(_luau_host_reference_wrap_directory "${CMAKE_CURRENT_BINARY_DIR}/reference-wrap-fixture")
        add_library(
            luau_host_reference_wrap
            SHARED
            tests/luau_host_reference_wrap_fixture.cpp
        )
        target_compile_features(luau_host_reference_wrap PRIVATE cxx_std_17)
        target_compile_definitions(
            luau_host_reference_wrap
            PRIVATE
                LUAU_HOST_BUILDING_LIBRARY=1
                "LUAU_HOST_UPSTREAM_REVISION=\"${_luau_host_actual_upstream_revision}\""
                "LUAU_HOST_BUILD_INPUT_FINGERPRINT=\"${LUAU_HOST_BUILD_INPUT_FINGERPRINT}\""
                "LUAU_HOST_BUILD_CONFIGURATION=\"${_luau_host_build_configuration}\""
        )
        target_include_directories(
            luau_host_reference_wrap
            PUBLIC "${LUAU_HOST_ROOT}/include" "${LUAU_HOST_ROOT}/tests"
        )
        target_link_libraries(
            luau_host_reference_wrap
            PRIVATE
                Luau.Compiler
                Luau.VM
                Luau.VM.Internals
        )
        set_target_properties(
            luau_host_reference_wrap
            PROPERTIES
                CXX_VISIBILITY_PRESET hidden
                VISIBILITY_INLINES_HIDDEN YES
                WINDOWS_EXPORT_ALL_SYMBOLS OFF
                RUNTIME_OUTPUT_DIRECTORY "${_luau_host_reference_wrap_directory}"
                LIBRARY_OUTPUT_DIRECTORY "${_luau_host_reference_wrap_directory}"
                ARCHIVE_OUTPUT_DIRECTORY "${_luau_host_reference_wrap_directory}"
        )
        if(UNIX AND NOT APPLE)
            target_link_options(
                luau_host_reference_wrap
                PRIVATE
                    "LINKER:--version-script=${LUAU_HOST_ELF_VERSION_SCRIPT}"
                    "LINKER:--exclude-libs,ALL"
            )
        endif()

        add_executable(
            luau_host_reference_wrap_conformance
            tests/luau_host_reference_wrap_conformance.cpp
        )
        target_compile_features(luau_host_reference_wrap_conformance PRIVATE cxx_std_17)
        target_link_libraries(luau_host_reference_wrap_conformance PRIVATE luau_host_reference_wrap Threads::Threads)
        set_target_properties(
            luau_host_reference_wrap_conformance
            PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${_luau_host_reference_wrap_directory}"
        )

        foreach(_luau_host_reference_wrap_target IN ITEMS luau_host_reference_wrap luau_host_reference_wrap_conformance)
            if(MSVC)
                target_compile_options("${_luau_host_reference_wrap_target}" PRIVATE /W4 /WX /permissive- /EHsc)
            else()
                target_compile_options("${_luau_host_reference_wrap_target}" PRIVATE -Wall -Wextra -Wpedantic -Werror)
                if(CMAKE_CXX_COMPILER_ID MATCHES "Clang")
                    target_compile_options("${_luau_host_reference_wrap_target}" PRIVATE -Wno-nested-anon-types)
                endif()
            endif()
        endforeach()

        add_test(NAME luau_host_reference_wrap_conformance COMMAND luau_host_reference_wrap_conformance)
    endif()

    # Purpose-built malformed ABI libraries exercise the managed handshake
    # without adding mutation hooks or test exports to the production host.
    # Each target compiles the same tiny C fixture with one fixed defect.
    set(
        _luau_host_invalid_abi_cases
        wrong_magic
        wrong_major
        missing_required_feature
        wrong_pointer_size
        wrong_compile_options_size
        wrong_callback_table_size
        shifted_tags
        truncated_record
    )
    set(_luau_host_invalid_abi_targets "")
    set(_luau_host_invalid_abi_case_id 0)

    foreach(_luau_host_invalid_abi_case IN LISTS _luau_host_invalid_abi_cases)
        math(EXPR _luau_host_invalid_abi_case_id "${_luau_host_invalid_abi_case_id} + 1")
        set(_luau_host_invalid_abi_target "luau_host_invalid_abi_${_luau_host_invalid_abi_case}")

        add_library(
            "${_luau_host_invalid_abi_target}"
            SHARED
            tests/luau_host_invalid_abi_fixture.c
        )
        target_compile_features("${_luau_host_invalid_abi_target}" PRIVATE c_std_11)
        target_compile_definitions(
            "${_luau_host_invalid_abi_target}"
            PRIVATE
                LUAU_HOST_BUILDING_LIBRARY=1
                "LUAU_HOST_INVALID_ABI_CASE=${_luau_host_invalid_abi_case_id}"
        )
        target_include_directories(
            "${_luau_host_invalid_abi_target}"
            PRIVATE
                "${LUAU_HOST_ROOT}/include"
        )
        set_target_properties(
            "${_luau_host_invalid_abi_target}"
            PROPERTIES
                C_VISIBILITY_PRESET hidden
                VISIBILITY_INLINES_HIDDEN YES
                WINDOWS_EXPORT_ALL_SYMBOLS OFF
                RUNTIME_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}/invalid-abi-fixtures"
                LIBRARY_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}/invalid-abi-fixtures"
                ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}/invalid-abi-fixtures"
        )

        if(MSVC)
            target_compile_options("${_luau_host_invalid_abi_target}" PRIVATE /W4 /WX)
        else()
            target_compile_options(
                "${_luau_host_invalid_abi_target}"
                PRIVATE
                    -Wall
                    -Wextra
                    -Wpedantic
                    -Werror
            )
        endif()

        list(APPEND _luau_host_invalid_abi_targets "${_luau_host_invalid_abi_target}")
    endforeach()

    add_custom_target(
        luau_host_invalid_abi_fixtures
        DEPENDS ${_luau_host_invalid_abi_targets}
    )
endif()

if(LUAU_HOST_BUILD_FUZZERS)
    if(NOT CMAKE_CXX_COMPILER_ID MATCHES "Clang" OR MSVC)
        message(FATAL_ERROR "LUAU_HOST_BUILD_FUZZERS requires Clang libFuzzer support")
    endif()

    foreach(_luau_host_fuzzer IN ITEMS compiler abi)
        add_executable(
            "luau_host_${_luau_host_fuzzer}_fuzzer"
            "tests/luau_host_${_luau_host_fuzzer}_fuzzer.cpp"
        )
        target_compile_features("luau_host_${_luau_host_fuzzer}_fuzzer" PRIVATE cxx_std_17)
        target_link_libraries("luau_host_${_luau_host_fuzzer}_fuzzer" PRIVATE luau_host)
        target_compile_options(
            "luau_host_${_luau_host_fuzzer}_fuzzer"
            PRIVATE
                -fsanitize=fuzzer
                -Wall
                -Wextra
                -Wpedantic
                -Werror
        )
        target_link_options("luau_host_${_luau_host_fuzzer}_fuzzer" PRIVATE -fsanitize=fuzzer)
    endforeach()

    set(_luau_host_fuzz_artifacts "${CMAKE_CURRENT_BINARY_DIR}/fuzz-artifacts")
    add_custom_target(
        luau_host_fuzz_smoke
        COMMAND "${CMAKE_COMMAND}" -E make_directory "${_luau_host_fuzz_artifacts}/compiler"
        COMMAND
            "$<TARGET_FILE:luau_host_compiler_fuzzer>"
            -runs=5000
            -max_len=65536
            -timeout=5
            -rss_limit_mb=2048
            "-artifact_prefix=${_luau_host_fuzz_artifacts}/compiler/"
            "${LUAU_HOST_ROOT}/fuzz/corpus/compiler"
        COMMAND "${CMAKE_COMMAND}" -E make_directory "${_luau_host_fuzz_artifacts}/abi"
        COMMAND
            "$<TARGET_FILE:luau_host_abi_fuzzer>"
            -runs=10000
            -max_len=4096
            -timeout=5
            -rss_limit_mb=2048
            "-artifact_prefix=${_luau_host_fuzz_artifacts}/abi/"
            "${LUAU_HOST_ROOT}/fuzz/corpus/abi"
        DEPENDS luau_host_compiler_fuzzer luau_host_abi_fuzzer
        USES_TERMINAL
        VERBATIM
    )
endif()

if(MSVC)
    # Every target that compiles code, here and in the upstream tree, must use
    # the static runtime: the host and the upstream archives linked into it, the
    # conformance executable, and the ABI fixtures. A target-level override, or
    # a runtime flag passed as a directory, target, or source option, would
    # otherwise mix runtimes in the plugin or test a different runtime than the
    # one that ships. These checks catch a wrong selection at configure time;
    # the import audit of the built DLL (native-artifacts, package-static) is
    # what enforces the result. Keep this block after every target.
    function(luau_host_reject_msvc_runtime_flag owner options)
        foreach(_option IN LISTS options)
            if("${_option}" MATCHES "${LUAU_HOST_MSVC_RUNTIME_FLAG_PATTERN}")
                message(FATAL_ERROR
                    "${owner} passes the C runtime flag '${_option}'; luau_host selects the "
                    "static runtime through CMAKE_MSVC_RUNTIME_LIBRARY.")
            endif()
        endforeach()
    endfunction()

    function(luau_host_require_msvc_runtime directory)
        get_property(_directory_options DIRECTORY "${directory}" PROPERTY COMPILE_OPTIONS)
        luau_host_reject_msvc_runtime_flag("Directory ${directory}" "${_directory_options}")
        get_property(_targets DIRECTORY "${directory}" PROPERTY BUILDSYSTEM_TARGETS)
        foreach(_target IN LISTS _targets)
            get_target_property(_type "${_target}" TYPE)
            if(_type MATCHES "^(STATIC_LIBRARY|SHARED_LIBRARY|MODULE_LIBRARY|OBJECT_LIBRARY|EXECUTABLE)$")
                get_target_property(_runtime "${_target}" MSVC_RUNTIME_LIBRARY)
                if(NOT "${_runtime}" STREQUAL "${LUAU_HOST_MSVC_RUNTIME_LIBRARY}")
                    message(FATAL_ERROR
                        "Target ${_target} uses MSVC_RUNTIME_LIBRARY '${_runtime}'; every luau_host "
                        "target must use ${LUAU_HOST_MSVC_RUNTIME_LIBRARY}.")
                endif()
                foreach(_property IN ITEMS COMPILE_OPTIONS COMPILE_FLAGS)
                    get_target_property(_target_options "${_target}" ${_property})
                    if(_target_options)
                        luau_host_reject_msvc_runtime_flag("Target ${_target}" "${_target_options}")
                    endif()
                endforeach()
                get_target_property(_sources "${_target}" SOURCES)
                if(_sources)
                    foreach(_source IN LISTS _sources)
                        if(NOT "${_source}" MATCHES "\\$<")
                            foreach(_property IN ITEMS COMPILE_OPTIONS COMPILE_FLAGS)
                                get_source_file_property(
                                    _source_options "${_source}" TARGET_DIRECTORY "${_target}" ${_property})
                                if(_source_options)
                                    luau_host_reject_msvc_runtime_flag(
                                        "Source ${_source} of target ${_target}" "${_source_options}")
                                endif()
                            endforeach()
                        endif()
                    endforeach()
                endif()
            endif()
        endforeach()
        get_property(_subdirectories DIRECTORY "${directory}" PROPERTY SUBDIRECTORIES)
        foreach(_subdirectory IN LISTS _subdirectories)
            luau_host_require_msvc_runtime("${_subdirectory}")
        endforeach()
    endfunction()
    luau_host_require_msvc_runtime("${CMAKE_CURRENT_SOURCE_DIR}")
endif()

install(
    TARGETS luau_host
    RUNTIME DESTINATION .
    LIBRARY DESTINATION .
    ARCHIVE DESTINATION .
)
install(FILES include/luau_host.h DESTINATION include)
install(
    FILES "${LUAU_HOST_EXPORT_ALLOWLIST}"
    DESTINATION share/luau-host
)
