# mfrmlui native tests (CTest).
#
#   mfrmlui_exports — dynamically loads the built library and resolves every function declared in mfrmlui.h
#                     (the list is generated from the header at configure time), guarding the export lists.
#   mfrmlui_smoke   — C11 client of the public header: no-op render interface with counters, in-memory file
#                     interface, data model (func / dynamic array-of-struct / event), document load + update + render,
#                     input routing, listeners, debugger, hot reload, and a leak check on shutdown.

# --- export list from the public header -----------------------------------------------------------------------------
file(READ "${CMAKE_CURRENT_SOURCE_DIR}/../include/mfrmlui.h" MF_RMLUI_HEADER)
string(REGEX MATCHALL "MFRMLUI_CALL[ \t\r\n]+mfrmlui_[a-z0-9_]+[ \t]*\\(" MF_RMLUI_DECLS "${MF_RMLUI_HEADER}")
set(MF_RMLUI_EXPORT_ARRAY "")
set(MF_RMLUI_EXPORT_COUNT 0)
foreach(decl IN LISTS MF_RMLUI_DECLS)
	string(REGEX REPLACE ".*(mfrmlui_[a-z0-9_]+).*" "\\1" name "${decl}")
	string(APPEND MF_RMLUI_EXPORT_ARRAY "\t\"${name}\",\n")
	math(EXPR MF_RMLUI_EXPORT_COUNT "${MF_RMLUI_EXPORT_COUNT} + 1")
endforeach()
if(MF_RMLUI_EXPORT_COUNT LESS 50)
	message(FATAL_ERROR "Parsed only ${MF_RMLUI_EXPORT_COUNT} functions from mfrmlui.h; the export-list regex is out of date")
endif()
configure_file(mfrmlui_exports.h.in "${CMAKE_CURRENT_BINARY_DIR}/mfrmlui_exports.h" @ONLY)

if(MSVC)
	set(MF_RMLUI_TEST_WARNINGS /W4 /WX)
else()
	set(MF_RMLUI_TEST_WARNINGS -Wall -Wextra -Wpedantic -Werror)
endif()

add_executable(mfrmlui_exports mfrmlui_exports.c)
set_target_properties(mfrmlui_exports PROPERTIES C_STANDARD 11 C_STANDARD_REQUIRED ON)
target_include_directories(mfrmlui_exports PRIVATE "${CMAKE_CURRENT_BINARY_DIR}")
target_compile_options(mfrmlui_exports PRIVATE ${MF_RMLUI_TEST_WARNINGS})
target_link_libraries(mfrmlui_exports PRIVATE ${CMAKE_DL_LIBS})
add_dependencies(mfrmlui_exports mfrmlui)
add_test(NAME mfrmlui_exports COMMAND mfrmlui_exports "$<TARGET_FILE:mfrmlui>")

add_executable(mfrmlui_smoke mfrmlui_smoke.c)
set_target_properties(mfrmlui_smoke PROPERTIES C_STANDARD 11 C_STANDARD_REQUIRED ON C_EXTENSIONS OFF)
target_link_libraries(mfrmlui_smoke PRIVATE mfrmlui)
target_compile_options(mfrmlui_smoke PRIVATE ${MF_RMLUI_TEST_WARNINGS})
if(MSVC)
	target_compile_definitions(mfrmlui_smoke PRIVATE _CRT_SECURE_NO_WARNINGS)
endif()
target_compile_definitions(mfrmlui_smoke PRIVATE
	"MFRMLUI_TEST_FONT=\"${CMAKE_CURRENT_SOURCE_DIR}/../external/RmlUi/Samples/assets/LatoLatin-Regular.ttf\"")
add_test(NAME mfrmlui_smoke COMMAND mfrmlui_smoke)
set_tests_properties(mfrmlui_smoke PROPERTIES TIMEOUT 60)

if(WIN32)
	set_tests_properties(mfrmlui_exports mfrmlui_smoke PROPERTIES
		ENVIRONMENT_MODIFICATION "PATH=path_list_prepend:$<TARGET_FILE_DIR:mfrmlui>")
endif()
