# The VST 3 SDK's hosting subset, built with our own source lists (the SDK's CMake would pull in VSTGUI, the
# validator and its global settings). Two static libraries:
#   mfph_vst3_base     base + pluginterfaces (position independent: the test plugin bundle links it too)
#   mfph_vst3_hosting  public.sdk hosting: module loading, PlugProvider, HostApplication, EventList, ParameterChanges
set(VST3SDK "${CMAKE_CURRENT_SOURCE_DIR}/external/vst3sdk")
if(NOT EXISTS "${VST3SDK}/pluginterfaces/base/funknown.h")
	message(FATAL_ERROR "MF_PLUGINHOST_VST3: run `git submodule update --init --recursive Native/PluginHost/external/vst3sdk` "
		"(base, pluginterfaces, public.sdk, cmake are needed; vstgui4/doc/tutorials are not).")
endif()

add_library(mfph_vst3_base STATIC
	${VST3SDK}/base/source/baseiids.cpp
	${VST3SDK}/base/source/fbuffer.cpp
	${VST3SDK}/base/source/fdebug.cpp
	${VST3SDK}/base/source/fdynlib.cpp
	${VST3SDK}/base/source/fobject.cpp
	${VST3SDK}/base/source/fstreamer.cpp
	${VST3SDK}/base/source/fstring.cpp
	${VST3SDK}/base/source/timer.cpp
	${VST3SDK}/base/source/updatehandler.cpp
	${VST3SDK}/base/thread/source/fcondition.cpp
	${VST3SDK}/base/thread/source/flock.cpp
	${VST3SDK}/pluginterfaces/base/conststringtable.cpp
	${VST3SDK}/pluginterfaces/base/coreiids.cpp
	${VST3SDK}/pluginterfaces/base/funknown.cpp
	${VST3SDK}/pluginterfaces/base/ustring.cpp
	${VST3SDK}/public.sdk/source/common/memorystream.cpp
	${VST3SDK}/public.sdk/source/common/commoniids.cpp
	${VST3SDK}/public.sdk/source/common/commonstringconvert.cpp
	${VST3SDK}/public.sdk/source/vst/vstinitiids.cpp
	${VST3SDK}/public.sdk/source/vst/utility/stringconvert.cpp)
target_include_directories(mfph_vst3_base SYSTEM PUBLIC "${VST3SDK}")
target_compile_definitions(mfph_vst3_base PUBLIC $<IF:$<CONFIG:Debug>,DEVELOPMENT=1,RELEASE=1>)
target_compile_features(mfph_vst3_base PUBLIC cxx_std_20)
set_target_properties(mfph_vst3_base PROPERTIES POSITION_INDEPENDENT_CODE ON)
if(APPLE)
	target_link_libraries(mfph_vst3_base PUBLIC "-framework CoreFoundation" "-framework Foundation")
elseif(UNIX)
	target_link_libraries(mfph_vst3_base PUBLIC pthread dl)
endif()

set(mfph_vst3_hosting_sources
	${VST3SDK}/public.sdk/source/vst/hosting/connectionproxy.cpp
	${VST3SDK}/public.sdk/source/vst/hosting/eventlist.cpp
	${VST3SDK}/public.sdk/source/vst/hosting/hostclasses.cpp
	${VST3SDK}/public.sdk/source/vst/hosting/module.cpp
	${VST3SDK}/public.sdk/source/vst/hosting/parameterchanges.cpp
	${VST3SDK}/public.sdk/source/vst/hosting/pluginterfacesupport.cpp
	${VST3SDK}/public.sdk/source/vst/hosting/plugprovider.cpp
	${VST3SDK}/public.sdk/source/vst/hosting/processdata.cpp)
if(APPLE)
	list(APPEND mfph_vst3_hosting_sources ${VST3SDK}/public.sdk/source/vst/hosting/module_mac.mm
		${VST3SDK}/public.sdk/source/common/threadchecker_mac.mm)
	set_source_files_properties(${VST3SDK}/public.sdk/source/vst/hosting/module_mac.mm
		${VST3SDK}/public.sdk/source/common/threadchecker_mac.mm PROPERTIES COMPILE_FLAGS "-fobjc-arc")
elseif(WIN32)
	list(APPEND mfph_vst3_hosting_sources ${VST3SDK}/public.sdk/source/vst/hosting/module_win32.cpp
		${VST3SDK}/public.sdk/source/common/threadchecker_win32.cpp)
else()
	list(APPEND mfph_vst3_hosting_sources ${VST3SDK}/public.sdk/source/vst/hosting/module_linux.cpp
		${VST3SDK}/public.sdk/source/common/threadchecker_linux.cpp)
endif()
add_library(mfph_vst3_hosting STATIC ${mfph_vst3_hosting_sources})
target_link_libraries(mfph_vst3_hosting PUBLIC mfph_vst3_base)
if(WIN32)
	target_link_libraries(mfph_vst3_hosting PUBLIC ole32 shell32)
endif()

foreach(lib mfph_vst3_base mfph_vst3_hosting)
	if(MSVC)
		# The SDK's own toolset flags: /Zc:__cplusplus so SMTG_CPP20 sees C++20 (MSVC reports 199711 without it; the
		# hosting code then takes its pre-C++20 path and misuses std::u8string), and _UNICODE.
		target_compile_options(${lib} PRIVATE /w /utf-8 PUBLIC /Zc:__cplusplus)
		target_compile_definitions(${lib} PRIVATE _CRT_SECURE_NO_WARNINGS NOMINMAX _UNICODE)
	else()
		target_compile_options(${lib} PRIVATE -w)
	endif()
endforeach()
