# ADR 0002 — RmlUi native shim: in-house flat C ABI (`mfrmlui`)

- **Date:** 2026-10-05
- **Status:** accepted
- **Milestone:** M8 native layer (W2 lane C)
- **Spec:** docs/design/future/game-ui.md

## Context

The game-UI design proposes forking the PourrezJ RmlUi.Net C shim (`RmlUiNative`), updating it to RmlUi 6.3 and
extending it with the full render interface. The plan's default is: fork after a licence check, or write it
in-house if the licence is incompatible.

## Licence finding

[PourrezJ/RmlUi.Net](https://github.com/PourrezJ/RmlUi.Net) is **MIT**.

- Checked at commit `dc27d65`, 2026-08-14.
- Copyright: chicken-with-lips (2022) and K. 'ashi/eden' J. (2024).
- The shim directory `RmlUi.Native/LICENSE` is the same MIT text.
- Forking would have been allowed.

## Decision

**Write the shim in-house** (design informed by RmlUi.Net, credited in `Native/RmlUi/shim/NOTICE.md`; no code copied).
The licence was fine, but the upstream ABI did not meet our bar:

- It passes C++ types across the boundary: `Rml::Vector2i&`, `Rml::Rectanglei` by value, `Rml::Event&`, `bool`.
- It has no public C header, no ABI versioning, no exception barrier and no null checks.
- It exports one function per C++ method.
- It targets RmlUi 6.2, and lacks the clip-mask, layer, filter and shader callbacks.

Fixing all of that would have been a rewrite anyway.

### Library

The library is **`mfrmlui`**, so `[LibraryImport("mfrmlui")]` resolves to:

- `mfrmlui.dll` (win-x64)
- `libmfrmlui.so` (linux-x64)
- `libmfrmlui.dylib` (universal, committed under both osx-arm64 and osx-x64)

It is a single shared library. RmlUi 6.3 (Core + Debugger) and FreeType 2.14.3 are linked in statically:

- FreeType uses the FTL option and has no optional system dependencies.
- RmlUi is pinned at tag `6.3` (`ba95ffe`) and FreeType at `VER-2-14-3` (`0a0221a`). Both are submodules under
  `Native/RmlUi/external/`.

Only `mfrmlui_*` is exported:

- clang/gcc: hidden visibility plus linker export lists.
- MSVC: `__declspec(dllexport)` on the API.
- Windows uses the static CRT; Linux links libstdc++ statically.

### ABI rules (`Native/RmlUi/include/mfrmlui.h`)

- **Versioning.** `mfrmlui_abi_version()` returns `(MAJOR << 16) | MINOR` and starts at 1.0.
  - The binding requires an equal MAJOR and a library MINOR at least as high as its own.
  - A MINOR bump may only append functions, or append fields to callback structs.
  - Every callback struct begins with `struct_size` and `user_data`. The library zero-fills fields beyond a smaller
    `struct_size`, so an older caller's struct reads as having the newer optional callbacks set to NULL.
- **Types.** cdecl (`MFRMLUI_CALL`), fixed-width integers, `mfrmlui_bool` as `int32_t`, blittable structs, 64-bit only.
  - The vertex layout is asserted equal to `Rml::Vertex`: 20 bytes, with position at offset 0, the premultiplied RGBA8
    colour at offset 8 and the texture coordinate at offset 12.
  - All enum constants and key identifiers are `static_assert`ed against RmlUi 6.3.
- **Handles.**
  - Owned: context, render interface, data model and event listener. These are validated against registries, so a
    NULL or destroyed handle returns `MFRMLUI_ERROR_INVALID_ARGUMENT`.
  - Borrowed: element, document, event, variant, dictionary and string-sink. These are reinterpret_casts of RmlUi
    objects and are valid until RmlUi destroys them, or for the duration of the callback.
- **Errors.** Status is `int32_t`: `0` means OK, negative means `MFRMLUI_ERROR_*`. Failing handle-returning functions
  return NULL. There is a thread-local `mfrmlui_get_last_error`. Every entry point catches all C++ exceptions.
- **Strings.** UTF-8. Inputs are NUL-terminated. Outputs use the caller-buffer pattern, which returns the full length.
  Callbacks return strings through `mfrmlui_string_set`.
- **Input.** `Process*` results are returned as `MFRMLUI_INPUT_CONSUMED` (1) or `MFRMLUI_INPUT_PROPAGATE` (0).
  RmlUi's raw bool means the opposite (true = *not* consumed), so the inversion is resolved once, in the shim.
- **Interfaces as callbacks.**
  - System: time, log, `translate_string` (the M9 hook), cursor, clipboard, IME activate/deactivate, `join_path`.
  - File: open/close/read/seek/tell, plus optional length and `load_file`.
  - Render: the basic set (compile/render/release geometry, load/generate/release texture, enable/set scissor) plus
    optional `set_transform`, clip mask, layers, composite, filters and shaders, for v2 on the engine's Vulkan device.
  - Render interfaces are separate handles, so several contexts can share one and with it the font atlases.
- **Data binding.**
  - `bind_func`: getter and setter on a variant.
  - `bind_event_callback`.
  - `bind_variable`: a dynamic graph. Scalar, array and struct nodes are identified by caller-chosen `uint64_t` tokens
    with get/set/size/child callbacks. This replaces `Struct<T>()`/`Array<T>()` without registering C++ types.
  - Dirtying: `dirty_variable`, `dirty_all_variables`, `is_variable_dirty`.
  - Each binding's release callback runs exactly once, when the model is removed, the context destroyed or the
    library shut down. This is where the binding frees GCHandles.
- **Listeners.** `mfrmlui_element_add_event_listener` returns a handle. Its `on_detach` runs exactly once, on removal
  or when the element is destroyed.

### Exported functions (121)

- **Library:**
  - `abi_version`, `get_rmlui_version`, `get_last_error`
  - `initialise`, `shutdown`, `is_initialised`
  - `load_font_face`, `load_font_face_from_memory`
  - `clear_style_sheet_cache`, `clear_template_cache`
  - `release_textures`, `release_compiled_geometry`
  - `string_set`
- **Render interface:** `render_interface_create`, `render_interface_destroy`
- **Context:**
  - `create`, `destroy`, `set_dimensions`, `set_density_independent_pixel_ratio`
  - `update`, `render`, `get_next_update_delay`
  - `enable_mouse_cursor`, `activate_theme`
  - `get_root_element`, `get_hover_element`, `get_focus_element`, `get_element_at_point`
  - `get_num_documents`, `get_document`, `is_mouse_interacting`
  - `process_mouse_move`, `process_mouse_button_down`, `process_mouse_button_up`, `process_mouse_wheel`,
    `process_mouse_leave`
  - `process_key_down`, `process_key_up`, `process_text_input`
  - `load_document`, `load_document_from_memory`
- **Document:**
  - `show`, `hide`, `close`, `reload`, `reload_style_sheet`
  - `is_visible`, `is_modal`, `pull_to_front`, `push_to_back`
  - `get_title`, `get_source_url`, `as_element`
- **Element:**
  - Tree: `get_owner_document`, `get_parent`, `get_num_children`, `get_child`, `get_element_by_id`,
    `query_selector`, `query_selector_all`
  - Identity and attributes: `get_tag_name`, `get_id`, `get/set/remove/has_attribute`
  - Classes: `set_class`, `is_class_set`, `set_class_names`, `set_pseudo_class`, `is_pseudo_class_set`
  - Style and content: `set/remove_property`, `get/set_inner_rml`, `get/set_value`
  - Interaction: `focus`, `blur`, `click`, `scroll_into_view`, `get_bounds`, `add_event_listener`
- **Events:**
  - `event_listener_remove`
  - `get_type`, `get_phase`, `get_target_element`, `get_current_element`
  - `stop_propagation`, `stop_immediate_propagation`, `get_parameters`
- **Variant:**
  - Getters: `get_type`, `get_bool`, `get_int64`, `get_double`, `get_string`, `get_float4`, `get_colourb`,
    `get_color_stops`
  - Setters: `set_none`, `set_bool`, `set_int64`, `set_double`, `set_string`
- **Dictionary:** `get_count`, `get_key`, `get_value`, `find`
- **Data model:**
  - `create`, `remove`
  - `bind_func`, `bind_event_callback`, `bind_variable`
  - `dirty_variable`, `dirty_all_variables`, `is_variable_dirty`
- **Debugger:** `initialise`, `set_context`, `set_visible`, `is_visible`, `shutdown`

All names carry the `mfrmlui_` prefix (and `mfrmlui_context_`, `mfrmlui_document_`, … per group).

## Consequences

- The M8 managed binding wraps this header:
  - `LibraryImport` and SafeHandles over the owned handles.
  - `[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]` callbacks.
  - A check of `mfrmlui_abi_version()` at load.
- `VulkanUiRenderer` implements `mfrmlui_render_callbacks`.
- Tests:
  - `mfrmlui_exports` resolves every declared function.
  - `mfrmlui_smoke`, written in C, exercises the whole surface with leak checks.
  - Both pass on macOS universal. Windows and Linux run in `natives.yml`.
- A change to the RmlUi submodule that breaks a layout or enum assumption fails the `static_assert`s in
  `shim/abi_checks.cpp`. When that happens, bump MAJOR.
- Binary size is about 5 MB for the universal dylib (two slices).
