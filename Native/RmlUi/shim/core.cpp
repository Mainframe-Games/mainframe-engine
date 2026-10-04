// Library lifetime, errors, strings, system and file interfaces, fonts and caches.
#include "mfrmlui_internal.h"

#include <RmlUi/Core/Factory.h>
#include <RmlUi/Core/FileInterface.h>
#include <RmlUi/Core/SystemInterface.h>

#include <limits>

namespace mfrmlui {

namespace {
	thread_local std::string last_error;
}

void SetLastError(std::string message)
{
	last_error = std::move(message);
}

State& GetState()
{
	static State state;
	return state;
}

int32_t CopyOut(const Rml::String& value, char* buffer, int32_t capacity)
{
	if (!ValidBuffer(buffer, capacity))
		return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, "invalid output buffer (NULL buffer with non-zero capacity, or negative capacity)");
	if (value.size() > static_cast<size_t>(std::numeric_limits<int32_t>::max() - 1))
		return Fail(MFRMLUI_ERROR_FAILED, "string too long for the ABI");

	const int32_t length = static_cast<int32_t>(value.size());
	if (capacity > 0)
	{
		const int32_t count = length < capacity - 1 ? length : capacity - 1;
		std::memcpy(buffer, value.data(), static_cast<size_t>(count));
		buffer[count] = '\0';
	}
	return length;
}

class SystemInterfaceImpl final : public Rml::SystemInterface {
public:
	explicit SystemInterfaceImpl(const mfrmlui_system_callbacks& in_callbacks) : cb(in_callbacks) {}

	double GetElapsedTime() override
	{
		return cb.get_elapsed_time ? cb.get_elapsed_time(cb.user_data) : Rml::SystemInterface::GetElapsedTime();
	}

	int TranslateString(Rml::String& translated, const Rml::String& input) override
	{
		translated = input;
		if (!cb.translate_string)
			return 0;
		return cb.translate_string(cb.user_data, input.c_str(), ToHandle(&translated));
	}

	void JoinPath(Rml::String& translated_path, const Rml::String& document_path, const Rml::String& path) override
	{
		if (!cb.join_path)
		{
			Rml::SystemInterface::JoinPath(translated_path, document_path, path);
			return;
		}
		translated_path.clear();
		cb.join_path(cb.user_data, document_path.c_str(), path.c_str(), ToHandle(&translated_path));
	}

	bool LogMessage(Rml::Log::Type type, const Rml::String& message) override
	{
		if (!cb.log_message)
			return Rml::SystemInterface::LogMessage(type, message);
		return cb.log_message(cb.user_data, static_cast<int32_t>(type), message.c_str()) != 0;
	}

	void SetMouseCursor(const Rml::String& cursor_name) override
	{
		if (cb.set_mouse_cursor)
			cb.set_mouse_cursor(cb.user_data, cursor_name.c_str());
	}

	void SetClipboardText(const Rml::String& text) override
	{
		if (cb.set_clipboard_text)
			cb.set_clipboard_text(cb.user_data, text.c_str());
		else
			Rml::SystemInterface::SetClipboardText(text);
	}

	void GetClipboardText(Rml::String& text) override
	{
		if (cb.get_clipboard_text)
		{
			text.clear();
			cb.get_clipboard_text(cb.user_data, ToHandle(&text));
		}
		else
			Rml::SystemInterface::GetClipboardText(text);
	}

	void ActivateKeyboard(Rml::Vector2f caret_position, float line_height) override
	{
		if (cb.activate_keyboard)
			cb.activate_keyboard(cb.user_data, caret_position.x, caret_position.y, line_height);
	}

	void DeactivateKeyboard() override
	{
		if (cb.deactivate_keyboard)
			cb.deactivate_keyboard(cb.user_data);
	}

private:
	mfrmlui_system_callbacks cb;
};

class FileInterfaceImpl final : public Rml::FileInterface {
public:
	explicit FileInterfaceImpl(const mfrmlui_file_callbacks& in_callbacks) : cb(in_callbacks) {}

	Rml::FileHandle Open(const Rml::String& path) override { return static_cast<Rml::FileHandle>(cb.open(cb.user_data, path.c_str())); }

	void Close(Rml::FileHandle file) override { cb.close(cb.user_data, static_cast<uint64_t>(file)); }

	size_t Read(void* buffer, size_t size, Rml::FileHandle file) override
	{
		return static_cast<size_t>(cb.read(cb.user_data, static_cast<uint64_t>(file), buffer, static_cast<uint64_t>(size)));
	}

	bool Seek(Rml::FileHandle file, long offset, int origin) override
	{
		return cb.seek(cb.user_data, static_cast<uint64_t>(file), static_cast<int64_t>(offset), static_cast<int32_t>(origin)) != 0;
	}

	size_t Tell(Rml::FileHandle file) override { return static_cast<size_t>(cb.tell(cb.user_data, static_cast<uint64_t>(file))); }

	size_t Length(Rml::FileHandle file) override
	{
		if (cb.length)
			return static_cast<size_t>(cb.length(cb.user_data, static_cast<uint64_t>(file)));
		return Rml::FileInterface::Length(file);
	}

	bool LoadFile(const Rml::String& path, Rml::String& out_data) override
	{
		if (cb.load_file)
		{
			out_data.clear();
			return cb.load_file(cb.user_data, path.c_str(), ToHandle(&out_data)) != 0;
		}
		return Rml::FileInterface::LoadFile(path, out_data);
	}

private:
	mfrmlui_file_callbacks cb;
};

void DestroyContext(mfrmlui_context* context)
{
	State& state = GetState();
	state.contexts.erase(context);

	// Destroying the RmlUi context unloads its documents (detaching element listeners) and destroys its data models,
	// so nothing can call into the bindings once our wrappers below release them.
	if (state.initialised && context->context)
		Rml::RemoveContext(context->name);
	context->context = nullptr;

	for (auto& model : context->data_models)
		state.data_models.erase(model.get());
	context->data_models.clear();

	if (context->render_interface)
		context->render_interface->context_count -= 1;
	delete context;
}

} // namespace mfrmlui

using namespace mfrmlui;

extern "C" {

MFRMLUI_API uint32_t MFRMLUI_CALL mfrmlui_abi_version(void)
{
	return MFRMLUI_ABI_VERSION;
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_get_rmlui_version(char* buffer, int32_t capacity)
{
	return Guard<int32_t>(MFRMLUI_ERROR_EXCEPTION, [&] { return CopyOut(Rml::GetVersion(), buffer, capacity); });
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_get_last_error(char* buffer, int32_t capacity)
{
	return Guard<int32_t>(MFRMLUI_ERROR_EXCEPTION, [&] {
		// Copy first: CopyOut itself may overwrite last_error on failure.
		const std::string message = last_error;
		return CopyOut(message, buffer, capacity);
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_initialise(const mfrmlui_system_callbacks* system, const mfrmlui_file_callbacks* file)
{
	return Guard<int32_t>(MFRMLUI_ERROR_EXCEPTION, [&]() -> int32_t {
		State& state = GetState();
		if (state.initialised)
			return Fail(MFRMLUI_ERROR_ALREADY_INITIALISED, "mfrmlui_initialise: already initialised");

		std::unique_ptr<SystemInterfaceImpl> system_interface;
		if (system)
		{
			mfrmlui_system_callbacks copy;
			if (!CopyCallbacks(system, copy, sizeof(mfrmlui_system_callbacks)))
				return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, "mfrmlui_initialise: system callbacks struct_size too small");
			system_interface = std::make_unique<SystemInterfaceImpl>(copy);
		}

		std::unique_ptr<FileInterfaceImpl> file_interface;
		if (file)
		{
			mfrmlui_file_callbacks copy;
			if (!CopyCallbacks(file, copy, sizeof(mfrmlui_file_callbacks)))
				return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, "mfrmlui_initialise: file callbacks struct_size too small");
			if (!copy.open || !copy.close || !copy.read || !copy.seek || !copy.tell)
				return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, "mfrmlui_initialise: file callbacks open/close/read/seek/tell are required");
			file_interface = std::make_unique<FileInterfaceImpl>(copy);
		}

		Rml::SetSystemInterface(system_interface.get());
		Rml::SetFileInterface(file_interface.get());
		Rml::SetRenderInterface(nullptr); // render interfaces are per context

		state.system_interface = std::move(system_interface);
		state.file_interface = std::move(file_interface);

		if (!Rml::Initialise())
		{
			Rml::SetSystemInterface(nullptr);
			Rml::SetFileInterface(nullptr);
			state.system_interface.reset();
			state.file_interface.reset();
			return Fail(MFRMLUI_ERROR_FAILED, "Rml::Initialise failed");
		}
		state.initialised = true;
		return MFRMLUI_OK;
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_shutdown(void)
{
	return Guard<int32_t>(MFRMLUI_ERROR_EXCEPTION, [&]() -> int32_t {
		State& state = GetState();
		if (!state.initialised)
			return Fail(MFRMLUI_ERROR_NOT_INITIALISED, "mfrmlui_shutdown: not initialised");

		// Destroy contexts through our wrappers so data model release callbacks run in a defined order.
		std::vector<mfrmlui_context*> contexts(state.contexts.begin(), state.contexts.end());
		for (mfrmlui_context* context : contexts)
			DestroyContext(context);

		// Releases every remaining texture/geometry through the (still alive) render interfaces.
		Rml::Shutdown();

		state.initialised = false;
		state.debugger_initialised = false;
		state.system_interface.reset();
		state.file_interface.reset();
		state.font_memory.clear();
		state.event_listeners.clear(); // all detached by now; defensive
		return MFRMLUI_OK;
	});
}

MFRMLUI_API mfrmlui_bool MFRMLUI_CALL mfrmlui_is_initialised(void)
{
	return GetState().initialised ? 1 : 0;
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_load_font_face(const char* path, mfrmlui_bool fallback_face, int32_t weight)
{
	return Guard<int32_t>(MFRMLUI_ERROR_EXCEPTION, [&]() -> int32_t {
		if (!GetState().initialised)
			return Fail(MFRMLUI_ERROR_NOT_INITIALISED, "mfrmlui_load_font_face: not initialised");
		if (!path || weight < 0 || weight > 1000)
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, "mfrmlui_load_font_face: NULL path or weight outside 0..1000");
		if (!Rml::LoadFontFace(path, fallback_face != 0, static_cast<Rml::Style::FontWeight>(weight)))
			return Fail(MFRMLUI_ERROR_FAILED, "mfrmlui_load_font_face: RmlUi could not load the font face");
		return MFRMLUI_OK;
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_load_font_face_from_memory(const uint8_t* data, int64_t size, const char* family,
	int32_t style, int32_t weight, mfrmlui_bool fallback_face)
{
	return Guard<int32_t>(MFRMLUI_ERROR_EXCEPTION, [&]() -> int32_t {
		State& state = GetState();
		if (!state.initialised)
			return Fail(MFRMLUI_ERROR_NOT_INITIALISED, "mfrmlui_load_font_face_from_memory: not initialised");
		if (!data || size <= 0 || weight < 0 || weight > 1000 ||
			(style != MFRMLUI_FONT_STYLE_NORMAL && style != MFRMLUI_FONT_STYLE_ITALIC))
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, "mfrmlui_load_font_face_from_memory: invalid data, size, style or weight");

		std::vector<Rml::byte> bytes(data, data + size);
		const Rml::Span<const Rml::byte> span(bytes.data(), bytes.size());
		const Rml::String family_name = family ? family : "";
		// Keep the bytes alive before handing them to RmlUi (moving the vector keeps its buffer address).
		state.font_memory.push_back(std::move(bytes));
		if (!Rml::LoadFontFace(span, family_name, static_cast<Rml::Style::FontStyle>(style), static_cast<Rml::Style::FontWeight>(weight),
				fallback_face != 0))
		{
			state.font_memory.pop_back();
			return Fail(MFRMLUI_ERROR_FAILED, "mfrmlui_load_font_face_from_memory: RmlUi could not load the font face");
		}
		return MFRMLUI_OK;
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_clear_style_sheet_cache(void)
{
	return Guard<int32_t>(MFRMLUI_ERROR_EXCEPTION, [&]() -> int32_t {
		if (!GetState().initialised)
			return Fail(MFRMLUI_ERROR_NOT_INITIALISED, "mfrmlui_clear_style_sheet_cache: not initialised");
		Rml::Factory::ClearStyleSheetCache();
		return MFRMLUI_OK;
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_clear_template_cache(void)
{
	return Guard<int32_t>(MFRMLUI_ERROR_EXCEPTION, [&]() -> int32_t {
		if (!GetState().initialised)
			return Fail(MFRMLUI_ERROR_NOT_INITIALISED, "mfrmlui_clear_template_cache: not initialised");
		Rml::Factory::ClearTemplateCache();
		return MFRMLUI_OK;
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_string_set(mfrmlui_string* out, const char* utf8, int32_t length)
{
	return Guard<int32_t>(MFRMLUI_ERROR_EXCEPTION, [&]() -> int32_t {
		if (!out || (!utf8 && length != 0))
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, "mfrmlui_string_set: NULL string handle or text");
		Rml::String* target = ToRml(out);
		if (!utf8)
			target->clear();
		else if (length < 0)
			target->assign(utf8);
		else
			target->assign(utf8, static_cast<size_t>(length));
		return MFRMLUI_OK;
	});
}

} // extern "C"
