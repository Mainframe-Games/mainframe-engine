# mfrmlui: flat C ABI shim over RmlUi 6.3 (Core + Debugger) with the FreeType font engine, built as ONE shared
# library with everything linked statically:
#   Windows: mfrmlui.dll   Linux: libmfrmlui.so   macOS: libmfrmlui.dylib
#
# Only `mfrmlui_*` functions are exported (hidden visibility by default + linker export lists on macOS/Linux,
# __declspec(dllexport) on Windows). Public header: include/mfrmlui.h. Design: docs/design/natives.md and
# memory/decisions/0002-rmlui-native-shim.md.
cmake_minimum_required(VERSION 3.25)

project(mfrmlui VERSION 1.0 LANGUAGES C CXX)

if(PROJECT_IS_TOP_LEVEL)
	include(CTest)
endif()

option(MF_NATIVES_WERROR "Treat warnings in our own (non third-party) native code as errors" ON)

set(MF_RMLUI_DIR "${CMAKE_CURRENT_SOURCE_DIR}/external/RmlUi")
set(MF_FREETYPE_DIR "${CMAKE_CURRENT_SOURCE_DIR}/external/freetype")
foreach(dir IN ITEMS "${MF_RMLUI_DIR}" "${MF_FREETYPE_DIR}")
	if(NOT EXISTS "${dir}/CMakeLists.txt")
		message(FATAL_ERROR "${dir} is empty. Run: git submodule update --init Native/RmlUi/external/RmlUi Native/RmlUi/external/freetype")
	endif()
endforeach()

# --- global settings for everything linked into the shared library -------------------------------------------------
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set(CMAKE_C_VISIBILITY_PRESET hidden)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)
set(CMAKE_VISIBILITY_INLINES_HIDDEN ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>") # static CRT (/MT)
set(BUILD_SHARED_LIBS OFF)

# --- FreeType (FTL option; see THIRD_PARTY_NOTICES.md) -------------------------------------------------------------
# No optional system dependencies: they are absent on some CI images and Homebrew builds are single-arch, which would
# break the universal macOS binary. FreeType falls back to its bundled zlib for compressed fonts.
set(FT_DISABLE_ZLIB ON CACHE BOOL "" FORCE)
set(FT_DISABLE_BZIP2 ON CACHE BOOL "" FORCE)
set(FT_DISABLE_PNG ON CACHE BOOL "" FORCE)
set(FT_DISABLE_HARFBUZZ ON CACHE BOOL "" FORCE)
set(FT_DISABLE_BROTLI ON CACHE BOOL "" FORCE)
set(SKIP_INSTALL_ALL ON CACHE BOOL "" FORCE)
add_subdirectory("${MF_FREETYPE_DIR}" "${CMAKE_CURRENT_BINARY_DIR}/freetype" EXCLUDE_FROM_ALL SYSTEM)

# Hand our FreeType target to RmlUi instead of letting it search the system.
add_library(Freetype::Freetype ALIAS freetype)
set(CMAKE_DISABLE_FIND_PACKAGE_Freetype ON)

# --- RmlUi 6.3 ---------------------------------------------------------------------------------------------------------
set(RMLUI_FONT_ENGINE "freetype" CACHE STRING "" FORCE)
set(RMLUI_SAMPLES OFF CACHE BOOL "" FORCE)
set(RMLUI_LUA_BINDINGS OFF CACHE BOOL "" FORCE)
set(RMLUI_SVG_PLUGIN OFF CACHE BOOL "" FORCE)
set(RMLUI_LOTTIE_PLUGIN OFF CACHE BOOL "" FORCE)
set(RMLUI_TRACY_PROFILING OFF CACHE BOOL "" FORCE)
# Precompiled headers do not work with multi-arch (universal) macOS compiles.
set(RMLUI_PRECOMPILED_HEADERS OFF CACHE BOOL "" FORCE)
add_subdirectory("${MF_RMLUI_DIR}" "${CMAKE_CURRENT_BINARY_DIR}/RmlUi" EXCLUDE_FROM_ALL SYSTEM)

# --- the shim ------------------------------------------------------------------------------------------------------------
add_library(mfrmlui SHARED
	include/mfrmlui.h
	shim/mfrmlui_internal.h
	shim/abi_checks.cpp
	shim/context.cpp
	shim/core.cpp
	shim/data.cpp
	shim/debugger.cpp
	shim/element.cpp
	shim/render.cpp
)
target_include_directories(mfrmlui PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include")
target_compile_definitions(mfrmlui PRIVATE MFRMLUI_BUILDING)
target_compile_features(mfrmlui PRIVATE cxx_std_17)
# RmlUi::Debugger brings in RmlUi::Core and FreeType transitively (listing them again duplicates them on the link line).
target_link_libraries(mfrmlui PRIVATE RmlUi::Debugger)
set_target_properties(mfrmlui PROPERTIES
	OUTPUT_NAME mfrmlui
	CXX_EXTENSIONS OFF
	INSTALL_NAME_DIR "@rpath"
	BUILD_WITH_INSTALL_NAME_DIR ON
)

if(MSVC)
	target_compile_options(mfrmlui PRIVATE /W4 /permissive- /utf-8 /EHsc)
	if(MF_NATIVES_WERROR)
		target_compile_options(mfrmlui PRIVATE /WX)
	endif()
else()
	target_compile_options(mfrmlui PRIVATE -Wall -Wextra -Wpedantic -Wshadow -Wconversion -Wsign-conversion -Wnon-virtual-dtor)
	if(MF_NATIVES_WERROR)
		target_compile_options(mfrmlui PRIVATE -Werror)
	endif()
endif()

if(APPLE)
	target_link_options(mfrmlui PRIVATE
		"LINKER:-exported_symbols_list,${CMAKE_CURRENT_SOURCE_DIR}/shim/exports.macos.txt"
		"LINKER:-dead_strip"
		"$<$<CONFIG:Release,MinSizeRel>:LINKER:-x>")
	set_property(TARGET mfrmlui APPEND PROPERTY LINK_DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/shim/exports.macos.txt")
elseif(UNIX)
	# Self-contained .so: libstdc++/libgcc linked statically (and kept local) so the binary only needs glibc.
	target_link_options(mfrmlui PRIVATE
		"LINKER:--version-script=${CMAKE_CURRENT_SOURCE_DIR}/shim/exports.linux.map"
		"LINKER:--exclude-libs,ALL"
		"LINKER:--no-undefined"
		"LINKER:--as-needed"
		-static-libstdc++
		-static-libgcc
		"$<$<CONFIG:Release,MinSizeRel>:-s>")
	set_property(TARGET mfrmlui APPEND PROPERTY LINK_DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/shim/exports.linux.map")
endif()

if(BUILD_TESTING)
	add_subdirectory(tests)
endif()
