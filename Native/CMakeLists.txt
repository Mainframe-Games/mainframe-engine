# Mainframe Engine native libraries (built per RID by .github/workflows/natives.yml).
#
#   cmake -S Native -B Native/build -G Ninja -DCMAKE_BUILD_TYPE=Release
#   cmake --build Native/build
#   ctest --test-dir Native/build --output-on-failure
#
# macOS universal: add -DCMAKE_OSX_ARCHITECTURES="arm64;x86_64" -DCMAKE_OSX_DEPLOYMENT_TARGET=11.0
# See docs/design/natives.md.
cmake_minimum_required(VERSION 3.25)

project(MainframeNatives LANGUAGES C CXX)

option(MF_NATIVES_ENET "Build the ENet native library (enet)" ON)
option(MF_NATIVES_RMLUI "Build the RmlUi C ABI shim (mfrmlui)" ON)
option(MF_NATIVES_SVG "Build the SVG rasteriser (mfsvg, ThorVG)" ON)
option(MF_NATIVES_PLUGINHOST "Build the music editor's helper executable (mfplughost: Vorbis encoding, later VST3/MIDI)" ON)

include(CTest)

if(MF_NATIVES_ENET)
	add_subdirectory(ENet)
endif()
if(MF_NATIVES_RMLUI)
	add_subdirectory(RmlUi)
endif()
if(MF_NATIVES_SVG)
	add_subdirectory(Svg)
endif()
if(MF_NATIVES_PLUGINHOST)
	add_subdirectory(PluginHost)
endif()
