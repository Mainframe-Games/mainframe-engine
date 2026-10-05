# mfsvg: SVG rasterisation through ThorVG 1.0.3 — the exact copy Godot 4.7.2 vendors (thirdparty/thorvg, Godot's patch
# applied, PNG loader off), built and called as Godot's modules/svg does — as ONE shared library:
#   Windows: mfsvg.dll   Linux: libmfsvg.so   macOS: libmfsvg.dylib
# Only `mfsvg_*` is exported. Public header: include/mfsvg.h. Design: docs/design/natives.md, ADR 0112.
cmake_minimum_required(VERSION 3.25)

project(mfsvg VERSION 1.0 LANGUAGES C CXX)

if(PROJECT_IS_TOP_LEVEL)
	include(CTest)
endif()

option(MF_NATIVES_WERROR "Treat warnings in our own (non third-party) native code as errors" ON)

set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set(CMAKE_C_VISIBILITY_PRESET hidden)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)
set(CMAKE_VISIBILITY_INLINES_HIDDEN ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>") # static CRT (/MT)

set(TVG "${CMAKE_CURRENT_SOURCE_DIR}/thorvg")

# The source list of Godot's modules/svg/SCsub (minus the PNG loader).
add_library(mfsvg_thorvg STATIC
	${TVG}/src/common/tvgColor.cpp
	${TVG}/src/common/tvgCompressor.cpp
	${TVG}/src/common/tvgMath.cpp
	${TVG}/src/common/tvgStr.cpp
	${TVG}/src/loaders/svg/tvgSvgCssStyle.cpp
	${TVG}/src/loaders/svg/tvgSvgLoader.cpp
	${TVG}/src/loaders/svg/tvgSvgPath.cpp
	${TVG}/src/loaders/svg/tvgSvgSceneBuilder.cpp
	${TVG}/src/loaders/svg/tvgSvgUtil.cpp
	${TVG}/src/loaders/svg/tvgXmlParser.cpp
	${TVG}/src/loaders/raw/tvgRawLoader.cpp
	${TVG}/src/renderer/tvgAccessor.cpp
	${TVG}/src/renderer/tvgCanvas.cpp
	${TVG}/src/renderer/tvgFill.cpp
	${TVG}/src/renderer/tvgInitializer.cpp
	${TVG}/src/renderer/tvgLoader.cpp
	${TVG}/src/renderer/tvgPaint.cpp
	${TVG}/src/renderer/tvgPicture.cpp
	${TVG}/src/renderer/tvgRender.cpp
	${TVG}/src/renderer/tvgScene.cpp
	${TVG}/src/renderer/tvgShape.cpp
	${TVG}/src/renderer/tvgTaskScheduler.cpp
	${TVG}/src/renderer/tvgText.cpp
	${TVG}/src/renderer/sw_engine/tvgSwFill.cpp
	${TVG}/src/renderer/sw_engine/tvgSwImage.cpp
	${TVG}/src/renderer/sw_engine/tvgSwMath.cpp
	${TVG}/src/renderer/sw_engine/tvgSwMemPool.cpp
	${TVG}/src/renderer/sw_engine/tvgSwPostEffect.cpp
	${TVG}/src/renderer/sw_engine/tvgSwRaster.cpp
	${TVG}/src/renderer/sw_engine/tvgSwRenderer.cpp
	${TVG}/src/renderer/sw_engine/tvgSwRle.cpp
	${TVG}/src/renderer/sw_engine/tvgSwShape.cpp
	${TVG}/src/renderer/sw_engine/tvgSwStroke.cpp
)
target_include_directories(mfsvg_thorvg PUBLIC "${TVG}/inc"
	PRIVATE "${TVG}/src/common" "${TVG}/src/loaders/svg" "${TVG}/src/loaders/raw" "${TVG}/src/renderer" "${TVG}/src/renderer/sw_engine")
target_compile_definitions(mfsvg_thorvg PUBLIC TVG_STATIC PRIVATE THORVG_FILE_IO_SUPPORT)
if(WIN32)
	# tvgCommon.h includes <windows.h>, whose min/max macros break std::min/std::max and tvgMath.h's min()/max()
	# under MSVC. Godot defines NOMINMAX for its whole Windows build; do the same for ThorVG.
	target_compile_definitions(mfsvg_thorvg PRIVATE NOMINMAX)
endif()
target_compile_features(mfsvg_thorvg PRIVATE cxx_std_17)
find_package(Threads REQUIRED)
target_link_libraries(mfsvg_thorvg PUBLIC Threads::Threads)
if(MSVC)
	target_compile_options(mfsvg_thorvg PRIVATE /w)
else()
	target_compile_options(mfsvg_thorvg PRIVATE -w)
endif()

add_library(mfsvg SHARED include/mfsvg.h shim/mfsvg.cpp)
target_include_directories(mfsvg PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include")
target_compile_definitions(mfsvg PRIVATE MFSVG_BUILDING)
target_compile_features(mfsvg PRIVATE cxx_std_17)
target_link_libraries(mfsvg PRIVATE mfsvg_thorvg)
set_target_properties(mfsvg PROPERTIES OUTPUT_NAME mfsvg CXX_EXTENSIONS OFF INSTALL_NAME_DIR "@rpath")
if(MSVC)
	target_compile_options(mfsvg PRIVATE /W4 /permissive- $<$<BOOL:${MF_NATIVES_WERROR}>:/WX>)
else()
	target_compile_options(mfsvg PRIVATE -Wall -Wextra -Wpedantic -Wshadow -Wconversion -Wsign-conversion
		$<$<BOOL:${MF_NATIVES_WERROR}>:-Werror>)
endif()
if(APPLE)
	target_link_options(mfsvg PRIVATE "LINKER:-exported_symbols_list,${CMAKE_CURRENT_SOURCE_DIR}/shim/exports.macos.txt")
elseif(UNIX)
	target_link_options(mfsvg PRIVATE "LINKER:--version-script=${CMAKE_CURRENT_SOURCE_DIR}/shim/exports.linux.map"
		"LINKER:--exclude-libs,ALL" -static-libstdc++ -static-libgcc)
endif()

if(BUILD_TESTING)
	add_executable(mfsvg_smoke tests/smoke.c)
	target_link_libraries(mfsvg_smoke PRIVATE mfsvg)
	add_test(NAME mfsvg_smoke COMMAND mfsvg_smoke)
endif()
