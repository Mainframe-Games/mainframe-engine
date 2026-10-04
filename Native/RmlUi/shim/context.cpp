// Contexts, input and documents.
#include "mfrmlui_internal.h"

#include <RmlUi/Core/Factory.h>

#include <limits>

using namespace mfrmlui;

namespace {

mfrmlui_context* LiveContext(mfrmlui_context* context)
{
	State& state = GetState();
	if (!context || !state.initialised || !state.contexts.count(context) || !context->context)
		return nullptr;
	return context;
}

constexpr const char* kInvalidContext = "invalid context handle (NULL, destroyed, or library not initialised)";
constexpr const char* kInvalidDocument = "NULL document handle";

// RmlUi's Process* functions return true when the event was NOT consumed. Convert once, here.
int32_t InputResult(bool rml_propagate)
{
	return rml_propagate ? MFRMLUI_INPUT_PROPAGATE : MFRMLUI_INPUT_CONSUMED;
}

template <typename F>
int32_t WithContext(mfrmlui_context* context, F&& body)
{
	return Guard<int32_t>(MFRMLUI_ERROR_EXCEPTION, [&]() -> int32_t {
		if (!LiveContext(context))
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, kInvalidContext);
		return body(*context->context);
	});
}

template <typename F>
int32_t WithDocument(mfrmlui_document* document, F&& body)
{
	return Guard<int32_t>(MFRMLUI_ERROR_EXCEPTION, [&]() -> int32_t {
		if (!document)
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, kInvalidDocument);
		return body(*ToRml(document));
	});
}

template <typename F>
mfrmlui_element* ContextElement(mfrmlui_context* context, F&& body)
{
	return Guard<mfrmlui_element*>(nullptr, [&]() -> mfrmlui_element* {
		if (!LiveContext(context))
			return FailNull<mfrmlui_element>(kInvalidContext);
		return ToHandle(body(*context->context));
	});
}

} // namespace

extern "C" {

// ----- context ----------------------------------------------------------------------------------------------------

MFRMLUI_API mfrmlui_context* MFRMLUI_CALL mfrmlui_context_create(const char* name, int32_t width, int32_t height,
	mfrmlui_render_interface* render_interface)
{
	return Guard<mfrmlui_context*>(nullptr, [&]() -> mfrmlui_context* {
		State& state = GetState();
		if (!state.initialised)
			return FailNull<mfrmlui_context>("mfrmlui_context_create: not initialised");
		if (!name || !*name || width < 0 || height < 0)
			return FailNull<mfrmlui_context>("mfrmlui_context_create: empty name or negative dimensions");
		if (!render_interface || !state.render_interfaces.count(render_interface))
			return FailNull<mfrmlui_context>("mfrmlui_context_create: invalid render interface");
		if (Rml::GetContext(name))
			return FailNull<mfrmlui_context>("mfrmlui_context_create: a context with this name already exists");

		auto wrapper = std::make_unique<mfrmlui_context>();
		wrapper->name = name;
		wrapper->render_interface = render_interface;
		wrapper->context = Rml::CreateContext(wrapper->name, Rml::Vector2i(width, height), render_interface);
		if (!wrapper->context)
			return FailNull<mfrmlui_context>("mfrmlui_context_create: Rml::CreateContext failed");

		render_interface->context_count += 1;
		state.contexts.insert(wrapper.get());
		return wrapper.release();
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_context_destroy(mfrmlui_context* context)
{
	return Guard<int32_t>(MFRMLUI_ERROR_EXCEPTION, [&]() -> int32_t {
		if (!LiveContext(context))
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, kInvalidContext);
		DestroyContext(context);
		return MFRMLUI_OK;
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_context_set_dimensions(mfrmlui_context* context, int32_t width, int32_t height)
{
	return WithContext(context, [&](Rml::Context& c) -> int32_t {
		if (width < 0 || height < 0)
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, "mfrmlui_context_set_dimensions: negative dimensions");
		c.SetDimensions(Rml::Vector2i(width, height));
		return MFRMLUI_OK;
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_context_set_density_independent_pixel_ratio(mfrmlui_context* context, float ratio)
{
	return WithContext(context, [&](Rml::Context& c) -> int32_t {
		if (!(ratio > 0.f) || ratio > 64.f)
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, "mfrmlui_context_set_density_independent_pixel_ratio: ratio must be in (0, 64]");
		c.SetDensityIndependentPixelRatio(ratio);
		return MFRMLUI_OK;
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_context_update(mfrmlui_context* context)
{
	return WithContext(context, [&](Rml::Context& c) -> int32_t {
		return c.Update() ? MFRMLUI_OK : Fail(MFRMLUI_ERROR_FAILED, "Rml::Context::Update failed");
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_context_render(mfrmlui_context* context)
{
	return WithContext(context, [&](Rml::Context& c) -> int32_t {
		return c.Render() ? MFRMLUI_OK : Fail(MFRMLUI_ERROR_FAILED, "Rml::Context::Render failed");
	});
}

MFRMLUI_API double MFRMLUI_CALL mfrmlui_context_get_next_update_delay(mfrmlui_context* context)
{
	return Guard<double>(-1.0, [&]() -> double {
		if (!LiveContext(context))
		{
			SetLastError(kInvalidContext);
			return -1.0;
		}
		return context->context->GetNextUpdateDelay();
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_context_enable_mouse_cursor(mfrmlui_context* context, mfrmlui_bool enable)
{
	return WithContext(context, [&](Rml::Context& c) -> int32_t {
		c.EnableMouseCursor(enable != 0);
		return MFRMLUI_OK;
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_context_activate_theme(mfrmlui_context* context, const char* theme_name, mfrmlui_bool activate)
{
	return WithContext(context, [&](Rml::Context& c) -> int32_t {
		if (!theme_name)
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, "mfrmlui_context_activate_theme: NULL theme name");
		c.ActivateTheme(theme_name, activate != 0);
		return MFRMLUI_OK;
	});
}

MFRMLUI_API mfrmlui_element* MFRMLUI_CALL mfrmlui_context_get_root_element(mfrmlui_context* context)
{
	return ContextElement(context, [](Rml::Context& c) { return c.GetRootElement(); });
}

MFRMLUI_API mfrmlui_element* MFRMLUI_CALL mfrmlui_context_get_hover_element(mfrmlui_context* context)
{
	return ContextElement(context, [](Rml::Context& c) { return c.GetHoverElement(); });
}

MFRMLUI_API mfrmlui_element* MFRMLUI_CALL mfrmlui_context_get_focus_element(mfrmlui_context* context)
{
	return ContextElement(context, [](Rml::Context& c) { return c.GetFocusElement(); });
}

MFRMLUI_API mfrmlui_element* MFRMLUI_CALL mfrmlui_context_get_element_at_point(mfrmlui_context* context, float x, float y)
{
	return ContextElement(context, [&](Rml::Context& c) { return c.GetElementAtPoint(Rml::Vector2f(x, y)); });
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_context_get_num_documents(mfrmlui_context* context)
{
	return WithContext(context, [&](Rml::Context& c) -> int32_t { return c.GetNumDocuments(); });
}

MFRMLUI_API mfrmlui_document* MFRMLUI_CALL mfrmlui_context_get_document(mfrmlui_context* context, int32_t index)
{
	return Guard<mfrmlui_document*>(nullptr, [&]() -> mfrmlui_document* {
		if (!LiveContext(context))
			return FailNull<mfrmlui_document>(kInvalidContext);
		if (index < 0 || index >= context->context->GetNumDocuments())
			return FailNull<mfrmlui_document>("mfrmlui_context_get_document: index out of range");
		return ToHandle(context->context->GetDocument(index));
	});
}

MFRMLUI_API mfrmlui_bool MFRMLUI_CALL mfrmlui_context_is_mouse_interacting(mfrmlui_context* context)
{
	return Guard<mfrmlui_bool>(0, [&]() -> mfrmlui_bool {
		if (!LiveContext(context))
		{
			SetLastError(kInvalidContext);
			return 0;
		}
		return context->context->IsMouseInteracting() ? 1 : 0;
	});
}

// ----- input --------------------------------------------------------------------------------------------------------

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_context_process_mouse_move(mfrmlui_context* context, int32_t x, int32_t y, int32_t key_modifiers)
{
	return WithContext(context, [&](Rml::Context& c) { return InputResult(c.ProcessMouseMove(x, y, key_modifiers)); });
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_context_process_mouse_button_down(mfrmlui_context* context, int32_t button, int32_t key_modifiers)
{
	return WithContext(context, [&](Rml::Context& c) -> int32_t {
		if (button < 0)
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, "mfrmlui_context_process_mouse_button_down: negative button");
		return InputResult(c.ProcessMouseButtonDown(button, key_modifiers));
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_context_process_mouse_button_up(mfrmlui_context* context, int32_t button, int32_t key_modifiers)
{
	return WithContext(context, [&](Rml::Context& c) -> int32_t {
		if (button < 0)
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, "mfrmlui_context_process_mouse_button_up: negative button");
		return InputResult(c.ProcessMouseButtonUp(button, key_modifiers));
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_context_process_mouse_wheel(mfrmlui_context* context, float delta_x, float delta_y,
	int32_t key_modifiers)
{
	return WithContext(context, [&](Rml::Context& c) {
		return InputResult(c.ProcessMouseWheel(Rml::Vector2f(delta_x, delta_y), key_modifiers));
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_context_process_mouse_leave(mfrmlui_context* context)
{
	return WithContext(context, [&](Rml::Context& c) { return InputResult(c.ProcessMouseLeave()); });
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_context_process_key_down(mfrmlui_context* context, int32_t key, int32_t key_modifiers)
{
	return WithContext(context, [&](Rml::Context& c) -> int32_t {
		if (key < 0 || key > std::numeric_limits<unsigned char>::max())
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, "mfrmlui_context_process_key_down: key identifier out of range");
		return InputResult(c.ProcessKeyDown(static_cast<Rml::Input::KeyIdentifier>(key), key_modifiers));
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_context_process_key_up(mfrmlui_context* context, int32_t key, int32_t key_modifiers)
{
	return WithContext(context, [&](Rml::Context& c) -> int32_t {
		if (key < 0 || key > std::numeric_limits<unsigned char>::max())
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, "mfrmlui_context_process_key_up: key identifier out of range");
		return InputResult(c.ProcessKeyUp(static_cast<Rml::Input::KeyIdentifier>(key), key_modifiers));
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_context_process_text_input(mfrmlui_context* context, const char* utf8)
{
	return WithContext(context, [&](Rml::Context& c) -> int32_t {
		if (!utf8)
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, "mfrmlui_context_process_text_input: NULL text");
		return InputResult(c.ProcessTextInput(Rml::String(utf8)));
	});
}

// ----- documents ----------------------------------------------------------------------------------------------------

MFRMLUI_API mfrmlui_document* MFRMLUI_CALL mfrmlui_context_load_document(mfrmlui_context* context, const char* path)
{
	return Guard<mfrmlui_document*>(nullptr, [&]() -> mfrmlui_document* {
		if (!LiveContext(context))
			return FailNull<mfrmlui_document>(kInvalidContext);
		if (!path)
			return FailNull<mfrmlui_document>("mfrmlui_context_load_document: NULL path");
		Rml::ElementDocument* document = context->context->LoadDocument(path);
		if (!document)
			return FailNull<mfrmlui_document>("mfrmlui_context_load_document: RmlUi could not load the document (see log)");
		return ToHandle(document);
	});
}

MFRMLUI_API mfrmlui_document* MFRMLUI_CALL mfrmlui_context_load_document_from_memory(mfrmlui_context* context, const char* rml,
	const char* source_url)
{
	return Guard<mfrmlui_document*>(nullptr, [&]() -> mfrmlui_document* {
		if (!LiveContext(context))
			return FailNull<mfrmlui_document>(kInvalidContext);
		if (!rml)
			return FailNull<mfrmlui_document>("mfrmlui_context_load_document_from_memory: NULL rml");
		Rml::ElementDocument* document = source_url ? context->context->LoadDocumentFromMemory(rml, source_url)
													: context->context->LoadDocumentFromMemory(rml);
		if (!document)
			return FailNull<mfrmlui_document>("mfrmlui_context_load_document_from_memory: RmlUi could not load the document (see log)");
		return ToHandle(document);
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_document_show(mfrmlui_document* document, int32_t modal, int32_t focus, int32_t scroll)
{
	return WithDocument(document, [&](Rml::ElementDocument& d) -> int32_t {
		if (modal < MFRMLUI_MODAL_NONE || modal > MFRMLUI_MODAL_KEEP || focus < MFRMLUI_FOCUS_NONE || focus > MFRMLUI_FOCUS_AUTO ||
			scroll < MFRMLUI_SCROLL_NONE || scroll > MFRMLUI_SCROLL_AUTO)
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, "mfrmlui_document_show: invalid modal/focus/scroll flag");
		d.Show(static_cast<Rml::ModalFlag>(modal), static_cast<Rml::FocusFlag>(focus), static_cast<Rml::ScrollFlag>(scroll));
		return MFRMLUI_OK;
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_document_hide(mfrmlui_document* document)
{
	return WithDocument(document, [&](Rml::ElementDocument& d) -> int32_t {
		d.Hide();
		return MFRMLUI_OK;
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_document_close(mfrmlui_document* document)
{
	return WithDocument(document, [&](Rml::ElementDocument& d) -> int32_t {
		d.Close();
		return MFRMLUI_OK;
	});
}

MFRMLUI_API mfrmlui_document* MFRMLUI_CALL mfrmlui_document_reload(mfrmlui_document* document)
{
	return Guard<mfrmlui_document*>(nullptr, [&]() -> mfrmlui_document* {
		if (!document)
			return FailNull<mfrmlui_document>(kInvalidDocument);
		Rml::ElementDocument* old_document = ToRml(document);
		Rml::Context* context = old_document->GetContext();
		if (!context)
			return FailNull<mfrmlui_document>("mfrmlui_document_reload: document has no context");
		const Rml::String source_url = old_document->GetSourceURL();
		if (source_url.empty() || source_url.front() == '[')
			return FailNull<mfrmlui_document>("mfrmlui_document_reload: document has no reloadable source URL");

		const bool was_visible = old_document->IsVisible();
		const bool was_modal = old_document->IsModal();

		Rml::Factory::ClearStyleSheetCache();
		Rml::Factory::ClearTemplateCache();

		Rml::ElementDocument* new_document = context->LoadDocument(source_url);
		if (!new_document)
			return FailNull<mfrmlui_document>("mfrmlui_document_reload: RmlUi could not load the document (see log)");

		old_document->Close();
		if (was_visible)
			new_document->Show(was_modal ? Rml::ModalFlag::Modal : Rml::ModalFlag::None, Rml::FocusFlag::Auto);
		return ToHandle(new_document);
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_document_reload_style_sheet(mfrmlui_document* document)
{
	return WithDocument(document, [&](Rml::ElementDocument& d) -> int32_t {
		d.ReloadStyleSheet();
		return MFRMLUI_OK;
	});
}

MFRMLUI_API mfrmlui_bool MFRMLUI_CALL mfrmlui_document_is_visible(mfrmlui_document* document)
{
	return Guard<mfrmlui_bool>(0, [&]() -> mfrmlui_bool { return document && ToRml(document)->IsVisible() ? 1 : 0; });
}

MFRMLUI_API mfrmlui_bool MFRMLUI_CALL mfrmlui_document_is_modal(mfrmlui_document* document)
{
	return Guard<mfrmlui_bool>(0, [&]() -> mfrmlui_bool { return document && ToRml(document)->IsModal() ? 1 : 0; });
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_document_pull_to_front(mfrmlui_document* document)
{
	return WithDocument(document, [&](Rml::ElementDocument& d) -> int32_t {
		d.PullToFront();
		return MFRMLUI_OK;
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_document_push_to_back(mfrmlui_document* document)
{
	return WithDocument(document, [&](Rml::ElementDocument& d) -> int32_t {
		d.PushToBack();
		return MFRMLUI_OK;
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_document_get_title(mfrmlui_document* document, char* buffer, int32_t capacity)
{
	return WithDocument(document, [&](Rml::ElementDocument& d) { return CopyOut(d.GetTitle(), buffer, capacity); });
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_document_get_source_url(mfrmlui_document* document, char* buffer, int32_t capacity)
{
	return WithDocument(document, [&](Rml::ElementDocument& d) { return CopyOut(d.GetSourceURL(), buffer, capacity); });
}

MFRMLUI_API mfrmlui_element* MFRMLUI_CALL mfrmlui_document_as_element(mfrmlui_document* document)
{
	return Guard<mfrmlui_element*>(nullptr, [&]() -> mfrmlui_element* {
		if (!document)
			return FailNull<mfrmlui_element>(kInvalidDocument);
		return ToHandle(static_cast<Rml::Element*>(ToRml(document)));
	});
}

} // extern "C"
