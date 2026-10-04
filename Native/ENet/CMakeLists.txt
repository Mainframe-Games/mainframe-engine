# ENet native library for the ENet-CSharp 2.4.8 managed binding.
#
# Builds upstream nxrighthere/ENet-CSharp `Source/Native/enet.c` (git submodule pinned to tag 2.4.8)
# as a shared library whose file name matches the binding's `DllImport("enet")`:
#   Windows: enet.dll   Linux: libenet.so   macOS: libenet.dylib (universal when CMAKE_OSX_ARCHITECTURES
#   lists both slices).
#
# Only the `enet_*` API is exported. On Windows upstream's ENET_DLL/ENET_IMPLEMENTATION macros mark the API
# dllexport; on macOS/Linux upstream declares the API with default visibility (it has no visibility macro),
# so a linker export list / version script restricts the dynamic symbol table to `enet_*` instead of
# -fvisibility=hidden (which would hide the API itself).
cmake_minimum_required(VERSION 3.21)

project(mf_enet VERSION 2.4.8 LANGUAGES C)

if(PROJECT_IS_TOP_LEVEL)
	include(CTest)
endif()

set(MF_ENET_UPSTREAM_DIR "${CMAKE_CURRENT_SOURCE_DIR}/upstream/Source/Native")
if(NOT EXISTS "${MF_ENET_UPSTREAM_DIR}/enet.c")
	message(FATAL_ERROR "ENet sources not found at ${MF_ENET_UPSTREAM_DIR}. Run: git submodule update --init Native/ENet/upstream")
endif()

add_library(enet SHARED "${MF_ENET_UPSTREAM_DIR}/enet.c")
# The implementation lives in enet.h (single-header style), so it is a normal include for the library itself
# (warnings visible) and a SYSTEM include for consumers such as the tests (third-party header noise hidden).
target_include_directories(enet PRIVATE "${MF_ENET_UPSTREAM_DIR}")
target_include_directories(enet SYSTEM INTERFACE "${MF_ENET_UPSTREAM_DIR}")
set_target_properties(enet PROPERTIES
	OUTPUT_NAME enet
	C_STANDARD 99
	C_STANDARD_REQUIRED ON
	C_EXTENSIONS ON
	# P/Invoke loads by file name: no VERSION/SOVERSION, so there is no symlink chain (SONAME is libenet.so).
	INSTALL_NAME_DIR "@rpath"
	BUILD_WITH_INSTALL_NAME_DIR ON
)

if(WIN32)
	target_compile_definitions(enet PRIVATE ENET_DLL _WINSOCK_DEPRECATED_NO_WARNINGS _CRT_SECURE_NO_WARNINGS)
	target_link_libraries(enet PRIVATE ws2_32 winmm)
	set_target_properties(enet PROPERTIES PREFIX "")
elseif(APPLE)
	target_link_options(enet PRIVATE "LINKER:-exported_symbols_list,${CMAKE_CURRENT_SOURCE_DIR}/exports.macos.txt")
	set_property(TARGET enet APPEND PROPERTY LINK_DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/exports.macos.txt")
else()
	target_link_options(enet PRIVATE "LINKER:--version-script=${CMAKE_CURRENT_SOURCE_DIR}/exports.linux.map")
	set_property(TARGET enet APPEND PROPERTY LINK_DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/exports.linux.map")
endif()

# Upstream code is third-party and is not patched: warnings are enabled (-Wall -Wextra / /W4) but not fatal.
# The three clang/gcc categories below were reviewed at 2.4.8 and are benign, so they are silenced to keep the
# build log clean (anything new still shows up):
#   -Wunused-parameter       protocol handlers share a signature and ignore some arguments
#   -Wsign-compare           enet_crc64-style loops compare an int index with a size_t length
#   -Wmisleading-indentation enet.h:2065 single-line `if (...) return -1;` followed by a loop
if(MSVC)
	target_compile_options(enet PRIVATE /W4)
else()
	target_compile_options(enet PRIVATE -Wall -Wextra -Wno-unused-parameter -Wno-sign-compare)
	if(CMAKE_C_COMPILER_ID MATCHES "Clang|GNU")
		target_compile_options(enet PRIVATE -Wno-misleading-indentation)
	endif()
endif()

if(BUILD_TESTING)
	add_subdirectory(tests)
endif()
