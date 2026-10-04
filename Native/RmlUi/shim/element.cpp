// Elements, element event listeners and events.
#include "mfrmlui_internal.h"

#include <RmlUi/Core/Elements/ElementFormControl.h>

#include <limits>

using namespace mfrmlui;

namespace {

constexpr const char* kInvalidElement = "NULL element handle";
constexpr const char* kInvalidEvent = "NULL event handle";

template <typename F>
int32_t WithElement(mfrmlui_element* element, F&& body)
{
	return Guard<int32_t>(MFRMLUI_ERROR_EXCEPTION, [&]() -> int32_t {
		if (!element)
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, kInvalidElement);
		return body(*ToRml(element));
	});
}

template <typename F>
int32_t WithElementAndName(mfrmlui_element* element, const char* name, F&& body)
{
	return Guard<int32_t>(MFRMLUI_ERROR_EXCEPTION, [&]() -> int32_t {
		if (!element || !name)
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, "NULL element handle or name");
		return body(*ToRml(element), Rml::String(name));
	});
}

template <typename F>
mfrmlui_bool ElementPredicate(mfrmlui_element* element, const char* name, F&& body)
{
	return Guard<mfrmlui_bool>(0, [&]() -> mfrmlui_bool {
		if (!element || !name)
		{
			SetLastError("NULL element handle or name");
			return 0;
		}
		return body(*ToRml(element), Rml::String(name)) ? 1 : 0;
	});
}

template <typename F>
mfrmlui_element* ElementQuery(mfrmlui_element* element, F&& body)
{
	return Guard<mfrmlui_element*>(nullptr, [&]() -> mfrmlui_element* {
		if (!element)
			return FailNull<mfrmlui_element>(kInvalidElement);
		return ToHandle(body(*ToRml(element)));
	});
}

template <typename F>
int32_t WithEvent(mfrmlui_event* event, F&& body)
{
	return Guard<int32_t>(MFRMLUI_ERROR_EXCEPTION, [&]() -> int32_t {
		if (!event)
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, kInvalidEvent);
		return body(*ToRml(event));
	});
}

} // namespace

// ----- listener -----------------------------------------------------------------------------------------------------

void mfrmlui_event_listener::ProcessEvent(Rml::Event& event)
{
	if (callback)
		callback(user_data, ToHandle(&event));
}

void mfrmlui_event_listener::OnAttach(Rml::Element* /*attached_element*/)
{
	attached = true;
}

void mfrmlui_event_listener::OnDetach(Rml::Element* /*detached_element*/)
{
	// Each listener object is attached to exactly one (element, event, phase), so detaching ends its life.
	GetState().event_listeners.erase(this);
	if (on_detach)
		on_detach(user_data);
	delete this;
}

extern "C" {

// ----- tree navigation ------------------------------------------------------------------------------------------------

MFRMLUI_API mfrmlui_document* MFRMLUI_CALL mfrmlui_element_get_owner_document(mfrmlui_element* element)
{
	return Guard<mfrmlui_document*>(nullptr, [&]() -> mfrmlui_document* {
		if (!element)
			return FailNull<mfrmlui_document>(kInvalidElement);
		return ToHandle(ToRml(element)->GetOwnerDocument());
	});
}

MFRMLUI_API mfrmlui_element* MFRMLUI_CALL mfrmlui_element_get_parent(mfrmlui_element* element)
{
	return ElementQuery(element, [](Rml::Element& e) { return e.GetParentNode(); });
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_element_get_num_children(mfrmlui_element* element)
{
	return WithElement(element, [](Rml::Element& e) -> int32_t { return e.GetNumChildren(); });
}

MFRMLUI_API mfrmlui_element* MFRMLUI_CALL mfrmlui_element_get_child(mfrmlui_element* element, int32_t index)
{
	return ElementQuery(element, [&](Rml::Element& e) -> Rml::Element* {
		if (index < 0 || index >= e.GetNumChildren())
		{
			SetLastError("mfrmlui_element_get_child: index out of range");
			return nullptr;
		}
		return e.GetChild(index);
	});
}

MFRMLUI_API mfrmlui_element* MFRMLUI_CALL mfrmlui_element_get_element_by_id(mfrmlui_element* element, const char* id)
{
	return ElementQuery(element, [&](Rml::Element& e) -> Rml::Element* { return id ? e.GetElementById(id) : nullptr; });
}

MFRMLUI_API mfrmlui_element* MFRMLUI_CALL mfrmlui_element_query_selector(mfrmlui_element* element, const char* selector)
{
	return ElementQuery(element, [&](Rml::Element& e) -> Rml::Element* { return selector ? e.QuerySelector(selector) : nullptr; });
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_element_query_selector_all(mfrmlui_element* element, const char* selector,
	mfrmlui_element** out_elements, int32_t capacity)
{
	return WithElement(element, [&](Rml::Element& e) -> int32_t {
		if (!selector || capacity < 0 || (!out_elements && capacity > 0))
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, "mfrmlui_element_query_selector_all: NULL selector or invalid output array");
		Rml::ElementList matches;
		e.QuerySelectorAll(matches, selector);
		if (matches.size() > static_cast<size_t>(std::numeric_limits<int32_t>::max()))
			return Fail(MFRMLUI_ERROR_FAILED, "mfrmlui_element_query_selector_all: too many matches");
		const int32_t count = static_cast<int32_t>(matches.size());
		for (int32_t i = 0; i < count && i < capacity; ++i)
			out_elements[i] = ToHandle(matches[static_cast<size_t>(i)]);
		return count;
	});
}

// ----- identity, attributes, classes ----------------------------------------------------------------------------------

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_element_get_tag_name(mfrmlui_element* element, char* buffer, int32_t capacity)
{
	return WithElement(element, [&](Rml::Element& e) { return CopyOut(e.GetTagName(), buffer, capacity); });
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_element_get_id(mfrmlui_element* element, char* buffer, int32_t capacity)
{
	return WithElement(element, [&](Rml::Element& e) { return CopyOut(e.GetId(), buffer, capacity); });
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_element_get_attribute(mfrmlui_element* element, const char* name, char* buffer, int32_t capacity)
{
	return WithElementAndName(element, name, [&](Rml::Element& e, const Rml::String& n) -> int32_t {
		const Rml::Variant* value = e.GetAttribute(n);
		if (!value)
			return Fail(MFRMLUI_ERROR_NOT_FOUND, "mfrmlui_element_get_attribute: attribute not set");
		return CopyOut(value->Get<Rml::String>(), buffer, capacity);
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_element_set_attribute(mfrmlui_element* element, const char* name, const char* value)
{
	return WithElementAndName(element, name, [&](Rml::Element& e, const Rml::String& n) -> int32_t {
		if (!value)
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, "mfrmlui_element_set_attribute: NULL value");
		e.SetAttribute(n, Rml::String(value));
		return MFRMLUI_OK;
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_element_remove_attribute(mfrmlui_element* element, const char* name)
{
	return WithElementAndName(element, name, [&](Rml::Element& e, const Rml::String& n) -> int32_t {
		e.RemoveAttribute(n);
		return MFRMLUI_OK;
	});
}

MFRMLUI_API mfrmlui_bool MFRMLUI_CALL mfrmlui_element_has_attribute(mfrmlui_element* element, const char* name)
{
	return ElementPredicate(element, name, [](Rml::Element& e, const Rml::String& n) { return e.HasAttribute(n); });
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_element_set_class(mfrmlui_element* element, const char* class_name, mfrmlui_bool activate)
{
	return WithElementAndName(element, class_name, [&](Rml::Element& e, const Rml::String& n) -> int32_t {
		e.SetClass(n, activate != 0);
		return MFRMLUI_OK;
	});
}

MFRMLUI_API mfrmlui_bool MFRMLUI_CALL mfrmlui_element_is_class_set(mfrmlui_element* element, const char* class_name)
{
	return ElementPredicate(element, class_name, [](Rml::Element& e, const Rml::String& n) { return e.IsClassSet(n); });
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_element_set_class_names(mfrmlui_element* element, const char* class_names)
{
	return WithElementAndName(element, class_names, [&](Rml::Element& e, const Rml::String& n) -> int32_t {
		e.SetClassNames(n);
		return MFRMLUI_OK;
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_element_set_pseudo_class(mfrmlui_element* element, const char* pseudo_class, mfrmlui_bool activate)
{
	return WithElementAndName(element, pseudo_class, [&](Rml::Element& e, const Rml::String& n) -> int32_t {
		e.SetPseudoClass(n, activate != 0);
		return MFRMLUI_OK;
	});
}

MFRMLUI_API mfrmlui_bool MFRMLUI_CALL mfrmlui_element_is_pseudo_class_set(mfrmlui_element* element, const char* pseudo_class)
{
	return ElementPredicate(element, pseudo_class, [](Rml::Element& e, const Rml::String& n) { return e.IsPseudoClassSet(n); });
}

// ----- style and content ----------------------------------------------------------------------------------------------

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_element_set_property(mfrmlui_element* element, const char* name, const char* value)
{
	return WithElementAndName(element, name, [&](Rml::Element& e, const Rml::String& n) -> int32_t {
		if (!value)
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, "mfrmlui_element_set_property: NULL value");
		if (!e.SetProperty(n, value))
			return Fail(MFRMLUI_ERROR_FAILED, "mfrmlui_element_set_property: unknown property or value does not parse");
		return MFRMLUI_OK;
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_element_remove_property(mfrmlui_element* element, const char* name)
{
	return WithElementAndName(element, name, [&](Rml::Element& e, const Rml::String& n) -> int32_t {
		e.RemoveProperty(n);
		return MFRMLUI_OK;
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_element_get_inner_rml(mfrmlui_element* element, char* buffer, int32_t capacity)
{
	return WithElement(element, [&](Rml::Element& e) { return CopyOut(e.GetInnerRML(), buffer, capacity); });
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_element_set_inner_rml(mfrmlui_element* element, const char* rml)
{
	return WithElementAndName(element, rml, [&](Rml::Element& e, const Rml::String& r) -> int32_t {
		e.SetInnerRML(r);
		return MFRMLUI_OK;
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_element_get_value(mfrmlui_element* element, char* buffer, int32_t capacity)
{
	return WithElement(element, [&](Rml::Element& e) -> int32_t {
		auto* control = dynamic_cast<Rml::ElementFormControl*>(&e);
		if (!control)
			return Fail(MFRMLUI_ERROR_TYPE_MISMATCH, "mfrmlui_element_get_value: element is not a form control");
		return CopyOut(control->GetValue(), buffer, capacity);
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_element_set_value(mfrmlui_element* element, const char* value)
{
	return WithElementAndName(element, value, [&](Rml::Element& e, const Rml::String& v) -> int32_t {
		auto* control = dynamic_cast<Rml::ElementFormControl*>(&e);
		if (!control)
			return Fail(MFRMLUI_ERROR_TYPE_MISMATCH, "mfrmlui_element_set_value: element is not a form control");
		control->SetValue(v);
		return MFRMLUI_OK;
	});
}

// ----- interaction ----------------------------------------------------------------------------------------------------

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_element_focus(mfrmlui_element* element, mfrmlui_bool focus_visible)
{
	return WithElement(element, [&](Rml::Element& e) -> int32_t { return e.Focus(focus_visible != 0) ? 1 : 0; });
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_element_blur(mfrmlui_element* element)
{
	return WithElement(element, [](Rml::Element& e) -> int32_t {
		e.Blur();
		return MFRMLUI_OK;
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_element_click(mfrmlui_element* element)
{
	return WithElement(element, [](Rml::Element& e) -> int32_t {
		e.Click();
		return MFRMLUI_OK;
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_element_scroll_into_view(mfrmlui_element* element, mfrmlui_bool align_with_top)
{
	return WithElement(element, [&](Rml::Element& e) -> int32_t {
		e.ScrollIntoView(align_with_top != 0);
		return MFRMLUI_OK;
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_element_get_bounds(mfrmlui_element* element, mfrmlui_rectf* out_bounds)
{
	return WithElement(element, [&](Rml::Element& e) -> int32_t {
		if (!out_bounds)
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, "mfrmlui_element_get_bounds: NULL output");
		const Rml::Vector2f offset = e.GetAbsoluteOffset(Rml::BoxArea::Border);
		const Rml::Vector2f size = e.GetBox().GetSize(Rml::BoxArea::Border);
		*out_bounds = mfrmlui_rectf{offset.x, offset.y, size.x, size.y};
		return MFRMLUI_OK;
	});
}

// ----- listeners --------------------------------------------------------------------------------------------------------

MFRMLUI_API mfrmlui_event_listener* MFRMLUI_CALL mfrmlui_element_add_event_listener(mfrmlui_element* element, const char* event_type,
	mfrmlui_bool in_capture_phase, mfrmlui_event_callback callback, mfrmlui_release_callback on_detach, void* user_data)
{
	return Guard<mfrmlui_event_listener*>(nullptr, [&]() -> mfrmlui_event_listener* {
		if (!element || !event_type || !*event_type || !callback)
			return FailNull<mfrmlui_event_listener>("mfrmlui_element_add_event_listener: NULL element, event type or callback");

		Rml::Element* target = ToRml(element);
		auto listener = std::make_unique<mfrmlui_event_listener>(target, Rml::String(event_type), in_capture_phase != 0, callback,
			on_detach, user_data);
		GetState().event_listeners.insert(listener.get());
		target->AddEventListener(listener->event_type, listener.get(), listener->in_capture_phase);
		if (!listener->attached)
		{
			GetState().event_listeners.erase(listener.get());
			return FailNull<mfrmlui_event_listener>("mfrmlui_element_add_event_listener: RmlUi did not attach the listener");
		}
		// From here the element owns the listener's lifetime (OnDetach deletes it).
		return listener.release();
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_event_listener_remove(mfrmlui_event_listener* listener)
{
	return Guard<int32_t>(MFRMLUI_ERROR_EXCEPTION, [&]() -> int32_t {
		if (!listener || !GetState().event_listeners.count(listener))
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, "mfrmlui_event_listener_remove: unknown or already detached listener");
		// Triggers OnDetach, which runs on_detach and deletes the listener.
		listener->element->RemoveEventListener(listener->event_type, listener, listener->in_capture_phase);
		return MFRMLUI_OK;
	});
}

// ----- events -------------------------------------------------------------------------------------------------------------

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_event_get_type(mfrmlui_event* event, char* buffer, int32_t capacity)
{
	return WithEvent(event, [&](Rml::Event& e) { return CopyOut(e.GetType(), buffer, capacity); });
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_event_get_phase(mfrmlui_event* event)
{
	return WithEvent(event, [](Rml::Event& e) -> int32_t { return static_cast<int32_t>(e.GetPhase()); });
}

MFRMLUI_API mfrmlui_element* MFRMLUI_CALL mfrmlui_event_get_target_element(mfrmlui_event* event)
{
	return Guard<mfrmlui_element*>(nullptr, [&]() -> mfrmlui_element* {
		if (!event)
			return FailNull<mfrmlui_element>(kInvalidEvent);
		return ToHandle(ToRml(event)->GetTargetElement());
	});
}

MFRMLUI_API mfrmlui_element* MFRMLUI_CALL mfrmlui_event_get_current_element(mfrmlui_event* event)
{
	return Guard<mfrmlui_element*>(nullptr, [&]() -> mfrmlui_element* {
		if (!event)
			return FailNull<mfrmlui_element>(kInvalidEvent);
		return ToHandle(ToRml(event)->GetCurrentElement());
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_event_stop_propagation(mfrmlui_event* event)
{
	return WithEvent(event, [](Rml::Event& e) -> int32_t {
		e.StopPropagation();
		return MFRMLUI_OK;
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_event_stop_immediate_propagation(mfrmlui_event* event)
{
	return WithEvent(event, [](Rml::Event& e) -> int32_t {
		e.StopImmediatePropagation();
		return MFRMLUI_OK;
	});
}

MFRMLUI_API const mfrmlui_dictionary* MFRMLUI_CALL mfrmlui_event_get_parameters(mfrmlui_event* event)
{
	return Guard<const mfrmlui_dictionary*>(nullptr, [&]() -> const mfrmlui_dictionary* {
		if (!event)
		{
			SetLastError(kInvalidEvent);
			return nullptr;
		}
		return ToHandle(&ToRml(event)->GetParameters());
	});
}

} // extern "C"
