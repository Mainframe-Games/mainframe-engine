# mfmeshopt: mesh simplification for generated levels of detail through meshoptimizer 1.3 (external/meshoptimizer,
# MIT), compiled static and hidden, as ONE shared library:
#   Windows: mfmeshopt.dll   Linux: libmfmeshopt.so   macOS: libmfmeshopt.dylib
# Only `mfmeshopt_*` is exported. Public header: include/mfmeshopt.h. Design: docs/design/natives.md, ADR 0256.
cmake_minimum_required(VERSION 3.25)

project(mfmeshopt VERSION 1.0 LANGUAGES C CXX)

if(PROJECT_IS_TOP_LEVEL)
	include(CTest)
endif()

option(MF_NATIVES_WERROR "Treat warnings in our own (non third-party) native code as errors" ON)

set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set(CMAKE_C_VISIBILITY_PRESET hidden)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)
set(CMAKE_VISIBILITY_INLINES_HIDDEN ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>") # static CRT (/MT)

set(MESHOPT "${CMAKE_CURRENT_SOURCE_DIR}/external/meshoptimizer")
if(NOT EXISTS "${MESHOPT}/src/meshoptimizer.h")
	message(FATAL_ERROR "meshoptimizer is missing: git submodule update --init Native/MeshOpt/external/meshoptimizer")
endif()

# meshoptimizer's own CMake builds every source the same way; the simplifier and the vertex cache optimiser are all
# this shim calls (the allocator is theirs too).
add_library(mfmeshopt_meshoptimizer STATIC
	${MESHOPT}/src/allocator.cpp
	${MESHOPT}/src/simplifier.cpp
	${MESHOPT}/src/vcacheoptimizer.cpp
)
# SYSTEM: our -Werror flags do not apply to meshoptimizer.h (GCC warns in its templates).
target_include_directories(mfmeshopt_meshoptimizer SYSTEM PUBLIC "${MESHOPT}/src")
target_compile_features(mfmeshopt_meshoptimizer PRIVATE cxx_std_11)
if(MSVC)
	target_compile_options(mfmeshopt_meshoptimizer PRIVATE /w)
else()
	target_compile_options(mfmeshopt_meshoptimizer PRIVATE -w)
endif()

add_library(mfmeshopt SHARED include/mfmeshopt.h shim/mfmeshopt.cpp)
target_include_directories(mfmeshopt PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include")
target_compile_definitions(mfmeshopt PRIVATE MFMESHOPT_BUILDING)
target_compile_features(mfmeshopt PRIVATE cxx_std_17)
target_link_libraries(mfmeshopt PRIVATE mfmeshopt_meshoptimizer)
set_target_properties(mfmeshopt PROPERTIES OUTPUT_NAME mfmeshopt CXX_EXTENSIONS OFF INSTALL_NAME_DIR "@rpath")
if(MSVC)
	target_compile_options(mfmeshopt PRIVATE /W4 /permissive- $<$<BOOL:${MF_NATIVES_WERROR}>:/WX>)
else()
	target_compile_options(mfmeshopt PRIVATE -Wall -Wextra -Wpedantic -Wshadow -Wconversion -Wsign-conversion
		$<$<BOOL:${MF_NATIVES_WERROR}>:-Werror>)
endif()
if(APPLE)
	target_link_options(mfmeshopt PRIVATE "LINKER:-exported_symbols_list,${CMAKE_CURRENT_SOURCE_DIR}/shim/exports.macos.txt")
	set_property(TARGET mfmeshopt APPEND PROPERTY LINK_DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/shim/exports.macos.txt")
elseif(UNIX)
	target_link_options(mfmeshopt PRIVATE "LINKER:--version-script=${CMAKE_CURRENT_SOURCE_DIR}/shim/exports.linux.map"
		"LINKER:--exclude-libs,ALL" -static-libstdc++ -static-libgcc)
	set_property(TARGET mfmeshopt APPEND PROPERTY LINK_DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/shim/exports.linux.map")
endif()

if(BUILD_TESTING)
	add_executable(mfmeshopt_smoke tests/smoke.c)
	target_link_libraries(mfmeshopt_smoke PRIVATE mfmeshopt)
	if(UNIX AND NOT APPLE)
		target_link_libraries(mfmeshopt_smoke PRIVATE m) # sinf/cosf
	endif()
	add_test(NAME mfmeshopt_smoke COMMAND mfmeshopt_smoke)
endif()
