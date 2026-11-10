set(LUAU_HOST_APPROVED_ANDROID_NDK_REVISION "27.2.12479018" CACHE STRING
    "Android NDK revision approved for the Unity package")

if(DEFINED ENV{ANDROID_NDK_HOME} AND NOT "$ENV{ANDROID_NDK_HOME}" STREQUAL "")
    set(_luau_host_android_ndk "$ENV{ANDROID_NDK_HOME}")
elseif(DEFINED ENV{ANDROID_NDK_ROOT} AND NOT "$ENV{ANDROID_NDK_ROOT}" STREQUAL "")
    set(_luau_host_android_ndk "$ENV{ANDROID_NDK_ROOT}")
else()
    message(FATAL_ERROR
        "Set ANDROID_NDK_HOME to the approved Android NDK before using an Android preset")
endif()

file(TO_CMAKE_PATH "${_luau_host_android_ndk}" _luau_host_android_ndk)
set(_luau_host_android_source_properties
    "${_luau_host_android_ndk}/source.properties")
set(_luau_host_android_toolchain
    "${_luau_host_android_ndk}/build/cmake/android.toolchain.cmake")

if(NOT EXISTS "${_luau_host_android_source_properties}")
    message(FATAL_ERROR
        "ANDROID_NDK_HOME does not contain source.properties: ${_luau_host_android_ndk}")
endif()

if(NOT EXISTS "${_luau_host_android_toolchain}")
    message(FATAL_ERROR
        "Android CMake toolchain was not found: ${_luau_host_android_toolchain}")
endif()

file(STRINGS "${_luau_host_android_source_properties}"
    _luau_host_android_revision_line REGEX "^Pkg.Revision[ \t]*=")
string(REGEX REPLACE "^[^=]*=[ \t]*" ""
    _luau_host_android_revision "${_luau_host_android_revision_line}")

if(NOT _luau_host_android_revision STREQUAL LUAU_HOST_APPROVED_ANDROID_NDK_REVISION)
    message(FATAL_ERROR
        "Android NDK ${_luau_host_android_revision} is installed, but "
        "${LUAU_HOST_APPROVED_ANDROID_NDK_REVISION} is approved for this package")
endif()

set(CMAKE_ANDROID_NDK "${_luau_host_android_ndk}" CACHE PATH "" FORCE)
set(ANDROID_PLATFORM "android-26" CACHE STRING "" FORCE)
# The host links the C++ runtime statically and exports only its C ABI, so the
# plugin needs nothing beyond the Android system libraries. A Unity player
# contains libc++_shared.so only when something else ships it (in practice the
# GameActivity entry point), so a shared runtime would fail to load in a player
# that uses the classic Activity entry point.
set(ANDROID_STL "c++_static" CACHE STRING "" FORCE)
# Required by Android 15+ devices/emulators that use 16 KiB pages. NDK r27
# translates this into a 16 KiB maximum ELF page size while retaining 4 KiB
# device compatibility.
set(ANDROID_SUPPORT_FLEXIBLE_PAGE_SIZES ON CACHE BOOL "" FORCE)

include("${_luau_host_android_toolchain}")
