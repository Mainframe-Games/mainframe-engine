/*
 * mfrmlui smoke test — a plain C11 client of mfrmlui.h.
 *
 * Exercises the whole ABI surface the M8 managed binding will use, with a no-op render interface that counts
 * callbacks and tracks live geometry/textures (so shutdown can be leak-checked), an in-memory file interface, a data
 * model with a scalar getter, a dynamic array-of-structs and an event callback, input routing, element listeners, the
 * debugger and hot reload.
 */
#include "mfrmlui.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static int failures = 0;

#define CHECK(cond)                                                                    \
	do                                                                                 \
	{                                                                                  \
		if (!(cond))                                                                   \
		{                                                                              \
			fprintf(stderr, "FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond);            \
			++failures;                                                                \
		}                                                                              \
	} while (0)

#define REQUIRE(cond)                                                                  \
	do                                                                                 \
	{                                                                                  \
		if (!(cond))                                                                   \
		{                                                                              \
			fprintf(stderr, "FATAL %s:%d: %s\n", __FILE__, __LINE__, #cond);           \
			print_last_error();                                                        \
			return 1;                                                                  \
		}                                                                              \
	} while (0)

static void print_last_error(void)
{
	char buffer[512];
	if (mfrmlui_get_last_error(buffer, (int32_t)sizeof buffer) > 0)
		fprintf(stderr, "  last error: %s\n", buffer);
}

/* Checks that exactly the expected number of RmlUi errors/warnings were logged by a deliberately failing call. */
#define EXPECT_LOGGED(errors_, warnings_)                                              \
	do                                                                                 \
	{                                                                                  \
		CHECK(ss.errors == (errors_) && ss.warnings == (warnings_));                   \
		ss.errors = 0;                                                                 \
		ss.warnings = 0;                                                               \
	} while (0)

/* ----- render interface (no-op, counting) --------------------------------------------------------------------- */

typedef struct render_stats {
	int compile_geometry, render_geometry, release_geometry;
	int load_texture, generate_texture, release_texture;
	int enable_scissor, set_scissor, set_transform;
	int live_geometry, live_textures;
	uint64_t next_handle;
	int vertices_ok;
} render_stats;

static uint64_t MFRMLUI_CALL rc_compile_geometry(void* user, const mfrmlui_vertex* vertices, int32_t num_vertices, const int32_t* indices,
	int32_t num_indices)
{
	render_stats* s = (render_stats*)user;
	s->compile_geometry++;
	s->live_geometry++;
	if (num_vertices > 0 && vertices && num_indices > 0 && indices && indices[0] >= 0 && indices[0] < num_vertices)
		s->vertices_ok++;
	return ++s->next_handle;
}

static void MFRMLUI_CALL rc_render_geometry(void* user, uint64_t geometry, float tx, float ty, uint64_t texture)
{
	(void)geometry;
	(void)tx;
	(void)ty;
	(void)texture;
	((render_stats*)user)->render_geometry++;
}

static void MFRMLUI_CALL rc_release_geometry(void* user, uint64_t geometry)
{
	render_stats* s = (render_stats*)user;
	(void)geometry;
	s->release_geometry++;
	s->live_geometry--;
}

static uint64_t MFRMLUI_CALL rc_load_texture(void* user, const char* source, int32_t* width, int32_t* height)
{
	render_stats* s = (render_stats*)user;
	(void)source;
	s->load_texture++;
	s->live_textures++;
	*width = 1;
	*height = 1;
	return ++s->next_handle;
}

static uint64_t MFRMLUI_CALL rc_generate_texture(void* user, const uint8_t* rgba, int32_t num_bytes, int32_t width, int32_t height)
{
	render_stats* s = (render_stats*)user;
	(void)rgba;
	if (num_bytes != width * height * 4)
		return 0;
	s->generate_texture++;
	s->live_textures++;
	return ++s->next_handle;
}

static void MFRMLUI_CALL rc_release_texture(void* user, uint64_t texture)
{
	render_stats* s = (render_stats*)user;
	(void)texture;
	s->release_texture++;
	s->live_textures--;
}

static void MFRMLUI_CALL rc_enable_scissor(void* user, mfrmlui_bool enable)
{
	(void)enable;
	((render_stats*)user)->enable_scissor++;
}

static void MFRMLUI_CALL rc_set_scissor(void* user, int32_t x, int32_t y, int32_t w, int32_t h)
{
	(void)x;
	(void)y;
	(void)w;
	(void)h;
	((render_stats*)user)->set_scissor++;
}

static void MFRMLUI_CALL rc_set_transform(void* user, const float* matrix)
{
	(void)matrix;
	((render_stats*)user)->set_transform++;
}

/* ----- system interface ------------------------------------------------------------------------------------------- */

typedef struct system_stats {
	double time;
	int errors, warnings, translations;
} system_stats;

static double MFRMLUI_CALL sc_time(void* user)
{
	system_stats* s = (system_stats*)user;
	s->time += 1.0 / 60.0;
	return s->time;
}

static mfrmlui_bool MFRMLUI_CALL sc_log(void* user, int32_t type, const char* message)
{
	system_stats* s = (system_stats*)user;
	if (type == MFRMLUI_LOG_ERROR || type == MFRMLUI_LOG_ASSERT)
	{
		s->errors++;
		fprintf(stderr, "  [rmlui error] %s\n", message);
	}
	else if (type == MFRMLUI_LOG_WARNING)
	{
		s->warnings++;
		fprintf(stderr, "  [rmlui warning] %s\n", message);
	}
	return 1;
}

static int32_t MFRMLUI_CALL sc_translate(void* user, const char* input, mfrmlui_string* out)
{
	system_stats* s = (system_stats*)user;
	if (strcmp(input, "[greeting]") == 0)
	{
		s->translations++;
		mfrmlui_string_set(out, "Hello", -1);
		return 1;
	}
	return 0;
}

/* ----- file interface: in-memory documents, stdio for everything else (the font) ---------------------------------- */

static const char test_rml[] =
	"<rml>\n"
	"<head>\n"
	"  <title>Smoke</title>\n"
	"  <link type=\"text/rcss\" href=\"test.rcss\"/>\n"
	"</head>\n"
	"<body data-model=\"hud\">\n"
	"  <h1 id=\"title\">[greeting]</h1>\n"
	"  <p id=\"health\">HP {{ health }}</p>\n"
	"  <div id=\"items\"><span data-for=\"item : items\">{{ item.name }}</span></div>\n"
	"  <button id=\"btn\" data-event-click=\"pressed(7)\">Press</button>\n"
	"  <input id=\"name\" type=\"text\" value=\"abc\"/>\n"
	"</body>\n"
	"</rml>\n";

static const char test_rcss[] =
	"body { font-family: LatoLatin; font-size: 16px; color: #ffffff; width: 400px; height: 300px; background-color: #202020; }\n"
	"h1, p, div, span { display: block; }\n"
	"button { display: block; width: 100px; height: 30px; background-color: #335; }\n"
	"input { display: block; width: 200px; height: 20px; }\n";

typedef struct mem_file {
	const char* data; /* NULL for stdio files */
	size_t size, pos;
	FILE* fp;
} mem_file;

static uint64_t MFRMLUI_CALL fc_open(void* user, const char* path)
{
	(void)user;
	mem_file* f = (mem_file*)calloc(1, sizeof(mem_file));
	if (!f)
		return 0;
	if (strcmp(path, "ui/test.rml") == 0)
	{
		f->data = test_rml;
		f->size = sizeof test_rml - 1;
	}
	else if (strcmp(path, "ui/test.rcss") == 0)
	{
		f->data = test_rcss;
		f->size = sizeof test_rcss - 1;
	}
	else
	{
		f->fp = fopen(path, "rb");
		if (!f->fp)
		{
			free(f);
			return 0;
		}
	}
	return (uint64_t)(uintptr_t)f;
}

static void MFRMLUI_CALL fc_close(void* user, uint64_t file)
{
	mem_file* f = (mem_file*)(uintptr_t)file;
	(void)user;
	if (f->fp)
		fclose(f->fp);
	free(f);
}

static uint64_t MFRMLUI_CALL fc_read(void* user, uint64_t file, void* buffer, uint64_t size)
{
	mem_file* f = (mem_file*)(uintptr_t)file;
	(void)user;
	if (f->fp)
		return (uint64_t)fread(buffer, 1, (size_t)size, f->fp);
	size_t n = f->size - f->pos;
	if (n > size)
		n = (size_t)size;
	memcpy(buffer, f->data + f->pos, n);
	f->pos += n;
	return (uint64_t)n;
}

static mfrmlui_bool MFRMLUI_CALL fc_seek(void* user, uint64_t file, int64_t offset, int32_t origin)
{
	mem_file* f = (mem_file*)(uintptr_t)file;
	(void)user;
	if (f->fp)
		return fseek(f->fp, (long)offset, origin) == 0;
	int64_t base = origin == 0 ? 0 : origin == 1 ? (int64_t)f->pos : (int64_t)f->size;
	int64_t target = base + offset;
	if (target < 0 || target > (int64_t)f->size)
		return 0;
	f->pos = (size_t)target;
	return 1;
}

static uint64_t MFRMLUI_CALL fc_tell(void* user, uint64_t file)
{
	mem_file* f = (mem_file*)(uintptr_t)file;
	(void)user;
	return f->fp ? (uint64_t)ftell(f->fp) : (uint64_t)f->pos;
}

/* ----- data model ------------------------------------------------------------------------------------------------------ */

typedef struct model_stats {
	int health_gets, item_gets, pressed, pressed_arg_ok, releases;
} model_stats;

static void MFRMLUI_CALL dm_get_health(void* user, mfrmlui_variant* out)
{
	model_stats* s = (model_stats*)user;
	s->health_gets++;
	mfrmlui_variant_set_int64(out, 42);
}

static void MFRMLUI_CALL dm_release(void* user)
{
	((model_stats*)user)->releases++;
}

static void MFRMLUI_CALL dm_pressed(void* user, mfrmlui_data_model* model, mfrmlui_event* event, const mfrmlui_variant* const* args,
	int32_t num_args)
{
	model_stats* s = (model_stats*)user;
	int64_t value = 0;
	(void)model;
	s->pressed++;
	if (event && num_args == 1 && mfrmlui_variant_get_type(args[0]) == MFRMLUI_VARIANT_FLOAT &&
		mfrmlui_variant_get_int64(args[0], &value) == MFRMLUI_OK && value == 7)
		s->pressed_arg_ok++;
	else if (event && num_args == 1 && mfrmlui_variant_get_int64(args[0], &value) == MFRMLUI_OK && value == 7)
		s->pressed_arg_ok++;
}

/* items: root node 1 (array of 2) -> 100+i (struct) -> 200+i ("name", scalar) */
static mfrmlui_bool MFRMLUI_CALL var_get(void* user, uint64_t node, mfrmlui_variant* out)
{
	model_stats* s = (model_stats*)user;
	char text[16];
	if (node < 200 || node > 201)
		return 0;
	s->item_gets++;
	snprintf(text, sizeof text, "item%d", (int)(node - 200));
	return mfrmlui_variant_set_string(out, text, -1) == MFRMLUI_OK;
}

static int32_t MFRMLUI_CALL var_size(void* user, uint64_t node)
{
	(void)user;
	return node == 1 ? 2 : 0;
}

static int32_t MFRMLUI_CALL var_child(void* user, uint64_t node, int32_t index, const char* name, uint64_t* out_child)
{
	(void)user;
	if (node == 1 && index >= 0 && index < 2 && name == NULL)
	{
		*out_child = 100 + (uint64_t)index;
		return MFRMLUI_VARIABLE_STRUCT;
	}
	if (node >= 100 && node <= 101 && index == -1 && name && strcmp(name, "name") == 0)
	{
		*out_child = node + 100;
		return MFRMLUI_VARIABLE_SCALAR;
	}
	return -1;
}

static void MFRMLUI_CALL var_release(void* user)
{
	((model_stats*)user)->releases++;
}

/* ----- element listeners --------------------------------------------------------------------------------------------- */

typedef struct listener_stats {
	int clicks, detaches, params_ok;
	char last_type[32];
} listener_stats;

static void MFRMLUI_CALL on_click(void* user, mfrmlui_event* event)
{
	listener_stats* s = (listener_stats*)user;
	s->clicks++;
	mfrmlui_event_get_type(event, s->last_type, (int32_t)sizeof s->last_type);
	const mfrmlui_dictionary* params = mfrmlui_event_get_parameters(event);
	const mfrmlui_variant* mouse_x = mfrmlui_dictionary_find(params, "mouse_x");
	double x = 0.0;
	if (mouse_x && mfrmlui_variant_get_double(mouse_x, &x) == MFRMLUI_OK && x > 0.0 && mfrmlui_dictionary_get_count(params) > 0)
		s->params_ok++;
}

static void MFRMLUI_CALL on_detach(void* user)
{
	((listener_stats*)user)->detaches++;
}

/* ----- helpers ------------------------------------------------------------------------------------------------------------- */

static int frame(mfrmlui_context* context)
{
	return mfrmlui_context_update(context) == MFRMLUI_OK && mfrmlui_context_render(context) == MFRMLUI_OK;
}

static int inner_rml_contains(mfrmlui_element* element, const char* text)
{
	char buffer[256];
	const int32_t n = mfrmlui_element_get_inner_rml(element, buffer, (int32_t)sizeof buffer);
	return n >= 0 && strstr(buffer, text) != NULL;
}

int main(void)
{
	char text[256];
	render_stats rs;
	system_stats ss;
	model_stats ms;
	listener_stats ls;
	memset(&rs, 0, sizeof rs);
	memset(&ss, 0, sizeof ss);
	memset(&ms, 0, sizeof ms);
	memset(&ls, 0, sizeof ls);

	/* --- ABI version and pre-initialisation behaviour --- */
	CHECK(mfrmlui_abi_version() == MFRMLUI_ABI_VERSION);
	CHECK((mfrmlui_abi_version() >> 16) == MFRMLUI_ABI_VERSION_MAJOR);
	CHECK(mfrmlui_is_initialised() == 0);
	CHECK(mfrmlui_context_update(NULL) == MFRMLUI_ERROR_INVALID_ARGUMENT);
	CHECK(mfrmlui_shutdown() == MFRMLUI_ERROR_NOT_INITIALISED);
	CHECK(mfrmlui_context_create("x", 1, 1, NULL) == NULL);
	CHECK(mfrmlui_get_last_error(NULL, 0) > 0);
	CHECK(mfrmlui_get_last_error(NULL, 5) == MFRMLUI_ERROR_INVALID_ARGUMENT);
	CHECK(mfrmlui_element_get_id(NULL, text, (int32_t)sizeof text) == MFRMLUI_ERROR_INVALID_ARGUMENT);
	CHECK(mfrmlui_variant_set_int64(NULL, 1) == MFRMLUI_ERROR_INVALID_ARGUMENT);
	CHECK(mfrmlui_document_show(NULL, 0, 0, 0) == MFRMLUI_ERROR_INVALID_ARGUMENT);
	CHECK(mfrmlui_event_listener_remove(NULL) == MFRMLUI_ERROR_INVALID_ARGUMENT);
	CHECK(mfrmlui_render_interface_destroy(NULL) == MFRMLUI_ERROR_INVALID_ARGUMENT);
	CHECK(mfrmlui_get_rmlui_version(text, (int32_t)sizeof text) > 0 && strcmp(text, "6.3") == 0);

	/* --- render interface --- */
	mfrmlui_render_callbacks rc;
	memset(&rc, 0, sizeof rc);
	rc.struct_size = (uint32_t)sizeof rc;
	rc.user_data = &rs;
	rc.compile_geometry = rc_compile_geometry;
	rc.render_geometry = rc_render_geometry;
	rc.release_geometry = rc_release_geometry;
	rc.load_texture = rc_load_texture;
	rc.generate_texture = rc_generate_texture;
	rc.release_texture = rc_release_texture;
	rc.enable_scissor_region = rc_enable_scissor;
	/* set_scissor_region missing -> rejected */
	CHECK(mfrmlui_render_interface_create(&rc) == NULL);
	rc.set_scissor_region = rc_set_scissor;
	rc.set_transform = rc_set_transform;
	mfrmlui_render_callbacks too_small = rc;
	too_small.struct_size = 8;
	CHECK(mfrmlui_render_interface_create(&too_small) == NULL);
	mfrmlui_render_interface* ri = mfrmlui_render_interface_create(&rc);
	REQUIRE(ri != NULL);

	/* --- initialise --- */
	mfrmlui_system_callbacks sc;
	memset(&sc, 0, sizeof sc);
	sc.struct_size = (uint32_t)sizeof sc;
	sc.user_data = &ss;
	sc.get_elapsed_time = sc_time;
	sc.log_message = sc_log;
	sc.translate_string = sc_translate;

	mfrmlui_file_callbacks fc;
	memset(&fc, 0, sizeof fc);
	fc.struct_size = (uint32_t)sizeof fc;
	fc.open = fc_open;
	fc.close = fc_close;
	fc.read = fc_read;
	fc.seek = fc_seek;
	fc.tell = fc_tell;

	REQUIRE(mfrmlui_initialise(&sc, &fc) == MFRMLUI_OK);
	CHECK(mfrmlui_is_initialised() == 1);
	CHECK(mfrmlui_initialise(&sc, &fc) == MFRMLUI_ERROR_ALREADY_INITIALISED);
	REQUIRE(mfrmlui_load_font_face(MFRMLUI_TEST_FONT, 1, MFRMLUI_FONT_WEIGHT_AUTO) == MFRMLUI_OK);
	CHECK(mfrmlui_load_font_face("does/not/exist.ttf", 0, 0) == MFRMLUI_ERROR_FAILED);
	EXPECT_LOGGED(1, 0); /* the missing font */

	/* --- context --- */
	mfrmlui_context* context = mfrmlui_context_create("main", 800, 600, ri);
	REQUIRE(context != NULL);
	CHECK(mfrmlui_context_create("main", 800, 600, ri) == NULL); /* duplicate name */
	CHECK(mfrmlui_context_set_density_independent_pixel_ratio(context, 1.0f) == MFRMLUI_OK);
	CHECK(mfrmlui_context_set_density_independent_pixel_ratio(context, -1.0f) == MFRMLUI_ERROR_INVALID_ARGUMENT);
	CHECK(mfrmlui_render_interface_destroy(ri) == MFRMLUI_ERROR_IN_USE);

	/* --- data model (before the document) --- */
	mfrmlui_data_model* model = mfrmlui_data_model_create(context, "hud");
	REQUIRE(model != NULL);
	CHECK(mfrmlui_data_model_create(context, "hud") == NULL);
	EXPECT_LOGGED(1, 0);
	CHECK(mfrmlui_data_model_bind_func(model, "health", dm_get_health, NULL, dm_release, &ms) == MFRMLUI_OK);
	CHECK(mfrmlui_data_model_bind_func(model, "health", dm_get_health, NULL, dm_release, &ms) == MFRMLUI_ERROR_FAILED);
	EXPECT_LOGGED(1, 0);
	CHECK(mfrmlui_data_model_bind_event_callback(model, "pressed", dm_pressed, dm_release, &ms) == MFRMLUI_OK);
	mfrmlui_variable_callbacks vc;
	memset(&vc, 0, sizeof vc);
	vc.struct_size = (uint32_t)sizeof vc;
	vc.user_data = &ms;
	vc.get = var_get;
	vc.size = var_size;
	vc.child = var_child;
	vc.release = var_release;
	CHECK(mfrmlui_data_model_bind_variable(model, "items", MFRMLUI_VARIABLE_ARRAY, 1, &vc) == MFRMLUI_OK);
	CHECK(mfrmlui_data_model_bind_variable(model, "bad", 9, 1, &vc) == MFRMLUI_ERROR_INVALID_ARGUMENT);

	/* a second model that is removed explicitly */
	model_stats tmp_stats;
	memset(&tmp_stats, 0, sizeof tmp_stats);
	mfrmlui_data_model* tmp = mfrmlui_data_model_create(context, "tmp");
	REQUIRE(tmp != NULL);
	CHECK(mfrmlui_data_model_bind_func(tmp, "x", dm_get_health, NULL, dm_release, &tmp_stats) == MFRMLUI_OK);
	CHECK(mfrmlui_data_model_remove(tmp) == MFRMLUI_OK);
	CHECK(tmp_stats.releases == 1);
	CHECK(mfrmlui_data_model_remove(tmp) == MFRMLUI_ERROR_INVALID_ARGUMENT);

	/* --- document --- */
	mfrmlui_document* document = mfrmlui_context_load_document(context, "ui/test.rml");
	REQUIRE(document != NULL);
	CHECK(mfrmlui_context_load_document(context, "ui/missing.rml") == NULL);
	EXPECT_LOGGED(0, 1);
	CHECK(mfrmlui_document_show(document, MFRMLUI_MODAL_NONE, MFRMLUI_FOCUS_AUTO, MFRMLUI_SCROLL_AUTO) == MFRMLUI_OK);
	CHECK(mfrmlui_document_show(document, 99, 0, 0) == MFRMLUI_ERROR_INVALID_ARGUMENT);
	CHECK(mfrmlui_document_is_visible(document) == 1);
	CHECK(mfrmlui_context_get_num_documents(context) >= 1);
	CHECK(frame(context));

	CHECK(rs.compile_geometry > 0);
	CHECK(rs.render_geometry > 0);
	CHECK(rs.vertices_ok == rs.compile_geometry);
	CHECK(rs.generate_texture >= 1); /* FreeType glyph atlas */
	CHECK(ss.translations >= 1);
	CHECK(ms.health_gets >= 1);
	CHECK(ms.item_gets >= 2);
	CHECK(mfrmlui_document_get_title(document, text, (int32_t)sizeof text) == 5 && strcmp(text, "Smoke") == 0);
	CHECK(mfrmlui_document_get_title(document, text, 3) == 5 && strcmp(text, "Sm") == 0); /* truncation */

	mfrmlui_element* root = mfrmlui_document_as_element(document);
	REQUIRE(root != NULL);
	mfrmlui_element* title = mfrmlui_element_get_element_by_id(root, "title");
	REQUIRE(title != NULL);
	CHECK(inner_rml_contains(title, "Hello"));
	CHECK(mfrmlui_element_get_tag_name(title, text, (int32_t)sizeof text) == 2 && strcmp(text, "h1") == 0);
	mfrmlui_element* health = mfrmlui_element_query_selector(root, "#health");
	REQUIRE(health != NULL);
	CHECK(inner_rml_contains(health, "42"));

	/* data-for keeps its (hidden) template element next to the generated ones: 1 template + 2 items. */
	mfrmlui_element* spans[8] = {NULL};
	const int32_t span_count = mfrmlui_element_query_selector_all(root, "#items span", spans, 8);
	CHECK(span_count == 3);
	CHECK(mfrmlui_element_query_selector_all(root, "#items span", NULL, 0) == span_count);
	int item0 = 0, item1 = 0;
	for (int32_t i = 0; i < span_count && i < 8; ++i)
	{
		item0 += inner_rml_contains(spans[i], "item0");
		item1 += inner_rml_contains(spans[i], "item1");
	}
	CHECK(item0 == 1 && item1 == 1);

	/* attributes, classes, properties */
	CHECK(mfrmlui_element_set_attribute(title, "data-x", "1") == MFRMLUI_OK);
	CHECK(mfrmlui_element_has_attribute(title, "data-x") == 1);
	CHECK(mfrmlui_element_get_attribute(title, "data-x", text, (int32_t)sizeof text) == 1 && strcmp(text, "1") == 0);
	CHECK(mfrmlui_element_get_attribute(title, "nope", text, (int32_t)sizeof text) == MFRMLUI_ERROR_NOT_FOUND);
	CHECK(mfrmlui_element_set_class(title, "big", 1) == MFRMLUI_OK);
	CHECK(mfrmlui_element_is_class_set(title, "big") == 1);
	CHECK(mfrmlui_element_set_property(title, "width", "50%") == MFRMLUI_OK);
	CHECK(mfrmlui_element_set_property(title, "width", "@@@") == MFRMLUI_ERROR_FAILED);
	EXPECT_LOGGED(0, 1);
	CHECK(mfrmlui_element_get_value(title, text, (int32_t)sizeof text) == MFRMLUI_ERROR_TYPE_MISMATCH);

	/* --- input routing and listeners --- */
	mfrmlui_element* button = mfrmlui_element_get_element_by_id(root, "btn");
	REQUIRE(button != NULL);
	mfrmlui_event_listener* listener = mfrmlui_element_add_event_listener(button, "click", 0, on_click, on_detach, &ls);
	REQUIRE(listener != NULL);
	listener_stats title_ls;
	memset(&title_ls, 0, sizeof title_ls);
	CHECK(mfrmlui_element_add_event_listener(title, "mouseover", 0, on_click, on_detach, &title_ls) != NULL);

	mfrmlui_rectf bounds;
	CHECK(mfrmlui_element_get_bounds(button, &bounds) == MFRMLUI_OK);
	CHECK(bounds.width == 100.0f && bounds.height == 30.0f);
	const int32_t bx = (int32_t)(bounds.x + bounds.width / 2), by = (int32_t)(bounds.y + bounds.height / 2);

	CHECK(mfrmlui_context_process_mouse_move(context, 700, 550, 0) == MFRMLUI_INPUT_PROPAGATE); /* outside the body */
	CHECK(mfrmlui_context_process_mouse_move(context, bx, by, 0) == MFRMLUI_INPUT_CONSUMED);
	CHECK(mfrmlui_context_get_hover_element(context) == button);
	CHECK(mfrmlui_context_process_mouse_button_down(context, 0, 0) == MFRMLUI_INPUT_CONSUMED);
	CHECK(mfrmlui_context_process_mouse_button_up(context, 0, 0) == MFRMLUI_INPUT_CONSUMED);
	CHECK(ls.clicks == 1);
	CHECK(strcmp(ls.last_type, "click") == 0);
	CHECK(ls.params_ok == 1);
	CHECK(ms.pressed == 1);
	CHECK(ms.pressed_arg_ok == 1);
	CHECK(mfrmlui_context_process_mouse_wheel(context, 0.0f, 1.0f, 0) >= 0);
	CHECK(mfrmlui_context_process_mouse_button_down(context, -1, 0) == MFRMLUI_ERROR_INVALID_ARGUMENT);

	CHECK(mfrmlui_element_click(button) == MFRMLUI_OK);
	CHECK(ls.clicks == 2);
	CHECK(mfrmlui_event_listener_remove(listener) == MFRMLUI_OK);
	CHECK(ls.detaches == 1);
	CHECK(mfrmlui_event_listener_remove(listener) == MFRMLUI_ERROR_INVALID_ARGUMENT);
	CHECK(mfrmlui_element_click(button) == MFRMLUI_OK);
	CHECK(ls.clicks == 2);

	/* text input into a form control */
	mfrmlui_element* name = mfrmlui_element_get_element_by_id(root, "name");
	REQUIRE(name != NULL);
	CHECK(mfrmlui_element_get_value(name, text, (int32_t)sizeof text) == 3 && strcmp(text, "abc") == 0);
	CHECK(mfrmlui_element_focus(name, 1) == 1);
	CHECK(mfrmlui_context_get_focus_element(context) == name);
	CHECK(mfrmlui_context_process_key_down(context, MFRMLUI_KI_END, 0) >= 0);
	CHECK(mfrmlui_context_process_text_input(context, "xyz") == MFRMLUI_INPUT_CONSUMED);
	CHECK(mfrmlui_element_get_value(name, text, (int32_t)sizeof text) > 0 && strstr(text, "xyz") != NULL);
	CHECK(mfrmlui_element_set_value(name, "reset") == MFRMLUI_OK);
	CHECK(mfrmlui_context_process_key_down(context, 999, 0) == MFRMLUI_ERROR_INVALID_ARGUMENT);
	CHECK(mfrmlui_context_process_mouse_leave(context) >= 0);

	/* data model dirtying re-reads the getter */
	const int gets_before = ms.health_gets;
	CHECK(mfrmlui_data_model_dirty_variable(model, "health") == MFRMLUI_OK);
	CHECK(mfrmlui_data_model_is_variable_dirty(model, "health") == 1);
	CHECK(frame(context));
	CHECK(ms.health_gets > gets_before);
	CHECK(mfrmlui_data_model_dirty_all_variables(model) == MFRMLUI_OK);
	CHECK(frame(context));

	/* --- debugger --- */
	CHECK(mfrmlui_debugger_initialise(context) == MFRMLUI_OK);
	CHECK(mfrmlui_debugger_initialise(context) == MFRMLUI_ERROR_ALREADY_INITIALISED);
	CHECK(mfrmlui_debugger_set_visible(1) == MFRMLUI_OK);
	CHECK(mfrmlui_debugger_is_visible() == 1);
	CHECK(frame(context));
	CHECK(mfrmlui_debugger_set_visible(0) == MFRMLUI_OK);

	/* --- hot reload --- */
	CHECK(mfrmlui_document_reload_style_sheet(document) == MFRMLUI_OK);
	mfrmlui_document* reloaded = mfrmlui_document_reload(document);
	CHECK(reloaded != NULL && reloaded != document);
	CHECK(frame(context)); /* old document unloaded here: its title listener detaches */
	CHECK(title_ls.detaches == 1);
	if (reloaded)
	{
		CHECK(mfrmlui_document_is_visible(reloaded) == 1);
		mfrmlui_element* new_title = mfrmlui_element_get_element_by_id(mfrmlui_document_as_element(reloaded), "title");
		CHECK(new_title && inner_rml_contains(new_title, "Hello"));
	}
	mfrmlui_document* memory_doc = mfrmlui_context_load_document_from_memory(context, "<rml><body style=\"font-family: LatoLatin;\"><p>mem</p></body></rml>", NULL);
	CHECK(memory_doc != NULL);
	CHECK(mfrmlui_document_reload(memory_doc) == NULL); /* no reloadable source */
	CHECK(mfrmlui_document_close(memory_doc) == MFRMLUI_OK);
	CHECK(frame(context));

	/* --- teardown --- */
	CHECK(mfrmlui_debugger_shutdown() == MFRMLUI_OK);
	CHECK(mfrmlui_context_destroy(context) == MFRMLUI_OK);
	CHECK(ms.releases == 3); /* health func, pressed event, items variable */
	CHECK(mfrmlui_context_destroy(context) == MFRMLUI_ERROR_INVALID_ARGUMENT);
	CHECK(mfrmlui_data_model_dirty_all_variables(model) == MFRMLUI_ERROR_INVALID_ARGUMENT);

	CHECK(mfrmlui_shutdown() == MFRMLUI_OK);
	CHECK(mfrmlui_is_initialised() == 0);
	CHECK(rs.live_geometry == 0);
	CHECK(rs.live_textures == 0);
	CHECK(mfrmlui_render_interface_destroy(ri) == MFRMLUI_OK);
	CHECK(mfrmlui_render_interface_destroy(ri) == MFRMLUI_ERROR_INVALID_ARGUMENT);

	CHECK(ss.errors == 0);
	CHECK(ss.warnings == 0);

	printf("render: compile=%d render=%d release=%d generate_texture=%d release_texture=%d scissor=%d/%d transform=%d\n",
		rs.compile_geometry, rs.render_geometry, rs.release_geometry, rs.generate_texture, rs.release_texture, rs.enable_scissor,
		rs.set_scissor, rs.set_transform);
	printf("rmlui log: errors=%d warnings=%d\n", ss.errors, ss.warnings);

	/* --- re-initialisation works after shutdown --- */
	CHECK(mfrmlui_initialise(NULL, NULL) == MFRMLUI_OK);
	CHECK(mfrmlui_shutdown() == MFRMLUI_OK);

	if (failures)
	{
		fprintf(stderr, "%d check(s) failed\n", failures);
		return 1;
	}
	printf("OK: mfrmlui smoke test passed\n");
	return 0;
}
