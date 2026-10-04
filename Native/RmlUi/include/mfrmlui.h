/*
 * mfrmlui.h — flat C ABI over RmlUi 6.3 for the Mainframe Engine (library: mfrmlui).
 *
 *   Windows: mfrmlui.dll   Linux: libmfrmlui.so   macOS: libmfrmlui.dylib   (.NET: [LibraryImport("mfrmlui")])
 *
 * Copyright (c) 2024-2026 Mainframe Games. MIT License: see LICENSE and THIRD_PARTY_NOTICES.md at the repo root.
 * The C ABI design was informed by the MIT-licensed RmlUi.Net native shim (chicken-with-lips, K. 'ashi/eden' J.,
 * PourrezJ fork); see Native/RmlUi/shim/NOTICE.md.
 *
 * ABI RULES (apply to every declaration in this file)
 * ----------------------------------------------------
 *  - Versioning: mfrmlui_abi_version() returns (MAJOR << 16) | MINOR. A binding built against MAJOR.MINOR must
 *    refuse to run unless the library's MAJOR is equal and its MINOR is >= the binding's MINOR. MINOR bumps only
 *    append functions or append fields to the end of callback structs; anything else bumps MAJOR.
 *  - Calling convention: every function and callback uses MFRMLUI_CALL (cdecl on Windows, the platform C
 *    convention elsewhere). In .NET use CallConvCdecl / [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])].
 *  - Types: fixed-width integers, float/double, pointers and the structs below only. All structs are blittable,
 *    with natural alignment and no padding surprises (sizes are asserted in the shim). No C++ types cross the ABI.
 *    64-bit targets only.
 *  - Booleans are mfrmlui_bool (int32_t): 0 = false, 1 = true. Inputs treat any non-zero value as true.
 *  - Strings: `const char*` inputs are NUL-terminated UTF-8 and only need to live for the duration of the call.
 *    String outputs use the "caller buffer" pattern: (char* buffer, int32_t capacity) -> returns the full length in
 *    bytes excluding the NUL (or a negative MFRMLUI_ERROR_*). At most capacity-1 bytes plus a NUL are written, so a
 *    return value >= capacity means "truncated: call again with return+1 bytes". buffer may be NULL with capacity 0
 *    to query the length. Strings handed to callbacks are valid only during the callback.
 *    Callbacks that must return a string write it through mfrmlui_string_set(mfrmlui_string*, ...).
 *  - Status codes: functions returning int32_t status return MFRMLUI_OK (0) or a negative MFRMLUI_ERROR_*.
 *    Functions returning a handle return NULL on failure. On failure a message is available from
 *    mfrmlui_get_last_error() (thread-local; not cleared on success).
 *  - Handles: NULL handles are rejected with MFRMLUI_ERROR_INVALID_ARGUMENT (never a crash). Context, render
 *    interface and data model handles are owned and validated by the library (a destroyed handle is rejected).
 *    Element / document / event / variant / dictionary handles are borrowed RmlUi objects: element and document
 *    handles stay valid until the element is destroyed (document closed and the context updated, or the context
 *    destroyed); event, variant and dictionary handles are valid only inside the callback that received them.
 *  - Exceptions never cross the ABI: every entry point catches all C++ exceptions (MFRMLUI_ERROR_EXCEPTION).
 *    Callbacks implemented by the caller must not throw or unwind (C# [UnmanagedCallersOnly] cannot).
 *  - Threading: RmlUi is single-threaded. Call everything (and expect every callback) on one thread.
 *  - Engine handles (geometry, texture, layer, filter, shader, file) are uint64_t chosen by the caller; 0 means
 *    "none / failure" exactly as in RmlUi.
 */
#ifndef MFRMLUI_H
#define MFRMLUI_H

#include <stdint.h>

#define MFRMLUI_ABI_VERSION_MAJOR 1
#define MFRMLUI_ABI_VERSION_MINOR 0
#define MFRMLUI_ABI_VERSION ((uint32_t)((MFRMLUI_ABI_VERSION_MAJOR << 16) | MFRMLUI_ABI_VERSION_MINOR))

#if defined(_WIN32)
#	define MFRMLUI_CALL __cdecl
#	if defined(MFRMLUI_BUILDING)
#		define MFRMLUI_API __declspec(dllexport)
#	else
#		define MFRMLUI_API __declspec(dllimport)
#	endif
#else
#	define MFRMLUI_CALL
#	if defined(MFRMLUI_BUILDING)
#		define MFRMLUI_API __attribute__((visibility("default")))
#	else
#		define MFRMLUI_API
#	endif
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* ---------------------------------------------------------------------------------------------------------------
 * Basic types and constants
 * ------------------------------------------------------------------------------------------------------------- */

typedef int32_t mfrmlui_bool;

/* Opaque handles. */
typedef struct mfrmlui_context mfrmlui_context;                   /* owned: mfrmlui_context_create/destroy */
typedef struct mfrmlui_render_interface mfrmlui_render_interface; /* owned: mfrmlui_render_interface_create/destroy */
typedef struct mfrmlui_data_model mfrmlui_data_model;             /* owned: mfrmlui_data_model_create/remove */
typedef struct mfrmlui_event_listener mfrmlui_event_listener;     /* owned by the element it is attached to */
typedef struct mfrmlui_element mfrmlui_element;                   /* borrowed Rml::Element */
typedef struct mfrmlui_document mfrmlui_document;                 /* borrowed Rml::ElementDocument */
typedef struct mfrmlui_event mfrmlui_event;                       /* borrowed Rml::Event (callback scope) */
typedef struct mfrmlui_variant mfrmlui_variant;                   /* borrowed Rml::Variant (callback scope) */
typedef struct mfrmlui_dictionary mfrmlui_dictionary;             /* borrowed Rml::Dictionary (callback scope) */
typedef struct mfrmlui_string mfrmlui_string;                     /* string sink for callback outputs */

/* Status codes. */
#define MFRMLUI_OK                          0
#define MFRMLUI_ERROR_INVALID_ARGUMENT      (-1) /* NULL/unknown handle, NULL required pointer, bad value */
#define MFRMLUI_ERROR_NOT_INITIALISED       (-2) /* mfrmlui_initialise has not been called */
#define MFRMLUI_ERROR_ALREADY_INITIALISED   (-3)
#define MFRMLUI_ERROR_FAILED                (-4) /* RmlUi reported failure (details usually logged via log_message) */
#define MFRMLUI_ERROR_EXCEPTION             (-5) /* a C++ exception was caught at the ABI boundary */
#define MFRMLUI_ERROR_IN_USE                (-6) /* e.g. destroying a render interface still used by a context */
#define MFRMLUI_ERROR_NOT_FOUND             (-7) /* e.g. attribute or parameter does not exist */
#define MFRMLUI_ERROR_TYPE_MISMATCH         (-8) /* e.g. variant cannot be converted to the requested type */

/*
 * Input results. NOTE: RmlUi's raw Context::Process*() functions return true when the event was NOT consumed.
 * This ABI does not pass that bool through; it returns one of these named values instead, so the inversion is
 * handled exactly once, here. Negative values are MFRMLUI_ERROR_* codes.
 */
#define MFRMLUI_INPUT_PROPAGATE 0 /* RmlUi did not consume the event: pass it to the next layer / the game */
#define MFRMLUI_INPUT_CONSUMED  1 /* RmlUi consumed the event: stop routing it */

/* Key modifier bit flags (Rml::Input::KeyModifier). */
#define MFRMLUI_KM_CTRL       (1 << 0)
#define MFRMLUI_KM_SHIFT      (1 << 1)
#define MFRMLUI_KM_ALT        (1 << 2)
#define MFRMLUI_KM_META       (1 << 3)
#define MFRMLUI_KM_CAPSLOCK   (1 << 4)
#define MFRMLUI_KM_NUMLOCK    (1 << 5)
#define MFRMLUI_KM_SCROLLLOCK (1 << 6)

/* Log message types (Rml::Log::Type). */
#define MFRMLUI_LOG_ALWAYS  0
#define MFRMLUI_LOG_ERROR   1
#define MFRMLUI_LOG_ASSERT  2
#define MFRMLUI_LOG_WARNING 3
#define MFRMLUI_LOG_INFO    4
#define MFRMLUI_LOG_DEBUG   5

/* Document show flags (Rml::ModalFlag, Rml::FocusFlag, Rml::ScrollFlag). */
#define MFRMLUI_MODAL_NONE     0
#define MFRMLUI_MODAL_MODAL    1
#define MFRMLUI_MODAL_KEEP     2
#define MFRMLUI_FOCUS_NONE     0
#define MFRMLUI_FOCUS_DOCUMENT 1
#define MFRMLUI_FOCUS_KEEP     2
#define MFRMLUI_FOCUS_AUTO     3
#define MFRMLUI_SCROLL_NONE    0
#define MFRMLUI_SCROLL_AUTO    1

/* Event phases (Rml::EventPhase). */
#define MFRMLUI_EVENT_PHASE_NONE    0
#define MFRMLUI_EVENT_PHASE_CAPTURE 1
#define MFRMLUI_EVENT_PHASE_TARGET  2
#define MFRMLUI_EVENT_PHASE_BUBBLE  4

/* Render interface enums (Rml::ClipMaskOperation, Rml::BlendMode). */
#define MFRMLUI_CLIP_MASK_SET         0
#define MFRMLUI_CLIP_MASK_SET_INVERSE 1
#define MFRMLUI_CLIP_MASK_INTERSECT   2
#define MFRMLUI_BLEND_MODE_BLEND   0
#define MFRMLUI_BLEND_MODE_REPLACE 1

/* Font face style / weight (Rml::Style::FontStyle, Rml::Style::FontWeight; weight is 0 = auto or 1..1000). */
#define MFRMLUI_FONT_STYLE_NORMAL 0
#define MFRMLUI_FONT_STYLE_ITALIC 1
#define MFRMLUI_FONT_WEIGHT_AUTO   0
#define MFRMLUI_FONT_WEIGHT_NORMAL 400
#define MFRMLUI_FONT_WEIGHT_BOLD   700

/* Variant value categories reported by mfrmlui_variant_get_type. */
#define MFRMLUI_VARIANT_NONE            0
#define MFRMLUI_VARIANT_BOOL            1
#define MFRMLUI_VARIANT_INT             2  /* any integer type (byte, int, uint, int64, uint64) */
#define MFRMLUI_VARIANT_FLOAT           3  /* float or double */
#define MFRMLUI_VARIANT_STRING          4  /* also char */
#define MFRMLUI_VARIANT_VECTOR2         5
#define MFRMLUI_VARIANT_VECTOR3         6
#define MFRMLUI_VARIANT_VECTOR4         7
#define MFRMLUI_VARIANT_COLOURF         8
#define MFRMLUI_VARIANT_COLOURB         9
#define MFRMLUI_VARIANT_COLOR_STOP_LIST 10 /* gradient shader parameter "color_stop_list" */
#define MFRMLUI_VARIANT_OTHER           255

/* Data variable kinds for mfrmlui_data_model_bind_variable. */
#define MFRMLUI_VARIABLE_SCALAR 0
#define MFRMLUI_VARIABLE_ARRAY  1
#define MFRMLUI_VARIABLE_STRUCT 2

/* RmlUi vertex, identical in layout to Rml::Vertex (20 bytes). colour is premultiplied RGBA8. */
typedef struct mfrmlui_vertex {
	float position[2];  /* offset 0 */
	uint8_t colour[4];  /* offset 8 */
	float tex_coord[2]; /* offset 12 */
} mfrmlui_vertex;

/* Rectangle in float pixels. */
typedef struct mfrmlui_rectf {
	float x, y, width, height;
} mfrmlui_rectf;

/* One stop of a gradient colour stop list. colour is premultiplied RGBA8; position is already resolved. */
typedef struct mfrmlui_color_stop {
	uint8_t colour[4];
	float position;
} mfrmlui_color_stop;

/* ---------------------------------------------------------------------------------------------------------------
 * Callback interfaces. Each struct starts with struct_size (set it to sizeof(the struct)) and user_data (passed
 * back as the first argument of every callback). Optional callbacks may be NULL: RmlUi's default behaviour applies.
 * The struct is copied; it does not need to outlive the call that receives it.
 * ------------------------------------------------------------------------------------------------------------- */

/* System interface (all optional). */
typedef struct mfrmlui_system_callbacks {
	uint32_t struct_size;
	void* user_data;
	/* Seconds since an arbitrary epoch (engine clock). Default: RmlUi's own clock. */
	double(MFRMLUI_CALL* get_elapsed_time)(void* user_data);
	/* Localization hook. `out` is pre-set to `input`; write a translation with mfrmlui_string_set and return the
	 * number of translations performed (0 = unchanged). */
	int32_t(MFRMLUI_CALL* translate_string)(void* user_data, const char* input, mfrmlui_string* out);
	/* Return 1 to continue, 0 to break into the debugger (asserts only). Default: print to stdout/stderr. */
	mfrmlui_bool(MFRMLUI_CALL* log_message)(void* user_data, int32_t type, const char* message);
	void(MFRMLUI_CALL* set_mouse_cursor)(void* user_data, const char* cursor_name);
	void(MFRMLUI_CALL* set_clipboard_text)(void* user_data, const char* text);
	void(MFRMLUI_CALL* get_clipboard_text)(void* user_data, mfrmlui_string* out);
	/* Text input started on an element: caret position (context pixels) and line height, for IME placement. */
	void(MFRMLUI_CALL* activate_keyboard)(void* user_data, float caret_x, float caret_y, float line_height);
	void(MFRMLUI_CALL* deactivate_keyboard)(void* user_data);
	/* Resolve `path` relative to `document_path` into `out`. Default: RmlUi's path joining. */
	void(MFRMLUI_CALL* join_path)(void* user_data, const char* document_path, const char* path, mfrmlui_string* out);
} mfrmlui_system_callbacks;

/* File interface. open/close/read/seek/tell are required when the struct is supplied; length and load_file are
 * optional fast paths. Passing no struct at all to mfrmlui_initialise uses RmlUi's stdio file interface. */
typedef struct mfrmlui_file_callbacks {
	uint32_t struct_size;
	void* user_data;
	/* Returns a non-zero file handle, or 0 if the file cannot be opened. */
	uint64_t(MFRMLUI_CALL* open)(void* user_data, const char* path);
	void(MFRMLUI_CALL* close)(void* user_data, uint64_t file);
	/* Reads up to `size` bytes into `buffer`; returns the number of bytes read. */
	uint64_t(MFRMLUI_CALL* read)(void* user_data, uint64_t file, void* buffer, uint64_t size);
	/* origin: 0 = SEEK_SET, 1 = SEEK_CUR, 2 = SEEK_END. Returns 1 on success. */
	mfrmlui_bool(MFRMLUI_CALL* seek)(void* user_data, uint64_t file, int64_t offset, int32_t origin);
	uint64_t(MFRMLUI_CALL* tell)(void* user_data, uint64_t file);
	/* Optional: total length in bytes. */
	uint64_t(MFRMLUI_CALL* length)(void* user_data, uint64_t file);
	/* Optional: load a whole file into `out`; return 1 on success. */
	mfrmlui_bool(MFRMLUI_CALL* load_file)(void* user_data, const char* path, mfrmlui_string* out);
} mfrmlui_file_callbacks;

/*
 * Render interface. The first eight callbacks are required (RmlUi's "basic" set); the rest are optional (clip
 * masks, transforms, layers, filters and shaders — the "advanced" set used by M8 phase 2). With an optional
 * callback NULL, RmlUi's default applies (the feature is skipped).
 *
 * Coordinates are context pixels, origin top-left, y down. Colours and textures are premultiplied alpha.
 * Geometry/texture/layer/filter/shader handles are caller-chosen non-zero uint64_t values (0 = failure / none).
 */
typedef struct mfrmlui_render_callbacks {
	uint32_t struct_size;
	void* user_data;

	/* --- required --- */
	/* vertices/indices stay valid and immutable until release_geometry for the returned handle. */
	uint64_t(MFRMLUI_CALL* compile_geometry)(void* user_data, const mfrmlui_vertex* vertices, int32_t num_vertices,
		const int32_t* indices, int32_t num_indices);
	/* texture is 0 for untextured geometry. */
	void(MFRMLUI_CALL* render_geometry)(void* user_data, uint64_t geometry, float translation_x, float translation_y,
		uint64_t texture);
	void(MFRMLUI_CALL* release_geometry)(void* user_data, uint64_t geometry);
	/* Load an image from `source` (already joined with the document path); write its size. */
	uint64_t(MFRMLUI_CALL* load_texture)(void* user_data, const char* source, int32_t* out_width, int32_t* out_height);
	/* rgba: width*height premultiplied RGBA8 pixels (num_bytes = width*height*4), valid during the call. */
	uint64_t(MFRMLUI_CALL* generate_texture)(void* user_data, const uint8_t* rgba, int32_t num_bytes, int32_t width,
		int32_t height);
	void(MFRMLUI_CALL* release_texture)(void* user_data, uint64_t texture);
	void(MFRMLUI_CALL* enable_scissor_region)(void* user_data, mfrmlui_bool enable);
	/* Window (context) pixel coordinates, regardless of any active transform. */
	void(MFRMLUI_CALL* set_scissor_region)(void* user_data, int32_t x, int32_t y, int32_t width, int32_t height);

	/* --- optional --- */
	/* matrix: 16 floats, column-major; NULL means identity. Applies to all geometry rendering calls. */
	void(MFRMLUI_CALL* set_transform)(void* user_data, const float* matrix);
	void(MFRMLUI_CALL* enable_clip_mask)(void* user_data, mfrmlui_bool enable);
	/* operation: MFRMLUI_CLIP_MASK_*. */
	void(MFRMLUI_CALL* render_to_clip_mask)(void* user_data, int32_t operation, uint64_t geometry, float translation_x,
		float translation_y);
	/* Returns the new layer handle (0 is reserved for the base layer). */
	uint64_t(MFRMLUI_CALL* push_layer)(void* user_data);
	/* blend_mode: MFRMLUI_BLEND_MODE_*. filters: num_filters compiled filter handles, applied in order. */
	void(MFRMLUI_CALL* composite_layers)(void* user_data, uint64_t source, uint64_t destination, int32_t blend_mode,
		const uint64_t* filters, int32_t num_filters);
	void(MFRMLUI_CALL* pop_layer)(void* user_data);
	uint64_t(MFRMLUI_CALL* save_layer_as_texture)(void* user_data);
	uint64_t(MFRMLUI_CALL* save_layer_as_mask_image)(void* user_data);
	/* name e.g. "opacity", "blur", "drop-shadow"; read parameters with mfrmlui_dictionary_* (callback scope). */
	uint64_t(MFRMLUI_CALL* compile_filter)(void* user_data, const char* name, const mfrmlui_dictionary* parameters);
	void(MFRMLUI_CALL* release_filter)(void* user_data, uint64_t filter);
	/* name e.g. "linear-gradient", "radial-gradient", "conic-gradient", "shader". */
	uint64_t(MFRMLUI_CALL* compile_shader)(void* user_data, const char* name, const mfrmlui_dictionary* parameters);
	void(MFRMLUI_CALL* render_shader)(void* user_data, uint64_t shader, uint64_t geometry, float translation_x,
		float translation_y, uint64_t texture);
	void(MFRMLUI_CALL* release_shader)(void* user_data, uint64_t shader);
} mfrmlui_render_callbacks;

/* Element event listener. */
typedef void(MFRMLUI_CALL* mfrmlui_event_callback)(void* user_data, mfrmlui_event* event);
/* Called exactly once when a listener is detached (removed, or its element destroyed); release user_data here. */
typedef void(MFRMLUI_CALL* mfrmlui_release_callback)(void* user_data);

/* Data model callbacks. */
typedef void(MFRMLUI_CALL* mfrmlui_data_get_callback)(void* user_data, mfrmlui_variant* out_value);
typedef void(MFRMLUI_CALL* mfrmlui_data_set_callback)(void* user_data, const mfrmlui_variant* value);
/* `arguments` holds num_arguments variant handles (e.g. data-event-click="buy(item.id, 2)"). */
typedef void(MFRMLUI_CALL* mfrmlui_data_event_callback)(void* user_data, mfrmlui_data_model* model,
	mfrmlui_event* event, const mfrmlui_variant* const* arguments, int32_t num_arguments);

/*
 * Dynamic data variable: lets the caller expose an arbitrary object graph (scalars, arrays, structs) to a data
 * model without registering C++ types. Every value in the graph is identified by a caller-chosen uint64_t "node"
 * token (e.g. a GCHandle or an index); the root node is given to mfrmlui_data_model_bind_variable and children are
 * discovered through `child`.
 */
typedef struct mfrmlui_variable_callbacks {
	uint32_t struct_size;
	void* user_data;
	/* Scalars: read the node's value. Return 1 on success. Required. */
	mfrmlui_bool(MFRMLUI_CALL* get)(void* user_data, uint64_t node, mfrmlui_variant* out_value);
	/* Scalars: write the node's value (two-way bindings such as data-value). Return 1 on success. Optional. */
	mfrmlui_bool(MFRMLUI_CALL* set)(void* user_data, uint64_t node, const mfrmlui_variant* value);
	/* Arrays: number of elements. Required when any node is an array. ("size" is answered by the library.) */
	int32_t(MFRMLUI_CALL* size)(void* user_data, uint64_t node);
	/* Arrays: index >= 0 and name == NULL. Structs: index == -1 and name is the member name.
	 * Write the child's node token and return its kind (MFRMLUI_VARIABLE_*), or -1 if there is no such child. */
	int32_t(MFRMLUI_CALL* child)(void* user_data, uint64_t node, int32_t index, const char* name,
		uint64_t* out_child_node);
	/* Optional: called once when the binding is destroyed (data model removed / context destroyed / shutdown). */
	void(MFRMLUI_CALL* release)(void* user_data);
} mfrmlui_variable_callbacks;

/* ---------------------------------------------------------------------------------------------------------------
 * Library
 * ------------------------------------------------------------------------------------------------------------- */

/* (MFRMLUI_ABI_VERSION_MAJOR << 16) | MFRMLUI_ABI_VERSION_MINOR of the loaded library. Callable at any time. */
MFRMLUI_API uint32_t MFRMLUI_CALL mfrmlui_abi_version(void);
/* RmlUi version string (e.g. "6.3"). Caller-buffer pattern. */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_get_rmlui_version(char* buffer, int32_t capacity);
/* Message of the last failure on this thread. Caller-buffer pattern. */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_get_last_error(char* buffer, int32_t capacity);

/* Installs the interfaces and initialises RmlUi (+ FreeType font engine). Either pointer may be NULL (defaults). */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_initialise(const mfrmlui_system_callbacks* system,
	const mfrmlui_file_callbacks* file);
/* Destroys all contexts (and their data models), shuts RmlUi down and releases every engine resource through the
 * render interfaces, which must therefore still be alive. Render interfaces remain valid and must be destroyed by
 * the caller afterwards. */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_shutdown(void);
MFRMLUI_API mfrmlui_bool MFRMLUI_CALL mfrmlui_is_initialised(void);

/* Load a font face from a path (through the file interface). weight: MFRMLUI_FONT_WEIGHT_* or 1..1000. */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_load_font_face(const char* path, mfrmlui_bool fallback_face, int32_t weight);
/* Load a font face from memory. The bytes are copied and kept until shutdown. family may be NULL (use the font's). */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_load_font_face_from_memory(const uint8_t* data, int64_t size,
	const char* family, int32_t style, int32_t weight, mfrmlui_bool fallback_face);

/* Hot reload support: drop cached style sheets / templates so the next load re-reads them from disk. */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_clear_style_sheet_cache(void);
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_clear_template_cache(void);
/* Release textures / compiled geometry held by RmlUi (NULL = all render interfaces); they are re-created on demand. */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_release_textures(mfrmlui_render_interface* render_interface);
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_release_compiled_geometry(mfrmlui_render_interface* render_interface);

/* Write a string result from inside a callback. length < 0 means `utf8` is NUL-terminated. */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_string_set(mfrmlui_string* out, const char* utf8, int32_t length);

/* ---------------------------------------------------------------------------------------------------------------
 * Render interface
 * ------------------------------------------------------------------------------------------------------------- */

/* May be called before mfrmlui_initialise. One render interface can be shared by many contexts (shared textures). */
MFRMLUI_API mfrmlui_render_interface* MFRMLUI_CALL mfrmlui_render_interface_create(
	const mfrmlui_render_callbacks* callbacks);
/* Fails with MFRMLUI_ERROR_IN_USE while a context uses it. Releases its RmlUi resources (through its callbacks)
 * before returning when RmlUi is initialised. */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_render_interface_destroy(mfrmlui_render_interface* render_interface);

/* ---------------------------------------------------------------------------------------------------------------
 * Context
 * ------------------------------------------------------------------------------------------------------------- */

/* name must be unique among live contexts. Dimensions are framebuffer pixels. */
MFRMLUI_API mfrmlui_context* MFRMLUI_CALL mfrmlui_context_create(const char* name, int32_t width, int32_t height,
	mfrmlui_render_interface* render_interface);
/* Unloads all documents, removes the context's data models (their release callbacks run) and destroys it. */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_context_destroy(mfrmlui_context* context);
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_context_set_dimensions(mfrmlui_context* context, int32_t width, int32_t height);
/* HiDPI: framebuffer pixels per density-independent pixel (dp). */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_context_set_density_independent_pixel_ratio(mfrmlui_context* context,
	float ratio);
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_context_update(mfrmlui_context* context);
/* Issues render interface callbacks for the context's visible documents. */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_context_render(mfrmlui_context* context);
/* Seconds until the context needs another update (animations, transitions); +infinity when idle; < 0 on error. */
MFRMLUI_API double MFRMLUI_CALL mfrmlui_context_get_next_update_delay(mfrmlui_context* context);
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_context_enable_mouse_cursor(mfrmlui_context* context, mfrmlui_bool enable);
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_context_activate_theme(mfrmlui_context* context, const char* theme_name,
	mfrmlui_bool activate);
MFRMLUI_API mfrmlui_element* MFRMLUI_CALL mfrmlui_context_get_root_element(mfrmlui_context* context);
MFRMLUI_API mfrmlui_element* MFRMLUI_CALL mfrmlui_context_get_hover_element(mfrmlui_context* context);
MFRMLUI_API mfrmlui_element* MFRMLUI_CALL mfrmlui_context_get_focus_element(mfrmlui_context* context);
MFRMLUI_API mfrmlui_element* MFRMLUI_CALL mfrmlui_context_get_element_at_point(mfrmlui_context* context, float x,
	float y);
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_context_get_num_documents(mfrmlui_context* context);
MFRMLUI_API mfrmlui_document* MFRMLUI_CALL mfrmlui_context_get_document(mfrmlui_context* context, int32_t index);
MFRMLUI_API mfrmlui_bool MFRMLUI_CALL mfrmlui_context_is_mouse_interacting(mfrmlui_context* context);

/* ---------------------------------------------------------------------------------------------------------------
 * Input. All return MFRMLUI_INPUT_CONSUMED / MFRMLUI_INPUT_PROPAGATE (see above) or a negative error.
 * Coordinates are context (framebuffer) pixels; key_modifiers is a MFRMLUI_KM_* mask.
 * ------------------------------------------------------------------------------------------------------------- */

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_context_process_mouse_move(mfrmlui_context* context, int32_t x, int32_t y,
	int32_t key_modifiers);
/* button: 0 = left, 1 = right, 2 = middle, 3+ = extra. */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_context_process_mouse_button_down(mfrmlui_context* context, int32_t button,
	int32_t key_modifiers);
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_context_process_mouse_button_up(mfrmlui_context* context, int32_t button,
	int32_t key_modifiers);
/* RmlUi convention: positive delta_y scrolls DOWN (content moves up), positive delta_x scrolls right. SDL wheel y is
 * positive away from the user, so pass -sdl_y. Units are "lines" (multiplied by the default scroll length). */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_context_process_mouse_wheel(mfrmlui_context* context, float delta_x,
	float delta_y, int32_t key_modifiers);
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_context_process_mouse_leave(mfrmlui_context* context);
/* key: MFRMLUI_KI_* (Rml::Input::KeyIdentifier). */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_context_process_key_down(mfrmlui_context* context, int32_t key,
	int32_t key_modifiers);
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_context_process_key_up(mfrmlui_context* context, int32_t key,
	int32_t key_modifiers);
/* UTF-8 text from the platform's text-input event (one or more characters). */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_context_process_text_input(mfrmlui_context* context, const char* utf8);

/* ---------------------------------------------------------------------------------------------------------------
 * Documents
 * ------------------------------------------------------------------------------------------------------------- */

/* Load an .rml document through the file interface. Data models it uses must be created first. Not shown yet. */
MFRMLUI_API mfrmlui_document* MFRMLUI_CALL mfrmlui_context_load_document(mfrmlui_context* context, const char* path);
/* source_url may be NULL; it is used to resolve relative paths (style sheets, images) and for reload. */
MFRMLUI_API mfrmlui_document* MFRMLUI_CALL mfrmlui_context_load_document_from_memory(mfrmlui_context* context,
	const char* rml, const char* source_url);
/* modal: MFRMLUI_MODAL_*, focus: MFRMLUI_FOCUS_*, scroll: MFRMLUI_SCROLL_*. */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_document_show(mfrmlui_document* document, int32_t modal, int32_t focus,
	int32_t scroll);
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_document_hide(mfrmlui_document* document);
/* Schedules the document for unloading; the handle becomes invalid after the next context update. */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_document_close(mfrmlui_document* document);
/* Hot reload: clears the style sheet and template caches, closes `document` and loads its source URL again,
 * restoring visibility and modality. Returns the new document (the old handle is closed), or NULL on failure (the
 * old document is then left untouched). Element handles and listeners of the old document are detached. */
MFRMLUI_API mfrmlui_document* MFRMLUI_CALL mfrmlui_document_reload(mfrmlui_document* document);
/* Re-reads the document's style sheets only (keeps the DOM and its state). */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_document_reload_style_sheet(mfrmlui_document* document);
MFRMLUI_API mfrmlui_bool MFRMLUI_CALL mfrmlui_document_is_visible(mfrmlui_document* document);
MFRMLUI_API mfrmlui_bool MFRMLUI_CALL mfrmlui_document_is_modal(mfrmlui_document* document);
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_document_pull_to_front(mfrmlui_document* document);
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_document_push_to_back(mfrmlui_document* document);
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_document_get_title(mfrmlui_document* document, char* buffer, int32_t capacity);
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_document_get_source_url(mfrmlui_document* document, char* buffer,
	int32_t capacity);
/* A document is an element: use the result with the mfrmlui_element_* functions. */
MFRMLUI_API mfrmlui_element* MFRMLUI_CALL mfrmlui_document_as_element(mfrmlui_document* document);

/* ---------------------------------------------------------------------------------------------------------------
 * Elements
 * ------------------------------------------------------------------------------------------------------------- */

MFRMLUI_API mfrmlui_document* MFRMLUI_CALL mfrmlui_element_get_owner_document(mfrmlui_element* element);
MFRMLUI_API mfrmlui_element* MFRMLUI_CALL mfrmlui_element_get_parent(mfrmlui_element* element);
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_element_get_num_children(mfrmlui_element* element);
MFRMLUI_API mfrmlui_element* MFRMLUI_CALL mfrmlui_element_get_child(mfrmlui_element* element, int32_t index);
/* Searches the element's descendants. */
MFRMLUI_API mfrmlui_element* MFRMLUI_CALL mfrmlui_element_get_element_by_id(mfrmlui_element* element, const char* id);
MFRMLUI_API mfrmlui_element* MFRMLUI_CALL mfrmlui_element_query_selector(mfrmlui_element* element,
	const char* selector);
/* Writes up to `capacity` matches to out_elements (may be NULL with capacity 0); returns the total match count. */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_element_query_selector_all(mfrmlui_element* element, const char* selector,
	mfrmlui_element** out_elements, int32_t capacity);

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_element_get_tag_name(mfrmlui_element* element, char* buffer, int32_t capacity);
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_element_get_id(mfrmlui_element* element, char* buffer, int32_t capacity);
/* Returns MFRMLUI_ERROR_NOT_FOUND when the attribute does not exist. */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_element_get_attribute(mfrmlui_element* element, const char* name,
	char* buffer, int32_t capacity);
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_element_set_attribute(mfrmlui_element* element, const char* name,
	const char* value);
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_element_remove_attribute(mfrmlui_element* element, const char* name);
MFRMLUI_API mfrmlui_bool MFRMLUI_CALL mfrmlui_element_has_attribute(mfrmlui_element* element, const char* name);
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_element_set_class(mfrmlui_element* element, const char* class_name,
	mfrmlui_bool activate);
MFRMLUI_API mfrmlui_bool MFRMLUI_CALL mfrmlui_element_is_class_set(mfrmlui_element* element, const char* class_name);
/* Replaces all classes with the space-separated list. */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_element_set_class_names(mfrmlui_element* element, const char* class_names);
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_element_set_pseudo_class(mfrmlui_element* element, const char* pseudo_class,
	mfrmlui_bool activate);
MFRMLUI_API mfrmlui_bool MFRMLUI_CALL mfrmlui_element_is_pseudo_class_set(mfrmlui_element* element,
	const char* pseudo_class);
/* Inline RCSS property, e.g. ("width", "50%"). MFRMLUI_ERROR_FAILED if the value does not parse. */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_element_set_property(mfrmlui_element* element, const char* name,
	const char* value);
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_element_remove_property(mfrmlui_element* element, const char* name);
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_element_get_inner_rml(mfrmlui_element* element, char* buffer,
	int32_t capacity);
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_element_set_inner_rml(mfrmlui_element* element, const char* rml);
/* Form controls (input, textarea, select): current value. Other elements: MFRMLUI_ERROR_TYPE_MISMATCH. */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_element_get_value(mfrmlui_element* element, char* buffer, int32_t capacity);
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_element_set_value(mfrmlui_element* element, const char* value);
/* Returns 1 if focus was taken, 0 if the element cannot be focused. */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_element_focus(mfrmlui_element* element, mfrmlui_bool focus_visible);
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_element_blur(mfrmlui_element* element);
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_element_click(mfrmlui_element* element);
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_element_scroll_into_view(mfrmlui_element* element,
	mfrmlui_bool align_with_top);
/* Border box in context pixels (absolute offset and size). */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_element_get_bounds(mfrmlui_element* element, mfrmlui_rectf* out_bounds);

/* ---------------------------------------------------------------------------------------------------------------
 * Element events
 * ------------------------------------------------------------------------------------------------------------- */

/* Attach a listener for `event_type` ("click", "change", "submit", "focus", "blur", "mouseover", ...). Returns the
 * listener handle (NULL on failure; on_detach is not called then). `on_detach` (optional) runs exactly once, when the
 * listener is removed or its element is destroyed; the handle is invalid afterwards. */
MFRMLUI_API mfrmlui_event_listener* MFRMLUI_CALL mfrmlui_element_add_event_listener(mfrmlui_element* element,
	const char* event_type, mfrmlui_bool in_capture_phase, mfrmlui_event_callback callback,
	mfrmlui_release_callback on_detach, void* user_data);
/* Detach a listener that has not been detached yet (its on_detach runs before this returns). */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_event_listener_remove(mfrmlui_event_listener* listener);

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_event_get_type(mfrmlui_event* event, char* buffer, int32_t capacity);
/* MFRMLUI_EVENT_PHASE_*, or a negative error. */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_event_get_phase(mfrmlui_event* event);
MFRMLUI_API mfrmlui_element* MFRMLUI_CALL mfrmlui_event_get_target_element(mfrmlui_event* event);
MFRMLUI_API mfrmlui_element* MFRMLUI_CALL mfrmlui_event_get_current_element(mfrmlui_event* event);
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_event_stop_propagation(mfrmlui_event* event);
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_event_stop_immediate_propagation(mfrmlui_event* event);
/* Event parameters (e.g. "mouse_x", "button", "key_identifier", "value"). */
MFRMLUI_API const mfrmlui_dictionary* MFRMLUI_CALL mfrmlui_event_get_parameters(mfrmlui_event* event);

/* ---------------------------------------------------------------------------------------------------------------
 * Variants and dictionaries (callback scope)
 * ------------------------------------------------------------------------------------------------------------- */

/* MFRMLUI_VARIANT_*, or a negative error. */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_variant_get_type(const mfrmlui_variant* variant);
/* Converting getters: MFRMLUI_ERROR_TYPE_MISMATCH if the value cannot be converted. */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_variant_get_bool(const mfrmlui_variant* variant, mfrmlui_bool* out_value);
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_variant_get_int64(const mfrmlui_variant* variant, int64_t* out_value);
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_variant_get_double(const mfrmlui_variant* variant, double* out_value);
/* Caller-buffer pattern; numbers and booleans are converted to text. */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_variant_get_string(const mfrmlui_variant* variant, char* buffer,
	int32_t capacity);
/* VECTOR2/3/4 and COLOURF: components in order, unused ones set to 0. COLOURB: bytes as 0..255 floats. */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_variant_get_float4(const mfrmlui_variant* variant, float* out_values4);
/* COLOURB: straight (not premultiplied) RGBA8, e.g. the drop-shadow filter "color"; premultiply before use. */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_variant_get_colourb(const mfrmlui_variant* variant, uint8_t* out_rgba4);
/* COLOR_STOP_LIST: returns the stop count, writes up to `capacity` stops (out_stops may be NULL). */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_variant_get_color_stops(const mfrmlui_variant* variant,
	mfrmlui_color_stop* out_stops, int32_t capacity);
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_variant_set_none(mfrmlui_variant* variant);
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_variant_set_bool(mfrmlui_variant* variant, mfrmlui_bool value);
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_variant_set_int64(mfrmlui_variant* variant, int64_t value);
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_variant_set_double(mfrmlui_variant* variant, double value);
/* length < 0 means `utf8` is NUL-terminated. */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_variant_set_string(mfrmlui_variant* variant, const char* utf8,
	int32_t length);

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_dictionary_get_count(const mfrmlui_dictionary* dictionary);
/* Entries are in unspecified order; index in [0, count). */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_dictionary_get_key(const mfrmlui_dictionary* dictionary, int32_t index,
	char* buffer, int32_t capacity);
MFRMLUI_API const mfrmlui_variant* MFRMLUI_CALL mfrmlui_dictionary_get_value(const mfrmlui_dictionary* dictionary,
	int32_t index);
/* NULL if the key does not exist. */
MFRMLUI_API const mfrmlui_variant* MFRMLUI_CALL mfrmlui_dictionary_find(const mfrmlui_dictionary* dictionary,
	const char* key);

/* ---------------------------------------------------------------------------------------------------------------
 * Data models (data binding: {{ value }}, data-for, data-event-*, data-value, ...)
 *
 * Create the model and bind everything before loading documents that use it (data-model="name"). After changing a
 * bound value, mark it dirty; views update on the next mfrmlui_context_update. Every binding's release callback runs
 * exactly once when the model is removed, its context destroyed, or the library shut down. If a bind call fails
 * (e.g. the name is already bound) the binding is not created and its release callback is never called.
 * ------------------------------------------------------------------------------------------------------------- */

MFRMLUI_API mfrmlui_data_model* MFRMLUI_CALL mfrmlui_data_model_create(mfrmlui_context* context, const char* name);
/* Removes the model from its context (elements bound to it lose the binding) and frees the handle. */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_data_model_remove(mfrmlui_data_model* model);
/* Scalar value through a getter and optional setter (NULL = read-only). */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_data_model_bind_func(mfrmlui_data_model* model, const char* name,
	mfrmlui_data_get_callback get, mfrmlui_data_set_callback set, mfrmlui_release_callback release, void* user_data);
/* Event callback used by data-event-* attributes. */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_data_model_bind_event_callback(mfrmlui_data_model* model, const char* name,
	mfrmlui_data_event_callback callback, mfrmlui_release_callback release, void* user_data);
/* Dynamic variable (arrays / structs / nested graphs); root_kind is MFRMLUI_VARIABLE_*. */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_data_model_bind_variable(mfrmlui_data_model* model, const char* name,
	int32_t root_kind, uint64_t root_node, const mfrmlui_variable_callbacks* callbacks);
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_data_model_dirty_variable(mfrmlui_data_model* model, const char* name);
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_data_model_dirty_all_variables(mfrmlui_data_model* model);
MFRMLUI_API mfrmlui_bool MFRMLUI_CALL mfrmlui_data_model_is_variable_dirty(mfrmlui_data_model* model,
	const char* name);

/* ---------------------------------------------------------------------------------------------------------------
 * Visual debugger (RmlUi Debugger plugin)
 * ------------------------------------------------------------------------------------------------------------- */

/* Creates the debugger's documents inside `host_context` and starts debugging it. Once per initialise. */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_debugger_initialise(mfrmlui_context* host_context);
/* Switch the debugged context. */
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_debugger_set_context(mfrmlui_context* context);
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_debugger_set_visible(mfrmlui_bool visible);
MFRMLUI_API mfrmlui_bool MFRMLUI_CALL mfrmlui_debugger_is_visible(void);
MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_debugger_shutdown(void);

/* ---------------------------------------------------------------------------------------------------------------
 * Key identifiers (Rml::Input::KeyIdentifier, RmlUi 6.3; values asserted in the shim)
 * ------------------------------------------------------------------------------------------------------------- */

#define MFRMLUI_KI_UNKNOWN             0
#define MFRMLUI_KI_SPACE               1
#define MFRMLUI_KI_0                   2
#define MFRMLUI_KI_1                   3
#define MFRMLUI_KI_2                   4
#define MFRMLUI_KI_3                   5
#define MFRMLUI_KI_4                   6
#define MFRMLUI_KI_5                   7
#define MFRMLUI_KI_6                   8
#define MFRMLUI_KI_7                   9
#define MFRMLUI_KI_8                   10
#define MFRMLUI_KI_9                   11
#define MFRMLUI_KI_A                   12
#define MFRMLUI_KI_B                   13
#define MFRMLUI_KI_C                   14
#define MFRMLUI_KI_D                   15
#define MFRMLUI_KI_E                   16
#define MFRMLUI_KI_F                   17
#define MFRMLUI_KI_G                   18
#define MFRMLUI_KI_H                   19
#define MFRMLUI_KI_I                   20
#define MFRMLUI_KI_J                   21
#define MFRMLUI_KI_K                   22
#define MFRMLUI_KI_L                   23
#define MFRMLUI_KI_M                   24
#define MFRMLUI_KI_N                   25
#define MFRMLUI_KI_O                   26
#define MFRMLUI_KI_P                   27
#define MFRMLUI_KI_Q                   28
#define MFRMLUI_KI_R                   29
#define MFRMLUI_KI_S                   30
#define MFRMLUI_KI_T                   31
#define MFRMLUI_KI_U                   32
#define MFRMLUI_KI_V                   33
#define MFRMLUI_KI_W                   34
#define MFRMLUI_KI_X                   35
#define MFRMLUI_KI_Y                   36
#define MFRMLUI_KI_Z                   37
#define MFRMLUI_KI_OEM_1               38
#define MFRMLUI_KI_OEM_PLUS            39
#define MFRMLUI_KI_OEM_COMMA           40
#define MFRMLUI_KI_OEM_MINUS           41
#define MFRMLUI_KI_OEM_PERIOD          42
#define MFRMLUI_KI_OEM_2               43
#define MFRMLUI_KI_OEM_3               44
#define MFRMLUI_KI_OEM_4               45
#define MFRMLUI_KI_OEM_5               46
#define MFRMLUI_KI_OEM_6               47
#define MFRMLUI_KI_OEM_7               48
#define MFRMLUI_KI_OEM_8               49
#define MFRMLUI_KI_OEM_102             50
#define MFRMLUI_KI_NUMPAD0             51
#define MFRMLUI_KI_NUMPAD1             52
#define MFRMLUI_KI_NUMPAD2             53
#define MFRMLUI_KI_NUMPAD3             54
#define MFRMLUI_KI_NUMPAD4             55
#define MFRMLUI_KI_NUMPAD5             56
#define MFRMLUI_KI_NUMPAD6             57
#define MFRMLUI_KI_NUMPAD7             58
#define MFRMLUI_KI_NUMPAD8             59
#define MFRMLUI_KI_NUMPAD9             60
#define MFRMLUI_KI_NUMPADENTER         61
#define MFRMLUI_KI_MULTIPLY            62
#define MFRMLUI_KI_ADD                 63
#define MFRMLUI_KI_SEPARATOR           64
#define MFRMLUI_KI_SUBTRACT            65
#define MFRMLUI_KI_DECIMAL             66
#define MFRMLUI_KI_DIVIDE              67
#define MFRMLUI_KI_OEM_NEC_EQUAL       68
#define MFRMLUI_KI_BACK                69
#define MFRMLUI_KI_TAB                 70
#define MFRMLUI_KI_CLEAR               71
#define MFRMLUI_KI_RETURN              72
#define MFRMLUI_KI_PAUSE               73
#define MFRMLUI_KI_CAPITAL             74
#define MFRMLUI_KI_KANA                75
#define MFRMLUI_KI_HANGUL              76
#define MFRMLUI_KI_JUNJA               77
#define MFRMLUI_KI_FINAL               78
#define MFRMLUI_KI_HANJA               79
#define MFRMLUI_KI_KANJI               80
#define MFRMLUI_KI_ESCAPE              81
#define MFRMLUI_KI_CONVERT             82
#define MFRMLUI_KI_NONCONVERT          83
#define MFRMLUI_KI_ACCEPT              84
#define MFRMLUI_KI_MODECHANGE          85
#define MFRMLUI_KI_PRIOR               86
#define MFRMLUI_KI_NEXT                87
#define MFRMLUI_KI_END                 88
#define MFRMLUI_KI_HOME                89
#define MFRMLUI_KI_LEFT                90
#define MFRMLUI_KI_UP                  91
#define MFRMLUI_KI_RIGHT               92
#define MFRMLUI_KI_DOWN                93
#define MFRMLUI_KI_SELECT              94
#define MFRMLUI_KI_PRINT               95
#define MFRMLUI_KI_EXECUTE             96
#define MFRMLUI_KI_SNAPSHOT            97
#define MFRMLUI_KI_INSERT              98
#define MFRMLUI_KI_DELETE              99
#define MFRMLUI_KI_HELP                100
#define MFRMLUI_KI_LWIN                101
#define MFRMLUI_KI_RWIN                102
#define MFRMLUI_KI_APPS                103
#define MFRMLUI_KI_POWER               104
#define MFRMLUI_KI_SLEEP               105
#define MFRMLUI_KI_WAKE                106
#define MFRMLUI_KI_F1                  107
#define MFRMLUI_KI_F2                  108
#define MFRMLUI_KI_F3                  109
#define MFRMLUI_KI_F4                  110
#define MFRMLUI_KI_F5                  111
#define MFRMLUI_KI_F6                  112
#define MFRMLUI_KI_F7                  113
#define MFRMLUI_KI_F8                  114
#define MFRMLUI_KI_F9                  115
#define MFRMLUI_KI_F10                 116
#define MFRMLUI_KI_F11                 117
#define MFRMLUI_KI_F12                 118
#define MFRMLUI_KI_F13                 119
#define MFRMLUI_KI_F14                 120
#define MFRMLUI_KI_F15                 121
#define MFRMLUI_KI_F16                 122
#define MFRMLUI_KI_F17                 123
#define MFRMLUI_KI_F18                 124
#define MFRMLUI_KI_F19                 125
#define MFRMLUI_KI_F20                 126
#define MFRMLUI_KI_F21                 127
#define MFRMLUI_KI_F22                 128
#define MFRMLUI_KI_F23                 129
#define MFRMLUI_KI_F24                 130
#define MFRMLUI_KI_NUMLOCK             131
#define MFRMLUI_KI_SCROLL              132
#define MFRMLUI_KI_OEM_FJ_JISHO        133
#define MFRMLUI_KI_OEM_FJ_MASSHOU      134
#define MFRMLUI_KI_OEM_FJ_TOUROKU      135
#define MFRMLUI_KI_OEM_FJ_LOYA         136
#define MFRMLUI_KI_OEM_FJ_ROYA         137
#define MFRMLUI_KI_LSHIFT              138
#define MFRMLUI_KI_RSHIFT              139
#define MFRMLUI_KI_LCONTROL            140
#define MFRMLUI_KI_RCONTROL            141
#define MFRMLUI_KI_LMENU               142
#define MFRMLUI_KI_RMENU               143
#define MFRMLUI_KI_BROWSER_BACK        144
#define MFRMLUI_KI_BROWSER_FORWARD     145
#define MFRMLUI_KI_BROWSER_REFRESH     146
#define MFRMLUI_KI_BROWSER_STOP        147
#define MFRMLUI_KI_BROWSER_SEARCH      148
#define MFRMLUI_KI_BROWSER_FAVORITES   149
#define MFRMLUI_KI_BROWSER_HOME        150
#define MFRMLUI_KI_VOLUME_MUTE         151
#define MFRMLUI_KI_VOLUME_DOWN         152
#define MFRMLUI_KI_VOLUME_UP           153
#define MFRMLUI_KI_MEDIA_NEXT_TRACK    154
#define MFRMLUI_KI_MEDIA_PREV_TRACK    155
#define MFRMLUI_KI_MEDIA_STOP          156
#define MFRMLUI_KI_MEDIA_PLAY_PAUSE    157
#define MFRMLUI_KI_LAUNCH_MAIL         158
#define MFRMLUI_KI_LAUNCH_MEDIA_SELECT 159
#define MFRMLUI_KI_LAUNCH_APP1         160
#define MFRMLUI_KI_LAUNCH_APP2         161
#define MFRMLUI_KI_OEM_AX              162
#define MFRMLUI_KI_ICO_HELP            163
#define MFRMLUI_KI_ICO_00              164
#define MFRMLUI_KI_PROCESSKEY          165
#define MFRMLUI_KI_ICO_CLEAR           166
#define MFRMLUI_KI_ATTN                167
#define MFRMLUI_KI_CRSEL               168
#define MFRMLUI_KI_EXSEL               169
#define MFRMLUI_KI_EREOF               170
#define MFRMLUI_KI_PLAY                171
#define MFRMLUI_KI_ZOOM                172
#define MFRMLUI_KI_PA1                 173
#define MFRMLUI_KI_OEM_CLEAR           174
#define MFRMLUI_KI_LMETA               175
#define MFRMLUI_KI_RMETA               176

#ifdef __cplusplus
} /* extern "C" */
#endif

#endif /* MFRMLUI_H */
