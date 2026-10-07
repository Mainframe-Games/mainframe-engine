# mf_test_plugins.vst3: the helper's test fixtures (never shipped). One bundle, three classes written against the SDK:
#   MF Test Gain     effect, stereo gain (parameter "Gain", default 0.5)
#   MF Test Synth    instrument, one sine voice per held note
#   MF Test Crasher  effect, pass-through with 128 frames of latency; setState "CRASH" arms a crash in the next
#                    process call, "CRASHNOW" crashes inside setState (crash-recovery tests)
set(name mf_test_plugins)
set(sdk "${VST3SDK}/public.sdk/source")
set(sources
	mf_test_plugins.cpp
	${sdk}/main/pluginfactory.cpp
	${sdk}/vst/vstsinglecomponenteffect.cpp
	${sdk}/vst/vstcomponentbase.cpp
	${sdk}/vst/vstbus.cpp
	${sdk}/vst/vstparameters.cpp
	${sdk}/common/pluginview.cpp)
if(APPLE)
	list(APPEND sources ${sdk}/main/macmain.cpp)
elseif(WIN32)
	list(APPEND sources ${sdk}/main/dllmain.cpp)
else()
	list(APPEND sources ${sdk}/main/linuxmain.cpp)
endif()
add_library(${name} MODULE ${sources})
target_link_libraries(${name} PRIVATE mfph_vst3_base)
set_target_properties(${name} PROPERTIES CXX_VISIBILITY_PRESET hidden PREFIX "")
if(MSVC)
	target_compile_options(${name} PRIVATE /w /utf-8)
else()
	target_compile_options(${name} PRIVATE -w)
endif()

set(bundle "${CMAKE_CURRENT_BINARY_DIR}/${name}.vst3")
if(APPLE)
	set_target_properties(${name} PROPERTIES BUNDLE TRUE BUNDLE_EXTENSION vst3
		LIBRARY_OUTPUT_DIRECTORY "$<1:${CMAKE_CURRENT_BINARY_DIR}>"
		MACOSX_BUNDLE_GUI_IDENTIFIER com.mainframe.mftestplugins MACOSX_BUNDLE_BUNDLE_NAME ${name})
	target_link_options(${name} PRIVATE "-Wl,-exported_symbol,_GetPluginFactory" "-Wl,-exported_symbol,_bundleEntry"
		"-Wl,-exported_symbol,_bundleExit")
	target_link_libraries(${name} PRIVATE "-framework CoreFoundation")
elseif(WIN32)
	set_target_properties(${name} PROPERTIES SUFFIX ".vst3"
		LIBRARY_OUTPUT_DIRECTORY "$<1:${bundle}/Contents/x86_64-win>")
else()
	execute_process(COMMAND uname -m OUTPUT_VARIABLE arch OUTPUT_STRIP_TRAILING_WHITESPACE)
	set_target_properties(${name} PROPERTIES SUFFIX ".so"
		LIBRARY_OUTPUT_DIRECTORY "$<1:${bundle}/Contents/${arch}-linux>")
endif()
set(MF_TEST_PLUGINS_BUNDLE "${bundle}" PARENT_SCOPE)
