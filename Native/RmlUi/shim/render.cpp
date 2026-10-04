// Render interface: forwards RmlUi's Rml::RenderInterface calls to the caller's C callbacks.
#include "mfrmlui_internal.h"

#include <limits>

using namespace mfrmlui;

namespace {

int32_t ClampToInt32(size_t value)
{
	constexpr size_t max = static_cast<size_t>(std::numeric_limits<int32_t>::max());
	return static_cast<int32_t>(value > max ? max : value);
}

constexpr size_t kRequiredRenderCallbacksSize = sizeof(mfrmlui_render_callbacks); // ABI 1.0

} // namespace

Rml::CompiledGeometryHandle mfrmlui_render_interface::CompileGeometry(Rml::Span<const Rml::Vertex> vertices, Rml::Span<const int> indices)
{
	constexpr size_t max = static_cast<size_t>(std::numeric_limits<int32_t>::max());
	if (vertices.size() > max || indices.size() > max)
		return 0;
	return static_cast<Rml::CompiledGeometryHandle>(cb.compile_geometry(cb.user_data,
		reinterpret_cast<const mfrmlui_vertex*>(vertices.data()), static_cast<int32_t>(vertices.size()),
		reinterpret_cast<const int32_t*>(indices.data()), static_cast<int32_t>(indices.size())));
}

void mfrmlui_render_interface::RenderGeometry(Rml::CompiledGeometryHandle geometry, Rml::Vector2f translation, Rml::TextureHandle texture)
{
	cb.render_geometry(cb.user_data, static_cast<uint64_t>(geometry), translation.x, translation.y, static_cast<uint64_t>(texture));
}

void mfrmlui_render_interface::ReleaseGeometry(Rml::CompiledGeometryHandle geometry)
{
	cb.release_geometry(cb.user_data, static_cast<uint64_t>(geometry));
}

Rml::TextureHandle mfrmlui_render_interface::LoadTexture(Rml::Vector2i& texture_dimensions, const Rml::String& source)
{
	int32_t width = 0;
	int32_t height = 0;
	const uint64_t texture = cb.load_texture(cb.user_data, source.c_str(), &width, &height);
	texture_dimensions = Rml::Vector2i(width, height);
	return static_cast<Rml::TextureHandle>(texture);
}

Rml::TextureHandle mfrmlui_render_interface::GenerateTexture(Rml::Span<const Rml::byte> source, Rml::Vector2i source_dimensions)
{
	if (source.size() > static_cast<size_t>(std::numeric_limits<int32_t>::max()))
		return 0;
	return static_cast<Rml::TextureHandle>(cb.generate_texture(cb.user_data, reinterpret_cast<const uint8_t*>(source.data()),
		static_cast<int32_t>(source.size()), source_dimensions.x, source_dimensions.y));
}

void mfrmlui_render_interface::ReleaseTexture(Rml::TextureHandle texture)
{
	cb.release_texture(cb.user_data, static_cast<uint64_t>(texture));
}

void mfrmlui_render_interface::EnableScissorRegion(bool enable)
{
	cb.enable_scissor_region(cb.user_data, enable ? 1 : 0);
}

void mfrmlui_render_interface::SetScissorRegion(Rml::Rectanglei region)
{
	cb.set_scissor_region(cb.user_data, region.Left(), region.Top(), region.Width(), region.Height());
}

void mfrmlui_render_interface::EnableClipMask(bool enable)
{
	if (cb.enable_clip_mask)
		cb.enable_clip_mask(cb.user_data, enable ? 1 : 0);
	else
		Rml::RenderInterface::EnableClipMask(enable);
}

void mfrmlui_render_interface::RenderToClipMask(Rml::ClipMaskOperation operation, Rml::CompiledGeometryHandle geometry, Rml::Vector2f translation)
{
	if (cb.render_to_clip_mask)
		cb.render_to_clip_mask(cb.user_data, static_cast<int32_t>(operation), static_cast<uint64_t>(geometry), translation.x, translation.y);
	else
		Rml::RenderInterface::RenderToClipMask(operation, geometry, translation);
}

void mfrmlui_render_interface::SetTransform(const Rml::Matrix4f* transform)
{
	if (cb.set_transform)
		cb.set_transform(cb.user_data, transform ? transform->data() : nullptr);
	else
		Rml::RenderInterface::SetTransform(transform);
}

Rml::LayerHandle mfrmlui_render_interface::PushLayer()
{
	if (cb.push_layer)
		return static_cast<Rml::LayerHandle>(cb.push_layer(cb.user_data));
	return Rml::RenderInterface::PushLayer();
}

void mfrmlui_render_interface::CompositeLayers(Rml::LayerHandle source, Rml::LayerHandle destination, Rml::BlendMode blend_mode,
	Rml::Span<const Rml::CompiledFilterHandle> filters)
{
	if (cb.composite_layers)
		cb.composite_layers(cb.user_data, static_cast<uint64_t>(source), static_cast<uint64_t>(destination), static_cast<int32_t>(blend_mode),
			reinterpret_cast<const uint64_t*>(filters.data()), ClampToInt32(filters.size()));
	else
		Rml::RenderInterface::CompositeLayers(source, destination, blend_mode, filters);
}

void mfrmlui_render_interface::PopLayer()
{
	if (cb.pop_layer)
		cb.pop_layer(cb.user_data);
	else
		Rml::RenderInterface::PopLayer();
}

Rml::TextureHandle mfrmlui_render_interface::SaveLayerAsTexture()
{
	if (cb.save_layer_as_texture)
		return static_cast<Rml::TextureHandle>(cb.save_layer_as_texture(cb.user_data));
	return Rml::RenderInterface::SaveLayerAsTexture();
}

Rml::CompiledFilterHandle mfrmlui_render_interface::SaveLayerAsMaskImage()
{
	if (cb.save_layer_as_mask_image)
		return static_cast<Rml::CompiledFilterHandle>(cb.save_layer_as_mask_image(cb.user_data));
	return Rml::RenderInterface::SaveLayerAsMaskImage();
}

Rml::CompiledFilterHandle mfrmlui_render_interface::CompileFilter(const Rml::String& name, const Rml::Dictionary& parameters)
{
	if (cb.compile_filter)
		return static_cast<Rml::CompiledFilterHandle>(cb.compile_filter(cb.user_data, name.c_str(), ToHandle(&parameters)));
	return Rml::RenderInterface::CompileFilter(name, parameters);
}

void mfrmlui_render_interface::ReleaseFilter(Rml::CompiledFilterHandle filter)
{
	if (cb.release_filter)
		cb.release_filter(cb.user_data, static_cast<uint64_t>(filter));
	else
		Rml::RenderInterface::ReleaseFilter(filter);
}

Rml::CompiledShaderHandle mfrmlui_render_interface::CompileShader(const Rml::String& name, const Rml::Dictionary& parameters)
{
	if (cb.compile_shader)
		return static_cast<Rml::CompiledShaderHandle>(cb.compile_shader(cb.user_data, name.c_str(), ToHandle(&parameters)));
	return Rml::RenderInterface::CompileShader(name, parameters);
}

void mfrmlui_render_interface::RenderShader(Rml::CompiledShaderHandle shader, Rml::CompiledGeometryHandle geometry, Rml::Vector2f translation,
	Rml::TextureHandle texture)
{
	if (cb.render_shader)
		cb.render_shader(cb.user_data, static_cast<uint64_t>(shader), static_cast<uint64_t>(geometry), translation.x, translation.y,
			static_cast<uint64_t>(texture));
	else
		Rml::RenderInterface::RenderShader(shader, geometry, translation, texture);
}

void mfrmlui_render_interface::ReleaseShader(Rml::CompiledShaderHandle shader)
{
	if (cb.release_shader)
		cb.release_shader(cb.user_data, static_cast<uint64_t>(shader));
	else
		Rml::RenderInterface::ReleaseShader(shader);
}

extern "C" {

MFRMLUI_API mfrmlui_render_interface* MFRMLUI_CALL mfrmlui_render_interface_create(const mfrmlui_render_callbacks* callbacks)
{
	return Guard<mfrmlui_render_interface*>(nullptr, [&]() -> mfrmlui_render_interface* {
		mfrmlui_render_callbacks copy;
		if (!CopyCallbacks(callbacks, copy, kRequiredRenderCallbacksSize))
			return FailNull<mfrmlui_render_interface>("mfrmlui_render_interface_create: NULL callbacks or struct_size too small");
		if (!copy.compile_geometry || !copy.render_geometry || !copy.release_geometry || !copy.load_texture || !copy.generate_texture ||
			!copy.release_texture || !copy.enable_scissor_region || !copy.set_scissor_region)
			return FailNull<mfrmlui_render_interface>("mfrmlui_render_interface_create: a required render callback is NULL");

		auto render_interface = std::make_unique<mfrmlui_render_interface>(copy);
		GetState().render_interfaces.insert(render_interface.get());
		return render_interface.release();
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_render_interface_destroy(mfrmlui_render_interface* render_interface)
{
	return Guard<int32_t>(MFRMLUI_ERROR_EXCEPTION, [&]() -> int32_t {
		State& state = GetState();
		if (!render_interface || !state.render_interfaces.count(render_interface))
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, "mfrmlui_render_interface_destroy: unknown render interface");
		if (render_interface->context_count > 0)
			return Fail(MFRMLUI_ERROR_IN_USE, "mfrmlui_render_interface_destroy: still used by a context");

		if (state.initialised)
		{
			// Hand back everything RmlUi still holds for this interface, then drop its (now unused) render manager so
			// a future interface allocated at the same address cannot inherit stale state.
			Rml::ReleaseTextures(render_interface);
			Rml::ReleaseCompiledGeometry(render_interface);
			Rml::ReleaseRenderManagers();
		}
		state.render_interfaces.erase(render_interface);
		delete render_interface;
		return MFRMLUI_OK;
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_release_textures(mfrmlui_render_interface* render_interface)
{
	return Guard<int32_t>(MFRMLUI_ERROR_EXCEPTION, [&]() -> int32_t {
		State& state = GetState();
		if (!state.initialised)
			return Fail(MFRMLUI_ERROR_NOT_INITIALISED, "mfrmlui_release_textures: not initialised");
		if (render_interface && !state.render_interfaces.count(render_interface))
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, "mfrmlui_release_textures: unknown render interface");
		Rml::ReleaseTextures(render_interface);
		return MFRMLUI_OK;
	});
}

MFRMLUI_API int32_t MFRMLUI_CALL mfrmlui_release_compiled_geometry(mfrmlui_render_interface* render_interface)
{
	return Guard<int32_t>(MFRMLUI_ERROR_EXCEPTION, [&]() -> int32_t {
		State& state = GetState();
		if (!state.initialised)
			return Fail(MFRMLUI_ERROR_NOT_INITIALISED, "mfrmlui_release_compiled_geometry: not initialised");
		if (render_interface && !state.render_interfaces.count(render_interface))
			return Fail(MFRMLUI_ERROR_INVALID_ARGUMENT, "mfrmlui_release_compiled_geometry: unknown render interface");
		Rml::ReleaseCompiledGeometry(render_interface);
		return MFRMLUI_OK;
	});
}

} // extern "C"
