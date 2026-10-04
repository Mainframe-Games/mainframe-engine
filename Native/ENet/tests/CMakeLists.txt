# ENet native tests (CTest).
#
#   enet_exports — loads the built library at run time (dlopen / LoadLibrary) and resolves every entry point
#                  ENet-CSharp 2.4.8 P/Invokes (../pinvoke-exports.txt). Guards against a missing export.
#   enet_smoke   — links the library, checks the linked version is 2.4.8 and round-trips a reliable packet
#                  between a server and a client host over loopback.

file(STRINGS "${CMAKE_CURRENT_SOURCE_DIR}/../pinvoke-exports.txt" MF_ENET_PINVOKES REGEX "^enet_")
list(LENGTH MF_ENET_PINVOKES MF_ENET_PINVOKE_COUNT)
set(MF_ENET_PINVOKE_ARRAY "")
foreach(name IN LISTS MF_ENET_PINVOKES)
	string(APPEND MF_ENET_PINVOKE_ARRAY "\t\"${name}\",\n")
endforeach()
configure_file(pinvoke_exports.h.in "${CMAKE_CURRENT_BINARY_DIR}/pinvoke_exports.h" @ONLY)

if(MSVC)
	set(MF_ENET_TEST_WARNINGS /W4 /WX)
else()
	set(MF_ENET_TEST_WARNINGS -Wall -Wextra -Werror)
endif()

add_executable(enet_exports enet_exports.c)
set_target_properties(enet_exports PROPERTIES C_STANDARD 11 C_STANDARD_REQUIRED ON)
target_include_directories(enet_exports PRIVATE "${CMAKE_CURRENT_BINARY_DIR}")
target_compile_options(enet_exports PRIVATE ${MF_ENET_TEST_WARNINGS})
target_link_libraries(enet_exports PRIVATE ${CMAKE_DL_LIBS})
add_dependencies(enet_exports enet)
add_test(NAME enet_exports COMMAND enet_exports "$<TARGET_FILE:enet>")

add_executable(enet_smoke enet_smoke.c)
set_target_properties(enet_smoke PROPERTIES C_STANDARD 11 C_STANDARD_REQUIRED ON)
target_link_libraries(enet_smoke PRIVATE enet)
if(WIN32)
	target_compile_definitions(enet_smoke PRIVATE ENET_DLL)
	target_link_libraries(enet_smoke PRIVATE ws2_32 winmm)
endif()
# The upstream header is third-party; keep -Werror for our own test code only.
target_compile_options(enet_smoke PRIVATE ${MF_ENET_TEST_WARNINGS})
add_test(NAME enet_smoke COMMAND enet_smoke)
set_tests_properties(enet_smoke PROPERTIES TIMEOUT 30)

# Windows has no rpath: run tests from the directory that holds enet.dll.
if(WIN32)
	set_tests_properties(enet_exports enet_smoke PROPERTIES
		ENVIRONMENT_MODIFICATION "PATH=path_list_prepend:$<TARGET_FILE_DIR:enet>")
endif()
