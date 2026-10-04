// Internal declarations shared by the mfrmlui shim translation units. Not part of the public ABI.
//
// Owned handles (context, render interface, data model, event listener) are the C++ definitions of the opaque
// structs declared in mfrmlui.h. Borrowed handles (element, document, event, variant, dictionary, string) are
// reinterpret_casts of the corresponding RmlUi objects and are never defined.
#pragma once

#include "mfrmlui.h"

#include <RmlUi/Core.h>
#include <RmlUi/Core/DataVariable.h>

#include <cstdint>
#include <cstring>
#include <exception>
#include <memory>
#include <string>
#include <unordered_set>
#include <utility>
#include <vector>

static_assert(sizeof(void*) == 8, "mfrmlui supports 64-bit targets only");

namespace mfrmlui {

class SystemInterfaceImpl;
class FileInterfaceImpl;

// ----- errors -------------------------------------------------------------------------------------------------

void SetLastError(std::string message);

inline int32_t Fail(int32_t code, const char* message)
{
	SetLastError(message);
	return code;
}

template <typename T>
inline T* FailNull(const char* message)
{
	SetLastError(message);
	return nullptr;
}

// Runs `body` and converts any escaping C++ exception into `on_exception` (+ last error). Every exported function
// goes through this, so no exception ever crosses the C ABI.
template <typename R, typename F>
R Guard(R on_exception, F&& body) noexcept
{
	try
	{
		return body();
	}
	catch (const std::exception& e)
	{
		try
		{
			SetLastError(std::string("C++ exception: ") + e.what());
		}
		catch (...)
		{
		}
	}
	catch (...)
	{
		try
		{
			SetLastError("unknown C++ exception");
		}
		catch (...)
		{
		}
	}
	return on_exception;
}

// Caller-buffer string output (see "Strings" in mfrmlui.h).
int32_t CopyOut(const Rml::String& value, char* buffer, int32_t capacity);

inline bool ValidBuffer(const char* buffer, int32_t capacity)
{
	return capacity >= 0 && (buffer != nullptr || capacity == 0);
}

// ----- callback struct copying ------------------------------------------------------------------------------------

// Copies a caller-supplied callback struct. Fields beyond the caller's struct_size (an older, smaller struct from a
// previous ABI minor version) are zeroed, i.e. treated as NULL optional callbacks. `minimum_size` is the size of the
// struct in ABI 1.0, below which the struct is rejected.
template <typename T>
bool CopyCallbacks(const T* in, T& out, size_t minimum_size)
{
	std::memset(&out, 0, sizeof(T));
	if (!in || in->struct_size < minimum_size)
		return false;
	const size_t size = in->struct_size < sizeof(T) ? in->struct_size : sizeof(T);
	std::memcpy(&out, in, size);
	out.struct_size = static_cast<uint32_t>(sizeof(T));
	return true;
}

// ----- borrowed handle conversions ----------------------------------------------------------------------------

inline Rml::Element* ToRml(mfrmlui_element* e) { return reinterpret_cast<Rml::Element*>(e); }
inline mfrmlui_element* ToHandle(Rml::Element* e) { return reinterpret_cast<mfrmlui_element*>(e); }
inline Rml::ElementDocument* ToRml(mfrmlui_document* d) { return reinterpret_cast<Rml::ElementDocument*>(d); }
inline mfrmlui_document* ToHandle(Rml::ElementDocument* d) { return reinterpret_cast<mfrmlui_document*>(d); }
inline Rml::Event* ToRml(mfrmlui_event* e) { return reinterpret_cast<Rml::Event*>(e); }
inline mfrmlui_event* ToHandle(Rml::Event* e) { return reinterpret_cast<mfrmlui_event*>(e); }
inline Rml::Variant* ToRml(mfrmlui_variant* v) { return reinterpret_cast<Rml::Variant*>(v); }
inline const Rml::Variant* ToRml(const mfrmlui_variant* v) { return reinterpret_cast<const Rml::Variant*>(v); }
inline mfrmlui_variant* ToHandle(Rml::Variant* v) { return reinterpret_cast<mfrmlui_variant*>(v); }
inline const mfrmlui_variant* ToHandle(const Rml::Variant* v) { return reinterpret_cast<const mfrmlui_variant*>(v); }
inline const Rml::Dictionary* ToRml(const mfrmlui_dictionary* d) { return reinterpret_cast<const Rml::Dictionary*>(d); }
inline const mfrmlui_dictionary* ToHandle(const Rml::Dictionary* d)
{
	return reinterpret_cast<const mfrmlui_dictionary*>(d);
}
inline Rml::String* ToRml(mfrmlui_string* s) { return reinterpret_cast<Rml::String*>(s); }
inline mfrmlui_string* ToHandle(Rml::String* s) { return reinterpret_cast<mfrmlui_string*>(s); }

inline void* NodeToPointer(uint64_t node) { return reinterpret_cast<void*>(static_cast<uintptr_t>(node)); }
inline uint64_t PointerToNode(void* ptr) { return static_cast<uint64_t>(reinterpret_cast<uintptr_t>(ptr)); }

// ----- library state ------------------------------------------------------------------------------------------------

struct State {
	bool initialised = false;
	bool debugger_initialised = false;
	std::unique_ptr<SystemInterfaceImpl> system_interface;
	std::unique_ptr<FileInterfaceImpl> file_interface;
	std::unordered_set<mfrmlui_context*> contexts;
	std::unordered_set<mfrmlui_render_interface*> render_interfaces;
	std::unordered_set<mfrmlui_data_model*> data_models;
	std::unordered_set<mfrmlui_event_listener*> event_listeners;
	// Font faces loaded from memory: RmlUi keeps pointers into these until shutdown.
	std::vector<std::vector<Rml::byte>> font_memory;
};

State& GetState();

// Destroys a context wrapper (Rml context first, then its data models). Assumes `context` is live.
void DestroyContext(mfrmlui_context* context);

} // namespace mfrmlui

// ----- owned handle definitions ----------------------------------------------------------------------------------

struct mfrmlui_render_interface final : public Rml::RenderInterface {
	explicit mfrmlui_render_interface(const mfrmlui_render_callbacks& in_callbacks) : cb(in_callbacks) {}

	Rml::CompiledGeometryHandle CompileGeometry(Rml::Span<const Rml::Vertex> vertices, Rml::Span<const int> indices) override;
	void RenderGeometry(Rml::CompiledGeometryHandle geometry, Rml::Vector2f translation, Rml::TextureHandle texture) override;
	void ReleaseGeometry(Rml::CompiledGeometryHandle geometry) override;
	Rml::TextureHandle LoadTexture(Rml::Vector2i& texture_dimensions, const Rml::String& source) override;
	Rml::TextureHandle GenerateTexture(Rml::Span<const Rml::byte> source, Rml::Vector2i source_dimensions) override;
	void ReleaseTexture(Rml::TextureHandle texture) override;
	void EnableScissorRegion(bool enable) override;
	void SetScissorRegion(Rml::Rectanglei region) override;

	void EnableClipMask(bool enable) override;
	void RenderToClipMask(Rml::ClipMaskOperation operation, Rml::CompiledGeometryHandle geometry, Rml::Vector2f translation) override;
	void SetTransform(const Rml::Matrix4f* transform) override;
	Rml::LayerHandle PushLayer() override;
	void CompositeLayers(Rml::LayerHandle source, Rml::LayerHandle destination, Rml::BlendMode blend_mode,
		Rml::Span<const Rml::CompiledFilterHandle> filters) override;
	void PopLayer() override;
	Rml::TextureHandle SaveLayerAsTexture() override;
	Rml::CompiledFilterHandle SaveLayerAsMaskImage() override;
	Rml::CompiledFilterHandle CompileFilter(const Rml::String& name, const Rml::Dictionary& parameters) override;
	void ReleaseFilter(Rml::CompiledFilterHandle filter) override;
	Rml::CompiledShaderHandle CompileShader(const Rml::String& name, const Rml::Dictionary& parameters) override;
	void RenderShader(Rml::CompiledShaderHandle shader, Rml::CompiledGeometryHandle geometry, Rml::Vector2f translation,
		Rml::TextureHandle texture) override;
	void ReleaseShader(Rml::CompiledShaderHandle shader) override;

	mfrmlui_render_callbacks cb;
	int32_t context_count = 0;
};

struct mfrmlui_data_model {
	struct Release {
		mfrmlui_release_callback callback;
		void* user_data;
	};
	struct Variable;

	mfrmlui_data_model(mfrmlui_context* in_owner, std::string in_name, Rml::DataModelConstructor in_constructor);
	~mfrmlui_data_model();
	mfrmlui_data_model(const mfrmlui_data_model&) = delete;
	mfrmlui_data_model& operator=(const mfrmlui_data_model&) = delete;

	mfrmlui_context* owner;
	std::string name;
	Rml::DataModelConstructor constructor;
	Rml::DataModelHandle handle;
	std::vector<Release> releases;
	std::vector<std::unique_ptr<Variable>> variables;
};

struct mfrmlui_context {
	Rml::Context* context = nullptr;
	std::string name;
	mfrmlui_render_interface* render_interface = nullptr;
	std::vector<std::unique_ptr<mfrmlui_data_model>> data_models;
};

struct mfrmlui_event_listener final : public Rml::EventListener {
	mfrmlui_event_listener(Rml::Element* in_element, Rml::String in_event_type, bool in_capture, mfrmlui_event_callback in_callback,
		mfrmlui_release_callback in_on_detach, void* in_user_data) :
		element(in_element), event_type(std::move(in_event_type)), in_capture_phase(in_capture), callback(in_callback),
		on_detach(in_on_detach), user_data(in_user_data)
	{}

	void ProcessEvent(Rml::Event& event) override;
	void OnAttach(Rml::Element* attached_element) override;
	void OnDetach(Rml::Element* detached_element) override;

	Rml::Element* element;
	Rml::String event_type;
	bool in_capture_phase;
	bool attached = false;
	mfrmlui_event_callback callback;
	mfrmlui_release_callback on_detach;
	void* user_data;
};
