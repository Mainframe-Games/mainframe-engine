// RmlUi visual debugger plugin.
#include "mfrmlui_internal.h"

#include <RmlUi/Debugger.h>

using namespace mfrmlui;

namespace {

bool IsLiveContext(mfrmlui_context* context)
{
	State& state = GetState();
	return context && state.initialised && state.contexts.count(context) && context->context;
}

} // namespace

extern "C" {

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_debugger_initialise(mfrmlui_context* host_context)
{
	return Guard<int32_t>(MFRMLUI_ERROR_EXCEPTION, [&]() -> int32_t {
		State& state = GetState();
		if (!IsLiveContext(host_context))
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, "mfrmlui_debugger_initialise: invalid context handle");
		if (state.debugger_initialised)
			return Fail(MFRMLUI_ERROR_ALREADY_INITIALISED, "mfrmlui_debugger_initialise: debugger already initialised");
		if (!Rml::Debugger::Initialise(host_context->context))
			return Fail(MFRMLUI_ERROR_FAILED, "Rml::Debugger::Initialise failed");
		state.debugger_initialised = true;
		return MFRMLUI_OK;
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_debugger_set_context(mfrmlui_context* context)
{
	return Guard<int32_t>(MFRMLUI_ERROR_EXCEPTION, [&]() -> int32_t {
		if (!GetState().debugger_initialised)
			return Fail(MFRMLUI_ERROR_NOT_INITIALISED, "mfrmlui_debugger_set_context: debugger not initialised");
		if (!IsLiveContext(context))
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, "mfrmlui_debugger_set_context: invalid context handle");
		return Rml::Debugger::SetContext(context->context) ? MFRMLUI_OK : Fail(MFRMLUI_ERROR_FAILED, "Rml::Debugger::SetContext failed");
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_debugger_set_visible(mfrmlui_bool visible)
{
	return Guard<int32_t>(MFRMLUI_ERROR_EXCEPTION, [&]() -> int32_t {
		if (!GetState().debugger_initialised)
			return Fail(MFRMLUI_ERROR_NOT_INITIALISED, "mfrmlui_debugger_set_visible: debugger not initialised");
		Rml::Debugger::SetVisible(visible != 0);
		return MFRMLUI_OK;
	});
}

MFRMLUI_API mfrmlui_bool MFRMLUI_CALL mfrmlui_debugger_is_visible(void)
{
	return Guard<mfrmlui_bool>(0, [&]() -> mfrmlui_bool { return GetState().debugger_initialised && Rml::Debugger::IsVisible() ? 1 : 0; });
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_debugger_shutdown(void)
{
	return Guard<int32_t>(MFRMLUI_ERROR_EXCEPTION, [&]() -> int32_t {
		State& state = GetState();
		if (!state.debugger_initialised)
			return Fail(MFRMLUI_ERROR_NOT_INITIALISED, "mfrmlui_debugger_shutdown: debugger not initialised");
		Rml::Debugger::Shutdown();
		state.debugger_initialised = false;
		return MFRMLUI_OK;
	});
}

} // extern "C"
